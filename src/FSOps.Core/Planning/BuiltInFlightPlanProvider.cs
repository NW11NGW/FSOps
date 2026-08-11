namespace FSOps.Core.Planning;

/// <summary>
/// Wraps RoutePreviewCalculator so the deterministic in-app estimate is just another
/// IFlightPlanProvider - the default, and the only one every sector had before an external
/// dispatch tool (SimBrief) could be consulted. Always succeeds: RoutePreviewCalculator never
/// throws (see its own doc comment), so this is guaranteed usable as the last entry in a
/// fallback chain regardless of what came before it.
/// </summary>
public sealed class BuiltInFlightPlanProvider : IFlightPlanProvider
{
    public string Name => "FSOps";

    public Task<FlightPlanOutcome> GetPlanAsync(FlightPlanRequest request, CancellationToken ct)
    {
        var preview = RoutePreviewCalculator.Calculate(
            request.EconomyConfig, request.Departure, request.Arrival, request.AircraftType, request.Strategy);

        var plan = new FlightPlan(
            BlockFuelKg: preview.FuelBreakdown.TotalFuelKg,
            CruiseAltitudeFt: preview.CruiseAltitudeFt,
            BlockTimeMinutes: preview.BlockTimeBreakdown.TotalMinutes,
            RouteString: null);

        return Task.FromResult(FlightPlanOutcome.Succeeded(Name, plan));
    }
}
