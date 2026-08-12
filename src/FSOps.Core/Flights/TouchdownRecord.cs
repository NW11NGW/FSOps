namespace FSOps.Core.Flights;

/// <summary>
/// Where a touchdown's sink rate came from. Recorded alongside the figure itself because "the sim
/// never told us" and "the sim told us zero" are completely different facts about a landing, and
/// conflating them is what made a real 59 fpm greaser read as a confident 0 fpm on the report card.
/// </summary>
public enum TouchdownRateSource
{
    /// <summary>No sink rate could be established at all. <c>TouchdownRecord.Fpm</c> is null and
    /// callers must present the landing as NOT MEASURED - never as zero.</summary>
    NotMeasured,

    /// <summary>The sim's own PLANE TOUCHDOWN NORMAL VELOCITY, which is slope-proof and is always
    /// preferred when it reports anything at all.</summary>
    SimTouchdownRate,

    /// <summary>Vertical speed from the last airborne sample before contact. The fallback for when
    /// the touchdown simvar stays at zero through the whole capture window (see
    /// <c>FlightPhaseStateMachine.Advance</c>). It measures the same physical quantity a frame
    /// earlier - below 2,000 ft the sim source samples every frame, so "a frame earlier" is
    /// milliseconds - but it is not slope-corrected, so it is only used when there is nothing
    /// better.</summary>
    VerticalSpeedBeforeContact,
}

/// <summary>
/// One ground-contact event captured during landing. Several of these can exist for a single
/// landing when the aircraft bounces - see <see cref="FlightPhaseStateMachine.Touchdowns"/>.
/// </summary>
/// <param name="Utc">When contact was made.</param>
/// <param name="LatitudeDeg">Aircraft position at contact.</param>
/// <param name="LongitudeDeg">Aircraft position at contact.</param>
/// <param name="TrueHeadingDeg">Aircraft track at contact, used to match against a runway.</param>
/// <param name="Fpm">
/// Sink rate at contact in feet per minute, always a positive magnitude - larger means a harder
/// landing. NULL means no rate could be measured, which is a genuinely different outcome from a
/// feather-light landing and must never be flattened to zero by anything downstream. See
/// <paramref name="FpmSource"/> for where a non-null figure came from.
/// </param>
/// <param name="GForce">Peak G-force observed in the short window around this contact.</param>
/// <param name="FpmSource">Provenance of <paramref name="Fpm"/>.</param>
public sealed record TouchdownRecord(
    DateTimeOffset Utc,
    double LatitudeDeg,
    double LongitudeDeg,
    double TrueHeadingDeg,
    double? Fpm,
    double GForce,
    TouchdownRateSource FpmSource = TouchdownRateSource.SimTouchdownRate);
