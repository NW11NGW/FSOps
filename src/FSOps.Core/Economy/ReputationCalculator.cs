namespace FSOps.Core.Economy;

/// <summary>
/// Advances <see cref="Entities.Airline.ReputationScore"/> by exactly one flight's worth of
/// outcome, via exponential smoothing toward a per-flight "target" score. Pure: takes the airline's current score and one
/// flight's already-known outcome, returns the new score. Never reads a database, never loops over
/// history, never touches <see cref="Entities.FlightStatus.Suspended"/> - that status is not this
/// calculator's business to know about at all; the caller (<see cref="Server.Services.ReputationPoster"/>
/// via the three completion paths) must simply never call this for one.
/// <para>
/// <b>Idempotency.</b> This is an ADVANCE, not a recompute-from-history: the caller mutates the
/// SAME tracked <see cref="Entities.Airline"/> instance that its own flight-row/watermark commit
/// already saves together in one <c>SaveChangesAsync</c> - exactly the same structural pattern this
/// project already trusts for <see cref="Server.Services.MaintenancePoster"/>'s AirframeHours/
/// HoursFlown accrual (see that class's own doc). A duplicate call for the same flight can only
/// happen if the caller's own idempotency gate (<see cref="Entities.Flight.RevenuePosted"/> for a
/// player flight, or the per-occurrence Flight-row + watermark commit for a virtual one) has
/// already failed - and if it never happens twice, this is never called twice either.
/// </para>
/// </summary>
public static class ReputationCalculator
{
    /// <summary>
    /// Advances <paramref name="currentScore"/> for one completed sector. <paramref name="delayMinutes"/>
    /// null means on-time performance could not be honestly measured for this sector (an elevated
    /// simulation rate, or a manually-completed flight with no reliable telemetry - see
    /// <see cref="Entities.Flight.SimRateElevated"/>'s own doc) and is excluded from the blend
    /// rather than guessed at. <paramref name="landingFpm"/> null means no landing rate was
    /// captured (again, most commonly a manual completion). If neither is available there is
    /// nothing honest to score, and the score is returned unchanged.
    /// </summary>
    public static double AdvanceForCompletedFlight(
        double currentScore, ReputationConfig config, double? delayMinutes, double? landingFpm)
    {
        var target = TargetForCompletedFlight(config, delayMinutes, landingFpm);
        return target is { } t ? Advance(currentScore, t, config.Alpha) : currentScore;
    }

    /// <summary>
    /// The blended on-time/landing target a completed sector's outcome advances reputation toward -
    /// exactly the same figure <see cref="AdvanceForCompletedFlight"/> feeds into <see cref="Advance"/>,
    /// pulled out so a read-only reporting view (e.g. "what's moving my reputation") can quote the
    /// same number without re-deriving the blend rules independently. Null when neither signal is
    /// available - see <see cref="AdvanceForCompletedFlight"/>'s own doc for why that means "nothing
    /// honest to score" rather than a zero.
    /// </summary>
    public static double? TargetForCompletedFlight(ReputationConfig config, double? delayMinutes, double? landingFpm)
    {
        double? onTimeScore = delayMinutes is { } delay ? OnTimeScore(config, delay) : null;
        double? landingScore = landingFpm is { } fpm ? LandingScore(config, fpm) : null;

        if (onTimeScore is { } ot && landingScore is { } ls)
        {
            return config.OnTimeWeight * ot + config.LandingWeight * ls;
        }

        if (onTimeScore is { } otOnly)
        {
            return otOnly;
        }

        if (landingScore is { } lsOnly)
        {
            return lsOnly;
        }

        return null;
    }

    /// <summary>
    /// Advances <paramref name="currentScore"/> for a sector that could not fly at all - see
    /// <see cref="Entities.FlightStatus.Skipped"/> and <see cref="Entities.FlightStatus.Cancelled"/>,
    /// both of which apply here identically - from a passenger's point of view a cancelled and a
    /// skipped sector are the same thing. Deliberately never called for <see cref="Entities.FlightStatus.Suspended"/> -
    /// a maintenance-suspended occurrence is not the schedule's fault, which is the entire reason
    /// that status exists as distinct from these two. Uses a harsher target and a larger step than
    /// an ordinary completed sector, so a cancellation always costs more standing than merely
    /// running late, even in the worst on-time/landing case the completed path could ever produce.
    /// </summary>
    public static double AdvanceForCancelledOrSkipped(double currentScore, ReputationConfig config) =>
        Advance(currentScore, config.CancelledTargetScore, config.Alpha * config.CancelledAlphaMultiplier);

    /// <summary>
    /// Advances <paramref name="currentScore"/> for a manually-completed sector - see
    /// <see cref="Entities.FlightStatus.Completed"/> via <c>FlightEndpoints.CompleteManualAsync</c>.
    /// Deliberately takes NO timing or landing input at all: a manual completion has no reliable
    /// telemetry (that is the entire reason the path exists), and the wall clock between starting
    /// and manually completing measures how long the player took to click a button, not how the
    /// sector actually went - see <see cref="ReputationConfig.ManualCompletionAlphaMultiplier"/>'s
    /// own doc for the exploit this replaced (an "immediately complete" flight scoring as a perfect
    /// arrival). The penalty is flat and small: worse than any genuinely completed sector's own
    /// on-time/landing credit, better than that same sector's own worst-case outcome, so a manual
    /// completion is never the reputation-optimal choice over actually flying the sector out, while
    /// a genuine sim crash or disconnect barely registers.
    /// </summary>
    public static double AdvanceForUnverifiedManualCompletion(double currentScore, ReputationConfig config) =>
        Advance(currentScore, config.ManualCompletionTargetScore, config.Alpha * config.ManualCompletionAlphaMultiplier);

    private static double Advance(double currentScore, double target, double alpha) =>
        Math.Clamp(currentScore + alpha * (target - currentScore), 0, 100);

    /// <summary>
    /// 100 within the grace tolerance, decaying linearly to 0 by
    /// <see cref="ReputationConfig.OnTimeZeroScoreDelayMinutes"/> - a grace band, not a cliff-edge,
    /// matching every other "informational, never punishing outright" threshold in this app.
    /// </summary>
    private static double OnTimeScore(ReputationConfig config, double delayMinutes)
    {
        var delay = Math.Max(0, delayMinutes);
        if (delay <= config.OnTimeToleranceMinutes)
        {
            return 100;
        }

        if (delay >= config.OnTimeZeroScoreDelayMinutes)
        {
            return 0;
        }

        var span = config.OnTimeZeroScoreDelayMinutes - config.OnTimeToleranceMinutes;
        return 100 * (config.OnTimeZeroScoreDelayMinutes - delay) / span;
    }

    /// <summary>
    /// Same fpm bounds as <see cref="Scheduling.VirtualPilotPerformanceCalculator"/>'s best/worst-case
    /// landing rate - deliberately, so a player's real touchdown and a virtual pilot's simulated one
    /// are scored on literally the same scale. That is the mitigation for the one real risk in
    /// scoring landing quality at all: rewarding flying yourself over delegating to a hired pilot.
    /// </summary>
    private static double LandingScore(ReputationConfig config, double landingFpm)
    {
        var span = config.LandingWorstFpm - config.LandingBestFpm;
        var raw = (config.LandingWorstFpm - landingFpm) / span;
        return 100 * Math.Clamp(raw, 0, 1);
    }
}
