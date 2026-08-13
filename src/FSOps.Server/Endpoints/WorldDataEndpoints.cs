using FSOps.Data;
using FSOps.Data.Import;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FSOps.Server.Endpoints;

public static class WorldDataEndpoints
{
    public static void MapWorldDataEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/worlddata/status", (WorldDataImportProgress progress) => Results.Ok(Describe(progress)));

        // Manual "refresh world data" from Settings. The app applies a newer bundled data set on
        // its own the first time it starts after an update; this is for anyone who wants it sooner
        // or suspects their airport table is wrong. It reads only the files shipped with the app -
        // there is no network call here, deliberately, because route planning must never depend on
        // being online.
        group.MapPost("/worlddata/refresh", (
            IServiceScopeFactory scopeFactory,
            WorldDataImportProgress progress,
            ILoggerFactory loggerFactory) =>
        {
            if (progress.IsBusy || progress.ImportInProgress || progress.RefreshInProgress)
            {
                return Results.Conflict(new
                {
                    message = "World data is already being imported. Wait for that to finish before starting another refresh.",
                });
            }

            // Claimed synchronously so a second click within the same second is refused rather
            // than queued. WorldDataImporter takes its own compare-and-swap on top of this, which
            // is the guarantee that actually matters - this only makes the UI honest immediately.
            progress.MarkRefreshStarted();

            var logger = loggerFactory.CreateLogger("WorldDataRefresh");

            // Deliberately not awaited: a full refresh takes far longer than any sensible request
            // timeout. The client polls /worlddata/status, exactly as it does for the first-run
            // import. The scope is created here rather than taken from the request, because the
            // request's scope (and its DbContext) is disposed the moment this returns.
            _ = Task.Run(async () =>
            {
                using var scope = scopeFactory.CreateScope();
                try
                {
                    var db = scope.ServiceProvider.GetRequiredService<FsOpsDbContext>();
                    var importer = scope.ServiceProvider.GetRequiredService<WorldDataImporter>();
                    var outcome = await importer.RefreshAsync(db, WorldDataSeedPaths.BundledSeedDirectory);
                    logger.LogInformation(
                        "Manual world data refresh finished: {Result} ({Airports} airports, {Runways} runways).",
                        outcome.Result, outcome.AirportCount, outcome.RunwayCount);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Manual world data refresh failed.");
                    progress.MarkFailed();
                }
            });

            return Results.Accepted(value: Describe(progress));
        });
    }

    private static object Describe(WorldDataImportProgress progress) => new
    {
        seeded = progress.Seeded,
        airportCount = progress.AirportCount,
        runwayCount = progress.RunwayCount,
        importInProgress = progress.ImportInProgress,
        refreshInProgress = progress.RefreshInProgress,
        progressPercent = progress.ProgressPercent,
        dataVersion = progress.DataVersion,
        lastAppliedUtc = progress.LastAppliedUtc?.ToString("o"),
    };
}
