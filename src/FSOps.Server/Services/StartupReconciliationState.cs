namespace FSOps.Server.Services;

/// <summary>
/// Holds what the startup reconciliation actually did, so it can be surfaced to the player rather
/// than only written to a log file nobody reads.
///
/// Reservation became a hard two-way invariant in E3, and a database written before that can hold
/// an aircraft that is both reserved for the player and carrying virtual-pilot legs (see
/// <see cref="ReservationReconciler"/>). Resolving that contradiction changes a setting the player
/// chose, and changing someone's setting silently is not acceptable - so the result is kept here
/// for the "while you were away" summary to read on the next request.
///
/// Registered as a singleton before the host is built and populated once during startup, because
/// nothing can be added to the service collection after <c>builder.Build()</c>. Null
/// <see cref="Result"/> simply means reconciliation has not run yet.
/// </summary>
public sealed class StartupReconciliationState
{
    public ReservationReconciliationResult? Result { get; set; }
}
