namespace FSOps.Core.Flights;

/// <summary>
/// Shape of <c>FlightEvent.PayloadJson</c> for a <c>FlightEventType.PhaseChange</c> row. Declared
/// here (rather than only where it's written) so <see cref="FlightPhaseStateMachine.RestoreFrom"/>
/// can deserialise the exact same shape when rehydrating a flight after a restart.
/// </summary>
public sealed record PhaseChangePayload(string FromPhase, string ToPhase, bool WasGoAround);

/// <summary>Shape of <c>FlightEvent.PayloadJson</c> for a <c>FlightEventType.Touchdown</c> row.</summary>
public sealed record TouchdownPayload(
    double LatitudeDeg,
    double LongitudeDeg,
    double TrueHeadingDeg,
    double Fpm,
    double GForce,
    int BounceIndex);
