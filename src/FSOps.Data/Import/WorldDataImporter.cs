using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using FSOps.Core;
using FSOps.Core.Airports;
using FSOps.Core.Csv;
using FSOps.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FSOps.Data.Import;

/// <summary>What a call to the importer actually did.</summary>
public enum WorldDataImportResult
{
    /// <summary>The bundled data is already what is in the database - nothing was read or written.</summary>
    UpToDate,

    /// <summary>An empty database was seeded from scratch.</summary>
    Seeded,

    /// <summary>Existing rows were brought into line with a newer bundle.</summary>
    Refreshed,

    /// <summary>The bundled CSVs were not found - nothing was changed.</summary>
    SeedFilesMissing,

    /// <summary>Another import or refresh was already running; this call did nothing.</summary>
    AlreadyRunning,
}

/// <summary>Result of an import, including what it changed. Counts are zero for a no-op.</summary>
/// <param name="AirportCount">Rows in the Airports table afterwards, including retained ones.</param>
/// <param name="AirportsInserted">Airports the bundle had and the database did not.</param>
/// <param name="AirportsChanged">
/// Airports whose stored details genuinely differed from the bundle and were rewritten - not
/// merely matched. A refresh that finds nothing new reports zero here, which is what makes the
/// log line worth reading.
/// </param>
/// <param name="AirportsRetainedNotInSource">
/// Airports in the database that the bundle no longer contains. Kept, never deleted - see the
/// importer's class doc.
/// </param>
public sealed record WorldDataImportOutcome(
    WorldDataImportResult Result,
    int AirportCount,
    int RunwayCount,
    int AirportsInserted,
    int AirportsChanged,
    int AirportsRetainedNotInSource);

/// <summary>
/// Loads the world airport/runway data from the OurAirports CSVs shipped gzip-compressed with the
/// app, so first run works with no network access and no download ever has to succeed.
///
/// <para><b>The data is refreshable, not frozen.</b> The guard used to be "does the Airports table
/// have any rows", which meant the ~78,000 airports imported on a user's very first launch were
/// permanent: shipping fresher CSVs in a new release changed nothing for anyone who already had
/// the app. The guard is now a content stamp of the bundled files
/// (<see cref="WorldDataStamp"/>), so a new bundle is noticed on the first launch after an update
/// and applied once, in the background, and never again until the bundle changes. Settings also
/// offers a manual refresh for anyone who wants it sooner. This never touches the network.</para>
///
/// <para><b>A refresh is an upsert keyed on ICAO, and it never deletes an airport.</b> This is the
/// whole reason a refresh is not a one-line version bump. Routes, flights, fleet locations and the
/// airline's home base all reference airports by ICAO string, and - deliberately - with no foreign
/// key, so a deleted airport would not fail loudly or cascade; it would leave the player's real
/// history pointing at nothing, silently. OurAirports genuinely removes and reclassifies entries
/// upstream, and does so for editorial reasons (duplicates merged, an entry demoted to a heliport,
/// a cleanup pass) at least as often as for real-world closures, which the app has no way to tell
/// apart. So an airport that has vanished from the source is <b>kept exactly as it is</b>: not
/// deleted, and not overwritten either. The cost is one stale reference row that the sim scenery
/// very likely still has; the alternative cost is a sector someone actually flew losing its
/// destination. This mirrors <see cref="AircraftTypeSeeder"/>'s rule for the same reason.</para>
///
/// <para>Runways are different, and are replaced per airport rather than upserted: nothing the
/// player owns references a runway row (no entity holds a runway id, and there is no foreign key
/// to one), so they are pure reference data. Only the runways of airports that appear in the new
/// bundle are replaced - an airport that vanished upstream keeps its runways along with itself.</para>
///
/// <para>Everything happens inside one transaction, so a crash mid-import leaves the previous
/// state completely intact rather than a half-applied mixture, and the stamp is written only
/// after that transaction commits.</para>
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

    /// <summary>
    /// The startup path. Seeds an empty database, refreshes one whose data came from an older
    /// bundle, and does nothing at all - not even opening the CSVs - when the data is current.
    /// Safe to call on every launch; the expensive work happens only when the bundle has changed.
    /// </summary>
    public Task<WorldDataImportOutcome> ImportIfNeededAsync(FsOpsDbContext db, string dataDirectory, CancellationToken ct = default) =>
        RunAsync(db, dataDirectory, AppPaths.DataDirectory, force: false, ct);

    /// <summary>
    /// The manual "refresh world data" path from Settings. Re-applies the bundled data even when
    /// the stamp says it is already current, for anyone who suspects their airport table is wrong.
    /// Identical upsert, identical guarantee: nothing is ever deleted.
    /// </summary>
    public Task<WorldDataImportOutcome> RefreshAsync(FsOpsDbContext db, string dataDirectory, CancellationToken ct = default) =>
        RunAsync(db, dataDirectory, AppPaths.DataDirectory, force: true, ct);

    /// <summary>
    /// Full form, with the stamp directory supplied explicitly so tests can point it at a
    /// temporary folder instead of the real data directory.
    /// </summary>
    public async Task<WorldDataImportOutcome> RunAsync(
        FsOpsDbContext db,
        string seedDirectory,
        string stampDirectory,
        bool force,
        CancellationToken ct = default)
    {
        var airportsPath = Path.Combine(seedDirectory, "airports.csv.gz");
        var runwaysPath = Path.Combine(seedDirectory, "runways.csv.gz");

        if (!File.Exists(airportsPath) || !File.Exists(runwaysPath))
        {
            _logger.LogWarning("World data seed files not found under {Directory} - skipping import.", seedDirectory);
            if (await db.Airports.AnyAsync(ct))
            {
                await PublishSeededStatusAsync(db, stampDirectory, ct);
            }

            return new WorldDataImportOutcome(WorldDataImportResult.SeedFilesMissing, 0, 0, 0, 0, 0);
        }

        var hasAirports = await db.Airports.AnyAsync(ct);
        var bundled = WorldDataStamp.Compute(airportsPath, runwaysPath);
        var applied = WorldDataStampStore.TryRead(stampDirectory);

        // The two conditions are checked independently on purpose. A stamp claiming current data
        // over an empty table (database deleted, stamp left behind) must still seed; a missing
        // stamp over a full table must still refresh. Neither can leave the app without airports.
        if (!force && hasAirports && applied is not null && applied.Stamp.Matches(bundled))
        {
            await PublishSeededStatusAsync(db, stampDirectory, ct);
            _logger.LogInformation("World data is current ({Version}) - skipping import.", bundled.ShortId);
            return new WorldDataImportOutcome(WorldDataImportResult.UpToDate, _progress.AirportCount, _progress.RunwayCount, 0, 0, 0);
        }

        if (!_progress.TryBegin())
        {
            _logger.LogInformation("A world data import is already running - ignoring this request.");
            return new WorldDataImportOutcome(WorldDataImportResult.AlreadyRunning, 0, 0, 0, 0, 0);
        }

        try
        {
            return await ImportAsync(db, airportsPath, runwaysPath, stampDirectory, bundled, isRefresh: hasAirports, ct);
        }
        finally
        {
            _progress.End();
        }
    }

    private async Task<WorldDataImportOutcome> ImportAsync(
        FsOpsDbContext db,
        string airportsPath,
        string runwaysPath,
        string stampDirectory,
        WorldDataStamp bundled,
        bool isRefresh,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "{Action} world data ({Version})...",
            isRefresh ? "Refreshing" : "Importing",
            bundled.ShortId);

        if (isRefresh)
        {
            _progress.MarkRefreshStarted();
        }
        else
        {
            _progress.MarkStarted();
        }

        var stopwatch = Stopwatch.StartNew();

        var previousAutoDetect = db.ChangeTracker.AutoDetectChangesEnabled;
        // Change detection is only needed when existing rows are being updated. A first-time seed
        // is pure inserts, and leaving it off there is what keeps that path as fast as it was.
        db.ChangeTracker.AutoDetectChangesEnabled = isRefresh;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var airports = await ImportAirportsAsync(db, airportsPath, isRefresh, ct);
            _progress.SetProgressPercent(50);

            var runwaysRead = await ImportRunwaysAsync(db, runwaysPath, airports.IdentToIcao, isRefresh, ct);
            _progress.SetProgressPercent(90);

            // A bundle that produced tens of thousands of airports and not one runway is a broken
            // file, not a world with no runways in it - a truncated download, a bad copy during
            // install, an archive that decompressed to nothing. A gzip stream that ends early does
            // not always throw; it can simply yield no rows. Committing that would stamp the
            // damaged bundle as applied, so the app would never retry and every runway-length check
            // in the game would quietly read as "unknown" forever. Far better to fail loudly here
            // and roll the whole thing back, leaving the previous data in place.
            if (airports.SourceIcaos.Count > 0 && runwaysRead == 0)
            {
                throw new InvalidDataException(
                    $"World data bundle looks damaged: {airports.SourceIcaos.Count} airports were read from " +
                    $"'{airportsPath}' but '{runwaysPath}' yielded no runways at all. Nothing has been changed.");
            }

            var retained = isRefresh
                ? await CountRetainedAirportsAsync(db, airports.SourceIcaos, ct)
                : 0;

            await transaction.CommitAsync(ct);

            // Counted from the tables, never from how many CSV rows went past. Those two numbers
            // differ by exactly the retained airports and their runways, and reporting the
            // processed count would quietly under-report a refresh by however much was kept.
            var airportCount = await db.Airports.CountAsync(ct);
            var runwayCount = await db.Runways.CountAsync(ct);
            var appliedUtc = DateTimeOffset.UtcNow;

            // Only ever after the commit: a stamp written first would, on a crash in between,
            // claim data that had been rolled back and freeze the app on stale rows.
            WorldDataStampStore.Write(stampDirectory, bundled, airportCount, runwayCount, appliedUtc);

            stopwatch.Stop();
            _logger.LogInformation(
                "World data {Action} finished in {ElapsedSeconds:F1}s - {AirportCount} airports " +
                "({Inserted} new, {Changed} changed, {Retained} kept but no longer in the source), {RunwayCount} runways.",
                isRefresh ? "refresh" : "import",
                stopwatch.Elapsed.TotalSeconds,
                airportCount,
                airports.Inserted,
                airports.Changed,
                retained,
                runwayCount);

            if (retained > 0)
            {
                // Worth its own line: these are the rows the refresh deliberately did not touch.
                _logger.LogInformation(
                    "{Retained} airports are no longer in the bundled source data and were kept, so existing " +
                    "routes, flights and parked aircraft still resolve.", retained);
            }

            _progress.MarkCompleted(airportCount, runwayCount);
            _progress.SetVersion(bundled.ShortId, appliedUtc);

            return new WorldDataImportOutcome(
                isRefresh ? WorldDataImportResult.Refreshed : WorldDataImportResult.Seeded,
                airportCount,
                runwayCount,
                airports.Inserted,
                airports.Changed,
                retained);
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

    /// <summary>Publishes counts and the applied stamp to the progress snapshot without importing anything.</summary>
    private async Task PublishSeededStatusAsync(FsOpsDbContext db, string stampDirectory, CancellationToken ct)
    {
        _progress.MarkAlreadySeeded(await db.Airports.CountAsync(ct), await db.Runways.CountAsync(ct));

        var applied = WorldDataStampStore.TryRead(stampDirectory);
        _progress.SetVersion(applied?.Stamp.ShortId, applied?.AppliedUtc);
    }

    private sealed record AirportPassResult(
        Dictionary<string, string> IdentToIcao,
        HashSet<string> SourceIcaos,
        int Inserted,
        int Changed);

    private async Task<AirportPassResult> ImportAirportsAsync(FsOpsDbContext db, string path, bool upsert, CancellationToken ct)
    {
        // Maps the OurAirports "ident" column to the ICAO code we actually chose as the PK,
        // since they aren't always the same value (see ResolveIcao) - runways.csv joins on
        // "ident", so this is how the runway pass finds the right airport.
        var identToIcao = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seenIcao = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batchSize = upsert ? UpdateBatchSize : InsertBatchSize;
        var batch = new List<Airport>(batchSize);
        var rowCount = 0;
        var inserted = 0;
        var changed = 0;

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

            if (batch.Count >= batchSize)
            {
                var (batchInserted, batchChanged) = await FlushAirportsAsync(db, batch, upsert, ct);
                inserted += batchInserted;
                changed += batchChanged;
            }

            if (rowCount % 20000 == 0)
            {
                _logger.LogInformation("Processed {Count} airport rows...", rowCount);
            }
        }

        var (finalInserted, finalChanged) = await FlushAirportsAsync(db, batch, upsert, ct);
        inserted += finalInserted;
        changed += finalChanged;

        return new AirportPassResult(identToIcao, seenIcao, inserted, changed);
    }

    private static async Task<(int Inserted, int Changed)> FlushAirportsAsync(
        FsOpsDbContext db,
        List<Airport> batch,
        bool upsert,
        CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            return (0, 0);
        }

        var inserted = 0;

        if (upsert)
        {
            var icaos = batch.ConvertAll(a => a.Icao);
            // Materialise before building the dictionary so the comparison is done in memory with
            // an explicit comparer, rather than relying on the provider's collation.
            var existingRows = await db.Airports.Where(a => icaos.Contains(a.Icao)).ToListAsync(ct);
            var existing = existingRows.ToDictionary(a => a.Icao, StringComparer.OrdinalIgnoreCase);

            foreach (var incoming in batch)
            {
                if (existing.TryGetValue(incoming.Icao, out var current))
                {
                    // Reference fields only. The key is never rewritten - it is what every route,
                    // flight and parked aircraft points at. Assigning an unchanged value produces
                    // no tracked change, so an unchanged airport costs nothing.
                    current.Iata = incoming.Iata;
                    current.Name = incoming.Name;
                    current.Municipality = incoming.Municipality;
                    current.Country = incoming.Country;
                    current.Latitude = incoming.Latitude;
                    current.Longitude = incoming.Longitude;
                    current.ElevationFt = incoming.ElevationFt;
                    current.SizeCategory = incoming.SizeCategory;
                    current.HasScheduledService = incoming.HasScheduledService;
                    // LongestRunwayFt is deliberately NOT reset here. The runway pass below
                    // restamps it from the new bundle for every airport that has runway rows, and
                    // blanking it first would rewrite all ~44,000 airport rows twice on every
                    // refresh - and leave them briefly claiming no runway at all. An airport whose
                    // runway rows have vanished upstream is never purged either, so its length and
                    // its runways stay consistent with each other.
                }
                else
                {
                    db.Airports.Add(incoming);
                    inserted++;
                }
            }
        }
        else
        {
            db.Airports.AddRange(batch);
            inserted += batch.Count;
        }

        // Rows EF actually wrote, not rows we looked at. Assigning a property its existing value
        // produces no tracked change, so an airport whose details are unchanged costs nothing and
        // is not counted - which is the whole point of reporting this number.
        var written = await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        batch.Clear();
        return (inserted, Math.Max(0, written - inserted));
    }

    /// <summary>
    /// Rewrites the runways of every airport the bundle contains. Returns how many runway rows the
    /// file actually produced - used only as a damaged-bundle check, never as the reported total,
    /// because runways belonging to retained airports never pass through here at all.
    /// </summary>
    private async Task<int> ImportRunwaysAsync(
        FsOpsDbContext db,
        string path,
        Dictionary<string, string> identToIcao,
        bool replaceExisting,
        CancellationToken ct)
    {
        var batch = new List<Runway>(InsertBatchSize);
        var longestByIcao = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Airports whose existing runways still have to be cleared before their new rows land.
        // An airport is registered here the first time it is seen and cleared exactly once, so
        // this works even though runways.csv is not guaranteed to group an airport's rows together.
        var purged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingPurge = new List<string>(UpdateBatchSize);
        var rowsRead = 0;

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

            if (replaceExisting && purged.Add(icao))
            {
                pendingPurge.Add(icao);
                if (pendingPurge.Count >= UpdateBatchSize)
                {
                    await PurgeRunwaysAsync(db, pendingPurge, ct);
                }
            }

            var lengthFt = ParseInt(Get(fields, columns, "length_ft")) ?? 0;
            var widthFt = ParseInt(Get(fields, columns, "width_ft")) ?? 0;
            var surface = Get(fields, columns, "surface") ?? string.Empty;
            var isLighted = Get(fields, columns, "lighted") == "1";
            var isClosed = Get(fields, columns, "closed") == "1";

            foreach (var runway in BuildDirectionalRunways(icao, fields, columns, lengthFt, widthFt, surface, isLighted, isClosed))
            {
                batch.Add(runway);
                rowsRead++;
            }

            // A closed runway must never count towards an airport's longest - it isn't ground a
            // player can actually use, and RunwaySuitabilityAssessor/StampLongestRunwaysAsync both
            // rely on this figure being genuinely usable.
            if (!isClosed && lengthFt > 0 && (!longestByIcao.TryGetValue(icao, out var current) || lengthFt > current))
            {
                longestByIcao[icao] = lengthFt;
            }

            if (batch.Count >= InsertBatchSize)
            {
                await FlushRunwaysAsync(db, batch, pendingPurge, ct);
            }
        }

        await FlushRunwaysAsync(db, batch, pendingPurge, ct);
        await StampLongestRunwaysAsync(db, longestByIcao, ct);

        return rowsRead;
    }

    /// <summary>
    /// Deletes the existing runways of the given airports. Only ever called for airports that are
    /// present in the new bundle, and only ever before their replacements are inserted. Runways
    /// are the one part of the world data that is safe to replace: nothing the player owns
    /// references a runway row.
    /// </summary>
    private static async Task PurgeRunwaysAsync(FsOpsDbContext db, List<string> pendingPurge, CancellationToken ct)
    {
        if (pendingPurge.Count == 0)
        {
            return;
        }

        var icaos = pendingPurge.ToList();
        await db.Runways.Where(r => icaos.Contains(r.AirportIcao)).ExecuteDeleteAsync(ct);
        pendingPurge.Clear();
    }

    private static async Task FlushRunwaysAsync(
        FsOpsDbContext db,
        List<Runway> batch,
        List<string> pendingPurge,
        CancellationToken ct)
    {
        // Always before the insert, never after: the purge must not be able to remove rows this
        // very batch just wrote.
        await PurgeRunwaysAsync(db, pendingPurge, ct);

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

    /// <summary>
    /// How many airports are in the database but no longer in the bundled source. These are the
    /// rows the refresh deliberately left alone - see the class doc. Counted, never touched.
    /// </summary>
    private static async Task<int> CountRetainedAirportsAsync(FsOpsDbContext db, HashSet<string> sourceIcaos, CancellationToken ct)
    {
        var stored = await db.Airports.AsNoTracking().Select(a => a.Icao).ToListAsync(ct);
        return stored.Count(icao => !sourceIcaos.Contains(icao));
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
