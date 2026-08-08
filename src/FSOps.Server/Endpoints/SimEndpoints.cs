using FSOps.Server.Services;

namespace FSOps.Server.Endpoints;

public static class SimEndpoints
{
    public static void MapSimEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/sim/status", GetStatus);
    }

    private static IResult GetStatus(SimTelemetryService telemetry) =>
        Results.Ok(new SimStatusResponse(
            telemetry.ConnectionState.ToString(),
            telemetry.SourceKind,
            telemetry.AircraftTitle,
            telemetry.LastSampleUtc?.ToString("o")));
}

public sealed record SimStatusResponse(string State, string SourceKind, string? AircraftTitle, string? LastSampleUtc);
