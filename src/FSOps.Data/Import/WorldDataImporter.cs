using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using FSOps.Core.Airports;
using FSOps.Core.Csv;
using FSOps.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FSOps.Data.Import;

/// <summary>
/// One-time offline seed of the world airport/runway data, shipped gzip-compressed with the
/// app so first run works with no network access. Reads OurAirports' airports.csv and
/// runways.csv, maps them onto our entities, and bulk-inserts everything inside a single
/// transaction so a crash mid-import just leaves the Airport table empty for a clean retry
/// on next launch, rather than a half-seeded database.
/// </summary>
public class WorldDataImporter
{
    private const int InsertBatchSize = 2000;
    private const int UpdateBatchSize = 500;

    private static readonly Regex IcaoLikePattern = new("^[A-Z0-9]{4}$", RegexOptions.Compiled);

    private readonly ILogger<WorldDataImporter> _logger;
    private readonly WorldDataImportProgress _progress;

    public WorldDataImporter(ILogger<WorldDataImporter> logger, WorldDataImportProgress progress)
    {
        _logger = logger;
        _progress = progress;
    }

    public async Task ImportIfNeededAsync(FsOpsDbContext db, string dataDirectory, CancellationToken ct = default)
    {
        if (await db.Airports.AnyAsync(ct))
        {
            _progress.MarkAlreadySeeded(await db.Airports.CountAsync(ct), await db.Runways.CountAsync(ct));
            _logger.LogInformation("World data already seeded - skipping import.");
            return;
        }

        var airportsPath = Path.Combine(dataDirectory, "airports.csv.gz");
        var runwaysPath = Path.Combine(dataDirectory, "runways.csv.gz");

        if (!File.Exists(airportsPath) || !File.Exists(runwaysPath))
        {
            _logger.LogWarning("World data seed files not found under {Directory} - skipping import.", dataDirectory);
            return;
        }

        _logger.LogInformation("Starting world data import from {Directory}...", dataDirectory);
        _progress.MarkStarted();
        var stopwatch = Stopwatch.StartNew();

        var previousAutoDetect = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var identToIcao = await ImportAirportsAsync(db, airportsPath, ct);
            _progress.SetProgressPercent(55);

            var runwayCount = await ImportRunwaysAsync(db, runwaysPath, identToIcao, ct);
            _progress.SetProgressPercent(95);

            await transaction.CommitAsync(ct);

            var airportCount = identToIcao.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            stopwatch.Stop();
            _logger.LogInformation(
                "World data import finished in {ElapsedSeconds:F1}s - {AirportCount} airports, {RunwayCount} runways.",
                stopwatch.Elapsed.TotalSeconds, airportCount, runwayCount);
            _progress.MarkCompleted(airportCount, runwayCount);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            _progress.MarkFailed();
            throw;
        }
        finally
        {
            db.ChangeTracker.AutoDetectChangesEnabled = previousAutoDetect;
        }
    }

    private async Task<Dictionary<string, string>> ImportAirportsAsync(FsOpsDbContext db, string path, CancellationToken ct)
    {
        // Maps the OurAirports "ident" column to the ICAO code we actually chose as the PK,
        // since they aren't always the same value (see ResolveIcao) - runways.csv joins on
        // "ident", so this is how the runway pass finds the right airport.
        var identToIcao = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seenIcao = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batch = new List<Airport>(InsertBatchSize);
        var rowCount = 0;

        using var reader = OpenGzipCsv(path);
        Dictionary<string, int>? columns = null;

        foreach (var fields in SimpleCsvReader.Read(reader))
        {
            ct.ThrowIfCancellationRequested();

            if (columns is null)
            {
                columns = BuildColumnIndex(fields);
                continue;
            }

            rowCount++;

            var ident = Get(fields, columns, "ident");
            var airport = MapAirport(fields, columns);
            if (airport is null)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(ident))
            {
                identToIcao.TryAdd(ident, airport.Icao);
            }

            if (!seenIcao.Add(airport.Icao))
            {
                continue; // duplicate resolved ICAO in the source - keep the first occurrence
            }

            batch.Add(airport);

            if (batch.Count >= InsertBatchSize)
            {
                await FlushAirportsAsync(db, batch, ct);
            }

            if (rowCount % 20000 == 0)
            {
                _logger.LogInformation("Processed {Count} airport rows...", rowCount);
            }
        }

        await FlushAirportsAsync(db, batch, ct);
        return identToIcao;
    }

    private static async Task FlushAirportsAsync(FsOpsDbContext db, List<Airport> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            return;
        }

        db.Airports.AddRange(batch);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        batch.Clear();
    }

    private async Task<int> ImportRunwaysAsync(
        FsOpsDbContext db,
        string path,
        Dictionary<string, string> identToIcao,
        CancellationToken ct)
    {
        var batch = new List<Runway>(InsertBatchSize);
        var longestByIcao = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var total = 0;

        using var reader = OpenGzipCsv(path);
        Dictionary<string, int>? columns = null;

        foreach (var fields in SimpleCsvReader.Read(reader))
        {
            ct.ThrowIfCancellationRequested();

            if (columns is null)
            {
                columns = BuildColumnIndex(fields);
                continue;
            }

            var airportIdent = Get(fields, columns, "airport_ident");
            if (string.IsNullOrEmpty(airportIdent) || !identToIcao.TryGetValue(airportIdent, out var icao))
            {
                continue; // airport wasn't imported (bad coords, unrecognised type, etc.)
            }

            var lengthFt = ParseInt(Get(fields, columns, "length_ft")) ?? 0;
            var widthFt = ParseInt(Get(fields, columns, "width_ft")) ?? 0;
            var surface = Get(fields, columns, "surface") ?? string.Empty;
            var isLighted = Get(fields, columns, "lighted") == "1";
            var isClosed = Get(fields, columns, "closed") == "1";

            foreach (var runway in BuildDirectionalRunways(icao, fields, columns, lengthFt, widthFt, surface, isLighted, isClosed))
            {
                batch.Add(runway);
                total++;
            }

            if (lengthFt > 0 && (!longestByIcao.TryGetValue(icao, out var current) || lengthFt > current))
            {
                longestByIcao[icao] = lengthFt;
            }

            if (batch.Count >= InsertBatchSize)
            {
                await FlushRunwaysAsync(db, batch, ct);
            }
        }

        await FlushRunwaysAsync(db, batch, ct);
        await StampLongestRunwaysAsync(db, longestByIcao, ct);

        return total;
    }

    private static async Task FlushRunwaysAsync(FsOpsDbContext db, List<Runway> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            return;
        }

        db.Runways.AddRange(batch);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        batch.Clear();
    }

    private static async Task StampLongestRunwaysAsync(
        FsOpsDbContext db,
        Dictionary<string, int> longestByIcao,
        CancellationToken ct)
    {
        if (longestByIcao.Count == 0)
        {
            return;
        }

        // Needs change detection back on so the targeted property update is picked up by
        // SaveChanges - the bulk-insert passes above didn't need it.
        db.ChangeTracker.AutoDetectChangesEnabled = true;

        foreach (var chunk in Chunk(longestByIcao.Keys, UpdateBatchSize))
        {
            var airports = await db.Airports.Where(a => chunk.Contains(a.Icao)).ToListAsync(ct);
            foreach (var airport in airports)
            {
                airport.LongestRunwayFt = longestByIcao[airport.Icao];
            }

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
    }

    private static StreamReader OpenGzipCsv(string path)
    {
        var fileStream = File.OpenRead(path);
        var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
        return new StreamReader(gzip);
    }

    private static Airport? MapAirport(string[] fields, Dictionary<string, int> columns)
    {
        var lat = ParseDouble(Get(fields, columns, "latitude_deg"));
        var lon = ParseDouble(Get(fields, columns, "longitude_deg"));
        if (lat is null || lon is null)
        {
            return null; // skip rows with missing/invalid coordinates
        }

        var icao = ResolveIcao(fields, columns);
        if (icao is null)
        {
            return null;
        }

        var iata = Get(fields, columns, "iata_code");
        iata = string.IsNullOrWhiteSpace(iata) ? null : iata.Trim().ToUpperInvariant();

        return new Airport
        {
            Icao = icao,
            Iata = iata,
            Name = Get(fields, columns, "name") ?? string.Empty,
            Municipality = Get(fields, columns, "municipality") ?? string.Empty,
            Country = Get(fields, columns, "iso_country") ?? string.Empty,
            Latitude = lat.Value,
            Longitude = lon.Value,
            ElevationFt = ParseInt(Get(fields, columns, "elevation_ft")) ?? 0,
            SizeCategory = AirportSizeCategoryMapper.Map(Get(fields, columns, "type")),
            HasScheduledService = string.Equals(Get(fields, columns, "scheduled_service"), "yes", StringComparison.OrdinalIgnoreCase),
            LongestRunwayFt = 0,
        };
    }

    private static string? ResolveIcao(string[] fields, Dictionary<string, int> columns)
    {
        var icaoCode = Get(fields, columns, "icao_code");
        if (IsIcaoLike(icaoCode))
        {
            return icaoCode!.Trim().ToUpperInvariant();
        }

        var gpsCode = Get(fields, columns, "gps_code");
        if (IsIcaoLike(gpsCode))
        {
            return gpsCode!.Trim().ToUpperInvariant();
        }

        var ident = Get(fields, columns, "ident");
        if (IsIcaoLike(ident))
        {
            return ident!.Trim().ToUpperInvariant();
        }

        return null;
    }

    private static bool IsIcaoLike(string? value) =>
        !string.IsNullOrWhiteSpace(value) && IcaoLikePattern.IsMatch(value.Trim().ToUpperInvariant());

    private static IEnumerable<Runway> BuildDirectionalRunways(
        string icao,
        string[] fields,
        Dictionary<string, int> columns,
        int lengthFt,
        int widthFt,
        string surface,
        bool isLighted,
        bool isClosed)
    {
        var leIdent = Get(fields, columns, "le_ident");
        var heIdent = Get(fields, columns, "he_ident");

        var leLat = ParseDouble(Get(fields, columns, "le_latitude_deg"));
        var leLon = ParseDouble(Get(fields, columns, "le_longitude_deg"));
        var heLat = ParseDouble(Get(fields, columns, "he_latitude_deg"));
        var heLon = ParseDouble(Get(fields, columns, "he_longitude_deg"));
        var leHeading = ParseDouble(Get(fields, columns, "le_heading_degT"));
        var heHeading = ParseDouble(Get(fields, columns, "he_heading_degT"));

        if (!string.IsNullOrWhiteSpace(leIdent))
        {
            yield return new Runway
            {
                Id = Guid.NewGuid(),
                AirportIcao = icao,
                Designator = leIdent.Trim(),
                LengthFt = lengthFt,
                WidthFt = widthFt,
                Surface = surface,
                HeadingTrue = leHeading ?? 0,
                LatitudeStart = leLat,
                LongitudeStart = leLon,
                LatitudeEnd = heLat,
                LongitudeEnd = heLon,
                IsLighted = isLighted,
                IsClosed = isClosed,
            };
        }

        if (!string.IsNullOrWhiteSpace(heIdent))
        {
            yield return new Runway
            {
                Id = Guid.NewGuid(),
                AirportIcao = icao,
                Designator = heIdent.Trim(),
                LengthFt = lengthFt,
                WidthFt = widthFt,
                Surface = surface,
                HeadingTrue = heHeading ?? 0,
                LatitudeStart = heLat,
                LongitudeStart = heLon,
                LatitudeEnd = leLat,
                LongitudeEnd = leLon,
                IsLighted = isLighted,
                IsClosed = isClosed,
            };
        }
    }

    private static Dictionary<string, int> BuildColumnIndex(string[] header)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++)
        {
            map[header[i]] = i;
        }

        return map;
    }

    private static string? Get(string[] fields, Dictionary<string, int> columns, string name)
    {
        if (!columns.TryGetValue(name, out var index) || index >= fields.Length)
        {
            return null;
        }

        var value = fields[index];
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static IEnumerable<List<string>> Chunk(IEnumerable<string> source, int size)
    {
        var chunk = new List<string>(size);
        foreach (var item in source)
        {
            chunk.Add(item);
            if (chunk.Count == size)
            {
                yield return chunk;
                chunk = new List<string>(size);
            }
        }

        if (chunk.Count > 0)
        {
            yield return chunk;
        }
    }
}
