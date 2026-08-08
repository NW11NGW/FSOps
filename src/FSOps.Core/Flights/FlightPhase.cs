namespace FSOps.Core.Flights;

/// <summary>
/// Stages of a single flight, in the order they normally occur. The state machine in
/// <see cref="FlightPhaseStateMachine"/> only ever moves forward through this list, with one
/// deliberate exception: <see cref="Landed"/> can go back to <see cref="Climb"/> on a go-around.
/// </summary>
public enum FlightPhase
{
    Preflight,
    TaxiOut,
    TakeoffRoll,
    Climb,
    Cruise,
    Descent,
    Approach,
    Landed,
    TaxiIn,
    Shutdown,
}
