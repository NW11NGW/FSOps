namespace FSOps.Core.Flights;

/// <summary>
/// Every tunable number the phase state machine uses to decide when to move on, gathered in one
/// place so the guards documented on <see cref="FlightPhaseStateMachine"/> are easy to compare
/// against these defaults. All durations are measured using the timestamps carried by the
/// telemetry samples themselves (never wall-clock "now"), which is what keeps the state machine
/// pure and deterministic - the same sample sequence always produces the same result, in a test
/// or in production.
/// </summary>
public sealed record FlightPhaseThresholds
{
    /// <summary>Preflight -> TaxiOut: on the ground, rolling, brake off.</summary>
    public double TaxiGroundSpeedKt { get; init; } = 2;

    /// <summary>TaxiOut -> TakeoffRoll: on the ground and accelerating hard.</summary>
    public double TakeoffRollGroundSpeedKt { get; init; } = 40;

    /// <summary>
    /// Cruise -> ... band: |VS| below this counts as "level" for both entering Cruise from Climb
    /// and (implicitly) for staying there.
    /// </summary>
    public double CruiseVerticalSpeedBandFpm { get; init; } = 300;

    /// <summary>
    /// How long VS must stay inside the level band before Climb -> Cruise fires. The task brief
    /// suggests ~120s; this is tuned down to keep interactive/demo runs and CI fast while still
    /// requiring dozens of consecutive in-band samples at typical telemetry rates (0.2-1s apart) -
    /// comfortably enough to reject a single noisy reading. Any sample outside the band resets the
    /// timer to zero, so "sustained" really does mean unbroken.
    /// </summary>
    public TimeSpan CruiseSustainDuration { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Cruise -> Descent: VS below (more negative than) minus this value.</summary>
    public double DescentVerticalSpeedFpm { get; init; } = 300;

    /// <summary>How long the descent rate must be sustained before Cruise -> Descent fires. See <see cref="CruiseSustainDuration"/> for why this is shorter than the brief's ~120s suggestion.</summary>
    public TimeSpan DescentSustainDuration { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Descent -> Approach: AGL altitude below this while still descending.</summary>
    public double ApproachAltitudeAglFt { get; init; } = 3000;

    /// <summary>Landed -> TaxiIn: ground speed has fallen below this after rollout.</summary>
    public double TaxiInGroundSpeedKt { get; init; } = 40;

    /// <summary>
    /// Landed -> Climb (go-around): the aircraft must be continuously airborne for longer than
    /// this before it counts as a go-around rather than a bounce.
    /// </summary>
    public TimeSpan GoAroundAirborneDuration { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Ground-contact events within this long of the first one are treated as bounces of the same landing.</summary>
    public TimeSpan BounceWindowDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long after each touchdown to keep watching the samples for late-arriving detail about
    /// that same impact: a higher G-force peak, and the sim's own touchdown rate, which is often
    /// still zero on the frame the aircraft is first reported on the ground.
    /// </summary>
    public TimeSpan TouchdownPeakGForceWindow { get; init; } = TimeSpan.FromSeconds(3);
}
