namespace FSOps.Core.Flights;

/// <summary>
/// Shape of <c>FlightEvent.PayloadJson</c> for a <c>FlightEventType.PhaseChange</c> row. Declared
/// here (rather than only where it's written) so <see cref="FlightPhaseStateMachine.RestoreFrom"/>
/// can deserialise the exact same shape when rehydrating a flight after a restart.
/// </summary>
public sealed record PhaseChangePayload(string FromPhase, string ToPhase, bool WasGoAround);

/// <summary>
/// Shape of <c>FlightEvent.PayloadJson</c> for a <c>FlightEventType.Touchdown</c> row.
/// <para>
/// <paramref name="Fpm"/> is nullable and <paramref name="FpmSource"/> is a trailing optional, both
/// so that rows written before landing-rate provenance existed still deserialise. Such a row always
/// carries a number (the old writer had no way to express "unknown"), so an absent source is
/// resolved by <see cref="FlightPhaseStateMachine.RestoreFrom"/> from whether Fpm is present rather
/// than by defaulting the enum - guessing via enum ordering is exactly the kind of silent
/// mis-parse that a stored-as-string enum default has already cost this project twice.
/// </para>
/// </summary>
public sealed record TouchdownPayload(
    double LatitudeDeg,
    double LongitudeDeg,
    double TrueHeadingDeg,
    double? Fpm,
    double GForce,
    int BounceIndex,
    TouchdownRateSource? FpmSource = null);
