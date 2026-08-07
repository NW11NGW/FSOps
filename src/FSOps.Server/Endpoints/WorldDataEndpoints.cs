using FSOps.Data.Import;

namespace FSOps.Server.Endpoints;

public static class WorldDataEndpoints
{
    public static void MapWorldDataEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/worlddata/status", (WorldDataImportProgress progress) => Results.Ok(new
        {
            seeded = progress.Seeded,
            airportCount = progress.AirportCount,
            runwayCount = progress.RunwayCount,
            importInProgress = progress.ImportInProgress,
            progressPercent = progress.ProgressPercent,
        }));
    }
}
