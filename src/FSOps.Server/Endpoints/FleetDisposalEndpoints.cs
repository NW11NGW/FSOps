using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Finance;
using FSOps.Core.Time;
using FSOps.Data;
using FSOps.Server.Auth;
using FSOps.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Endpoints;

/// <summary>
/// Getting rid of an aircraft - selling an owned one, or ending a leased one's lease early - see
/// so the fleet stops being a one-way ratchet. Acquisition
/// (<see cref="FleetEndpoints.BuyAsync"/>/<see cref="FleetEndpoints.LeaseAsync"/>) was a one-way
/// ratchet before this; both actions here are deliberately never free (see
/// <see cref="AircraftDepreciationCalculator"/>/<see cref="LeaseTerminationCalculator"/>'s own docs
/// for how each exploit - selling at cost, returning a lease between billing ticks - is closed).
/// <para>
/// <b>Every commit here recomputes its quote from current state and charges what it recomputes</b> -
/// the figure the caller confirmed is evidence about what they were shown, never the amount that
/// moves money. The world keeps moving while a confirmation dialog is open (billing and virtual
/// flights both resolve against real time, not app-open time), and both actions are irreversible, so
/// each commit also decides whether what the player agreed to has materially changed and refuses
/// rather than posting a surprise.
/// </para>
/// <para>
/// <b>The two commits ask that question differently, and deliberately.</b> A sale value moves only
/// when the airframe does - a virtual pilot's background flight adds hours and wears condition - so
/// <see cref="SellAsync"/> compares the value exactly. A lease-termination charge is pro-rata rent
/// and therefore grows with the clock every second, so an exact comparison there was unsatisfiable
/// by a human and refused every click; <see cref="EndLeaseAsync"/> compares the billing period
/// instead. See that method's own doc - it is the more interesting of the two.
/// </para>
/// <para>
/// All time reads go through <see cref="IClock"/> rather than <see cref="DateTimeOffset.UtcNow"/>
/// directly, so this is deterministically testable.
/// </para>
/// </summary>
public static class FleetDisposalEndpoints
{
    public static void MapFleetDisposalEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/fleet/{id:guid}/sale-quote", SaleQuoteAsync);
        group.MapPost("/fleet/{id:guid}/sell", SellAsync);
        group.MapGet("/fleet/{id:guid}/lease-termination-quote", LeaseTerminationQuoteAsync);
        group.MapPost("/fleet/{id:guid}/end-lease", EndLeaseAsync);
    }

    private const string LoanNote = "Selling does not pay off any outstanding loan - your loan repayments continue as scheduled.";

    internal static async Task<IResult> SaleQuoteAsync(
        Guid id, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline before disposing of aircraft." });
        }

        var aircraft = await db.FleetAircraft.FirstOrDefaultAsync(f => f.Id == id && f.AirlineId == airline.Id && f.DeletedUtc == null, ct);
        if (aircraft is null)
        {
            return Results.NotFound(new { error = "Aircraft not found." });
        }

        var aircraftType = await db.AircraftTypes.FindAsync([aircraft.AircraftTypeId], ct);
        if (aircraftType is null)
        {
            return Results.NotFound(new { error = "Aircraft type not found." });
        }

        var quote = ComputeSaleQuote(airline, aircraft, aircraftType, economyConfigCatalog);

        var (canDispose, blockReason, conflict) = await EvaluateDisposalBlockersAsync(db, aircraft, requireOwned: true, ct);
        var isLastAircraft = await IsLastAircraftAsync(db, airline.Id, ct);

        return Results.Ok(new
        {
            fleetAircraftId = aircraft.Id,
            registration = aircraft.Registration,
            aircraftTypeName = aircraftType.Name,
            conditionPercent = aircraft.ConditionPercent,
            airframeHours = aircraft.AirframeHours,
            newPrice = quote.NewPrice,
            resaleFactorApplied = quote.ResaleFactorApplied,
            saleValue = quote.SaleValue,
            isGroundedForMaintenance = aircraft.Status == FleetAircraftStatus.InMaintenance,
            isLastAircraft,
            standingScheduleConflict = conflict,
            canSell = canDispose,
            blockReason,
            loanNote = LoanNote,
        });
    }

    /// <summary>
    /// Sells the aircraft. Requires <see cref="SellAircraftRequest.ExpectedSaleValue"/> - the exact
    /// figure the player was shown and confirmed on <see cref="SaleQuoteAsync"/>. The quote is
    /// recomputed here from the aircraft's CURRENT state and compared against it; on any mismatch
    /// nothing is posted and the new figure is returned so the caller can re-confirm - see this
    /// class's own doc for why.
    /// </summary>
    internal static async Task<IResult> SellAsync(
        Guid id, SellAircraftRequest request, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, IClock clock, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline before disposing of aircraft." });
        }

        var aircraft = await db.FleetAircraft.FirstOrDefaultAsync(f => f.Id == id && f.AirlineId == airline.Id && f.DeletedUtc == null, ct);
        if (aircraft is null)
        {
            return Results.NotFound(new { error = "Aircraft not found." });
        }

        var (canDispose, blockReason, _) = await EvaluateDisposalBlockersAsync(db, aircraft, requireOwned: true, ct);
        if (!canDispose)
        {
            return Results.BadRequest(new { error = blockReason });
        }

        var aircraftType = await db.AircraftTypes.FindAsync([aircraft.AircraftTypeId], ct);
        if (aircraftType is null)
        {
            return Results.NotFound(new { error = "Aircraft type not found." });
        }

        var quote = ComputeSaleQuote(airline, aircraft, aircraftType, economyConfigCatalog);

        if (quote.SaleValue != request.ExpectedSaleValue)
        {
            return Results.BadRequest(new
            {
                error = $"The sale value has changed since you last checked (was {request.ExpectedSaleValue:F2}, now {quote.SaleValue:F2}) - please confirm the new figure.",
                currentSaleValue = quote.SaleValue,
            });
        }

        var now = clock.UtcNow;

        aircraft.DeletedUtc = now;

        db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            AirlineId = airline.Id,
            Utc = now,
            Category = LedgerCategory.AircraftPurchase,
            Amount = quote.SaleValue,
            Description = $"Sold: {aircraftType.Name} ({aircraft.Registration}) - {aircraft.ConditionPercent:F0}% condition, {aircraft.AirframeHours:F0}h airframe",
        });

        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            fleetAircraftId = aircraft.Id,
            saleValue = quote.SaleValue,
            cashBalance = await CashBalanceAsync(db, airline.Id, ct),
        });
    }

    private static AircraftSaleQuote ComputeSaleQuote(Airline airline, FleetAircraft aircraft, AircraftType aircraftType, EconomyConfigCatalog economyConfigCatalog)
    {
        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);
        var newPrice = economyConfig.PurchasePriceFor(aircraftType);
        var isGrounded = aircraft.Status == FleetAircraftStatus.InMaintenance;

        return AircraftDepreciationCalculator.ComputeSaleValue(
            newPrice,
            aircraft.ConditionPercent,
            aircraft.HoursSinceACheck,
            aircraft.HoursSinceCCheck,
            economyConfig.Maintenance.ACheckIntervalHours,
            economyConfig.Maintenance.CCheckIntervalHours,
            isGrounded,
            economyConfig.Depreciation);
    }

    internal static async Task<IResult> LeaseTerminationQuoteAsync(
        Guid id, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, IClock clock, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline before ending a lease." });
        }

        var aircraft = await db.FleetAircraft.FirstOrDefaultAsync(f => f.Id == id && f.AirlineId == airline.Id && f.DeletedUtc == null, ct);
        if (aircraft is null)
        {
            return Results.NotFound(new { error = "Aircraft not found." });
        }

        var aircraftType = await db.AircraftTypes.FindAsync([aircraft.AircraftTypeId], ct);
        var lease = await db.Leases.FirstOrDefaultAsync(l => l.FleetAircraftId == aircraft.Id && l.IsActive && l.DeletedUtc == null, ct);

        var (canDispose, blockReason, conflict) = await EvaluateDisposalBlockersAsync(db, aircraft, requireOwned: false, ct);
        if (lease is null)
        {
            canDispose = false;
            blockReason ??= "No active lease found for this aircraft.";
        }

        var isLastAircraft = await IsLastAircraftAsync(db, airline.Id, ct);

        if (lease is null)
        {
            return Results.Ok(new
            {
                fleetAircraftId = aircraft.Id,
                leaseId = (Guid?)null,
                registration = aircraft.Registration,
                aircraftTypeName = aircraftType?.Name ?? string.Empty,
                monthlyRate = 0m,
                currentPeriodStartUtc = (DateTimeOffset?)null,
                nextScheduledPaymentUtc = (DateTimeOffset?)null,
                daysIntoCurrentPeriod = 0.0,
                proRataAmount = 0m,
                earlyTerminationFee = 0m,
                totalCharge = 0m,
                isLastAircraft,
                standingScheduleConflict = conflict,
                canEndLease = false,
                blockReason,
            });
        }

        var settlement = await ComputeLeaseSettlementAsync(db, airline, lease, economyConfigCatalog, clock, ct);

        return Results.Ok(new
        {
            fleetAircraftId = aircraft.Id,
            leaseId = lease.Id,
            registration = aircraft.Registration,
            aircraftTypeName = aircraftType?.Name ?? string.Empty,
            monthlyRate = lease.MonthlyRate,
            currentPeriodStartUtc = settlement.PeriodStart,
            nextScheduledPaymentUtc = settlement.PeriodStart + EconomyClockService.PeriodLength,
            daysIntoCurrentPeriod = settlement.Settlement.DaysIntoCurrentPeriod,
            proRataAmount = settlement.Settlement.ProRataAmount,
            earlyTerminationFee = settlement.Settlement.EarlyTerminationFee,
            totalCharge = settlement.Settlement.TotalCharge,
            isLastAircraft,
            standingScheduleConflict = conflict,
            canEndLease = canDispose,
            blockReason,
        });
    }

    /// <summary>
    /// Ends the lease early, charging the settlement recomputed here and now - never the figure the
    /// caller confirmed. See <see cref="EndLeaseRequest"/> for what the confirmed figures are for.
    /// <para>
    /// <b>This commit guards on the billing PERIOD, not on the money.</b> Its siblings
    /// (<see cref="SellAsync"/>, <c>FinanceEndpoints.SettleLoanAsync</c>) compare their quote
    /// against discrete state - airframe hours, a loan's remaining balance - which only moves when
    /// something happens, so an exact comparison there is a question the player can answer. A
    /// lease-termination charge is not like that: it is pro-rata rent, so it grows continuously with
    /// the clock (about a cent a second on a mid-size narrowbody). Comparing it exactly asked the
    /// player to click within the same fraction of a second the quote was priced in, which nobody
    /// can do - <b>every</b> click was refused, and the "figures changed, confirm again" re-quote
    /// that followed looked exactly like a button that had swallowed the click. It was reported as
    /// "you have to click nearly 3 times to end a lease".
    /// </para>
    /// <para>
    /// So the guard asks the question it always meant to ask: <i>is this lease still in the billing
    /// period it was quoted in?</i> That is a fact rather than a magnitude - no threshold to justify
    /// and no cliff-edge - and only the one event the guard exists for can change it: a rent payment
    /// falling due, which <c>EconomyClockService</c> posts by advancing
    /// <c>EconomyState.LastProcessedUtc</c> a whole <see cref="EconomyClockService.PeriodLength"/>.
    /// That resets the pro-rata to nearly nothing, a change worth stopping to re-read. Mere clock
    /// drift inside one period is not: it is rent genuinely accruing for time the aircraft was
    /// genuinely held, and it is charged, which is the honest answer.
    /// </para>
    /// </summary>
    internal static async Task<IResult> EndLeaseAsync(
        Guid id, EndLeaseRequest request, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, IClock clock, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline before ending a lease." });
        }

        var aircraft = await db.FleetAircraft.FirstOrDefaultAsync(f => f.Id == id && f.AirlineId == airline.Id && f.DeletedUtc == null, ct);
        if (aircraft is null)
        {
            return Results.NotFound(new { error = "Aircraft not found." });
        }

        var lease = await db.Leases.FirstOrDefaultAsync(l => l.FleetAircraftId == aircraft.Id && l.IsActive && l.DeletedUtc == null, ct);
        if (lease is null)
        {
            return Results.BadRequest(new { error = "No active lease found for this aircraft." });
        }

        var (canDispose, blockReason, _) = await EvaluateDisposalBlockersAsync(db, aircraft, requireOwned: false, ct);
        if (!canDispose)
        {
            return Results.BadRequest(new { error = blockReason });
        }

        var (settlement, periodStart) = await ComputeLeaseSettlementAsync(db, airline, lease, economyConfigCatalog, clock, ct);

        // Exact equality is safe here despite being a timestamp comparison: this value is one the
        // server itself serialised on the quote and the caller echoes back untouched, never one
        // reconstructed from a parsed local Date. A caller that omits it gets the same refusal as a
        // stale one - the failure direction is "re-quote", never "charge something unconfirmed".
        if (request.ExpectedPeriodStartUtc != periodStart)
        {
            return Results.BadRequest(new
            {
                error = $"A rent payment fell due while you were deciding, so this lease has moved into a new billing period (the charge was {request.ExpectedTotalCharge:F2}, it is now {settlement.TotalCharge:F2}) - nothing has been charged, please confirm the new figure.",
                currentTotalCharge = settlement.TotalCharge,
            });
        }

        var cashBalance = await CashBalanceAsync(db, airline.Id, ct);
        if (cashBalance < settlement.TotalCharge)
        {
            return Results.BadRequest(new
            {
                error = $"Ending this lease now would cost {settlement.TotalCharge:F2} (pro-rata rent plus early-termination fee), you have {cashBalance:F2}.",
            });
        }

        var now = clock.UtcNow;

        lease.IsActive = false;
        lease.EndUtc = now;
        aircraft.DeletedUtc = now;

        // Both lines come from `settlement`, computed a few statements ago from the current clock -
        // never from `request.ExpectedTotalCharge`. Charging the confirmed figure would let a player
        // hold the dialog open to freeze the price while the rent kept accruing, which is a day of
        // free rent for anyone who notices. What is charged is what is owed, to the second.
        db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            AirlineId = airline.Id,
            Utc = now,
            Category = LedgerCategory.LeasePayment,
            Amount = -settlement.ProRataAmount,
            Description = $"Lease pro-rata settlement (early return): {aircraft.Registration}, {settlement.DaysIntoCurrentPeriod:F1} day(s) of the current period",
        });
        db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            AirlineId = airline.Id,
            Utc = now,
            Category = LedgerCategory.LeasePayment,
            Amount = -settlement.EarlyTerminationFee,
            Description = $"Early lease termination fee: {aircraft.Registration}",
        });

        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            fleetAircraftId = aircraft.Id,
            proRataAmount = settlement.ProRataAmount,
            earlyTerminationFee = settlement.EarlyTerminationFee,
            totalCharge = settlement.TotalCharge,
            cashBalance = await CashBalanceAsync(db, airline.Id, ct),
        });
    }

    private static async Task<(LeaseTerminationSettlement Settlement, DateTimeOffset PeriodStart)> ComputeLeaseSettlementAsync(
        FsOpsDbContext db, Airline airline, Lease lease, EconomyConfigCatalog economyConfigCatalog, IClock clock, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var economyState = await db.EconomyStates.FirstOrDefaultAsync(ct);
        var periodStart = economyState?.LastProcessedUtc ?? lease.StartUtc;

        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);
        var settlement = LeaseTerminationCalculator.ComputeSettlement(
            lease.MonthlyRate, periodStart, now, EconomyClockService.PeriodLength, economyConfig.LeaseEarlyTermination);

        return (settlement, periodStart);
    }

    /// <summary>
    /// Shared blocking rules for both disposal actions: never while
    /// flying, never while on a virtual pilot's standing schedule (say which pilot and which
    /// schedule rather than silently unassigning), and the right ownership kind for the action
    /// requested (sell needs Owned, end-lease needs Leased). Grounded-for-maintenance is
    /// deliberately NOT a blocker - the plan is explicit that a worse sale figure, not a refusal, is
    /// the correct handling for that case.
    /// </summary>
    private static async Task<(bool CanDispose, string? BlockReason, object? Conflict)> EvaluateDisposalBlockersAsync(
        FsOpsDbContext db, FleetAircraft aircraft, bool requireOwned, CancellationToken ct)
    {
        if (requireOwned && aircraft.Ownership != AircraftOwnership.Owned)
        {
            return (false, $"{aircraft.Registration} is leased, not owned - end the lease instead of selling it.", null);
        }

        if (!requireOwned && aircraft.Ownership != AircraftOwnership.Leased)
        {
            return (false, $"{aircraft.Registration} is owned, not leased - sell it instead of ending a lease.", null);
        }

        if (aircraft.Status == FleetAircraftStatus.InFlight)
        {
            return (false, $"{aircraft.Registration} is currently flying - it can't be disposed of mid-flight.", null);
        }

        var entries = await db.PilotScheduleEntries
            .Where(e => e.FleetAircraftId == aircraft.Id && e.DeletedUtc == null)
            .ToListAsync(ct);

        if (entries.Count > 0)
        {
            var scheduleIds = entries.Select(e => e.PilotScheduleId).Distinct().ToList();
            var schedules = await db.PilotSchedules
                .Where(s => scheduleIds.Contains(s.Id) && s.DeletedUtc == null)
                .ToListAsync(ct);

            if (schedules.Count > 0)
            {
                var pilotIds = schedules.Select(s => s.PilotId).Distinct().ToList();
                var pilots = await db.Pilots.Where(p => pilotIds.Contains(p.Id)).ToListAsync(ct);
                var pilot = pilots.FirstOrDefault();

                if (pilot is not null)
                {
                    var conflict = new { pilotId = pilot.Id, pilotName = pilot.Name, scheduleEntryCount = entries.Count };
                    return (false,
                        $"{aircraft.Registration} is on {pilot.Name}'s standing schedule ({entries.Count} leg(s)/week) - remove it from their schedule first.",
                        conflict);
                }
            }
        }

        return (true, null, null);
    }

    private static async Task<bool> IsLastAircraftAsync(FsOpsDbContext db, Guid airlineId, CancellationToken ct)
    {
        var count = await db.FleetAircraft.CountAsync(f => f.AirlineId == airlineId && f.DeletedUtc == null, ct);
        return count <= 1;
    }

    private static async Task<decimal> CashBalanceAsync(FsOpsDbContext db, Guid airlineId, CancellationToken ct)
    {
        var amounts = await db.LedgerTransactions.Where(t => t.AirlineId == airlineId).Select(t => t.Amount).ToListAsync(ct);
        return amounts.Sum();
    }
}

/// <summary>Body for <see cref="FleetDisposalEndpoints.SellAsync"/> - carries the sale value the
/// player confirmed on the quote, so the commit can detect drift rather than silently posting a
/// different figure. See the class doc.</summary>
public record SellAircraftRequest(decimal ExpectedSaleValue);

/// <summary>
/// Body for <see cref="FleetDisposalEndpoints.EndLeaseAsync"/>.
/// <para>
/// <paramref name="ExpectedPeriodStartUtc"/> is the guard: it is <c>currentPeriodStartUtc</c> from
/// the quote, echoed back untouched, and the commit refuses unless the lease is still in that same
/// billing period. <paramref name="ExpectedTotalCharge"/> is <b>not</b> a guard and is never
/// charged - it is carried only so a refusal can tell the player what they were looking at versus
/// what it is now. See <see cref="FleetDisposalEndpoints.EndLeaseAsync"/> for why the money cannot
/// be the thing compared.
/// </para>
/// </summary>
public record EndLeaseRequest(decimal ExpectedTotalCharge, DateTimeOffset? ExpectedPeriodStartUtc);
