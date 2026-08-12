using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Time;
using FSOps.Data;
using FSOps.Server.Auth;
using FSOps.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Endpoints;

/// <summary>
/// Maintenance controls beyond the automatic accrual/grounding <see cref="MaintenancePoster"/>
/// already does at flight completion:
/// <list type="bullet">
/// <item><b>Perform maintenance now</b> - bring an A- or C-check forward at the player's own
/// choosing, at full cost, forfeiting whatever hours remained on the current cycle. Quote-then-
/// confirm, same pattern as <see cref="FleetDisposalEndpoints"/>: the quote is recomputed at
/// commit time and compared against what the player was shown, and the aircraft's live status
/// (airborne, already grounded) is rechecked rather than trusted from the quote - the world keeps
/// moving between a dialog opening and the player clicking confirm.</item>
/// <item><b>"While you were away"</b> - a catch-up summary of everything the wall clock resolved
/// since the player last acknowledged one: charges by category, what virtual pilots flew and
/// earned, maintenance that fell due, and anything skipped/cancelled/suspended and why. See
/// <see cref="EconomyState.AwaySummaryLastViewedUtc"/>'s own doc for why this is a separate cursor
/// from the billing/schedule watermarks those services own.</item>
/// </list>
/// <b>Maintenance never interrupts a flight in progress, and this file does not change that.</b>
/// <see cref="MaintenancePoster.PostFlightHours"/> is only ever called at flight completion (see
/// FlightLifecycleService, FlightEndpoints, VirtualFlightResolverService) - a check that becomes
/// due mid-sector simply waits until shutdown, for a player or a virtual pilot alike. The
/// perform-now path here is no exception: it is blocked outright while the aircraft is airborne
/// (<see cref="FleetAircraftStatus.InFlight"/>), so there is no way to force a grounding out from
/// under a flight that is currently happening. Confirmed with the user and documented as a
/// permanent invariant, not a decision this file is free to revisit.
/// </summary>
public static class MaintenanceEndpoints
{
    /// <summary>How long a gap since the last acknowledged away-summary has to be before it's
    /// worth surfacing one - see <see cref="AwaySummaryAsync"/>. Long enough that a normal reload
    /// or a background tab never trips it, short enough that any genuine "closed the app and came
    /// back" gap always does.</summary>
    private static readonly TimeSpan AwayThreshold = TimeSpan.FromMinutes(5);

    public static void MapMaintenanceEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/fleet/{id:guid}/maintenance-quote", MaintenanceQuoteAsync);
        group.MapPost("/fleet/{id:guid}/perform-maintenance", PerformMaintenanceAsync);
        group.MapGet("/away-summary", AwaySummaryAsync);
        group.MapPost("/away-summary/acknowledge", AcknowledgeAwaySummaryAsync);
    }

    /// <summary>
    /// Everything the player needs to decide whether, and which check, to bring forward - see
    /// shown before confirming: hours remaining until it would fall due naturally, the cost, the
    /// downtime, and which pilots' schedules will be affected. The trade-off has to be stated,
    /// because bringing a check forward forfeits hours already paid for through accrual, and what
    /// it buys is control over when the downtime lands. Returns a quote for
    /// BOTH check types so the choice is made with full information in one screen, rather than a
    /// round trip per option.
    /// </summary>
    internal static async Task<IResult> MaintenanceQuoteAsync(
        Guid id, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline before managing maintenance." });
        }

        var aircraft = await db.FleetAircraft.FirstOrDefaultAsync(f => f.Id == id && f.AirlineId == airline.Id && f.DeletedUtc == null, ct);
        if (aircraft is null)
        {
            return Results.NotFound(new { error = "Aircraft not found." });
        }

        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);
        var (canPerform, blockReason) = EvaluateBlockers(aircraft);
        var affectedSchedules = await LoadAffectedSchedulesAsync(db, aircraft.Id, ct);

        var quotes = new[] { MaintenanceEventType.ACheck, MaintenanceEventType.CCheck }
            .Select(type => BuildQuote(aircraft, type, economyConfig))
            .ToList();

        return Results.Ok(new MaintenanceQuoteResponse(
            aircraft.Id,
            aircraft.Registration,
            aircraft.Status.ToString(),
            canPerform,
            blockReason,
            affectedSchedules,
            quotes));
    }

    /// <summary>
    /// Commits a forced check. Re-validates the aircraft's live status and recomputes the quote
    /// rather than trusting what <see cref="MaintenanceQuoteAsync"/> returned - see the class doc's
    /// note on why every commit here mirrors <see cref="FleetDisposalEndpoints"/>'s recompute-and-
    /// compare pattern (a background virtual-pilot flight can ground or move the aircraft between
    /// the quote being shown and the player confirming it).
    /// </summary>
    internal static async Task<IResult> PerformMaintenanceAsync(
        Guid id, PerformMaintenanceRequest request, FsOpsDbContext db, ICurrentUser currentUser,
        EconomyConfigCatalog economyConfigCatalog, IClock clock, CancellationToken ct)
    {
        if (request.Type is not (MaintenanceEventType.ACheck or MaintenanceEventType.CCheck))
        {
            return Results.BadRequest(new { error = "Only an A-check or a C-check can be performed early." });
        }

        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline before managing maintenance." });
        }

        var aircraft = await db.FleetAircraft.FirstOrDefaultAsync(f => f.Id == id && f.AirlineId == airline.Id && f.DeletedUtc == null, ct);
        if (aircraft is null)
        {
            return Results.NotFound(new { error = "Aircraft not found." });
        }

        var (canPerform, blockReason) = EvaluateBlockers(aircraft);
        if (!canPerform)
        {
            return Results.BadRequest(new { error = blockReason });
        }

        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);
        var quote = BuildQuote(aircraft, request.Type, economyConfig);

        if (quote.Cost != request.ExpectedCost || quote.DowntimeHours != request.ExpectedDowntimeHours)
        {
            return Results.BadRequest(new
            {
                error = "The quote has changed since you last checked - please confirm the new figures.",
                currentQuote = quote,
            });
        }

        var now = clock.UtcNow;
        MaintenancePoster.PostForcedCheck(db, aircraft, airline, request.Type, economyConfig, now);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            fleetAircraftId = aircraft.Id,
            registration = aircraft.Registration,
            type = request.Type.ToString(),
            groundedUntilUtc = aircraft.GroundedUntilUtc,
            cost = quote.Cost,
            cashBalance = await CashBalanceAsync(db, airline.Id, ct),
        });
    }

    /// <summary>
    /// Blocked while airborne (the mid-flight invariant this whole file respects - see the class
    /// doc) or while already grounded (nothing to bring forward - it's already down).
    /// </summary>
    private static (bool CanPerform, string? BlockReason) EvaluateBlockers(FleetAircraft aircraft)
    {
        if (aircraft.Status == FleetAircraftStatus.InFlight)
        {
            return (false, $"{aircraft.Registration} is currently flying - maintenance can't be brought forward mid-flight.");
        }

        if (aircraft.Status == FleetAircraftStatus.InMaintenance)
        {
            return (false, $"{aircraft.Registration} is already in maintenance.");
        }

        return (true, null);
    }

    private static MaintenanceCheckQuote BuildQuote(FleetAircraft aircraft, MaintenanceEventType type, EconomyConfig economyConfig)
    {
        var outcome = MaintenanceScheduler.ApplyForced(aircraft, type, economyConfig);
        var hoursSinceCheck = type == MaintenanceEventType.ACheck ? aircraft.HoursSinceACheck : aircraft.HoursSinceCCheck;
        var intervalHours = type == MaintenanceEventType.ACheck
            ? economyConfig.Maintenance.ACheckIntervalHours
            : economyConfig.Maintenance.CCheckIntervalHours;

        return new MaintenanceCheckQuote(
            type.ToString(),
            outcome.Cost,
            outcome.DowntimeHours,
            hoursSinceCheck,
            Math.Max(0, intervalHours - hoursSinceCheck));
    }

    private static async Task<List<AffectedScheduleEntry>> LoadAffectedSchedulesAsync(FsOpsDbContext db, Guid fleetAircraftId, CancellationToken ct)
    {
        var entries = await db.PilotScheduleEntries.Where(e => e.FleetAircraftId == fleetAircraftId && e.DeletedUtc == null).ToListAsync(ct);
        if (entries.Count == 0)
        {
            return [];
        }

        var scheduleIds = entries.Select(e => e.PilotScheduleId).Distinct().ToList();
        var schedules = await db.PilotSchedules.Where(s => scheduleIds.Contains(s.Id) && s.DeletedUtc == null).ToDictionaryAsync(s => s.Id, ct);

        var pilotIds = schedules.Values.Select(s => s.PilotId).Distinct().ToList();
        var pilotsById = await db.Pilots.Where(p => pilotIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);

        var routeIds = entries.Select(e => e.RouteId).Distinct().ToList();
        var routesById = await db.Routes.Where(r => routeIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);

        return entries
            .Where(e => schedules.ContainsKey(e.PilotScheduleId))
            .Select(e =>
            {
                var schedule = schedules[e.PilotScheduleId];
                pilotsById.TryGetValue(schedule.PilotId, out var pilot);
                routesById.TryGetValue(e.RouteId, out var route);
                var routeLabel = route is not null ? $"{route.DepartureIcao}-{route.ArrivalIcao}" : "Unknown route";

                return new AffectedScheduleEntry(
                    schedule.PilotId,
                    pilot?.Name ?? "Unknown pilot",
                    e.DayOfWeek.ToString(),
                    e.DepartureTimeUtc.ToString(@"hh\:mm"),
                    routeLabel);
            })
            .OrderBy(a => a.PilotName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The "while you were away" catch-up summary. Time is real-world time, not time with the app
    /// open, so reopening after months charges months of bills at once - which looks exactly like a
    /// bug unless it is explained: what was charged, what virtual pilots flew and earned, maintenance
    /// that fell due, and anything skipped/cancelled/suspended and why, for the window since the
    /// player last acknowledged a summary. Side-effect free (never advances
    /// <see cref="EconomyState.AwaySummaryLastViewedUtc"/> itself) so polling or refreshing the
    /// Dashboard mid-session is always safe - see <see cref="AcknowledgeAwaySummaryAsync"/> for the
    /// only thing that consumes it.
    /// </summary>
    internal static async Task<IResult> AwaySummaryAsync(
        FsOpsDbContext db, ICurrentUser currentUser, IClock clock, StartupReconciliationState startupState, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(AwaySummaryResponse.None);
        }

        // Startup findings (a reservation reconciled at boot, a catch-up pass that threw) are
        // tracked independently of the ledger-based watermark below - see StartupReconciliationState's
        // own doc for why. They can make this call worth reporting even when nothing else below does.
        var (reconciliationResult, catchUpFailures) = startupState.GetUnacknowledged(airline.Id);
        var reservationChanges = BuildReservationChangeSummary(reconciliationResult, airline.Id);
        var catchUpFailureSummaries = catchUpFailures
            .Select(f => new StartupCatchUpFailureSummary(f.Service, f.Message, f.OccurredUtc))
            .ToList();
        var hasStartupFindings = reservationChanges is not null || catchUpFailureSummaries.Count > 0;

        var state = await db.EconomyStates.FirstOrDefaultAsync(ct);
        var now = clock.UtcNow;

        if (state is null)
        {
            return hasStartupFindings
                ? Results.Ok(await BuildStartupOnlyResponseAsync(db, airline.Id, now, reservationChanges, catchUpFailureSummaries, ct))
                : Results.Ok(AwaySummaryResponse.None);
        }

        if (state.AwaySummaryLastViewedUtc is null)
        {
            // Bootstrap only, same "no opinion until something explicitly sets it" convention as
            // VirtualFlightResolverService's own watermark - a brand-new airline has nothing to
            // catch up on ledger-wise, so this pass reports nothing on that front. Startup findings
            // are a separate concern and still get reported even on this very first call.
            state.AwaySummaryLastViewedUtc = now;
            await db.SaveChangesAsync(ct);
            return hasStartupFindings
                ? Results.Ok(await BuildStartupOnlyResponseAsync(db, airline.Id, now, reservationChanges, catchUpFailureSummaries, ct))
                : Results.Ok(AwaySummaryResponse.None);
        }

        var windowStart = state.AwaySummaryLastViewedUtc.Value;
        var windowEnd = now;

        if (windowEnd - windowStart < AwayThreshold && !hasStartupFindings)
        {
            return Results.Ok(AwaySummaryResponse.None);
        }

        var ledgerLines = (await db.LedgerTransactions.Where(t => t.AirlineId == airline.Id).ToListAsync(ct))
            .Where(t => t.Utc > windowStart && t.Utc <= windowEnd)
            .ToList();

        var flights = (await db.Flights.Where(f => f.AirlineId == airline.Id && f.DeletedUtc == null).ToListAsync(ct))
            .Where(f => f.CreatedUtc > windowStart && f.CreatedUtc <= windowEnd)
            .ToList();

        var maintenanceEvents = (await db.MaintenanceEvents.Where(m => m.DeletedUtc == null).ToListAsync(ct))
            .Where(m => m.CreatedUtc > windowStart && m.CreatedUtc <= windowEnd)
            .ToList();

        if (ledgerLines.Count == 0 && flights.Count == 0 && maintenanceEvents.Count == 0 && !hasStartupFindings)
        {
            return Results.Ok(AwaySummaryResponse.None);
        }

        var chargesByCategory = ledgerLines
            .GroupBy(t => DisplayCategoryFor(t))
            .Select(g => new LedgerCategoryTotal(g.Key, g.Sum(t => t.Amount), g.Count()))
            .OrderBy(c => c.Amount)
            .ToList();

        var virtualPilotIds = (await db.Pilots.Where(p => p.AirlineId == airline.Id && !p.IsPlayer).Select(p => p.Id).ToListAsync(ct)).ToHashSet();
        var virtualFlightsCompleted = flights.Where(f => f.Status == FlightStatus.Completed && virtualPilotIds.Contains(f.PilotId)).ToList();
        var virtualPilots = new VirtualPilotActivitySummary(
            virtualFlightsCompleted.Count,
            virtualFlightsCompleted.Sum(f => f.Revenue),
            virtualFlightsCompleted.Sum(f => f.TotalCost),
            virtualFlightsCompleted.Sum(f => f.Revenue - f.TotalCost));

        var fleetIds = maintenanceEvents.Select(m => m.FleetAircraftId).Distinct().ToList();
        var fleetById = await db.FleetAircraft.Where(f => fleetIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);
        var maintenanceSummaries = maintenanceEvents
            .OrderBy(m => m.StartUtc)
            .Select(m => new MaintenanceEventSummary(
                m.FleetAircraftId,
                fleetById.TryGetValue(m.FleetAircraftId, out var fa) ? fa.Registration : "Unknown aircraft",
                m.Type.ToString(),
                m.StartUtc,
                m.EndUtc,
                m.Cost))
            .ToList();

        var routeIds = flights.Select(f => f.RouteId).Distinct().ToList();
        var routesById = await db.Routes.Where(r => routeIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var unresolved = flights
            .Where(f => f.Status is FlightStatus.Skipped or FlightStatus.Cancelled or FlightStatus.Suspended)
            .OrderBy(f => f.PlannedDepartureUtc)
            .Select(f => new UnflyableOccurrenceSummary(
                f.Id,
                f.Status.ToString(),
                routesById.TryGetValue(f.RouteId, out var r) ? $"{r.DepartureIcao}-{r.ArrivalIcao}" : "Unknown route",
                f.PlannedDepartureUtc,
                f.UnflyableReason))
            .ToList();

        var cashBalance = (await db.LedgerTransactions.Where(t => t.AirlineId == airline.Id).Select(t => t.Amount).ToListAsync(ct)).Sum();

        return Results.Ok(new AwaySummaryResponse(
            true,
            windowStart,
            windowEnd,
            (windowEnd - windowStart).TotalHours,
            cashBalance,
            chargesByCategory,
            ledgerLines.Sum(t => t.Amount),
            virtualPilots,
            maintenanceSummaries,
            unresolved,
            reservationChanges,
            catchUpFailureSummaries));
    }

    /// <summary>
    /// Reports a startup finding (reservation reconciled, catch-up pass failed) on its own, for the
    /// calls where there is genuinely nothing else to say - no ledger activity, or not even an
    /// <see cref="EconomyState"/> row yet. <paramref name="instant"/> is used for both ends of the
    /// window: there is no meaningful "since" for a finding that isn't itself ledger-window-based,
    /// and a zero-length window reads better in the dialog than an invented one. See
    /// AwaySummaryDialog's own handling of a zero <c>awayHours</c> for the other half of this.
    /// </summary>
    private static async Task<AwaySummaryResponse> BuildStartupOnlyResponseAsync(
        FsOpsDbContext db, Guid airlineId, DateTimeOffset instant,
        ReservationChangeSummary? reservationChanges, List<StartupCatchUpFailureSummary> catchUpFailures, CancellationToken ct)
    {
        var cashBalance = await CashBalanceAsync(db, airlineId, ct);
        return new AwaySummaryResponse(
            true,
            instant,
            instant,
            0,
            cashBalance,
            [],
            0,
            new VirtualPilotActivitySummary(0, 0, 0, 0),
            [],
            [],
            reservationChanges,
            catchUpFailures);
    }

    /// <summary>Narrows a raw <see cref="ReservationReconciliationResult"/> down to what one
    /// airline needs to see, or null if it turns out this airline had no part in it (reconciliation
    /// can touch several airlines' fleets in one pass under a future multi-user model, even though
    /// today there is only ever one).</summary>
    private static ReservationChangeSummary? BuildReservationChangeSummary(ReservationReconciliationResult? result, Guid airlineId)
    {
        if (result is null)
        {
            return null;
        }

        var released = result.Released
            .Where(r => r.AirlineId == airlineId)
            .Select(r => new ReservationReleaseSummary(r.FleetAircraftId, r.Registration, r.ScheduledLegCount))
            .ToList();
        var fallbackReserved = result.FallbackReserved
            .Where(f => f.AirlineId == airlineId)
            .Select(f => new ReservationFallbackSummary(f.FleetAircraftId, f.Registration))
            .ToList();
        var leftWithNoReservedAircraft = result.AirlinesLeftWithNoReservedAircraft.Contains(airlineId);

        if (released.Count == 0 && fallbackReserved.Count == 0 && !leftWithNoReservedAircraft)
        {
            return null;
        }

        return new ReservationChangeSummary(released, fallbackReserved, leftWithNoReservedAircraft);
    }

    /// <summary>
    /// The <see cref="LedgerCategoryTotal.Category"/> a ledger line is grouped under for the
    /// away-summary's "Charges by category" breakdown. Almost always just the raw
    /// <see cref="LedgerTransaction.Category"/> name, EXCEPT a lease deposit: it is posted under the
    /// same <see cref="LedgerCategory.LeasePayment"/> category as the ordinary recurring monthly
    /// charge (see <see cref="AirlineEndpoints.CreateAsync"/> and
    /// <see cref="FleetEndpoints.LeaseAsync"/>, both of which post it with a description starting
    /// "Lease deposit"), but it is not a charge that happened WHILE the player was away in the sense
    /// this summary means - it is a one-off, self-inflicted by adding an aircraft, that would
    /// otherwise be misread as "you were billed a lease payment you didn't expect" if lumped in with
    /// the recurring line. Same description-prefix technique <see cref="FinanceEndpoints.CostsAsync"/>
    /// already uses to split early-termination fees out of the same category there.
    /// </summary>
    private static string DisplayCategoryFor(LedgerTransaction transaction) =>
        transaction.Category == LedgerCategory.LeasePayment &&
        transaction.Description.StartsWith("Lease deposit", StringComparison.Ordinal)
            ? "LeaseDeposit"
            : transaction.Category.ToString();

    /// <summary>Marks the away-summary window as seen - moves <see cref="EconomyState.AwaySummaryLastViewedUtc"/>
    /// forward to now. The only endpoint that ever advances that cursor - see <see cref="AwaySummaryAsync"/>'s
    /// own doc for why every GET there is deliberately side-effect free.</summary>
    internal static async Task<IResult> AcknowledgeAwaySummaryAsync(
        FsOpsDbContext db, ICurrentUser currentUser, IClock clock, StartupReconciliationState startupState, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline first." });
        }

        // Moves the startup-findings cursor forward alongside the ledger one below, so from the
        // player's side "Got it" clears the whole dialog in one action even though the two are
        // tracked independently under the hood - see StartupReconciliationState.Acknowledge.
        startupState.Acknowledge();

        var state = await db.EconomyStates.FirstOrDefaultAsync(ct);
        if (state is null)
        {
            return Results.NoContent();
        }

        state.AwaySummaryLastViewedUtc = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<decimal> CashBalanceAsync(FsOpsDbContext db, Guid airlineId, CancellationToken ct)
    {
        var amounts = await db.LedgerTransactions.Where(t => t.AirlineId == airlineId).Select(t => t.Amount).ToListAsync(ct);
        return amounts.Sum();
    }
}

/// <summary>Body for <see cref="MaintenanceEndpoints.PerformMaintenanceAsync"/> - carries the cost
/// and downtime the player confirmed on the quote, so the commit can detect drift rather than
/// silently posting different figures. See the class doc.</summary>
public record PerformMaintenanceRequest(MaintenanceEventType Type, decimal ExpectedCost, double ExpectedDowntimeHours);

public record MaintenanceQuoteResponse(
    Guid FleetAircraftId,
    string Registration,
    string CurrentStatus,
    bool CanPerform,
    string? BlockReason,
    List<AffectedScheduleEntry> AffectedSchedules,
    List<MaintenanceCheckQuote> Quotes);

public record MaintenanceCheckQuote(
    string Type,
    decimal Cost,
    double DowntimeHours,
    double HoursSinceCheck,
    double HoursRemainingUntilNatural);

public record AffectedScheduleEntry(Guid PilotId, string PilotName, string DayOfWeek, string DepartureTimeUtc, string RouteLabel);

public record AwaySummaryResponse(
    bool HasSummary,
    DateTimeOffset SinceUtc,
    DateTimeOffset UntilUtc,
    double AwayHours,
    decimal CashBalance,
    List<LedgerCategoryTotal> ChargesByCategory,
    decimal NetLedgerChange,
    VirtualPilotActivitySummary VirtualPilots,
    List<MaintenanceEventSummary> MaintenanceEvents,
    List<UnflyableOccurrenceSummary> UnresolvedOccurrences,
    ReservationChangeSummary? ReservationChanges,
    List<StartupCatchUpFailureSummary> CatchUpFailures)
{
    public static readonly AwaySummaryResponse None = new(
        false, default, default, 0, 0, [], 0,
        new VirtualPilotActivitySummary(0, 0, 0, 0), [], [], null, []);
}

public record LedgerCategoryTotal(string Category, decimal Amount, int Count);

public record VirtualPilotActivitySummary(int SectorsFlown, decimal Revenue, decimal Cost, decimal Net);

public record MaintenanceEventSummary(Guid FleetAircraftId, string Registration, string Type, DateTimeOffset StartUtc, DateTimeOffset? EndUtc, decimal Cost);

public record UnflyableOccurrenceSummary(Guid FlightId, string Status, string RouteLabel, DateTimeOffset PlannedDepartureUtc, string? Reason);

/// <summary>One aircraft the startup <see cref="Services.ReservationReconciler"/> released because
/// it had a contradictory reservation - see <see cref="Services.ReleasedAircraft"/> for the full
/// story of why this happens.</summary>
public record ReservationReleaseSummary(Guid FleetAircraftId, string Registration, int ScheduledLegCount);

/// <summary>One aircraft the reconciler reserved as a replacement after a release above left the
/// airline with none - see <see cref="Services.FallbackReservedAircraft"/>.</summary>
public record ReservationFallbackSummary(Guid FleetAircraftId, string Registration);

/// <summary>What the startup reservation reconciler did to this player's fleet, if anything -
/// folded into the away-summary rather than shown separately. <see cref="LeftWithNoReservedAircraft"/>
/// is true when a release left the airline with nothing reserved and no safe replacement could be
/// chosen (every remaining aircraft had scheduled legs of its own) - the player has to pick one
/// explicitly on the Fleet screen.</summary>
public record ReservationChangeSummary(
    List<ReservationReleaseSummary> Released,
    List<ReservationFallbackSummary> FallbackReserved,
    bool LeftWithNoReservedAircraft);

/// <summary>One wall-clock catch-up pass (<see cref="Services.EconomyClockService"/> or
/// <see cref="Services.VirtualFlightResolverService"/>) that threw instead of completing - see
/// <see cref="Services.StartupCatchUpFailure"/> for why this matters enough to surface. Carries only
/// the exception's message, not its type - the type is an implementation detail the player has no
/// use for; the log has the full exception if a developer needs it.</summary>
public record StartupCatchUpFailureSummary(string Service, string Message, DateTimeOffset OccurredUtc);
