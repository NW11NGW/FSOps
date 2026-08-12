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
    private readonly List<StartupCatchUpFailure> _catchUpFailures = new();

    public ReservationReconciliationResult? Result { get; set; }

    /// <summary>
    /// Wall-clock catch-up passes that threw instead of completing, newest last. Empty in the
    /// normal case.
    ///
    /// <para>This exists because a failed catch-up is not cosmetic: <see cref="EconomyClockService"/>
    /// posts the monthly lease, salary and insurance charges, and
    /// <see cref="VirtualFlightResolverService"/> resolves flights the player's virtual pilots have
    /// already flown. A pass that failed means real money movements are missing, and the player
    /// must not be shown a balance that looks settled when it is not. Both services now log the
    /// failure at Critical and carry on rather than killing the process - the periodic timer
    /// retries on its next tick - so this is the record that lets the UI say so.</para>
    ///
    /// <para>Both services run their first pass concurrently at startup and both write here, so
    /// the list is guarded and handed out as a snapshot rather than exposed directly.</para>
    /// </summary>
    public IReadOnlyList<StartupCatchUpFailure> CatchUpFailures
    {
        get
        {
            lock (_catchUpFailures)
            {
                return _catchUpFailures.ToArray();
            }
        }
    }

    public void RecordCatchUpFailure(string service, Exception exception, DateTimeOffset occurredUtc)
    {
        lock (_catchUpFailures)
        {
            _catchUpFailures.Add(new StartupCatchUpFailure(service, exception.GetType().Name, exception.Message, occurredUtc));
        }
    }
}

/// <summary>
/// One catch-up pass that threw. Carries the exception's type and message rather than the
/// exception itself: the full detail belongs in the log, and this is read by request-handling code
/// that only needs enough to tell the player something is wrong and roughly what.
/// </summary>
/// <param name="Service">Which service failed, e.g. "EconomyClockService".</param>
public sealed record StartupCatchUpFailure(string Service, string ExceptionType, string Message, DateTimeOffset OccurredUtc);
