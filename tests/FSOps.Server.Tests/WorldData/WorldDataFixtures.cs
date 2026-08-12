using System.IO.Compression;
using System.Text;

namespace FSOps.Server.Tests.WorldData;

/// <summary>One airport row as it appears in OurAirports' airports.csv.</summary>
public sealed record AirportRow(
    string Ident,
    string Type,
    string Name,
    double Latitude,
    double Longitude,
    int ElevationFt,
    string IsoCountry,
    string Municipality,
    string ScheduledService,
    string? GpsCode = null,
    string? IataCode = null);

/// <summary>One runway row as it appears in OurAirports' runways.csv.</summary>
public sealed record RunwayRow(
    string AirportIdent,
    int LengthFt,
    int WidthFt,
    string Surface,
    bool Lighted,
    bool Closed,
    string LeIdent,
    string HeIdent,
    double LeHeading = 90,
    double HeHeading = 270);

/// <summary>
/// Writes gzip-compressed CSVs in the same shape and column order as the real OurAirports files,
/// so tests exercise the actual parsing path (quoted names, the ident/gps_code/icao_code fallback,
/// the type-to-size mapping) rather than a convenient fake.
/// </summary>
public static class WorldDataFixtures
{
    public static void WriteBundle(string seedDirectory, IEnumerable<AirportRow> airports, IEnumerable<RunwayRow> runways)
    {
        Directory.CreateDirectory(seedDirectory);
        WriteGzip(Path.Combine(seedDirectory, "airports.csv.gz"), BuildAirportsCsv(airports));
        WriteGzip(Path.Combine(seedDirectory, "runways.csv.gz"), BuildRunwaysCsv(runways));
    }

    private static string BuildAirportsCsv(IEnumerable<AirportRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("\"id\",\"ident\",\"type\",\"name\",\"latitude_deg\",\"longitude_deg\",\"elevation_ft\"," +
                      "\"continent\",\"iso_country\",\"iso_region\",\"municipality\",\"scheduled_service\"," +
                      "\"icao_code\",\"gps_code\",\"iata_code\",\"local_code\"");

        var id = 1000;
        foreach (var row in rows)
        {
            id++;
            sb.Append(id).Append(',')
              .Append(Quote(row.Ident)).Append(',')
              .Append(Quote(row.Type)).Append(',')
              .Append(Quote(row.Name)).Append(',')
              .Append(row.Latitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(row.Longitude.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(row.ElevationFt).Append(',')
              .Append(Quote("EU")).Append(',')
              .Append(Quote(row.IsoCountry)).Append(',')
              .Append(Quote($"{row.IsoCountry}-XX")).Append(',')
              .Append(Quote(row.Municipality)).Append(',')
              .Append(Quote(row.ScheduledService)).Append(',')
              .Append(Quote(row.Ident)).Append(',')
              .Append(Quote(row.GpsCode ?? row.Ident)).Append(',')
              .Append(Quote(row.IataCode ?? string.Empty)).Append(',')
              .Append(Quote(string.Empty))
              .AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildRunwaysCsv(IEnumerable<RunwayRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("\"id\",\"airport_ref\",\"airport_ident\",\"length_ft\",\"width_ft\",\"surface\",\"lighted\"," +
                      "\"closed\",\"le_ident\",\"le_latitude_deg\",\"le_longitude_deg\",\"le_elevation_ft\"," +
                      "\"le_heading_degT\",\"le_displaced_threshold_ft\",\"he_ident\",\"he_latitude_deg\"," +
                      "\"he_longitude_deg\",\"he_elevation_ft\",\"he_heading_degT\",\"he_displaced_threshold_ft\"");

        var id = 5000;
        foreach (var row in rows)
        {
            id++;
            sb.Append(id).Append(",1,")
              .Append(Quote(row.AirportIdent)).Append(',')
              .Append(row.LengthFt).Append(',')
              .Append(row.WidthFt).Append(',')
              .Append(Quote(row.Surface)).Append(',')
              .Append(row.Lighted ? "1" : "0").Append(',')
              .Append(row.Closed ? "1" : "0").Append(',')
              .Append(Quote(row.LeIdent)).Append(",51.1,-2.1,100,")
              .Append(row.LeHeading.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(",0,")
              .Append(Quote(row.HeIdent)).Append(",51.2,-2.2,100,")
              .Append(row.HeHeading.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(",0")
              .AppendLine();
        }

        return sb.ToString();
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static void WriteGzip(string path, string content)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        using var writer = new StreamWriter(gzip, new UTF8Encoding(false));
        writer.Write(content);
    }
}
