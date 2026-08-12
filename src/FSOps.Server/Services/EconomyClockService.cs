using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Finance;
using FSOps.Core.Time;
using FSOps.Data;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Services;

/// <summary>
/// Posts the monthly fixed costs that Chunk D's balance rebalance assumed but nothing ever
/// charged: aircraft lease payments, pilot salaries, and insurance per aircraft (see
/// without these lines the balance is fiction, since the monthly pressure the whole economy is
/// tuned around would be validated in tests but never applied in play). Also amortises every outstanding <see cref="Loan"/> - the
/// founding loan Chunk B could create at airline creation, and any mid-game loan FleetEndpoints
/// now offers, are both repaid monthly right here (see <see cref="LoanCalculator.ApplyMonthlyPayment"/>);
/// this was the one piece of the loan feature that never actually existed before E1. Runs once at
/// startup (catching up on however long the app was closed) and every <see cref="TickInterval"/>
/// after that.
/// <para>
/// <b>A "month" is exactly <see cref="PeriodLength"/> (30 days) of wall-clock time from
/// <see cref="EconomyState.LastProcessedUtc"/></b>, not a calendar month - this matches how the
/// balance tests already express monthly figures (30 x the daily sector rate,
/// see StartupTrajectoryTests) and avoids calendar arithmetic entirely.
/// </para>
/// <para>
/// <b>Idempotency is structural, not best-effort.</b> Each due period is posted and
/// <see cref="EconomyState.LastProcessedUtc"/> is advanced past it in the same
/// <c>SaveChangesAsync</c> call - the ledger rows for a period and the watermark that says "this
/// period is done" either land together or neither lands at all. A crash between periods loses at
/// most the not-yet-committed remainder of a catch-up pass, never a half-charged month and never a
/// re-charged one. A <see cref="SemaphoreSlim"/> additionally serialises overlapping calls to
/// <see cref="RunOnceAsync"/> within this process (the only place two "ticks" could ever actually
/// race - the app runs a single instance against a local SQLite file), so the atomic-commit
/// property above is the only thing that has to hold to guarantee no double charge, not a hope
/// that timing happens to work out.
/// </para>
/// <para>
/// <b>Wall-clock integrity.</b> Winding the system clock forward would otherwise be the single
/// most lucrative exploit in the design, and the easiest to perform.
/// <see cref="EconomyState.LastProcessedUtc"/> only ever advances forward, in whole
/// <see cref="PeriodLength"/> steps, and never past <see cref="IClock.UtcNow"/> - so winding the
/// clock forward cannot mint more than <see cref="MaxPeriodsPerPass"/> months of charges in one
/// pass, and a clock that reports a time before the watermark is treated as backwards: nothing is
/// processed, the watermark does not move, and it is logged rather than silently absorbed.
/// </para>
/// </summary>
public sealed class EconomyClockService : BackgroundService
{
    /// <summary>Length of one billing period. 30 days, not a calendar month - see the class doc.</summary>
    public static readonly TimeSpan PeriodLength = TimeSpan.FromDays(30);

    /// <summary>
    /// Upper bound on how many periods a single <see cref="RunOnceAsync"/> pass will post. An
    /// enormous apparent jump (a wound-forward system clock, or the app having been closed for
    /// years) processes forward at this bounded rate - one pass, then the next tick continues -
    /// rather than minting an unbounded run of months in one go. 24 (two years) comfortably covers
    /// any realistic "closed for a while" gap in a single pass while still capping the worst case;
    /// remaining catch-up simply continues on the next 60s tick, which for even a very large jump
    /// resolves in a handful of minutes rather than one unbounded burst.
    /// </summary>
    private const int MaxPeriodsPerPass = 24;

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EconomyConfigCatalog _economyConfigCatalog;
    private readonly IClock _clock;
    private readonly ILogger<EconomyClockService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Where a failed pass is recorded so the UI can tell the player, rather than the failure
    /// living only in a log file. Optional so tests can construct this service directly without
    /// standing one up; in the running app DI always supplies it.
    /// </summary>
    private readonly StartupReconciliationState? _startupState;

    // Every config-derived charge is resolved per-airline from this catalog (see
    // PostMonthlyChargesAsync) rather than from a single flat EconomyConfig - insurance differs by
    // AirlinePlaystyle (Casual 6,000 vs True-life 50,000), so a flat config would silently bill
    // every True-life airline the Casual rate.
    // Lease and salary amounts are unaffected by this - both are read from the stored Lease.MonthlyRate
    // and Pilot.MonthlySalary rows, which were already set per-playstyle at creation time.
    public EconomyClockService(
        IServiceScopeFactory scopeFactory,
        EconomyConfigCatalog economyConfigCatalog,
        IClock clock,
        ILogger<EconomyClockService> logger,
        StartupReconciliationState? startupState = null)
    {
        _scopeFactory = scopeFactory;
        _economyConfigCatalog = economyConfigCatalog;
        _clock = clock;
        _logger = logger;
        _startupState = startupState;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run once immediately so a startup catch-up (the app having been closed for days) happens
        // before the first 60s tick, not up to a minute after.
        //
        // This first pass MUST NOT be allowed to throw. It runs before ExecuteAsync has yielded, so
        // BackgroundService.StartAsync hands the host an already-faulted task and the exception
        // propagates straight out of Host.StartAsync() -> Run() -> Main. That path never reaches
        // BackgroundServiceExceptionBehavior, so the host's own "BackgroundService failed" critical
        // log never runs either: the process simply died with nothing in the app's log. That was
        // observed for real - a malformed database took the whole process down from this exact
        // line, leaving only EF's routine "Failed executing DbCommand" behind.
        //
        // Killing the app is also the wrong answer on its own terms. FSOps is perfectly usable with
        // the catch-up not yet done - the airline, fleet and history are all still readable - and
        // the timer below retries a minute later. A transient database hiccup must not cost the
        // player the entire application. So: log loudly, record it where the UI can see it, carry
        // on. See StartupReconciliationState.CatchUpFailures for why silence is not an option
        // either - a failed pass means money movements are missing.
        await RunGuardedPassAsync(stoppingToken, isStartupPass: true);

        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunGuardedPassAsync(stoppingToken, isStartupPass: false);
        }
    }

    /// <summary>
    /// One pass with the failure policy applied. A pass that throws is logged at Critical, recorded
    /// for the UI, and swallowed so the service survives to try again on the next tick. Shutdown
    /// cancellation is not a failure and is rethrown so the timer loop ends normally.
    /// </summary>
    private async Task RunGuardedPassAsync(CancellationToken stoppingToken, bool isStartupPass)
    {
        try
        {
            await RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var when = isStartupPass ? "startup catch-up" : "scheduled";
            _logger.LogCritical(
                ex,
                "The economy clock's {When} pass failed. Monthly lease, salary and insurance charges may not have been " +
                "posted; the next tick in {TickSeconds}s will retry. The app is still usable, but any cash balance shown " +
                "until then may be missing charges.",
                when,
                TickInterval.TotalSeconds);

            _startupState?.RecordCatchUpFailure(nameof(EconomyClockService), ex, _clock.UtcNow);
        }
    }

    /// <summary>
    /// Runs one resolution pass. Internal (not private) so tests can drive it directly instead of
    /// waiting on the timer - the same pattern as <see cref="FlightLifecycleService.FinalizeFlightAsync"/>.
    /// Creates the <see cref="EconomyState"/> singleton row if this is the very first pass anywhere
    /// in the app's life (nothing else seeds it - see <c>FlightEconomicsPoster.ResolveWorldSeedAsync</c>),
    /// anchored at "now" so a fresh install never back-charges for time before the app existed.
    /// </summary>
    internal async Task<EconomyCyclePassResult> RunOnceAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FsOpsDbContext>();

            var state = await db.EconomyStates.FirstOrDefaultAsync(ct);
            var now = _clock.UtcNow;

            if (state is null)
            {
                state = new EconomyState
                {
                    Id = Guid.NewGuid(),
                    LastProcessedUtc = now,
                    WorldSeed = Random.Shared.Next(1, int.MaxValue),
                    FuelPricePerKg = 0m,
                };
                db.EconomyStates.Add(state);
                await db.SaveChangesAsync(ct);

                _logger.LogInformation("Economy world state initialised; the monthly cycle starts counting from {Now:o}.", now);
                return new EconomyCyclePassResult(PeriodsPosted: 0, ClockMovedBackwards: false, MoreCatchUpRemaining: false, LastProcessedUtc: now);
            }

            // Strictly monotonic: a clock reading before the watermark is a backwards jump, not a
            // no-op tick. Recorded rather than silently absorbed; the watermark never moves.
            if (now < state.LastProcessedUtc)
            {
                _logger.LogWarning(
                    "System clock appears to have moved backwards: last processed {LastProcessed:o}, observed {Now:o}. " +
                    "Ignoring this tick - the economy clock never moves backwards and nothing is charged for it.",
                    state.LastProcessedUtc, now);
                return new EconomyCyclePassResult(PeriodsPosted: 0, ClockMovedBackwards: true, MoreCatchUpRemaining: false, LastProcessedUtc: state.LastProcessedUtc);
            }

            var periodsPosted = 0;
            while (periodsPosted < MaxPeriodsPerPass)
            {
                var periodEnd = state.LastProcessedUtc + PeriodLength;
                if (periodEnd > now)
                {
                    break;
                }

                await PostMonthlyChargesAsync(db, periodEnd, ct);

                // The watermark advance and the ledger rows it accounts for are committed in the
                // same SaveChangesAsync call - see the class doc's idempotency paragraph.
                state.LastProcessedUtc = periodEnd;
                await db.SaveChangesAsync(ct);
                periodsPosted++;
            }

            var moreCatchUpRemaining = state.LastProcessedUtc + PeriodLength <= now;
            if (periodsPosted > 0)
            {
                _logger.LogInformation(
                    "Economy monthly cycle posted {PeriodsPosted} period(s); now caught up to {LastProcessed:o}.{More}",
                    periodsPosted, state.LastProcessedUtc, moreCatchUpRemaining ? " More catch-up remains for the next pass." : string.Empty);
            }

            return new EconomyCyclePassResult(periodsPosted, ClockMovedBackwards: false, moreCatchUpRemaining, state.LastProcessedUtc);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Posts every fixed monthly line due for the period ending at <paramref name="periodEnd"/>:
    /// one lease payment per active lease, one salary line per pilot, one insurance line per fleet
    /// aircraft - itemised separately so the ledger names exactly what and which aircraft/pilot,
    /// matching how every other cost in the app is posted (see FlightEconomicsPoster). Only rows
    /// that existed by the period boundary are charged, and only for airlines that still exist -
    /// a soft-deleted airline's leftover lease rows (cascade cleanup is not this service's job to
    /// fix, but it must never keep billing a deleted airline) are excluded defensively.
    /// <para>
    /// <b>Every config-derived figure is resolved from the owning airline's own
    /// <see cref="Airline.Playstyle"/></b>, never from one shared config - see the constructor's
    /// doc comment. Lease and salary amounts don't need this (they're read straight off the stored
    /// <see cref="Lease.MonthlyRate"/>/<see cref="Pilot.MonthlySalary"/> rows, already correct for
    /// whichever playstyle the airline was created under), but insurance is a live lookup against
    /// <see cref="EconomyConfigCatalog"/> every period, so it always reflects that airline's rate.
    /// </para>
    /// </summary>
    private async Task PostMonthlyChargesAsync(FsOpsDbContext db, DateTimeOffset periodEnd, CancellationToken ct)
    {
        var activeAirlines = await db.Airlines.Where(a => a.DeletedUtc == null).ToListAsync(ct);
        if (activeAirlines.Count == 0)
        {
            return;
        }

        var airlineById = activeAirlines.ToDictionary(a => a.Id);

        // SQLite's EF provider can't translate a DateTimeOffset comparison inside this Where (same
        // pitfall as OrderBy over DateTimeOffset - see the project convention on this), so the
        // IsActive/DeletedUtc filter runs in SQL and the periodEnd cutoff is applied in memory
        // after materialising.
        var leases = (await db.Leases
                .Where(l => l.IsActive && l.DeletedUtc == null)
                .ToListAsync(ct))
            .Where(l => l.StartUtc <= periodEnd)
            .ToList();
        var fleetAircraft = (await db.FleetAircraft
                .Where(f => f.DeletedUtc == null)
                .ToListAsync(ct))
            .Where(f => f.CreatedUtc <= periodEnd)
            .ToList();
        var pilots = (await db.Pilots
                .Where(p => p.DeletedUtc == null)
                .ToListAsync(ct))
            .Where(p => p.CreatedUtc <= periodEnd)
            .ToList();

        var registrationByFleetAircraftId = fleetAircraft.ToDictionary(f => f.Id, f => f.Registration);

        foreach (var lease in leases)
        {
            if (!airlineById.ContainsKey(lease.AirlineId) || lease.MonthlyRate == 0m)
            {
                continue;
            }

            var aircraftLabel = registrationByFleetAircraftId.GetValueOrDefault(lease.FleetAircraftId, lease.FleetAircraftId.ToString());
            db.LedgerTransactions.Add(new LedgerTransaction
            {
                Id = Guid.NewGuid(),
                AirlineId = lease.AirlineId,
                Utc = periodEnd,
                Category = LedgerCategory.LeasePayment,
                Amount = -lease.MonthlyRate,
                Description = $"Monthly lease payment: {aircraftLabel}",
            });
        }

        foreach (var aircraft in fleetAircraft)
        {
            if (!airlineById.TryGetValue(aircraft.AirlineId, out var airline))
            {
                continue;
            }

            var insurancePerAircraft = _economyConfigCatalog.Get(airline.Playstyle).FleetFinance.MonthlyInsurancePerAircraft;
            if (insurancePerAircraft == 0m)
            {
                continue;
            }

            db.LedgerTransactions.Add(new LedgerTransaction
            {
                Id = Guid.NewGuid(),
                AirlineId = aircraft.AirlineId,
                Utc = periodEnd,
                Category = LedgerCategory.Insurance,
                Amount = -insurancePerAircraft,
                Description = $"Monthly insurance: {aircraft.Registration}",
            });
        }

        foreach (var pilot in pilots)
        {
            if (!airlineById.ContainsKey(pilot.AirlineId) || pilot.MonthlySalary == 0m)
            {
                continue;
            }

            db.LedgerTransactions.Add(new LedgerTransaction
            {
                Id = Guid.NewGuid(),
                AirlineId = pilot.AirlineId,
                Utc = periodEnd,
                Category = LedgerCategory.Salary,
                Amount = -pilot.MonthlySalary,
                Description = $"Monthly salary: {pilot.Name}",
            });
        }

        // Every outstanding loan (the founding loan from airline creation, or a mid-game one from
        // FleetEndpoints) amortises here - see LoanCalculator.ApplyMonthlyPayment for the
        // interest/principal split. Materialised first, same DateTimeOffset/SQLite reason as the
        // leases/fleet/pilots above.
        var loans = (await db.Loans
                .Where(l => !l.IsPaidOff && l.DeletedUtc == null)
                .ToListAsync(ct))
            .Where(l => l.StartUtc <= periodEnd)
            .ToList();

        foreach (var loan in loans)
        {
            if (!airlineById.ContainsKey(loan.AirlineId))
            {
                continue;
            }

            var breakdown = LoanCalculator.ApplyMonthlyPayment(loan.RemainingBalance, loan.AnnualInterestRate, loan.MonthlyPayment);
            loan.RemainingBalance = breakdown.NewRemainingBalance;
            loan.IsPaidOff = breakdown.IsPaidOff;

            if (breakdown.ActualPayment == 0m)
            {
                continue;
            }

            db.LedgerTransactions.Add(new LedgerTransaction
            {
                Id = Guid.NewGuid(),
                AirlineId = loan.AirlineId,
                Utc = periodEnd,
                Category = LedgerCategory.LoanPayment,
                Amount = -breakdown.ActualPayment,
                Description = breakdown.IsPaidOff
                    ? "Final loan payment - loan paid off"
                    : $"Monthly loan payment (balance {loan.RemainingBalance:F2})",
            });
        }
    }
}

/// <summary>Result of one <see cref="EconomyClockService.RunOnceAsync"/> pass - exposed purely so
/// tests can assert on it directly rather than re-deriving the same facts from the ledger.</summary>
public sealed record EconomyCyclePassResult(
    int PeriodsPosted,
    bool ClockMovedBackwards,
    bool MoreCatchUpRemaining,
    DateTimeOffset LastProcessedUtc);
