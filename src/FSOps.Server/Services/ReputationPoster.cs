using FSOps.Core.Economy;
using FSOps.Core.Entities;

namespace FSOps.Server.Services;

/// <summary>
/// Applies one flight's worth of outcome to <see cref="Airline.ReputationScore"/> - the shared call
/// point for every completion path (<see cref="FlightLifecycleService"/>'s telemetry completion,
/// <c>FlightEndpoints.CompleteManualAsync</c>, and <see cref="VirtualFlightResolverService"/>'s
/// virtual-pilot flown/unflyable paths), mirroring <see cref="MaintenancePoster"/>'s own reason for
/// existing at all: three-plus call sites computing the same effect independently is exactly how
/// paths went out of sync before in this project (see MaintenancePoster's class doc). Never called
/// for <see cref="FlightStatus.Suspended"/> - see <see cref="ReputationCalculator"/>'s own doc.
/// <para>
/// Mutates the SAME tracked <see cref="Airline"/> instance the caller's own flight/watermark commit
/// already saves - the reputation update is therefore covered by exactly the same idempotency
/// guarantee that commit already relies on (<see cref="Flight.RevenuePosted"/> for a player flight,
/// the per-occurrence Flight-row + <c>EconomyState.LastScheduleResolvedUtc</c> watermark commit for
/// a virtual one - see <see cref="VirtualFlightResolverService"/>'s class doc), never a write of
/// its own that could race or double-apply independently.
/// </para>
/// </summary>
public static class ReputationPoster
{
    public static void PostCompletedFlight(Airline airline, EconomyConfig config, double? delayMinutes, double? landingFpm)
    {
        airline.ReputationScore = ReputationCalculator.AdvanceForCompletedFlight(
            airline.ReputationScore, config.Reputation, delayMinutes, landingFpm);
    }

    public static void PostCancelledOrSkipped(Airline airline, EconomyConfig config)
    {
        airline.ReputationScore = ReputationCalculator.AdvanceForCancelledOrSkipped(airline.ReputationScore, config.Reputation);
    }

    /// <summary>Flat, timing-independent penalty for a manually-completed (unverified) sector - see
    /// <see cref="ReputationCalculator.AdvanceForUnverifiedManualCompletion"/>'s own doc.</summary>
    public static void PostUnverifiedManualCompletion(Airline airline, EconomyConfig config)
    {
        airline.ReputationScore = ReputationCalculator.AdvanceForUnverifiedManualCompletion(airline.ReputationScore, config.Reputation);
    }
}
