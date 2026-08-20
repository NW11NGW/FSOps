using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FSOps.Data;

namespace FSOps.Server.Services.Backup;

/// <summary>
/// The outcome of looking at a file somebody wants to restore. Either it was accepted, or there is
/// one sentence explaining why not - never both, and never a half-opened archive left behind.
/// </summary>
/// <param name="Accepted">True when every check passed and <paramref name="DatabasePath"/> holds a
/// verified database ready to be staged.</param>
/// <param name="Refusal">Written for the player, not for a log. Says what is wrong and, where there
/// is one, what to do about it.</param>
/// <param name="Manifest">What the file said about itself. Present whenever it could be read at
/// all, including for some refusals, so the caller can name the version that made it.</param>
/// <param name="DatabasePath">The extracted, verified database. Only set when accepted; the caller
/// owns it and must move or delete it.</param>
/// <param name="MigrationVersion">The migration actually recorded inside the database.</param>
public sealed record BackupInspection(
    bool Accepted,
    string? Refusal,
    BackupManifest? Manifest,
    string? DatabasePath,
    string? MigrationVersion)
{
    public static BackupInspection Reject(string refusal, BackupManifest? manifest = null) =>
        new(false, refusal, manifest, null, null);

    public static BackupInspection Accept(BackupManifest manifest, string databasePath, string? migrationVersion) =>
        new(true, null, manifest, databasePath, migrationVersion);
}

/// <summary>
/// Reads and writes the <c>.fsopsbak</c> file: a zip holding a checkpointed copy of the database
/// and a <see cref="BackupManifest"/> describing it.
///
/// <para><b>Why a container rather than the database file on its own.</b> A bare <c>.db</c> carries
/// nothing about the build that produced it, so nothing on the restore side could tell a save from
/// a newer FSOps apart from one it can safely read - and that particular mistake does not fail
/// cleanly. The zip also gives truncation detection for free: a file cut short loses its central
/// directory and will not open at all, which is exactly the moment to notice, rather than half-way
/// through overwriting the player's airline.</para>
///
/// <para>Every refusal below is deliberately a plain sentence with no exception text in it. The
/// person reading it is being told their backup will not be restored; the detail belongs in the
/// log.</para>
/// </summary>
public static class BackupArchive
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Writes <paramref name="databasePath"/> and <paramref name="manifest"/> into a new archive at
    /// <paramref name="destinationPath"/>. The manifest's size and checksum fields are filled in
    /// here from the file that is actually stored, so they can never describe a different file than
    /// the one in the archive.
    /// </summary>
    public static void Create(string databasePath, BackupManifest manifest, string destinationPath)
    {
        var databaseBytes = File.ReadAllBytes(databasePath);
        manifest.Format = BackupManifest.FormatMarker;
        manifest.FormatVersion = BackupManifest.CurrentFormatVersion;
        manifest.DatabaseBytes = databaseBytes.LongLength;
        manifest.DatabaseSha256 = Sha256Hex(databaseBytes);

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        using var stream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        // Manifest first so a reader can identify the file without scanning past the database.
        var manifestEntry = archive.CreateEntry(BackupManifest.ManifestEntryName, CompressionLevel.NoCompression);
        using (var manifestStream = manifestEntry.Open())
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            manifestStream.Write(json, 0, json.Length);
        }

        var databaseEntry = archive.CreateEntry(BackupManifest.DatabaseEntryName, CompressionLevel.Optimal);
        using var databaseStream = databaseEntry.Open();
        databaseStream.Write(databaseBytes, 0, databaseBytes.Length);
    }

    /// <summary>
    /// Checks a candidate file all the way through before anything is allowed to act on it, and
    /// extracts its database into <paramref name="workingDirectory"/> when it passes.
    ///
    /// <para>The order matters and is the whole design: identify the file, verify its bytes, verify
    /// the database, then decide whether this build can read the schema. Every one of those is
    /// answered before the current database has been touched, so a refusal costs the player
    /// nothing.</para>
    /// </summary>
    /// <param name="knownMigrations">Every migration this build of FSOps knows about, from
    /// <c>db.Database.GetMigrations()</c>. A backup whose migration is not in here was taken by a
    /// newer build.</param>
    public static BackupInspection Inspect(
        string candidatePath,
        string workingDirectory,
        IReadOnlyCollection<string> knownMigrations,
        string currentAppVersion)
    {
        if (!File.Exists(candidatePath) || new FileInfo(candidatePath).Length == 0)
        {
            return BackupInspection.Reject("That file is empty, so there is nothing to restore from.");
        }

        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(candidatePath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            // A zip whose central directory is missing cannot be opened, and that is where both
            // "never was a backup" and "was a backup, and got cut short" land. They deserve
            // different answers - "your copy did not finish" is actionable, "wrong file" is not -
            // so the two are told apart by the start of the file, which a truncation leaves intact.
            // The manifest is written first and uncompressed precisely so this is possible.
            return LooksLikeATruncatedBackup(candidatePath)
                ? BackupInspection.Reject(
                    "That backup file is incomplete - it looks like an FSOps backup whose copy did not finish. " +
                    "Nothing was changed. Copy it again from wherever you saved it, or use another backup.")
                : BackupInspection.Reject(
                    "That file is not an FSOps backup, or it is damaged. FSOps backups end in .fsopsbak and are " +
                    "created by the Back up button on this page.");
        }

        using (archive)
        {
            var manifestEntry = archive.GetEntry(BackupManifest.ManifestEntryName);
            var databaseEntry = archive.GetEntry(BackupManifest.DatabaseEntryName);
            if (manifestEntry is null || databaseEntry is null)
            {
                return BackupInspection.Reject(
                    "That file is not an FSOps backup. FSOps backups end in .fsopsbak and are created by the " +
                    "Back up button on this page.");
            }

            BackupManifest? manifest;
            try
            {
                using var manifestStream = manifestEntry.Open();
                manifest = JsonSerializer.Deserialize<BackupManifest>(manifestStream, JsonOptions);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                return BackupInspection.Reject("That backup's description could not be read, so it is damaged.");
            }

            if (manifest is null || !string.Equals(manifest.Format, BackupManifest.FormatMarker, StringComparison.Ordinal))
            {
                return BackupInspection.Reject(
                    "That file is not an FSOps backup. FSOps backups end in .fsopsbak and are created by the " +
                    "Back up button on this page.");
            }

            if (manifest.FormatVersion > BackupManifest.CurrentFormatVersion)
            {
                return BackupInspection.Reject(
                    $"That backup was made by a newer version of FSOps ({Describe(manifest.AppVersion)}) and this " +
                    $"version ({currentAppVersion}) cannot read it. Update FSOps and try again. Backups from older " +
                    "versions restore into newer ones without any trouble - it is only this direction that does not work.",
                    manifest);
            }

            Directory.CreateDirectory(workingDirectory);
            var extractedPath = Path.Combine(workingDirectory, $"restore-{Guid.NewGuid():N}.db");

            try
            {
                using (var entryStream = databaseEntry.Open())
                using (var file = new FileStream(extractedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    entryStream.CopyTo(file);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                // A zip entry whose compressed data is cut short fails here rather than at open time.
                DatabaseSnapshot.Delete(extractedPath);
                return BackupInspection.Reject(
                    "That backup file is incomplete or damaged, so nothing was changed. If you have another copy, try that one.",
                    manifest);
            }

            var actualBytes = new FileInfo(extractedPath).Length;
            if (actualBytes != manifest.DatabaseBytes)
            {
                DatabaseSnapshot.Delete(extractedPath);
                return BackupInspection.Reject(
                    "That backup file is incomplete or damaged, so nothing was changed. If you have another copy, try that one.",
                    manifest);
            }

            if (!string.Equals(Sha256Hex(File.ReadAllBytes(extractedPath)), manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
            {
                DatabaseSnapshot.Delete(extractedPath);
                return BackupInspection.Reject(
                    "That backup file has been altered or damaged since it was made - its contents no longer match its " +
                    "own checksum - so nothing was changed.",
                    manifest);
            }

            var integrity = DatabaseSnapshot.IntegrityCheck(extractedPath);
            if (!string.Equals(integrity, DatabaseSnapshot.IntegrityOk, StringComparison.Ordinal))
            {
                DatabaseSnapshot.Delete(extractedPath);
                return BackupInspection.Reject(
                    "The database inside that backup is damaged, so nothing was changed. If you have another copy, try that one.",
                    manifest);
            }

            // The authoritative answer, read from the database rather than from the manifest beside it.
            var migration = DatabaseSnapshot.ReadLatestMigration(extractedPath) ?? manifest.MigrationVersion;
            if (string.IsNullOrWhiteSpace(migration))
            {
                DatabaseSnapshot.Delete(extractedPath);
                return BackupInspection.Reject(
                    "That backup does not record which version of FSOps made it, so it cannot be restored safely.",
                    manifest);
            }

            if (!knownMigrations.Contains(migration))
            {
                DatabaseSnapshot.Delete(extractedPath);
                return BackupInspection.Reject(
                    $"That backup was made by a newer version of FSOps ({Describe(manifest.AppVersion)}) and this " +
                    $"version ({currentAppVersion}) cannot read it. Update FSOps to at least that version and try " +
                    "again. Backups from older versions restore into newer ones without any trouble - it is only " +
                    "this direction that does not work.",
                    manifest);
            }

            return BackupInspection.Accept(manifest, extractedPath, migration);
        }
    }

    /// <summary>Reads a manifest without extracting or verifying anything - for describing a file
    /// that is already known to be good, such as a safety copy this app just wrote.</summary>
    public static BackupManifest? TryReadManifest(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.GetEntry(BackupManifest.ManifestEntryName);
            if (entry is null)
            {
                return null;
            }

            using var stream = entry.Open();
            return JsonSerializer.Deserialize<BackupManifest>(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException)
        {
            return null;
        }
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// True when a file that will not open as an archive nevertheless begins like one of ours: a
    /// zip local-file-header signature followed by our manifest's entry name and format marker.
    /// That combination is what an interrupted copy of a real backup looks like - the beginning
    /// survives, the end does not - and it is worth saying so rather than telling somebody their
    /// backup was never a backup.
    /// </summary>
    private static bool LooksLikeATruncatedBackup(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var head = new byte[4096];
            var read = stream.Read(head, 0, head.Length);
            if (read < 4 || head[0] != 'P' || head[1] != 'K' || head[2] != 0x03 || head[3] != 0x04)
            {
                return false;
            }

            var text = Encoding.ASCII.GetString(head, 0, read);
            return text.Contains(BackupManifest.ManifestEntryName, StringComparison.Ordinal) &&
                   text.Contains(BackupManifest.FormatMarker, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Describe(string? appVersion) =>
        string.IsNullOrWhiteSpace(appVersion) ? "version unknown" : appVersion;

    /// <summary>
    /// A file name that still means something in a folder six months later: the airline, the word
    /// backup, and the date. Never a GUID.
    /// </summary>
    public static string SuggestFileName(string? airlineName, DateTimeOffset when)
    {
        var name = string.IsNullOrWhiteSpace(airlineName) ? "FSOps" : airlineName.Trim();

        var cleaned = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            cleaned.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), character) >= 0 ? '-' : character);
        }

        var safe = cleaned.ToString().Trim().Trim('.');
        if (safe.Length == 0)
        {
            safe = "FSOps";
        }

        if (safe.Length > 60)
        {
            safe = safe[..60].TrimEnd();
        }

        return $"{safe} backup {when.ToLocalTime():yyyy-MM-dd HHmm}{BackupManifest.FileExtension}";
    }
}
