using System.Text.Json;
using System.Text.Json.Serialization;
using FSOps.Core;

namespace FSOps.Server.Services;

/// <summary>
/// Everything the updater remembers between runs. Deliberately NOT a database entity: the on/off
/// switch has to be readable without the database being up, because the whole design goal is that
/// nothing about the update check ever sits on the startup path. The rest (when we last looked, what
/// we found, which version the user already said no to) is cache and dismissal state rather than
/// user settings, and has no business in the ledger database either.
/// <para>
/// It lives in the app's own data directory, which survives a reinstall exactly as the database
/// does, so the preference is not lost by upgrading - which is the reason it needed to be persistent
/// in the first place.
/// </para>
/// </summary>
public sealed class UpdateState
{
    /// <summary>
    /// The kill switch. When false, no outbound request is made for any reason - not a check, not a
    /// download. This is checked before the HTTP client is ever touched, so "off" means no packet
    /// leaves the machine rather than "a request is made and the answer is hidden".
    /// </summary>
    public bool Enabled { get; set; } = true;

    public DateTimeOffset? LastCheckedUtc { get; set; }

    /// <summary>True when the last attempt could not reach GitHub - drives a shorter retry interval
    /// than a successful check, without ever turning into a retry storm.</summary>
    public bool LastCheckFailed { get; set; }

    /// <summary>The newest stable release found, or null when the running build is already current.</summary>
    public string? LatestVersion { get; set; }

    public string? ReleaseUrl { get; set; }

    public string? ReleaseNotes { get; set; }

    public DateTimeOffset? ReleasePublishedUtc { get; set; }

    public string? InstallerAssetName { get; set; }

    public string? InstallerDownloadUrl { get; set; }

    public string? ChecksumDownloadUrl { get; set; }

    /// <summary>The version the user dismissed. Only suppresses the banner while it still matches
    /// <see cref="LatestVersion"/>, so a later release starts talking again.</summary>
    public string? DismissedVersion { get; set; }

    /// <summary>Set only after a download's SHA-256 matched the release's published checksum.</summary>
    public string? VerifiedFilePath { get; set; }

    public string? VerifiedSha256 { get; set; }

    public string? VerifiedVersion { get; set; }
}

/// <summary>
/// Everywhere the updater is allowed to touch the disk. Both the state file and the downloads
/// directory come from here so that no code path in the updater can construct a path of its own -
/// and so a test can point the whole thing at a temporary directory instead of the real save.
/// </summary>
public interface IUpdateStorage
{
    UpdateState Load();

    void Save(UpdateState state);

    /// <summary>The only directory a downloaded installer may be written to.</summary>
    string UpdatesDirectory { get; }
}

/// <summary>
/// Reads and writes <see cref="UpdateState"/> as JSON. Split out from the store so the file
/// behaviour - a missing file, a truncated file, a file full of nonsense - can be tested against a
/// temporary path without going near the real data directory or the DI container.
/// <para>
/// Every read failure degrades to defaults rather than throwing. A corrupt state file must not be
/// able to stop the app: the worst acceptable outcome is one forgotten dismissal.
/// </para>
/// </summary>
public static class UpdateStateFile
{
    public const string FileName = "update-state.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static UpdateState Read(string path, ILogger? logger = null)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new UpdateState();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<UpdateState>(json, Options) ?? new UpdateState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            logger?.LogWarning(ex, "Update state file could not be read - starting from defaults");
            return new UpdateState();
        }
    }

    public static void Write(string path, UpdateState state, ILogger? logger = null)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write-then-replace so a crash mid-write cannot leave a half-written file that the next
            // read has to recover from.
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, Options));
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger?.LogWarning(ex, "Update state file could not be written - the preference will not survive a restart");
        }
    }
}

/// <summary>
/// The live storage, rooted at %LOCALAPPDATA%\FSOps (or wherever FSOPS_DATA_DIR points). Nothing is
/// ever written beside the executable - the app installs into Program Files, which is read-only for
/// a standard user. See <see cref="AppPaths"/>.
/// </summary>
public sealed class FileUpdateStorage : IUpdateStorage
{
    private readonly ILogger<FileUpdateStorage> _logger;
    private readonly object _gate = new();

    public FileUpdateStorage(ILogger<FileUpdateStorage> logger) => _logger = logger;

    private static string StateFilePath => Path.Combine(AppPaths.DataDirectory, UpdateStateFile.FileName);

    public string UpdatesDirectory => Path.Combine(AppPaths.DataDirectory, "updates");

    public UpdateState Load()
    {
        lock (_gate)
        {
            return UpdateStateFile.Read(StateFilePath, _logger);
        }
    }

    public void Save(UpdateState state)
    {
        lock (_gate)
        {
            UpdateStateFile.Write(StateFilePath, state, _logger);
        }
    }
}
