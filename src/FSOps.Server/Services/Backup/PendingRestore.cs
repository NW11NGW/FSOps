using System.Text.Json;
using System.Text.Json.Serialization;
using FSOps.Data;
using Microsoft.Extensions.Logging;

namespace FSOps.Server.Services.Backup;

/// <summary>What is waiting to be swapped in, written beside the staged database file.</summary>
public sealed class PendingRestoreState
{
    /// <summary>The name of the file the player picked, so the UI can name it back to them.</summary>
    public string SourceFileName { get; set; } = string.Empty;

    public DateTimeOffset StagedUtc { get; set; }

    /// <summary>Where the airline that is about to be replaced was saved. Always set - a staged
    /// restore without a safety copy is not something this app will create.</summary>
    public string SafetyCopyPath { get; set; } = string.Empty;

    public string? BackupAppVersion { get; set; }

    public DateTimeOffset? BackupCreatedUtc { get; set; }

    public string? BackupAirlineName { get; set; }
}

/// <summary>What happened the last time a staged restore was applied at startup.</summary>
public sealed class RestoreResult
{
    public bool Succeeded { get; set; }

    public DateTimeOffset AppliedUtc { get; set; }

    public string SourceFileName { get; set; } = string.Empty;

    public string SafetyCopyPath { get; set; } = string.Empty;

    public string? AirlineName { get; set; }

    /// <summary>Written for the player. Set on failure; null when it simply worked.</summary>
    public string? Message { get; set; }
}

/// <summary>
/// The restore itself: staged by a request, applied at the next startup, and reported afterwards.
///
/// <para><b>Why it cannot happen while the app is running.</b> The server holds the database open
/// for its whole life - six hosted services write to it, EF pools its connections, and SQLite keeps
/// <c>-wal</c> and <c>-shm</c> files alongside it. Swapping the file underneath all of that would
/// leave the process reading a database that no longer exists, with a stale write-ahead log pointed
/// at a file it does not describe. That is not a restore that half-worked; it is a corrupt database
/// produced by the feature meant to prevent one. So the request only ever stages a verified file
/// and says so, and the swap is done at startup, before anything has opened the database. The
/// player restarts FSOps; that is the whole mechanism, and it is deliberately the only one.</para>
///
/// <para><b>Why the outcome is written down.</b> A restore that needs a restart is a restore the
/// player cannot watch finish. Leaving them to guess whether it worked would be its own failure, so
/// the startup pass records what it did and the Settings page reports it back on the next run.</para>
/// </summary>
public static class PendingRestore
{
    public const string StagedDatabaseFileName = "restore-pending.db";
    public const string StagedStateFileName = "restore-pending.json";
    public const string ResultFileName = "restore-result.json";

    /// <summary>Where safety copies and the working files for a backup live.</summary>
    public const string BackupsDirectoryName = "backups";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string StagedDatabasePath(string dataDirectory) =>
        Path.Combine(dataDirectory, StagedDatabaseFileName);

    public static string StagedStatePath(string dataDirectory) =>
        Path.Combine(dataDirectory, StagedStateFileName);

    public static string ResultPath(string dataDirectory) => Path.Combine(dataDirectory, ResultFileName);

    public static string BackupsDirectory(string dataDirectory) =>
        Path.Combine(dataDirectory, BackupsDirectoryName);

    /// <summary>
    /// Puts a verified database into place as the pending restore. Both files are written, database
    /// first, so a half-written stage is never mistaken for a complete one - <see cref="Read"/>
    /// requires both.
    /// </summary>
    public static void Stage(string dataDirectory, string verifiedDatabasePath, PendingRestoreState state)
    {
        Directory.CreateDirectory(dataDirectory);
        var databasePath = StagedDatabasePath(dataDirectory);

        DatabaseSnapshot.Delete(databasePath);
        File.Move(verifiedDatabasePath, databasePath);

        // The verified file was opened for its integrity check, so it has -wal and -shm beside it
        // in whatever directory it was extracted to. They belong to a file that has just moved and
        // must not be left behind - see DatabaseSnapshot.Delete.
        DatabaseSnapshot.Delete(verifiedDatabasePath);
        File.WriteAllText(StagedStatePath(dataDirectory), JsonSerializer.Serialize(state, JsonOptions));
    }

    /// <summary>The staged restore, or null when there is not a complete one.</summary>
    public static PendingRestoreState? Read(string dataDirectory)
    {
        var databasePath = StagedDatabasePath(dataDirectory);
        var statePath = StagedStatePath(dataDirectory);
        if (!File.Exists(databasePath) || !File.Exists(statePath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PendingRestoreState>(File.ReadAllText(statePath), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Throws away a staged restore. The safety copy is deliberately left alone - it is
    /// the player's file now, and this app does not delete backups.</summary>
    public static void Clear(string dataDirectory)
    {
        DatabaseSnapshot.Delete(StagedDatabasePath(dataDirectory));
        TryDelete(StagedStatePath(dataDirectory));
    }

    public static RestoreResult? ReadResult(string dataDirectory)
    {
        var path = ResultPath(dataDirectory);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RestoreResult>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    public static void ClearResult(string dataDirectory) => TryDelete(ResultPath(dataDirectory));

    /// <summary>
    /// Applies a staged restore if there is one. Called at startup, before anything opens the
    /// database, and safe to call when there is nothing staged.
    ///
    /// <para>The swap moves the current database aside rather than deleting it, so a failure part
    /// of the way through can put it back. The <c>-wal</c> and <c>-shm</c> files are deleted with
    /// it and not kept: a write-ahead log left beside a database it does not belong to is exactly
    /// how a good file becomes a corrupt one.</para>
    ///
    /// <para>Migrations run immediately after this returns, which is what makes restoring an older
    /// backup into a newer build work: the restored schema is simply brought forward the same way
    /// any existing database would be.</para>
    /// </summary>
    public static RestoreResult? ApplyIfStaged(string dataDirectory, string databasePath, ILogger? logger = null)
    {
        var state = Read(dataDirectory);
        if (state is null)
        {
            // Tidy up a stage that only got half-written before something went wrong.
            Clear(dataDirectory);
            return null;
        }

        var stagedPath = StagedDatabasePath(dataDirectory);
        var supersededPath = databasePath + ".superseded";

        var result = new RestoreResult
        {
            AppliedUtc = DateTimeOffset.UtcNow,
            SourceFileName = state.SourceFileName,
            SafetyCopyPath = state.SafetyCopyPath,
            AirlineName = state.BackupAirlineName,
        };

        // Re-checked here and not only at upload. Between staging and this moment the machine may
        // have lost power or a disk may have gone bad, and the one thing that must never happen is
        // replacing a working database with a broken one.
        var integrity = DatabaseSnapshot.IntegrityCheck(stagedPath);
        if (!string.Equals(integrity, DatabaseSnapshot.IntegrityOk, StringComparison.Ordinal))
        {
            logger?.LogError("The staged restore failed its integrity check ({Integrity}); it was not applied.", integrity);
            result.Succeeded = false;
            result.Message =
                "The backup that was waiting to be restored turned out to be damaged, so your airline was left " +
                "exactly as it was. Nothing has changed.";
            Clear(dataDirectory);
            WriteResult(dataDirectory, result);
            return result;
        }

        var movedAside = false;
        try
        {
            TryDelete(supersededPath);

            if (File.Exists(databasePath))
            {
                File.Move(databasePath, supersededPath);
                movedAside = true;
            }

            TryDelete(databasePath + "-wal");
            TryDelete(databasePath + "-shm");

            File.Move(stagedPath, databasePath);

            // The staged file's own -wal and -shm, left by the integrity check above. They describe
            // a file that no longer exists under that name, so they go with it.
            DatabaseSnapshot.Delete(stagedPath);
            TryDelete(StagedStatePath(dataDirectory));
            DatabaseSnapshot.Delete(supersededPath);

            result.Succeeded = true;
            logger?.LogInformation(
                "Restored the database from {SourceFileName}. The previous airline was saved to {SafetyCopyPath}.",
                state.SourceFileName, state.SafetyCopyPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogError(ex, "The staged restore could not be applied.");

            if (movedAside && !File.Exists(databasePath) && File.Exists(supersededPath))
            {
                try
                {
                    File.Move(supersededPath, databasePath);
                }
                catch (Exception putBack) when (putBack is IOException or UnauthorizedAccessException)
                {
                    logger?.LogCritical(putBack, "The previous database could not be put back; it is at {Path}.", supersededPath);
                }
            }

            result.Succeeded = false;
            result.Message =
                "The restore could not be completed, so your airline was left as it was. Your backup file is " +
                "unchanged - close anything else that might be using FSOps' data folder and try again.";
            Clear(dataDirectory);
        }

        WriteResult(dataDirectory, result);
        return result;
    }

    private static void WriteResult(string dataDirectory, RestoreResult result)
    {
        try
        {
            File.WriteAllText(ResultPath(dataDirectory), JsonSerializer.Serialize(result, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The restore itself already happened; losing the note about it is not worth failing a
            // startup over, and the log line above says the same thing.
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
            // Best effort. Every caller here has a working answer without the delete succeeding.
        }
    }
}
