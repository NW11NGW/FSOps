using FSOps.Data;
using FSOps.Server.Services;
using FSOps.Server.Services.Backup;
using Microsoft.AspNetCore.Http.Features;

namespace FSOps.Server.Endpoints;

/// <summary>
/// Saving the player's airline to a file, and putting one back.
///
/// <para>The shape of this surface follows from one fact: <b>the server holds the database open for
/// its whole life</b>. Taking a backup is therefore something that can happen at any moment and is
/// a plain download; putting one back is not, and cannot be pretended into one. So a restore is two
/// steps that the player can see - the file is checked and staged now, and applied when FSOps next
/// starts - rather than one step that would have to swap a file out from underneath six running
/// services. See <see cref="PendingRestore"/>.</para>
///
/// <para>Every refusal is a 400 with a sentence written for the person, and every one of them
/// happens before anything of theirs has been touched. A backup from a newer build, a truncated
/// file, a file that was never a backup: all three are refused with the current airline still
/// exactly where it was.</para>
/// </summary>
public static class BackupEndpoints
{
    /// <summary>
    /// The largest file this will accept. Generous - a long-running airline's database with full
    /// world data is tens of megabytes, and the ceiling exists to stop an accidental upload of
    /// something enormous, not to police real backups. Kestrel's own default is 30 MB, which a real
    /// backup would exceed, so it has to be raised per request.
    /// </summary>
    public const long MaxRestoreBytes = 2L * 1024 * 1024 * 1024;

    public static void MapBackupEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/backup/status", GetStatusAsync);
        group.MapGet("/backup/file", DownloadAsync);
        group.MapPost("/backup/restore", StageRestoreAsync);
        group.MapPost("/backup/restore/cancel", CancelRestore);
        group.MapPost("/backup/restore/acknowledge", AcknowledgeRestore);
    }

    private static async Task<IResult> GetStatusAsync(BackupService backups, FsOpsDbContext db, CancellationToken ct) =>
        Results.Ok(await BuildStatusAsync(backups, db, ct));

    /// <summary>
    /// Streams a freshly taken backup. A GET on purpose: the browser's own save dialog is how the
    /// player chooses where the file goes, and that path has to work from a plain link as well as
    /// from a fetch.
    /// </summary>
    private static async Task<IResult> DownloadAsync(
        BackupService backups,
        FsOpsDbContext db,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var logger = loggers.CreateLogger("Backup");

        PreparedBackup prepared;
        try
        {
            prepared = await backups.PrepareAsync(db, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogError(ex, "The backup could not be created.");
            return Results.BadRequest(new
            {
                error = "FSOps could not create the backup. Make sure there is enough free disk space and try again.",
            });
        }

        // DeleteOnClose so the temporary copy goes away the moment the response finishes, whether it
        // finished by being sent or by the player closing the tab half-way through. FileShare.Delete
        // is what makes that legal while the handle is still open.
        var stream = new FileStream(
            prepared.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.DeleteOnClose);

        return Results.File(stream, "application/octet-stream", prepared.FileName);
    }

    /// <summary>
    /// Takes the raw bytes of a <c>.fsopsbak</c> the player chose, checks it completely, and stages
    /// it. Raw body rather than a multipart form deliberately: there is exactly one file, and
    /// streaming it straight to disk avoids form buffering limits that would silently cap how large
    /// an airline can be restored.
    /// </summary>
    private static async Task<IResult> StageRestoreAsync(
        HttpContext context,
        BackupService backups,
        FsOpsDbContext db,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var logger = loggers.CreateLogger("Backup");

        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = MaxRestoreBytes;
        }

        var fileName = context.Request.Query["fileName"].ToString();
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "backup" + BackupManifest.FileExtension;
        }
        else
        {
            // Only ever displayed back to the player; never used to build a path.
            fileName = Path.GetFileName(fileName.Trim());
        }

        Directory.CreateDirectory(backups.BackupsDirectory);
        var uploadedPath = Path.Combine(backups.BackupsDirectory, $"working-{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var file = new FileStream(uploadedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await context.Request.Body.CopyToAsync(file, ct);
            }

            if (new FileInfo(uploadedPath).Length == 0)
            {
                return Results.BadRequest(new { error = "No file was received, so there is nothing to restore from." });
            }

            var staging = await backups.StageRestoreAsync(uploadedPath, fileName, db, ct);
            if (!staging.Staged)
            {
                return Results.BadRequest(new { error = staging.Refusal });
            }

            return Results.Ok(await BuildStatusAsync(backups, db, ct));
        }
        catch (BadHttpRequestException)
        {
            return Results.BadRequest(new
            {
                error = "That file is too large to be an FSOps backup, so it was not read.",
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "The uploaded backup could not be written to disk.");
            return Results.BadRequest(new
            {
                error = "FSOps could not read that file. Make sure there is enough free disk space and try again.",
            });
        }
        finally
        {
            TryDelete(uploadedPath);
        }
    }

    /// <summary>Throws away a staged restore. The safety copy taken when it was staged is left
    /// alone - it is the player's file, and this app does not delete backups.</summary>
    private static async Task<IResult> CancelRestore(BackupService backups, FsOpsDbContext db, CancellationToken ct)
    {
        backups.CancelPending();
        return Results.Ok(await BuildStatusAsync(backups, db, ct));
    }

    /// <summary>Clears the "this is what happened last time" note once the player has read it, so
    /// it is not still on screen weeks later.</summary>
    private static async Task<IResult> AcknowledgeRestore(BackupService backups, FsOpsDbContext db, CancellationToken ct)
    {
        backups.AcknowledgeLastResult();
        return Results.Ok(await BuildStatusAsync(backups, db, ct));
    }

    private static async Task<BackupStatusResponse> BuildStatusAsync(
        BackupService backups,
        FsOpsDbContext db,
        CancellationToken ct)
    {
        var pending = backups.ReadPending();
        var last = backups.ReadLastResult();

        return new BackupStatusResponse(
            backups.DatabaseSizeBytes,
            backups.DataDirectory,
            backups.BackupsDirectory,
            await backups.SuggestFileNameAsync(db, ct),
            backups.CurrentAppVersion,
            pending is null
                ? null
                : new PendingRestoreResponse(
                    pending.SourceFileName,
                    pending.StagedUtc,
                    pending.SafetyCopyPath,
                    pending.BackupAppVersion,
                    pending.BackupCreatedUtc,
                    pending.BackupAirlineName),
            last is null
                ? null
                : new LastRestoreResponse(
                    last.Succeeded,
                    last.AppliedUtc,
                    last.SourceFileName,
                    last.SafetyCopyPath,
                    last.AirlineName,
                    last.Message));
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
            // The upload has already been copied out of the way or refused; a leftover scratch file
            // in the app's own folder is swept on the next backup.
        }
    }
}

/// <param name="DatabaseSizeBytes">Roughly what a backup will weigh - the archive is compressed, so
/// the real file is smaller, but this is the honest order of magnitude to show before pressing a
/// button.</param>
/// <param name="SuggestedFileName">Offered to the save dialog: the airline's name and the date.</param>
public sealed record BackupStatusResponse(
    long DatabaseSizeBytes,
    string DataDirectory,
    string BackupsDirectory,
    string SuggestedFileName,
    string AppVersion,
    PendingRestoreResponse? PendingRestore,
    LastRestoreResponse? LastRestore);

public sealed record PendingRestoreResponse(
    string SourceFileName,
    DateTimeOffset StagedUtc,
    string SafetyCopyPath,
    string? BackupAppVersion,
    DateTimeOffset? BackupCreatedUtc,
    string? BackupAirlineName);

public sealed record LastRestoreResponse(
    bool Succeeded,
    DateTimeOffset AppliedUtc,
    string SourceFileName,
    string SafetyCopyPath,
    string? AirlineName,
    string? Message);
