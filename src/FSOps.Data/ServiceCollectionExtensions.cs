using FSOps.Core;
using FSOps.Data.Import;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FSOps.Data;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFsOpsData(this IServiceCollection services)
    {
        var connectionString = $"Data Source={AppPaths.DatabasePath}";

        services.AddDbContext<FsOpsDbContext>(options =>
            options
                .UseSqlite(connectionString)
                .AddInterceptors(new WalModeConnectionInterceptor()));

        services.AddSingleton<WorldDataImportProgress>();
        services.AddScoped<WorldDataImporter>();

        return services;
    }

    /// <summary>
    /// Applies pending migrations. Fast, so this runs synchronously before the server starts listening.
    ///
    /// <para>Takes a copy of the database first, but <b>only when a migration is actually going to be
    /// applied</b> - copying a growing database on every launch would be a real cost for no benefit
    /// on the overwhelming majority of starts, where nothing is pending. A migration that quietly
    /// rewrites data is the one mistake here with no recovery - the user has real flight history -
    /// and this copy is that recovery. If the copy cannot be taken the migration does not run at all, because proceeding
    /// without the safety net is precisely the trade that has no way back.</para>
    ///
    /// <para>A database that cannot be opened at all is turned into
    /// <see cref="FsOpsDatabaseUnavailableException"/>, which carries text written for the user. See
    /// that type for why this is a fail-fast case and why the app must never repair or delete the
    /// file itself.</para>
    /// </summary>
    /// <exception cref="FsOpsDatabaseUnavailableException">The database is damaged or unreadable.</exception>
    /// <exception cref="FsOpsBackupFailedException">The pre-migration copy could not be taken; nothing was migrated.</exception>
    public static void MigrateFsOpsDatabase(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FsOpsDbContext>();

        // Read the path off the context rather than AppPaths so tests can point this anywhere.
        var databasePath = db.Database.GetDbConnection().DataSource;
        var logger = scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("DatabaseMigration");

        List<string> pending;
        try
        {
            pending = db.Database.GetPendingMigrations().ToList();
        }
        catch (Exception ex) when (FsOpsDatabaseUnavailableException.IsUnusableDatabase(ex))
        {
            throw FsOpsDatabaseUnavailableException.For(databasePath, ex);
        }

        if (pending.Count > 0)
        {
            BackUpBeforeMigrating(databasePath, pending, logger);
        }

        try
        {
            db.Database.Migrate();
        }
        catch (Exception ex) when (FsOpsDatabaseUnavailableException.IsUnusableDatabase(ex))
        {
            throw FsOpsDatabaseUnavailableException.For(databasePath, ex);
        }
    }

    /// <summary>
    /// Copies the database to a single <c>.bak</c> beside it. One backup, not a history - the goal
    /// is surviving the migration in front of us, not an archive.
    ///
    /// <para>Written to a temporary file and only then moved into place, so a copy that fails
    /// half-way can never leave a truncated <c>.bak</c> where a good one used to be, and can never
    /// touch the original. Uses SQLite's own backup API rather than a file copy because the
    /// database runs in WAL mode: copying <c>fsops.db</c> on its own would silently miss everything
    /// still sitting in <c>fsops.db-wal</c>, which on a fresh close is potentially the whole of the
    /// last session.</para>
    /// </summary>
    private static void BackUpBeforeMigrating(string databasePath, IReadOnlyCollection<string> pending, ILogger? logger)
    {
        // Nothing to lose on a first run - the file is about to be created by the migration itself.
        if (!File.Exists(databasePath))
        {
            logger?.LogInformation(
                "Creating a new database and applying {Count} migration(s): {Migrations}.", pending.Count, string.Join(", ", pending));
            return;
        }

        var backupPath = databasePath + ".bak";
        var temporaryPath = backupPath + ".tmp";

        try
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            // Pooling=False matters here and is not a detail. Microsoft.Data.Sqlite returns a closed
            // connection to its pool rather than releasing the file handle, so with pooling on, the
            // File.Move below fails with "the process cannot access the file because it is being
            // used by another process" - a backup that appears to work and then cannot be put in
            // place. These are two one-shot connections at startup; there is nothing for a pool to
            // gain.
            using (var source = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            using (var destination = new SqliteConnection($"Data Source={temporaryPath};Pooling=False"))
            {
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
            }

            // Verify before trusting it. An empty or missing file here means the safety net does
            // not exist, and that has to stop the migration rather than be discovered afterwards.
            var written = new FileInfo(temporaryPath);
            if (!written.Exists || written.Length == 0)
            {
                throw FsOpsBackupFailedException.For(databasePath, backupPath);
            }

            File.Move(temporaryPath, backupPath, overwrite: true);
        }
        catch (FsOpsBackupFailedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw FsOpsBackupFailedException.For(databasePath, backupPath, ex);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // A stray .tmp is untidy, not dangerous, and never worth failing a startup over.
                }
            }
        }

        logger?.LogInformation(
            "Backed up the database to {BackupPath} before applying {Count} migration(s): {Migrations}.",
            backupPath, pending.Count, string.Join(", ", pending));
    }

    /// <summary>
    /// Seeds world airport/runway data on first run and refreshes it whenever the bundled data set
    /// changes (compared by content hash, not by a version number anyone has to remember to bump -
    /// see WorldDataImporter), and reconciles the aircraft
    /// catalogue against the current code definitions (insert-or-update by IcaoType, never
    /// delete - see AircraftTypeSeeder's own doc) on every startup, not just the first. Meant to
    /// be kicked off as a background task after the server starts listening - it can take a while
    /// on first run, and WorldDataImportProgress lets the UI show it.
    /// </summary>
    public static async Task SeedWorldDataAsync(this IServiceProvider services, string seedDataDirectory, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("WorldDataSeed");

        try
        {
            var db = provider.GetRequiredService<FsOpsDbContext>();
            var importer = provider.GetRequiredService<WorldDataImporter>();

            await importer.ImportIfNeededAsync(db, seedDataDirectory, ct);
            await AircraftTypeSeeder.ReconcileAsync(db, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "World data seed failed.");
        }
    }
}
