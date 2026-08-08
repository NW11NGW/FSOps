namespace FSOps.Core.Flights;

/// <summary>What happened, if anything, as a result of one <see cref="FlightPhaseStateMachine.Advance"/> call.</summary>
/// <param name="Phase">The phase after processing this sample.</param>
/// <param name="PhaseChanged">True if this sample caused a transition.</param>
/// <param name="PreviousPhase">The phase before this sample, only meaningful when <see cref="PhaseChanged"/> is true.</param>
/// <param name="IsGoAround">True when this transition was specifically the Landed -> Climb go-around case.</param>
/// <param name="NewTouchdown">Set when this sample registered a new ground-contact event.</param>
public sealed record FlightPhaseAdvanceResult(
    FlightPhase Phase,
    bool PhaseChanged,
    FlightPhase? PreviousPhase,
    bool IsGoAround,
    TouchdownRecord? NewTouchdown);
