using FSOps.Core.Economy;
using FSOps.Core.Entities;

namespace FSOps.Core.Planning;

/// <summary>
/// Everything a flight plan source needs to produce (or decline to produce) a plan for one
/// sector. <see cref="SimBriefPilotId"/> is only ever consulted by a provider that needs it (the
/// SimBrief import); the built-in estimator ignores it entirely. Never logged and never placed in
/// a URL the SPA constructs - see SimBriefFlightPlanProvider's own doc comment.
/// </summary>
public sealed record FlightPlanRequest(
    Airport Departure,
    Airport Arrival,
    AircraftType AircraftType,
    EconomyConfig EconomyConfig,
    AirlineStrategyProfile? Strategy,
    string? SimBriefPilotId);

/// <summary>
/// The enrichable figures for the sector being flown. Deliberately just the totals a real
/// dispatch OFP actually hands back (block fuel, cruise level, block time, filed route) rather
/// than the built-in estimator's full phase-by-phase breakdown - a provider that can only offer
/// totals (SimBrief) is still fully usable this way, and the breakdown UI is driven separately by
/// RoutePreviewCalculator, which callers keep using unchanged alongside this.
/// </summary>
public sealed record FlightPlan(
    double BlockFuelKg,
    int CruiseAltitudeFt,
    int BlockTimeMinutes,
    string? RouteString);

/// <summary>
/// Either a usable plan (<see cref="Success"/> true, <see cref="Plan"/> set) or a reason one
/// couldn't be produced - a provider must never throw for an ordinary "nothing to offer" outcome
/// (no Pilot ID configured, unknown Pilot ID, network timeout, malformed response, an OFP filed
/// for a different city pair). <see cref="Message"/> is set on failure so the caller always has
/// something honest to tell the player about why a fallback happened.
/// </summary>
public sealed record FlightPlanOutcome(bool Success, string ProviderName, string? Message, FlightPlan? Plan)
{
    public static FlightPlanOutcome Failed(string providerName, string message) => new(false, providerName, message, null);

    public static FlightPlanOutcome Succeeded(string providerName, FlightPlan plan, string? message = null) =>
        new(true, providerName, message, plan);
}

/// <summary>
/// Source of the block fuel, cruise level, block time, and filed route planned for a sector.
/// Callers (FlightEndpoints) compose an ordered list - a real dispatch tool such as SimBrief
/// first, the deterministic in-app estimator last - so a sector is enriched with a real OFP when
/// one is available and matches the route being flown, and always has something to fall back to
/// otherwise. It is an abstraction rather than a single hardcoded source because FSOps has to be a
/// good citizen among the tools simmers already use: planning elsewhere, or not at all, must never
/// change what a flight earns, so the plan is a briefing rather than a contract.
/// </summary>
public interface IFlightPlanProvider
{
    /// <summary>Short, human-readable name surfaced to the player as where a plan came from
    /// (e.g. "SimBrief", "FSOps") and used in fallback/failure messaging.</summary>
    string Name { get; }

    Task<FlightPlanOutcome> GetPlanAsync(FlightPlanRequest request, CancellationToken ct);
}
