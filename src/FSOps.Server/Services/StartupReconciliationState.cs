namespace FSOps.Server.Services;

/// <summary>
/// Holds what the startup reconciliation actually did, so it can be surfaced to the player rather
/// than only written to a log file nobody reads. Both <see cref="Result"/> and
/// <see cref="CatchUpFailures"/> are read by <see cref="Endpoints.MaintenanceEndpoints.AwaySummaryAsync"/>
/// and folded into the same "while you were away" dialog the ledger/schedule catch-up already uses -
/// there is deliberately no second notice mechanism for startup-time findings.
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
///
/// <para><b>Deliberately not persisted - do not "fix" this by adding a column or a migration.</b>
/// Both fields live only as long as the process does, so there is no server-side cursor to advance
/// the way <see cref="Entities.EconomyState.AwaySummaryLastViewedUtc"/> does for ledger activity.
/// Instead this class tracks its own "has this been shown" state in-process:
/// <see cref="GetUnacknowledged"/> reads what is still new, <see cref="Acknowledge"/> marks it seen.
/// This is correct, not a shortcut taken for lack of time: what this class describes is THIS
/// PROCESS's startup - the reconciliation pass and the two services' first catch-up attempt - and a
/// restart produces a fresh one of each. Losing acknowledgement state on restart is therefore not a
/// gap, it is the right behaviour: if the same problem is still there afterwards (a database that is
/// still unreadable, an aircraft that is still contradictorily reserved), reconciliation finds it
/// again and the player hears about it again, because it genuinely happened again on this boot. And
/// if it doesn't recur, there is nothing to persist in the first place. Persisting it would also mean
/// finding a home for what is not the player's data - it is a record of what the SERVER did, not
/// something like a route or a ledger line that belongs to their airline - so a migration here would
/// be solving a problem this class does not have.</para>
/// </summary>
public sealed class StartupReconciliationState
{
    private readonly object _sync = new();
    private readonly List<StartupCatchUpFailure> _catchUpFailures = new();
    private ReservationReconciliationResult? _result;
    private bool _reconciliationAcknowledged;
    private int _acknowledgedCatchUpFailureCount;

    public ReservationReconciliationResult? Result
    {
        get
        {
            lock (_sync)
            {
                return _result;
            }
        }
        set
        {
            lock (_sync)
            {
                _result = value;
                _reconciliationAcknowledged = false;
            }
        }
    }

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
            lock (_sync)
            {
                return _catchUpFailures.ToArray();
            }
        }
    }

    public void RecordCatchUpFailure(string service, Exception exception, DateTimeOffset occurredUtc)
    {
        lock (_sync)
        {
            _catchUpFailures.Add(new StartupCatchUpFailure(service, exception.GetType().Name, exception.Message, occurredUtc));
        }
    }

    /// <summary>
    /// What has happened since it was last <see cref="Acknowledge"/>d, scoped to one airline for
    /// the reconciliation side (reconciliation acts per-airline; a release or fallback-reservation
    /// belonging to a different airline is not this player's business). Catch-up failures are not
    /// airline-scoped - <see cref="EconomyClockService"/> and <see cref="VirtualFlightResolverService"/>
    /// process every airline in one pass, so a failure there is process-wide, not attributable to
    /// one airline; that is fine under the current one-airline-per-user model and will need
    /// revisiting if that model changes.
    /// </summary>
    public (ReservationReconciliationResult? Reconciliation, IReadOnlyList<StartupCatchUpFailure> CatchUpFailures) GetUnacknowledged(Guid airlineId)
    {
        lock (_sync)
        {
            var reconciliation = _reconciliationAcknowledged || _result is not { HasFindings: true }
                ? null
                : _result;

            if (reconciliation is not null &&
                !reconciliation.Released.Any(a => a.AirlineId == airlineId) &&
                !reconciliation.FallbackReserved.Any(a => a.AirlineId == airlineId) &&
                !reconciliation.AirlinesLeftWithNoReservedAircraft.Contains(airlineId))
            {
                reconciliation = null;
            }

            var failures = _acknowledgedCatchUpFailureCount >= _catchUpFailures.Count
                ? Array.Empty<StartupCatchUpFailure>()
                : _catchUpFailures.Skip(_acknowledgedCatchUpFailureCount).ToArray();

            return (reconciliation, failures);
        }
    }

    /// <summary>Marks whatever <see cref="GetUnacknowledged"/> would currently return as seen, so
    /// the next call reports nothing until something new happens. Called from the same place that
    /// advances the ledger-based away-summary's own cursor
    /// (<see cref="Endpoints.MaintenanceEndpoints.AcknowledgeAwaySummaryAsync"/>), so both move
    /// together from the player's point of view even though they are tracked independently here.</summary>
    public void Acknowledge()
    {
        lock (_sync)
        {
            _reconciliationAcknowledged = true;
            _acknowledgedCatchUpFailureCount = _catchUpFailures.Count;
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
