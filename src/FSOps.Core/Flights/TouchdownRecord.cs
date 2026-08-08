namespace FSOps.Core.Flights;

/// <summary>
/// One ground-contact event captured during landing. Several of these can exist for a single
/// landing when the aircraft bounces - see <see cref="FlightPhaseStateMachine.Touchdowns"/>.
/// </summary>
/// <param name="Utc">When contact was made.</param>
/// <param name="LatitudeDeg">Aircraft position at contact.</param>
/// <param name="LongitudeDeg">Aircraft position at contact.</param>
/// <param name="TrueHeadingDeg">Aircraft track at contact, used to match against a runway.</param>
/// <param name="Fpm">
/// Sink rate at contact in feet per minute, derived from the sim's own
/// PLANE TOUCHDOWN NORMAL VELOCITY (slope-proof, unlike a plain vertical-speed reading). Always a
/// positive magnitude - larger means a harder landing.
/// </param>
/// <param name="GForce">Peak G-force observed in the short window around this contact.</param>
public sealed record TouchdownRecord(
    DateTimeOffset Utc,
    double LatitudeDeg,
    double LongitudeDeg,
    double TrueHeadingDeg,
    double Fpm,
    double GForce);
