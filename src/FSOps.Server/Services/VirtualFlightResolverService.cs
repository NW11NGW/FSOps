using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Planning;
using FSOps.Core.Scheduling;
using FSOps.Core.Time;
using FSOps.Data;
using Microsoft.EntityFrameworkCore;
using Route = FSOps.Core.Entities.Route;

namespace FSOps.Server.Services;

/// <summary>
/// Resolves every virtual pilot's scheduled occurrence whose block time has elapsed on the real
/// wall clock, so a standing schedule keeps flying even while the app is closed.
/// A virtual flight is a full economic citizen: it goes through the exact same
/// <see cref="FlightEconomicsCalculator"/>/<see cref="FlightEconomicsPoster"/>/<see cref="MaintenancePoster"/>
/// pipeline a player flight does, so it earns ticket revenue, pays every itemised cost, advances
/// airframe hours, moves the aircraft, wears its condition, and can trigger maintenance exactly
/// like <c>FlightEndpoints.CompleteManualAsync</c> does for a player's manually-completed flight -
/// that path is this service's closest relative and is followed wherever the two overlap.
/// <b>Player flights are never touched here</b> - this service only ever reads
/// <see cref="PilotScheduleEntry"/> rows, which a player pilot can never own (see
/// PilotEndpoints.SaveScheduleAsync's IsPlayer guard).
/// <para>
/// <b>Reuses EconomyClockService's idempotency/monotonicity/capped-catch-up shape exactly</b>,
/// deliberately rather than inventing a second pattern for the same problem:
/// </para>
/// <list type="bullet">
/// <item><b>Idempotency is structural.</b> Each occurrence's Flight row (and whatever ledger lines
/// it produces) and the <see cref="EconomyState.LastScheduleResolvedUtc"/> watermark advancing past
/// that occurrence's arrival commit in the SAME <c>SaveChangesAsync</c> call, occurrence by
/// occurrence - so a crash mid-catch-up loses at most the not-yet-committed remainder, never posts
/// an occurrence twice and never silently drops one. (EconomyClockService does this once per
/// 30-day period; here the natural unit is one occurrence, since that's what "idempotent" has to
/// mean at this granularity - a monthly period is atomic, a whole pass of hundreds of flights is
/// not.) A <see cref="SemaphoreSlim"/> additionally serialises overlapping calls to
/// <see cref="RunOnceAsync"/> within this process, same as EconomyClockService.</item>
/// <item><b>Strictly monotonic.</b> <see cref="EconomyState.LastScheduleResolvedUtc"/> only ever
/// advances forward and never past <see cref="IClock.UtcNow"/>; a clock reading before it is
/// treated as a backwards jump - nothing processed, watermark unmoved, logged rather than absorbed.</item>
/// <item><b>Capped catch-up.</b> Bounded two ways per pass - at most <see cref="MaxOccurrencesPerPass"/>
/// occurrences actually resolved (directly bounds how many flights one pass can mint, which is the
/// thing that matters, since winding the system clock forward would otherwise be the single most
/// lucrative exploit in the design), and the enumeration window itself never
/// looks further ahead than <see cref="MaxLookaheadDays"/> - so neither a huge backlog of schedule
/// entries nor an enormous apparent clock jump can produce an unbounded burst in one pass; the
/// remainder continues on the next 60s tick.</item>
/// </list>
/// <para>
/// <b>Shares EconomyState with EconomyClockService rather than inventing a second singleton row</b> -
/// see <see cref="EconomyState.LastScheduleResolvedUtc"/>'s own doc. Only EconomyClockService ever
/// creates that row (its own first pass, anchored at "now"); if this service finds it missing, it
/// does nothing this pass rather than racing to create a second row - the next tick (at most 60s
/// later, and startup runs both services' first pass essentially immediately) finds it.
/// </para>
/// </summary>
public sealed class VirtualFlightResolverService : BackgroundService
{
    /// <summary>Upper bound on occurrences resolved in a single pass - see the class doc's capped
    /// catch-up paragraph. Generous enough that a realistic backlog (weeks of a full multi-pilot
    /// schedule) clears in one or two ticks, small enough that even a worst-case backlog resolves
    /// in a handful of minutes rather than one unbounded burst - same reasoning as
    /// EconomyClockService.MaxPeriodsPerPass, applied to flights instead of billing periods.</summary>
    private const int MaxOccurrencesPerPass = 500;

    /// <summary>How far ahead of the watermark a single pass will even look for due occurrences,
    /// regardless of how many entries exist - bounds enumeration cost independently of
    /// <see cref="MaxOccurrencesPerPass"/>, so a huge apparent clock jump - whether genuine or a
    /// system clock wound forward - can't force this pass to build an enormous in-memory occurrence list before
    /// even getting to the count cap. ~13 months comfortably covers any realistic "closed for a
    /// while" gap; a larger one simply takes a few extra ticks to fully catch up.</summary>
    private const int MaxLookaheadDays = 400;

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EconomyConfigCatalog _economyConfigCatalog;
    private readonly IClock _clock;
    private readonly ILogger<VirtualFlightResolverService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Where a failed pass is recorded so the UI can tell the player, rather than the failure
    /// living only in a log file. Optional so tests can construct this service directly without
    /// standing one up; in the running app DI always supplies it.
    /// </summary>
    private readonly StartupReconciliationState? _startupState;

    public VirtualFlightResolverService(
        IServiceScopeFactory scopeFactory,
        EconomyConfigCatalog economyConfigCatalog,
        IClock clock,
        ILogger<VirtualFlightResolverService> logger,
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
        // Deliberately identical failure handling to EconomyClockService, whose ExecuteAsync doc
        // explains it in full: this first pass runs before ExecuteAsync yields, so an exception here
        // propagates out of Host.StartAsync() and kills the process with nothing in the app's log.
        // Two wall-clock catch-up services with different startup failure behaviour would be worse
        // than the bug, so this reuses that shape exactly rather than inventing a second one - the
        // same rule the rest of this class already follows.
        await RunGuardedPassAsync(stoppingToken, isStartupPass: true);

        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunGuardedPassAsync(stoppingToken, isStartupPass: false);
        }
    }

    /// <summary>
    /// One pass with the failure policy applied - see EconomyClockService.RunGuardedPassAsync,
    /// which this mirrors exactly. Shutdown cancellation is rethrown so the timer loop ends
    /// normally; anything else is logged at Critical, recorded for the UI, and survived.
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
                "The virtual flight resolver's {When} pass failed. Flights your virtual pilots have already flown may not " +
                "have been resolved or paid; the next tick in {TickSeconds}s will retry. The app is still usable, but the " +
                "schedule and any figures derived from it may be behind until then.",
                when,
                TickInterval.TotalSeconds);

            _startupState?.RecordCatchUpFailure(nameof(VirtualFlightResolverService), ex, _clock.UtcNow);
        }
    }

    /// <summary>Runs one resolution pass. Internal so tests can drive it directly instead of
    /// waiting on the timer - same pattern as EconomyClockService.RunOnceAsync.</summary>
    internal async Task<VirtualFlightResolutionResult> RunOnceAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FsOpsDbContext>();

            var state = await db.EconomyStates.FirstOrDefaultAsync(ct);
            if (state is null)
            {
                return VirtualFlightResolutionResult.Empty(null);
            }

            var now = _clock.UtcNow;

            if (state.LastScheduleResolvedUtc is null)
            {
                state.LastScheduleResolvedUtc = now;
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("Virtual-flight resolution watermark initialised at {Now:o}.", now);
                return VirtualFlightResolutionResult.Empty(now);
            }

            var lastResolved = state.LastScheduleResolvedUtc.Value;

            if (now < lastResolved)
            {
                _logger.LogWarning(
                    "System clock appears to have moved backwards for virtual-flight resolution: last resolved {LastResolved:o}, observed {Now:o}. " +
                    "Ignoring this tick - nothing is resolved and the watermark never moves backwards.",
                    lastResolved, now);
                return new VirtualFlightResolutionResult(0, 0, 0, 0, 0, ClockMovedBackwards: true, MoreCatchUpRemaining: false, lastResolved);
            }

            var windowEnd = lastResolved.AddDays(MaxLookaheadDays);
            var cutoff = now < windowEnd ? now : windowEnd;
            if (cutoff <= lastResolved)
            {
                return new VirtualFlightResolutionResult(0, 0, 0, 0, 0, false, true, lastResolved);
            }

            var context = await LoadContextAsync(db, _economyConfigCatalog, ct);

            // Pilot skill idle decay. Runs every tick regardless of whether
            // anything is due for resolution this pass, so a pilot left with no schedule keeps
            // decaying purely from the passage of real time since they last flew - see
            // ApplyIdlePilotSkillDecay's own doc for why "now" (not any occurrence's timestamp) is
            // the correct clock for this. Saved immediately rather than waiting for the occurrence
            // loop below, since that loop does nothing at all on a tick with no pending occurrences.
            ApplyIdlePilotSkillDecay(context, now);
            await db.SaveChangesAsync(ct);

            var pending = BuildPendingOccurrences(context, lastResolved, cutoff);

            var toProcess = pending.Take(MaxOccurrencesPerPass).ToList();

            var completed = 0;
            var skipped = 0;
            var cancelled = 0;
            var suspended = 0;

            foreach (var occurrence in toProcess)
            {
                var outcome = await ResolveOccurrenceAsync(db, context, occurrence, now, ct);
                switch (outcome)
                {
                    case VirtualOccurrenceOutcome.Completed:
                        completed++;
                        break;
                    case VirtualOccurrenceOutcome.Skipped:
                        skipped++;
                        break;
                    case VirtualOccurrenceOutcome.Cancelled:
                        cancelled++;
                        break;
                    case VirtualOccurrenceOutcome.Suspended:
                        suspended++;
                        break;
                }

                // Idempotency is structural: this occurrence's Flight row/ledger lines and the
                // watermark advancing past its arrival commit together, occurrence by occurrence -
                // see the class doc.
                state.LastScheduleResolvedUtc = occurrence.Arrival;
                await db.SaveChangesAsync(ct);
            }

            if (toProcess.Count == pending.Count)
            {
                // Nothing left due in this window (either there was nothing pending at all, or
                // everything pending was processed) - safe to jump the watermark all the way to
                // cutoff rather than stopping at the last occurrence's arrival, so a quiet stretch
                // with few or no scheduled flights doesn't need its own pass per gap.
                state.LastScheduleResolvedUtc = cutoff;
                await db.SaveChangesAsync(ct);
            }

            var moreCatchUpRemaining = state.LastScheduleResolvedUtc < now;
            if (toProcess.Count > 0)
            {
                _logger.LogInformation(
                    "Virtual-flight resolution processed {Count} occurrence(s) ({Completed} completed, {Skipped} skipped, {Cancelled} cancelled, {Suspended} suspended); now caught up to {LastResolved:o}.{More}",
                    toProcess.Count, completed, skipped, cancelled, suspended, state.LastScheduleResolvedUtc,
                    moreCatchUpRemaining ? " More catch-up remains for the next pass." : string.Empty);
            }

            return new VirtualFlightResolutionResult(toProcess.Count, completed, skipped, cancelled, suspended, false, moreCatchUpRemaining, state.LastScheduleResolvedUtc);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<ResolutionContext> LoadContextAsync(FsOpsDbContext db, EconomyConfigCatalog catalog, CancellationToken ct)
    {
        var schedules = await db.PilotSchedules.ToListAsync(ct);
        var entries = await db.PilotScheduleEntries.ToListAsync(ct);
        var pilots = await db.Pilots.ToListAsync(ct);
        var airlines = await db.Airlines.ToListAsync(ct);
        var routes = await db.Routes.ToListAsync(ct);
        var fleet = await db.FleetAircraft.ToListAsync(ct);
        var aircraftTypes = await db.AircraftTypes.ToListAsync(ct);
        var airports = await db.Airports.ToListAsync(ct);
        var worldSeed = await FlightEconomicsPoster.ResolveWorldSeedAsync(db, ct);

        return new ResolutionContext(
            catalog,
            schedules.ToDictionary(s => s.Id),
            entries,
            pilots.ToDictionary(p => p.Id),
            airlines.ToDictionary(a => a.Id),
            routes.ToDictionary(r => r.Id),
            fleet.ToDictionary(f => f.Id),
            aircraftTypes.ToDictionary(t => t.Id),
            airports.ToDictionary(a => a.Icao),
            worldSeed);
    }

    /// <summary>
    /// Recomputes every non-player pilot's SkillRating fresh from (HoursFlown, LastFlewUtc, now) -
    /// see <see cref="PilotSkillCalculator"/>'s own doc. A full recompute, not an increment, so
    /// calling it every tick (even when nothing else in this pass has anything to do) can never
    /// move a pilot's skill "twice" - the same value in, the same value out, no matter how many
    /// times or how often this runs. <paramref name="now"/> is always the REAL current clock
    /// reading (this pass's own <c>_clock.UtcNow</c>), deliberately never an occurrence's own
    /// (possibly historical, mid-catch-up) timestamp - decay reflects how idle a pilot genuinely is
    /// right now, not how idle they were at some earlier point being caught up. Never touches a
    /// <see cref="Pilot.IsPlayer"/> pilot: the player's own record never decays. SkillRating is
    /// only ever consumed to SIMULATE a virtual flight, and the player's real flying is measured by
    /// real telemetry - decaying their number would punish them while changing nothing.
    /// A player's SkillRating is only ever touched by their own flights, via
    /// <see cref="MaintenancePoster.PostFlightHours"/>.
    /// </summary>
    private static void ApplyIdlePilotSkillDecay(ResolutionContext context, DateTimeOffset now)
    {
        foreach (var pilot in context.Pilots.Values)
        {
            if (pilot.IsPlayer)
            {
                continue;
            }

            if (!context.Airlines.TryGetValue(pilot.AirlineId, out var airline))
            {
                continue;
            }

            var config = context.EconomyConfig(airline.Playstyle).PilotSkill;
            pilot.SkillRating = PilotSkillCalculator.Compute(pilot.HoursFlown, pilot.LastFlewUtc, now, config);
        }
    }

    /// <summary>
    /// Every occurrence due in <c>(lastResolved, cutoff]</c> - "due" meaning its ARRIVAL (departure
    /// + this leg's own block time), not its departure, falls in that window, matching "whose block
    /// time has elapsed". Sorted by arrival ascending so aircraft/pilot state is
    /// resolved in the same order it would actually happen in, which is what makes each occurrence's
    /// flyability check (aircraft must be where THIS leg departs from) correct without needing to
    /// re-simulate the whole week from scratch. Entries whose route/aircraft/type can no longer be
    /// resolved (a dangling reference - route or aircraft deleted after being scheduled) are silently
    /// excluded rather than crashing the pass; there's nothing sane to charge or record for a leg
    /// whose shape genuinely can't be determined, and the schedule builder is the place that should
    /// have caught this before it was ever saved.
    /// </summary>
    private static List<PendingOccurrence> BuildPendingOccurrences(ResolutionContext context, DateTimeOffset lastResolved, DateTimeOffset cutoff)
    {
        var pending = new List<PendingOccurrence>();

        foreach (var entry in context.Entries)
        {
            if (!context.Schedules.TryGetValue(entry.PilotScheduleId, out var schedule))
            {
                continue;
            }

            if (!context.Pilots.TryGetValue(schedule.PilotId, out var pilot) || pilot.IsPlayer)
            {
                continue;
            }

            if (!context.Routes.TryGetValue(entry.RouteId, out var route) ||
                !context.Fleet.TryGetValue(entry.FleetAircraftId, out var aircraft) ||
                !context.AircraftTypes.TryGetValue(aircraft.AircraftTypeId, out var aircraftType) ||
                !context.Airlines.TryGetValue(schedule.AirlineId, out var airline) ||
                !context.Airports.TryGetValue(route.DepartureIcao, out var departureAirport) ||
                !context.Airports.TryGetValue(route.ArrivalIcao, out var arrivalAirport))
            {
                continue;
            }

            var economyConfig = context.EconomyConfig(airline.Playstyle);
            var plan = RoutePreviewCalculator.Calculate(economyConfig, departureAirport, arrivalAirport, aircraftType, airline.StrategyProfile);
            var blockMinutes = plan.BlockTimeBreakdown.TotalMinutes;
            if (blockMinutes <= 0)
            {
                continue;
            }

            var blockSpan = TimeSpan.FromMinutes(blockMinutes);
            var departures = WeeklyOccurrenceCalculator.OccurrencesBetween(
                entry.DayOfWeek, entry.DepartureTimeUtc, lastResolved - blockSpan, cutoff - blockSpan);

            foreach (var departure in departures)
            {
                pending.Add(new PendingOccurrence(entry, schedule, pilot, route, aircraft, aircraftType, airline, departureAirport, arrivalAirport, departure, departure + blockSpan, blockMinutes));
            }
        }

        pending.Sort((a, b) => a.Arrival.CompareTo(b.Arrival));
        return pending;
    }

    /// <summary>
    /// Resolves exactly one occurrence: decides flyability, then either flies it as a full economic
    /// citizen (same FlightEconomicsCalculator/Poster/MaintenancePoster pipeline as a player's
    /// manually-completed flight) or records it as Skipped/Cancelled per the airline's playstyle -
    /// Casual skips it quietly and unpenalised, True-life cancels it with a real charge. Never touches aircraft position for an
    /// unflyable occurrence - the aircraft stays exactly where it already was.
    /// </summary>
    private static async Task<VirtualOccurrenceOutcome> ResolveOccurrenceAsync(
        FsOpsDbContext db, ResolutionContext context, PendingOccurrence occurrence, DateTimeOffset resolvedAtUtc, CancellationToken ct)
    {
        var aircraft = occurrence.Aircraft;
        var route = occurrence.Route;
        var economyConfig = context.EconomyConfig(occurrence.Airline.Playstyle);

        string? unflyableReason = null;

        // A grounding whose downtime elapsed before this occurrence's departure is released first -
        // same lazy-release principle as MaintenanceReleaser, applied at the occurrence's own
        // departure time (which may be in the past relative to "now") rather than at "now" itself,
        // since resolution walks the timeline in order.
        if (aircraft.Status == FleetAircraftStatus.InMaintenance && aircraft.GroundedUntilUtc is { } groundedUntil && groundedUntil <= occurrence.Departure)
        {
            aircraft.Status = FleetAircraftStatus.Active;
            aircraft.GroundedUntilUtc = null;
        }

        if (aircraft.Status == FleetAircraftStatus.InMaintenance)
        {
            var reason = aircraft.GroundedUntilUtc is { } until
                ? $"{aircraft.Registration} is in maintenance until {until:yyyy-MM-dd HH:mm} UTC."
                : $"{aircraft.Registration} is in maintenance.";

            // Scoped to exactly this case - a schedule the player built badly (wrong location,
            // aircraft already flying) still skips/cancels normally below; only maintenance the
            // simulation imposed is eligible for suspension. See PilotSchedule.AutoSuspendOnMaintenance's
            // own doc. Without this, a 14-day True-life C-check against a daily schedule would be
            // fourteen cancellation fees for something the player could not have avoided.
            if (occurrence.Schedule.AutoSuspendOnMaintenance)
            {
                return await ResolveSuspendedAsync(db, occurrence, reason, resolvedAtUtc, ct);
            }

            unflyableReason = reason;
        }
        else if (aircraft.Status == FleetAircraftStatus.InFlight)
        {
            unflyableReason = $"{aircraft.Registration} is currently in flight.";
        }
        else if (!string.Equals(aircraft.LocationIcao, route.DepartureIcao, StringComparison.OrdinalIgnoreCase))
        {
            unflyableReason = $"{aircraft.Registration} is still at {aircraft.LocationIcao} from an earlier leg - this leg needs it at {route.DepartureIcao}.";
        }

        if (unflyableReason is not null)
        {
            return await ResolveUnflyableAsync(db, occurrence, economyConfig, unflyableReason, resolvedAtUtc, ct);
        }

        return await ResolveFlownAsync(db, occurrence, economyConfig, context.WorldSeed, resolvedAtUtc, ct);
    }

    /// <summary>
    /// Records a maintenance-suspended occurrence: same shape as <see cref="ResolveUnflyableAsync"/>'s
    /// Skipped case (a Flight row for history, no ledger line at all) but tagged
    /// <see cref="FlightStatus.Suspended"/> rather than Skipped or Cancelled, so the UI can tell
    /// "the schedule paused itself for a check" apart from "this occurrence genuinely couldn't fly".
    /// No fee under any playstyle - see PilotSchedule.AutoSuspendOnMaintenance's doc for why a
    /// cancellation fee would be the wrong signal here. Resumption needs no code of its own: the
    /// very next occurrence for this schedule simply re-evaluates the aircraft's status again, and
    /// once <see cref="MaintenanceReleaser"/> (or this service's own lazy release a few lines up)
    /// has cleared the grounding, it resolves normally.
    /// </summary>
    private static async Task<VirtualOccurrenceOutcome> ResolveSuspendedAsync(
        FsOpsDbContext db, PendingOccurrence occurrence, string reason, DateTimeOffset resolvedAtUtc, CancellationToken ct)
    {
        var flight = new Flight
        {
            Id = Guid.NewGuid(),
            AirlineId = occurrence.Airline.Id,
            RouteId = occurrence.Route.Id,
            ScheduleId = occurrence.Entry.Id,
            FleetAircraftId = occurrence.Aircraft.Id,
            PilotId = occurrence.Pilot.Id,
            Status = FlightStatus.Suspended,
            PlannedDepartureUtc = occurrence.Departure,
            PlannedBlockMinutes = occurrence.BlockMinutes,
            PaxBooked = 0,
            PaxFlown = 0,
            TitleFlown = string.Empty,
            Revenue = 0m,
            TotalCost = 0m,
            RevenuePosted = true,
            UnflyableReason = $"Schedule suspended - {reason}",
            CreatedUtc = resolvedAtUtc,
        };

        db.Flights.Add(flight);

        await Task.CompletedTask;
        return VirtualOccurrenceOutcome.Suspended;
    }

    private static async Task<VirtualOccurrenceOutcome> ResolveUnflyableAsync(
        FsOpsDbContext db, PendingOccurrence occurrence, EconomyConfig economyConfig, string reason, DateTimeOffset resolvedAtUtc, CancellationToken ct)
    {
        var fee = economyConfig.UnflyableSchedule.CancellationFee;
        var status = fee > 0 ? FlightStatus.Cancelled : FlightStatus.Skipped;

        // Reputation: cancelled and skipped sectors count as one and the same thing to a
        // passenger, so both playstyles' outcomes hit reputation identically here. A schedule that
        // cannot fly costs standing whichever playstyle you chose; only the ledger line (below)
        // differs by playstyle. Never reached for a maintenance suspension - see ResolveSuspendedAsync,
        // which is a structurally separate method that never calls this one.
        ReputationPoster.PostCancelledOrSkipped(occurrence.Airline, economyConfig);

        var flight = new Flight
        {
            Id = Guid.NewGuid(),
            AirlineId = occurrence.Airline.Id,
            RouteId = occurrence.Route.Id,
            ScheduleId = occurrence.Entry.Id,
            FleetAircraftId = occurrence.Aircraft.Id,
            PilotId = occurrence.Pilot.Id,
            Status = status,
            PlannedDepartureUtc = occurrence.Departure,
            PlannedBlockMinutes = occurrence.BlockMinutes,
            PaxBooked = 0,
            PaxFlown = 0,
            TitleFlown = string.Empty,
            Revenue = 0m,
            TotalCost = fee,
            RevenuePosted = true,
            UnflyableReason = reason,
            CreatedUtc = resolvedAtUtc,
        };

        db.Flights.Add(flight);

        if (fee > 0)
        {
            db.LedgerTransactions.Add(new LedgerTransaction
            {
                Id = Guid.NewGuid(),
                AirlineId = occurrence.Airline.Id,
                Utc = occurrence.Arrival,
                Category = LedgerCategory.CancellationFee,
                Amount = -fee,
                FlightId = flight.Id,
                Description = $"Flight cancelled - {reason}",
            });
        }

        await Task.CompletedTask;
        return status == FlightStatus.Cancelled ? VirtualOccurrenceOutcome.Cancelled : VirtualOccurrenceOutcome.Skipped;
    }

    private static async Task<VirtualOccurrenceOutcome> ResolveFlownAsync(
        FsOpsDbContext db, PendingOccurrence occurrence, EconomyConfig economyConfig, int worldSeed, DateTimeOffset resolvedAtUtc, CancellationToken ct)
    {
        var aircraft = occurrence.Aircraft;
        var route = occurrence.Route;
        var airline = occurrence.Airline;

        // Landing quality and delay variance come from the pilot's skill via a seeded PRNG -
        // deterministic (same seed/entry/occurrence always resolves identically) and, critically,
        // never feeding revenue - only cost, through the extra flightHours below. See
        // VirtualPilotPerformanceCalculator's own doc for why this is what makes a virtual pilot
        // earn less per sector than an engaged player on average without ever breaking the "revenue
        // comes from passengers, never from the clock" integrity rule.
        var performance = VirtualPilotPerformanceCalculator.Resolve(worldSeed, occurrence.Entry.Id, occurrence.Departure, occurrence.Pilot.SkillRating);
        var flightHours = (occurrence.BlockMinutes + performance.DelayMinutes) / 60.0;
        var actualArrival = occurrence.Departure.AddMinutes(occurrence.BlockMinutes + performance.DelayMinutes);

        // Reputation. A virtual flight always has both signals (unlike a
        // player's manual completion), so this always blends on-time + landing, never falls back to
        // one alone - see ReputationCalculator.AdvanceForCompletedFlight.
        ReputationPoster.PostCompletedFlight(airline, economyConfig, performance.DelayMinutes, performance.LandingFpm);

        var flight = new Flight
        {
            Id = Guid.NewGuid(),
            AirlineId = airline.Id,
            RouteId = route.Id,
            ScheduleId = occurrence.Entry.Id,
            FleetAircraftId = aircraft.Id,
            PilotId = occurrence.Pilot.Id,
            Status = FlightStatus.Completed,
            PlannedDepartureUtc = occurrence.Departure,
            PlannedBlockMinutes = occurrence.BlockMinutes,
            OutUtc = occurrence.Departure,
            OffUtc = occurrence.Departure,
            OnUtc = actualArrival,
            InUtc = actualArrival,
            PaxBooked = occurrence.AircraftType.PaxCapacity,
            LandingFpmFirst = performance.LandingFpm,
            LandingFpmHardest = performance.LandingFpm,
            LandingGForce = performance.GForce,
            // No aircraft-type mismatch check applies to a virtual pilot - there is no sim to check
            // against. Null (unknown), not false: false means "checked and
            // matched", and nothing was ever checked here - no sim was ever loaded for this flight.
            TitleFlown = occurrence.AircraftType.Name,
            TypeMismatch = null,
            Revenue = 0m,
            TotalCost = 0m,
            CreatedUtc = resolvedAtUtc,
        };

        db.Flights.Add(flight);

        // Fuel: a virtual pilot has no telemetry, so its burn is the sector's own planned figure -
        // billed outright at the departure airport's price, same as any other no-telemetry path in
        // this app (see FuelBreakdown.ChargedFuelKg's own doc).
        var plan = RoutePreviewCalculator.Calculate(economyConfig, occurrence.DepartureAirport, occurrence.ArrivalAirport, occurrence.AircraftType, airline.StrategyProfile);
        flight.FuelUsedKg = plan.FuelBreakdown.ChargedFuelKg;
        var fuelCost = FlightEconomicsPoster.PostFuelBurn(db, flight, economyConfig, occurrence.DepartureAirport, flight.FuelUsedKg, occurrence.Departure, worldSeed);
        flight.TotalCost = fuelCost;

        await FlightEconomicsPoster.PostCompletionAsync(
            db, flight, airline, route, occurrence.AircraftType, occurrence.ArrivalAirport, economyConfig, flightHours, actualArrival, ct);

        // Full economic citizen: advances airframe hours, wears condition, and can trigger
        // maintenance exactly like a player's flight - see MaintenancePoster. A check triggered here
        // grounds the aircraft, which the NEXT occurrence's flyability check picks up naturally.
        MaintenancePoster.PostFlightHours(db, aircraft, occurrence.Pilot, airline, economyConfig, flightHours, actualArrival);

        // No telemetry means no reliable in-flight fuel reading - informational only now (see
        // FleetAircraft.FuelOnBoardKg's own doc), so this stays the same conservative "treat as
        // consumed" convention FlightEndpoints.CompleteManualAsync uses for its own no-telemetry
        // path, rather than guessing at a persisted remainder.
        aircraft.FuelOnBoardKg = 0;
        aircraft.LocationIcao = route.ArrivalIcao;
        if (aircraft.Status == FleetAircraftStatus.Active)
        {
            // Left untouched if MaintenancePoster just set it to InMaintenance above.
            aircraft.Status = FleetAircraftStatus.Active;
        }

        return VirtualOccurrenceOutcome.Completed;
    }

    private sealed record ResolutionContext(
        EconomyConfigCatalog Catalog,
        IReadOnlyDictionary<Guid, PilotSchedule> Schedules,
        IReadOnlyList<PilotScheduleEntry> Entries,
        IReadOnlyDictionary<Guid, Pilot> Pilots,
        IReadOnlyDictionary<Guid, Airline> Airlines,
        IReadOnlyDictionary<Guid, Route> Routes,
        IReadOnlyDictionary<Guid, FleetAircraft> Fleet,
        IReadOnlyDictionary<Guid, AircraftType> AircraftTypes,
        IReadOnlyDictionary<string, Airport> Airports,
        int WorldSeed)
    {
        /// <summary>Every config figure is resolved per-airline from the catalog, never from a
        /// flat/shared EconomyConfig - same rule as every other service in this project (see
        /// EconomyClockService's constructor doc).</summary>
        public EconomyConfig EconomyConfig(AirlinePlaystyle playstyle) => Catalog.Get(playstyle);
    }

    private sealed record PendingOccurrence(
        PilotScheduleEntry Entry,
        PilotSchedule Schedule,
        Pilot Pilot,
        Route Route,
        FleetAircraft Aircraft,
        AircraftType AircraftType,
        Airline Airline,
        Airport DepartureAirport,
        Airport ArrivalAirport,
        DateTimeOffset Departure,
        DateTimeOffset Arrival,
        int BlockMinutes);

    private enum VirtualOccurrenceOutcome
    {
        Completed,
        Skipped,
        Cancelled,
        Suspended,
    }
}

/// <summary>Result of one <see cref="VirtualFlightResolverService.RunOnceAsync"/> pass - exposed
/// purely so tests can assert on it directly, same as EconomyClockService's EconomyCyclePassResult.</summary>
public sealed record VirtualFlightResolutionResult(
    int OccurrencesProcessed,
    int FlightsCompleted,
    int FlightsSkipped,
    int FlightsCancelled,
    int FlightsSuspended,
    bool ClockMovedBackwards,
    bool MoreCatchUpRemaining,
    DateTimeOffset? LastScheduleResolvedUtc)
{
    public static VirtualFlightResolutionResult Empty(DateTimeOffset? lastScheduleResolvedUtc) =>
        new(0, 0, 0, 0, 0, false, false, lastScheduleResolvedUtc);
}
