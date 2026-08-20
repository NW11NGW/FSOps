using FSOps.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FSOps.Server.Services.Backup;

/// <summary>A backup that has been written and is waiting to be sent to the player.</summary>
/// <param name="Path">A temporary file. The caller streams it and deletes it.</param>
public sealed record PreparedBackup(string Path, string FileName, long SizeBytes);

/// <summary>The answer to "I want to restore this file". Either it is staged, or it is refused.</summary>
public sealed record RestoreStaging(bool Staged, string? Refusal, PendingRestoreState? State);

/// <summary>
/// Everything the backup feature does with the disk, in one place, so no request handler builds a
/// path of its own and a test can point the whole feature at a temporary directory rather than the
/// player's real save.
///
/// <para>The rule that shapes this class: <b>nothing may destroy anything until a replacement has
/// been proved good and the thing being replaced has been copied.</b> A restore checks the incoming
/// file completely, then takes a safety copy of the airline it is about to displace, and only then
/// stages the swap. Every failure before that point leaves the player exactly where they started,
/// which is why the refusals are worth as much as the feature.</para>
/// </summary>
public sealed class BackupService
{
    private readonly ILogger<BackupService>? _logger;

    public BackupService(string dataDirectory, string databasePath, ILogger<BackupService>? logger = null)
    {
        DataDirectory = dataDirectory;
        DatabasePath = databasePath;
        _logger = logger;
    }

    public string DataDirectory { get; }

    public string DatabasePath { get; }

    public string BackupsDirectory => PendingRestore.BackupsDirectory(DataDirectory);

    /// <summary>The build reported in manifests. Settable so tests can drive the version checks.</summary>
    public string CurrentAppVersion { get; init; } = AppVersion.Current;

    /// <summary>
    /// Writes a complete, verified backup to a temporary file for the caller to stream out.
    ///
    /// <para>The copy is taken with SQLite's backup API while the app is still running and still
    /// holds the database open - see <see cref="DatabaseSnapshot"/> for why a file copy would
    /// silently lose the most recent flights. The result is checked with
    /// <c>PRAGMA integrity_check</c> before it is put in an archive, because a backup nobody looked
    /// at is only a belief that a backup exists.</para>
    /// </summary>
    public async Task<PreparedBackup> PrepareAsync(FsOpsDbContext db, CancellationToken ct = default)
    {
        Directory.CreateDirectory(BackupsDirectory);
        SweepStaleWorkingFiles();

        var airline = await TryReadAirlineAsync(db, ct);
        var manifest = await BuildManifestAsync(db, airline?.Name, airline?.IcaoCode, ct);
        var fileName = BackupArchive.SuggestFileName(airline?.Name, DateTimeOffset.UtcNow);

        var workingDatabase = Path.Combine(BackupsDirectory, $"working-{Guid.NewGuid():N}.db");
        var workingArchive = Path.Combine(BackupsDirectory, $"working-{Guid.NewGuid():N}.tmp");

        try
        {
            DatabaseSnapshot.WriteTo(DatabasePath, workingDatabase);

            var integrity = DatabaseSnapshot.IntegrityCheck(workingDatabase);
            if (!string.Equals(integrity, DatabaseSnapshot.IntegrityOk, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The copy of the database did not pass its integrity check ({integrity}), so it was not offered as a backup.");
            }

            BackupArchive.Create(workingDatabase, manifest, workingArchive);
            return new PreparedBackup(workingArchive, fileName, new FileInfo(workingArchive).Length);
        }
        catch
        {
            TryDelete(workingArchive);
            throw;
        }
        finally
        {
            // Sidecars too - see DatabaseSnapshot.Delete. The integrity check above opens the copy,
            // which recreates its -wal and -shm however cleanly the backup itself closed.
            DatabaseSnapshot.Delete(workingDatabase);
        }
    }

    /// <summary>
    /// Writes a backup of the current database into the app's own backups folder and returns where
    /// it went. Taken automatically before a restore is staged: a player who picks the wrong file
    /// must not lose an airline over it, and telling them afterwards where the old one is only
    /// works if it was actually saved first.
    /// </summary>
    public async Task<string> WriteSafetyCopyAsync(FsOpsDbContext db, CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(db, ct);
        var destination = Path.Combine(BackupsDirectory, "Before restore - " + prepared.FileName);

        try
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Move(prepared.Path, destination);
        }
        catch
        {
            TryDelete(prepared.Path);
            throw;
        }

        _logger?.LogInformation("Saved the current airline to {Path} before restoring.", destination);
        return destination;
    }

    /// <summary>
    /// Checks a file the player wants to restore and, if it passes every check, stages it for the
    /// next startup. Nothing here touches the live database: the swap happens before EF opens it,
    /// on the next run. See <see cref="PendingRestore"/> for why that is the only safe order.
    /// </summary>
    public async Task<RestoreStaging> StageRestoreAsync(
        string uploadedFilePath,
        string sourceFileName,
        FsOpsDbContext db,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(BackupsDirectory);

        var known = db.Database.GetMigrations().ToList();
        var inspection = BackupArchive.Inspect(uploadedFilePath, BackupsDirectory, known, CurrentAppVersion);

        if (!inspection.Accepted)
        {
            _logger?.LogWarning("Refused a restore from {FileName}: {Refusal}", sourceFileName, inspection.Refusal);
            return new RestoreStaging(false, inspection.Refusal, null);
        }

        // Only now, with a verified replacement in hand, is anything allowed to be at risk.
        var safetyCopy = await WriteSafetyCopyAsync(db, ct);

        var state = new PendingRestoreState
        {
            SourceFileName = sourceFileName,
            StagedUtc = DateTimeOffset.UtcNow,
            SafetyCopyPath = safetyCopy,
            BackupAppVersion = inspection.Manifest?.AppVersion,
            BackupCreatedUtc = inspection.Manifest?.CreatedUtc,
            BackupAirlineName = inspection.Manifest?.AirlineName,
        };

        PendingRestore.Stage(DataDirectory, inspection.DatabasePath!, state);
        _logger?.LogInformation(
            "Staged a restore from {FileName}; it will be applied the next time FSOps starts.", sourceFileName);

        return new RestoreStaging(true, null, state);
    }

    public PendingRestoreState? ReadPending() => PendingRestore.Read(DataDirectory);

    public void CancelPending() => PendingRestore.Clear(DataDirectory);

    public RestoreResult? ReadLastResult() => PendingRestore.ReadResult(DataDirectory);

    public void AcknowledgeLastResult() => PendingRestore.ClearResult(DataDirectory);

    public long DatabaseSizeBytes => File.Exists(DatabasePath) ? new FileInfo(DatabasePath).Length : 0;

    public async Task<string> SuggestFileNameAsync(FsOpsDbContext db, CancellationToken ct = default)
    {
        var airline = await TryReadAirlineAsync(db, ct);
        return BackupArchive.SuggestFileName(airline?.Name, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The airline, or null if it cannot be read. Deliberately swallows a schema mismatch: the
    /// airline's name is used to label the file and nothing else, and a database this build's model
    /// cannot query - an older one that has not been migrated yet, most obviously - is precisely
    /// the database somebody most needs a backup of. Refusing to copy it because the label would be
    /// missing would be the wrong way round.
    /// </summary>
    private async Task<Core.Entities.Airline?> TryReadAirlineAsync(FsOpsDbContext db, CancellationToken ct)
    {
        try
        {
            return await db.Airlines.FirstOrDefaultAsync(ct);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            _logger?.LogWarning(ex, "The airline could not be read while naming a backup; the file will use a generic name.");
            return null;
        }
    }

    private async Task<BackupManifest> BuildManifestAsync(
        FsOpsDbContext db,
        string? airlineName,
        string? airlineIcao,
        CancellationToken ct)
    {
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct))
            .OrderBy(id => id, StringComparer.Ordinal)
            .LastOrDefault();

        return new BackupManifest
        {
            AppVersion = CurrentAppVersion,
            MigrationVersion = applied,
            CreatedUtc = DateTimeOffset.UtcNow,
            AirlineName = airlineName,
            AirlineIcaoCode = airlineIcao,
        };
    }

    /// <summary>
    /// Removes working files an interrupted request left behind. Only ever touches this feature's
    /// own <c>working-*</c> and <c>restore-*</c> scratch files, and only ones older than an hour -
    /// a player's actual backups live in the same folder and are never deleted by this app.
    /// </summary>
    private void SweepStaleWorkingFiles()
    {
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
            foreach (var path in Directory.EnumerateFiles(BackupsDirectory))
            {
                var name = Path.GetFileName(path);

                // The two prefixes this feature's own scratch files use, and never a .fsopsbak -
                // the player's backups and the safety copies live in this folder too, and this app
                // does not delete backups.
                var isScratch =
                    (name.StartsWith("working-", StringComparison.Ordinal) || name.StartsWith("restore-", StringComparison.Ordinal)) &&
                    !name.EndsWith(BackupManifest.FileExtension, StringComparison.OrdinalIgnoreCase);

                if (isScratch && File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    TryDelete(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Tidying is never worth failing a backup over.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // See SweepStaleWorkingFiles - a leftover scratch file is untidy, never dangerous.
        }
    }
}
