using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FSOps.Data.Import;

/// <summary>
/// Identifies exactly which bundled world-data files produced the rows currently in the Airports
/// and Runways tables, so a newer bundle shipped in an app update can be noticed and applied
/// instead of being silently ignored forever.
///
/// <para><b>Why a content hash rather than a hand-maintained version number.</b> A version file
/// bundled next to the CSVs would need a human to remember to bump it every time the data is
/// refreshed from OurAirports; the one time that is forgotten, every existing install silently
/// keeps stale data and nothing anywhere reports a problem. Hashing the two .gz files instead
/// makes "the data changed" and "the stamp changed" the same fact, with no step that can be
/// skipped. Both files together are ~5 MB, so hashing them costs single-digit milliseconds once
/// per launch.</para>
///
/// <para><see cref="ImporterVersion"/> is part of the identity too, so that changing how a CSV row
/// is mapped onto our entities (<c>MapAirport</c>, <c>ResolveIcao</c>, the size-category mapping,
/// ...) re-imports unchanged files. Bump it whenever the mapping changes in a way that should
/// alter existing rows.</para>
/// </summary>
public sealed record WorldDataStamp(
    [property: JsonPropertyName("importerVersion")] int ImporterVersion,
    [property: JsonPropertyName("airportsSha256")] string AirportsSha256,
    [property: JsonPropertyName("runwaysSha256")] string RunwaysSha256)
{
    /// <summary>
    /// Bump when the CSV-to-entity mapping changes in a way that should rewrite existing rows.
    /// </summary>
    public const int CurrentImporterVersion = 1;

    /// <summary>File name of the stamp, written into the user's data directory (never the install directory).</summary>
    public const string FileName = "worlddata.stamp.json";

    /// <summary>A short, human-readable identity for the UI - not used for any comparison.</summary>
    [JsonIgnore]
    public string ShortId =>
        $"v{ImporterVersion}-{AirportsSha256[..Math.Min(8, AirportsSha256.Length)]}";

    public bool Matches(WorldDataStamp other) =>
        ImporterVersion == other.ImporterVersion
        && string.Equals(AirportsSha256, other.AirportsSha256, StringComparison.OrdinalIgnoreCase)
        && string.Equals(RunwaysSha256, other.RunwaysSha256, StringComparison.OrdinalIgnoreCase);

    public static WorldDataStamp Compute(string airportsPath, string runwaysPath) =>
        new(CurrentImporterVersion, HashFile(airportsPath), HashFile(runwaysPath));

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

/// <summary>
/// What was written to the stamp file: the bundle identity plus the counts and time it was
/// applied, purely so Settings can show the player when their world data last changed.
/// </summary>
public sealed record WorldDataStampFile
{
    [JsonPropertyName("importerVersion")]
    public int ImporterVersion { get; init; }

    [JsonPropertyName("airportsSha256")]
    public string AirportsSha256 { get; init; } = string.Empty;

    [JsonPropertyName("runwaysSha256")]
    public string RunwaysSha256 { get; init; } = string.Empty;

    [JsonPropertyName("appliedUtc")]
    public DateTimeOffset AppliedUtc { get; init; }

    [JsonPropertyName("airportCount")]
    public int AirportCount { get; init; }

    [JsonPropertyName("runwayCount")]
    public int RunwayCount { get; init; }

    [JsonIgnore]
    public WorldDataStamp Stamp => new(ImporterVersion, AirportsSha256, RunwaysSha256);
}

/// <summary>
/// Reads and writes <see cref="WorldDataStamp.FileName"/>.
///
/// <para><b>Where the stamp lives, and why it is a file rather than a table.</b> It sits in the
/// user's data directory (<c>%LOCALAPPDATA%\FSOps\</c>, or wherever <c>FSOPS_DATA_DIR</c> points),
/// beside the database it describes - so copying or redirecting the data directory carries the
/// database and its stamp together, and a test run under <c>FSOPS_DATA_DIR</c> can never see or
/// disturb the real one. It deliberately does <b>not</b> live in the install directory: that is
/// Program Files, read-only for a standard user and replaced wholesale by the installer.</para>
///
/// <para>Keeping it out of the database avoids a schema migration for what is not user data - it
/// describes a bundled asset, not anything the player owns. The cost of that choice is that the
/// file and the tables can in principle disagree, so the importer treats the two possible
/// disagreements asymmetrically: it is written only <i>after</i> the import transaction commits,
/// and the presence of airport rows is checked independently of it. A lost or unreadable stamp
/// therefore causes at worst one extra background refresh (idempotent, and never destructive),
/// while an emptied database re-seeds regardless of what the stamp claims. Both failure
/// directions are cheap; neither loses anything.</para>
/// </summary>
public static class WorldDataStampStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string PathIn(string directory) => Path.Combine(directory, WorldDataStamp.FileName);

    /// <summary>Reads the stamp, or null when it is missing, empty, or unparseable - all treated the same.</summary>
    public static WorldDataStampFile? TryRead(string directory)
    {
        var path = PathIn(directory);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var parsed = JsonSerializer.Deserialize<WorldDataStampFile>(json, Options);
            if (parsed is null
                || string.IsNullOrWhiteSpace(parsed.AirportsSha256)
                || string.IsNullOrWhiteSpace(parsed.RunwaysSha256))
            {
                return null;
            }

            return parsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable stamp must never stop the app starting; it just means the next
            // launch re-runs a harmless refresh.
            return null;
        }
    }

    /// <summary>
    /// Writes the stamp. Called only after the import transaction has committed, so the stamp can
    /// never claim data that was rolled back.
    /// </summary>
    public static void Write(string directory, WorldDataStamp stamp, int airportCount, int runwayCount, DateTimeOffset appliedUtc)
    {
        Directory.CreateDirectory(directory);
        var payload = new WorldDataStampFile
        {
            ImporterVersion = stamp.ImporterVersion,
            AirportsSha256 = stamp.AirportsSha256,
            RunwaysSha256 = stamp.RunwaysSha256,
            AppliedUtc = appliedUtc,
            AirportCount = airportCount,
            RunwayCount = runwayCount,
        };

        File.WriteAllText(PathIn(directory), JsonSerializer.Serialize(payload, Options));
    }
}
