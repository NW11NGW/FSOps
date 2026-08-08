using FSOps.Core;
using FSOps.Data.Import;
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

    /// <summary>Applies pending migrations. Fast, so this runs synchronously before the server starts listening.</summary>
    public static void MigrateFsOpsDatabase(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FsOpsDbContext>();
        db.Database.Migrate();
    }

    /// <summary>
    /// Seeds world airport/runway data if the database is empty, and reconciles the aircraft
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
