using FSOps.Core.Airlines;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Finance;
using FSOps.Core.Money;
using FSOps.Data;
using FSOps.Server.Auth;
using FSOps.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Endpoints;

/// <summary>
/// Fleet finance and the Fleet page's backing data.
/// Buying/leasing additional aircraft, buying used (cheap to buy, expensive to
/// run - <see cref="MaintenanceScheduler.ResolveUsedAircraftState"/>), and mid-game loans
/// (<see cref="LoanEligibilityCalculator"/>), all on top of the founding lease/aircraft
/// AirlineEndpoints.CreateAsync already sets up.
/// </summary>
public static class FleetEndpoints
{
    public static void MapFleetEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/fleet", ListAsync);
        group.MapGet("/fleet/aircraft-types", ListAircraftTypesAsync);
        group.MapGet("/fleet/registration-suggestion", GetRegistrationSuggestionAsync);
        group.MapPost("/fleet/lease", LeaseAsync);
        group.MapPost("/fleet/buy", BuyAsync);
        group.MapGet("/fleet/loans", ListLoansAsync);
        group.MapGet("/fleet/loan-eligibility", GetLoanEligibilityAsync);
        group.MapGet("/fleet/loan-quote", GetLoanQuoteAsync);
        group.MapPost("/fleet/loans", TakeLoanAsync);
        group.MapPut("/fleet/{id:guid}/reservation", SetReservationAsync);
        group.MapPut("/fleet/{id:guid}/registration", RenameAsync);
    }

    /// <summary>
    /// Everything the Fleet page shows per aircraft: identity, location, ownership, condition/hours
    /// and - for a grounded aircraft - why and until when, never just "in maintenance", which
    /// tells the player nothing they can act on. Releases any aircraft whose downtime has already
    /// elapsed first, same as the Fly
    /// screen's options endpoint, so status here is never stale.
    /// </summary>
    internal static async Task<IResult> ListAsync(FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(Array.Empty<object>());
        }

        await MaintenanceReleaser.ReleaseDueAsync(db, airline.Id, DateTimeOffset.UtcNow, ct);

        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);

        var fleet = (await db.FleetAircraft.Where(f => f.AirlineId == airline.Id).ToListAsync(ct))
            .OrderBy(f => f.CreatedUtc)
            .ToList();
        if (fleet.Count == 0)
        {
            return Results.Ok(Array.Empty<object>());
        }

        var typeIds = fleet.Select(f => f.AircraftTypeId).Distinct().ToList();
        var typesById = await db.AircraftTypes.Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);

        var summaries = fleet.Select(f =>
        {
            typesById.TryGetValue(f.AircraftTypeId, out var type);
            var hoursToNextACheck = Math.Max(0, economyConfig.Maintenance.ACheckIntervalHours - f.HoursSinceACheck);
            var hoursToNextCCheck = Math.Max(0, economyConfig.Maintenance.CCheckIntervalHours - f.HoursSinceCCheck);

            string? groundedReason = f.Status == FleetAircraftStatus.InMaintenance
                ? (f.GroundedUntilUtc is { } until
                    ? $"In maintenance until {until:yyyy-MM-dd HH:mm} UTC."
                    : "In maintenance.")
                : null;

            return new FleetAircraftSummary(
                f.Id,
                f.Registration,
                f.AircraftTypeId,
                type?.Name ?? "Unknown type",
                type?.Family ?? string.Empty,
                type?.PaxCapacity ?? 0,
                f.Ownership.ToString(),
                f.Status.ToString(),
                f.LocationIcao,
                f.AirframeHours,
                f.HoursSinceACheck,
                f.HoursSinceCCheck,
                hoursToNextACheck,
                hoursToNextCCheck,
                f.ConditionPercent,
                f.FuelOnBoardKg,
                f.GroundedUntilUtc,
                groundedReason,
                f.ReservedForPlayer,
                f.CreatedUtc);
        }).ToList();

        return Results.Ok(summaries);
    }

    /// <summary>
    /// The buy/lease picker's catalogue: every seeded aircraft type with its new purchase price,
    /// lease rate, and - because the trade-off must be informed before purchase, never a surprise
    /// - exactly what a used example of this type would cost and what
    /// hours/condition it would start at, computed with the same
    /// <see cref="MaintenanceScheduler.ResolveUsedAircraftState"/> call BuyAsync actually applies.
    /// </summary>
    internal static async Task<IResult> ListAircraftTypesAsync(FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(Array.Empty<object>());
        }

        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);

        var types = (await db.AircraftTypes.ToListAsync(ct))
            .Select(t =>
            {
                // economyConfig.PurchasePriceFor, never t.PurchasePrice directly - see that
                // method's own doc for why (True-life charges it unmodified, Casual applies the
                // catalogue-wide multiplier).
                var newPrice = economyConfig.PurchasePriceFor(t);
                var used = MaintenanceScheduler.ResolveUsedAircraftState(newPrice, economyConfig);
                // economyConfig.LeaseRateFor, never t.MonthlyLeaseRate - see EconomyConfig.LeaseRates'
                // doc comment for why the DB column is never read for pricing or display.
                return new AircraftTypeOption(
                    t.Id,
                    t.IcaoType,
                    t.Family,
                    t.Manufacturer,
                    t.Name,
                    AircraftCategoryFor(t.Family),
                    t.PaxCapacity,
                    t.RangeNm,
                    newPrice,
                    used.PurchasePrice,
                    economyConfig.LeaseRateFor(t.IcaoType),
                    // The Fleet screen's lease preview derives its deposit from THIS figure - never
                    // a hardcoded "1 month" - so it can never show a different deposit than
                    // LeaseAsync actually charges (2 months in True-life, not 1 - see
                    // AirlineStartupConfig.LeaseDepositMonths' own doc).
                    economyConfig.AirlineStartup.LeaseDepositMonths,
                    used.AirframeHours,
                    used.HoursSinceACheck,
                    used.HoursSinceCCheck,
                    used.ConditionPercent,
                    economyConfig.Maintenance.ACheckIntervalHours,
                    economyConfig.Maintenance.CCheckIntervalHours);
            })
            .OrderBy(t => t.PurchasePriceNew)
            .ToList();

        return Results.Ok(types);
    }

    /// <summary>
    /// Coarse size/role grouping for the buy/lease dialog's filter chips, so a catalogue this long
    /// can be filtered by size or role rather than scrolled. Purely presentational, derived from <see cref="AircraftType.Family"/>
    /// rather than a stored column, so a new family only ever needs adding here, never a migration.
    /// Unrecognised families default to Narrowbody rather than throwing - informational grouping,
    /// not a safety-relevant lookup like <see cref="EconomyConfig.LeaseRateFor"/>.
    /// </summary>
    private static readonly HashSet<string> WidebodyFamilies = new(StringComparer.OrdinalIgnoreCase)
        { "A330", "A350", "A380", "B767", "B777", "B787", "B747" };

    private static readonly HashSet<string> RegionalFamilies = new(StringComparer.OrdinalIgnoreCase)
        { "E-Jet", "CRJ", "ATR", "Dash8" };

    private static string AircraftCategoryFor(string family)
    {
        if (WidebodyFamilies.Contains(family))
        {
            return "Widebody";
        }

        return RegionalFamilies.Contains(family) ? "Regional" : "Narrowbody";
    }

    /// <summary>
    /// Leases an additional aircraft, same shape as the founding lease (AirlineEndpoints.CreateAsync):
    /// a deposit charged up-front, a recurring Lease row EconomyClockService bills monthly. Always a
    /// fresh airframe - leasing an already-worn example isn't offered, because the condition/age
    /// trade-off is deliberately a BUYING decision. Leasing is what gets you a clean aircraft with
    /// no capital outlay and no inherited maintenance, and that contrast is what keeps both routes
    /// to a second aircraft worth taking.
    /// </summary>
    internal static async Task<IResult> LeaseAsync(
        LeaseAircraftRequest request, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline before leasing aircraft." });
        }

        if (request.AircraftTypeId is not Guid aircraftTypeId)
        {
            return Results.BadRequest(new { error = "aircraftTypeId is required." });
        }

        var aircraftType = await db.AircraftTypes.FindAsync([aircraftTypeId], ct);
        if (aircraftType is null)
        {
            return Results.NotFound(new { error = "Aircraft type not found." });
        }

        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);
        // economyConfig.LeaseRateFor, never aircraftType.MonthlyLeaseRate - the same
        // single-source-of-truth rule the founding lease follows (AirlineEndpoints.CreateAsync),
        // now applied to every lease this endpoint writes too. See EconomyConfig.LeaseRates' doc.
        var monthlyRate = economyConfig.LeaseRateFor(aircraftType.IcaoType);
        var deposit = Math.Round(monthlyRate * (decimal)economyConfig.AirlineStartup.LeaseDepositMonths, 2, MidpointRounding.AwayFromZero);

        var cashBalance = await CashBalanceAsync(db, airline.Id, ct);
        if (cashBalance < deposit)
        {
            var currency = await ResolveCurrencyAsync(db, currentUser.UserId, ct);
            return Results.BadRequest(new
            {
                error = $"Insufficient funds - leasing a {aircraftType.Name} needs a deposit of {MoneyFormatter.Format(deposit, currency)}, you have {MoneyFormatter.Format(cashBalance, currency)}.",
            });
        }

        var (registrationError, registration) = await ResolveRegistrationAsync(db, airline, request.Registration, ct);
        if (registrationError is not null)
        {
            return Results.BadRequest(new { error = registrationError });
        }

        var now = DateTimeOffset.UtcNow;

        var fleetAircraft = new FleetAircraft
        {
            Id = Guid.NewGuid(),
            AirlineId = airline.Id,
            AircraftTypeId = aircraftType.Id,
            Registration = registration,
            Ownership = AircraftOwnership.Leased,
            AirframeHours = 0,
            HoursSinceACheck = 0,
            HoursSinceCCheck = 0,
            ConditionPercent = 100,
            LocationIcao = airline.HomeAirportIcao,
            Status = FleetAircraftStatus.Active,
            // Never auto-reserved itself - see EnsureSoleAircraftIsReservedAsync below, which
            // protects the PRE-EXISTING aircraft the moment this one becomes the second, per
            // preferring to reserve an aircraft where the player actually is, so the one held back
            // for them is genuinely usable rather than an arbitrary airframe at the wrong airport.
            ReservedForPlayer = false,
            CreatedUtc = now,
        };

        await EnsureSoleAircraftIsReservedAsync(db, airline.Id, ct);

        db.FleetAircraft.Add(fleetAircraft);
        db.Leases.Add(new Lease
        {
            Id = Guid.NewGuid(),
            AirlineId = airline.Id,
            FleetAircraftId = fleetAircraft.Id,
            MonthlyRate = monthlyRate,
            StartUtc = now,
            IsActive = true,
            CreatedUtc = now,
        });
        db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            AirlineId = airline.Id,
            Utc = now,
            Category = LedgerCategory.LeasePayment,
            Amount = -deposit,
            Description = $"Lease deposit ({economyConfig.AirlineStartup.LeaseDepositMonths:0.#} months): {aircraftType.Name} ({registration})",
        });

        await db.SaveChangesAsync(ct);

        return Results.Created("/api/v1/fleet", new { fleetAircraft, cashBalance = await CashBalanceAsync(db, airline.Id, ct) });
    }

    /// <summary>
    /// Buys an aircraft outright - New at full <see cref="AircraftType.PurchasePrice"/>, or Used at
    /// the discounted price and worn-in hours/condition <see cref="MaintenanceScheduler.ResolveUsedAircraftState"/>
    /// computes - cheap to buy, expensive to run. Genuine milestone
    /// pricing throughout - purchase prices are realistic, unlike the deliberately-cut starter lease
    /// rate, so this is never cheap even used.
    /// </summary>
    internal static async Task<IResult> BuyAsync(
        BuyAircraftRequest request, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline before buying aircraft." });
        }

        if (request.AircraftTypeId is not Guid aircraftTypeId)
        {
            return Results.BadRequest(new { error = "aircraftTypeId is required." });
        }

        var aircraftType = await db.AircraftTypes.FindAsync([aircraftTypeId], ct);
        if (aircraftType is null)
        {
            return Results.NotFound(new { error = "Aircraft type not found." });
        }

        var condition = (request.Condition ?? "New").Trim();
        if (!string.Equals(condition, "New", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(condition, "Used", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = "condition must be 'New' or 'Used'." });
        }

        var isUsed = string.Equals(condition, "Used", StringComparison.OrdinalIgnoreCase);
        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);
        // economyConfig.PurchasePriceFor, never aircraftType.PurchasePrice directly - see that
        // method's own doc. "Used" is always 55% of THIS playstyle's new price.
        var newPrice = economyConfig.PurchasePriceFor(aircraftType);
        var usedState = MaintenanceScheduler.ResolveUsedAircraftState(newPrice, economyConfig);
        var price = isUsed ? usedState.PurchasePrice : newPrice;

        var cashBalance = await CashBalanceAsync(db, airline.Id, ct);
        if (cashBalance < price)
        {
            var currency = await ResolveCurrencyAsync(db, currentUser.UserId, ct);
            return Results.BadRequest(new
            {
                error = $"Insufficient funds - a {condition.ToLowerInvariant()} {aircraftType.Name} costs {MoneyFormatter.Format(price, currency)}, you have {MoneyFormatter.Format(cashBalance, currency)}.",
            });
        }

        var (registrationError, registration) = await ResolveRegistrationAsync(db, airline, request.Registration, ct);
        if (registrationError is not null)
        {
            return Results.BadRequest(new { error = registrationError });
        }

        var now = DateTimeOffset.UtcNow;

        var fleetAircraft = new FleetAircraft
        {
            Id = Guid.NewGuid(),
            AirlineId = airline.Id,
            AircraftTypeId = aircraftType.Id,
            Registration = registration,
            Ownership = AircraftOwnership.Owned,
            AirframeHours = isUsed ? usedState.AirframeHours : 0,
            HoursSinceACheck = isUsed ? usedState.HoursSinceACheck : 0,
            HoursSinceCCheck = isUsed ? usedState.HoursSinceCCheck : 0,
            ConditionPercent = isUsed ? usedState.ConditionPercent : 100,
            LocationIcao = airline.HomeAirportIcao,
            Status = FleetAircraftStatus.Active,
            ReservedForPlayer = false,
            CreatedUtc = now,
        };

        await EnsureSoleAircraftIsReservedAsync(db, airline.Id, ct);

        db.FleetAircraft.Add(fleetAircraft);
        db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            AirlineId = airline.Id,
            Utc = now,
            Category = LedgerCategory.AircraftPurchase,
            Amount = -price,
            Description = $"Purchased {condition.ToLowerInvariant()}: {aircraftType.Name} ({registration})",
        });

        await db.SaveChangesAsync(ct);

        return Results.Created("/api/v1/fleet", new { fleetAircraft, cashBalance = await CashBalanceAsync(db, airline.Id, ct) });
    }

    internal static async Task<IResult> ListLoansAsync(FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(Array.Empty<object>());
        }

        var loans = (await db.Loans.Where(l => l.AirlineId == airline.Id).ToListAsync(ct))
            .OrderByDescending(l => l.StartUtc)
            .ToList();

        return Results.Ok(loans);
    }

    /// <summary>
    /// What the airline could currently borrow - see <see cref="LoanEligibilityCalculator"/>'s class
    /// doc for the cash-flow-based limit and why fleet value doesn't work for this. Exposed
    /// separately from POST /fleet/loans so the borrowing UI can show the limit before the player
    /// commits to a specific principal/term/rate.
    /// </summary>
    internal static async Task<IResult> GetLoanEligibilityAsync(FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.NoContent();
        }

        var trailingNetCashFlow = await TrailingNetOperatingCashFlowAsync(db, airline.Id, DateTimeOffset.UtcNow, ct);
        var maxMonthlyPayment = LoanEligibilityCalculator.MaxMonthlyPayment(trailingNetCashFlow);

        return Results.Ok(new
        {
            trailing30DayNetOperatingCashFlow = trailingNetCashFlow,
            maxMonthlyPayment,
            maxDebtServiceFraction = LoanEligibilityCalculator.MaxDebtServiceFraction,
        });
    }

    /// <summary>
    /// Live preview for the loan dialog: the rate, the monthly repayment and the total interest
    /// over the term are all shown before the player commits, so borrowing is an informed decision
    /// rather than a surprise. Runs the exact same
    /// <see cref="LoanRateCalculator"/> + <see cref="LoanEligibilityCalculator"/> pipeline
    /// <see cref="TakeLoanAsync"/> uses, so what the dialog shows can never disagree with what
    /// taking the loan actually charges. Read-only and side-effect-free - callable as often as the
    /// amount/term inputs change, unlike POST /fleet/loans which actually commits the loan.
    /// </summary>
    internal static async Task<IResult> GetLoanQuoteAsync(
        decimal amount, int termMonths, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline before taking a loan." });
        }

        if (amount <= 0)
        {
            return Results.BadRequest(new { error = "amount must be greater than zero." });
        }

        if (termMonths is < 1 or > 360)
        {
            return Results.BadRequest(new { error = "termMonths must be between 1 and 360." });
        }

        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);
        var trailingNetCashFlow = await TrailingNetOperatingCashFlowAsync(db, airline.Id, DateTimeOffset.UtcNow, ct);
        var annualRatePct = LoanRateCalculator.ComputeAnnualRatePct(amount, termMonths, trailingNetCashFlow, economyConfig.Loan);
        var eligibility = LoanEligibilityCalculator.Evaluate(amount, annualRatePct, termMonths, trailingNetCashFlow);
        var totalInterest = eligibility.MonthlyPayment * termMonths - amount;

        return Results.Ok(new
        {
            annualRatePct,
            monthlyPayment = eligibility.MonthlyPayment,
            totalInterest,
            isEligible = eligibility.IsEligible,
            maxMonthlyPayment = eligibility.MaxMonthlyPayment,
            trailing30DayNetOperatingCashFlow = trailingNetCashFlow,
            maxDebtServiceFraction = LoanEligibilityCalculator.MaxDebtServiceFraction,
        });
    }

    /// <summary>
    /// Mid-game borrowing, the classic leverage decision: borrow to grow faster and carry the
    /// repayment, or wait and stay unencumbered. Extends the Loan entity/annuity
    /// amortisation Chunk B built for airline creation to any point in an airline's life. Bounded by
    /// <see cref="LoanEligibilityCalculator"/> so borrowing accelerates progression rather than
    /// trivialising it. Repaid monthly through EconomyClockService, same as lease/salary/insurance.
    /// The rate is ALWAYS computed by <see cref="LoanRateCalculator"/>, exactly like
    /// <see cref="GetLoanQuoteAsync"/> previews it. The rate is the simulation's to set and never
    /// the player's: one they control can be set to zero, which makes borrowing free.
    /// TakeLoanRequest has no rate field at all, so there is
    /// nothing here for a caller to supply or for this endpoint to trust.
    /// </summary>
    internal static async Task<IResult> TakeLoanAsync(
        TakeLoanRequest request, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline before taking a loan." });
        }

        if (request.Amount <= 0)
        {
            return Results.BadRequest(new { error = "amount must be greater than zero." });
        }

        if (request.TermMonths is < 1 or > 360)
        {
            return Results.BadRequest(new { error = "termMonths must be between 1 and 360." });
        }

        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);
        var trailingNetCashFlow = await TrailingNetOperatingCashFlowAsync(db, airline.Id, DateTimeOffset.UtcNow, ct);
        var annualRatePct = LoanRateCalculator.ComputeAnnualRatePct(request.Amount, request.TermMonths, trailingNetCashFlow, economyConfig.Loan);
        var eligibility = LoanEligibilityCalculator.Evaluate(request.Amount, annualRatePct, request.TermMonths, trailingNetCashFlow);

        if (!eligibility.IsEligible)
        {
            var currency = await ResolveCurrencyAsync(db, currentUser.UserId, ct);
            return Results.BadRequest(new
            {
                error = $"This loan's monthly payment ({MoneyFormatter.Format(eligibility.MonthlyPayment, currency)}) exceeds what your airline's recent cash flow can service " +
                         $"(max {MoneyFormatter.Format(eligibility.MaxMonthlyPayment, currency)}/month, {LoanEligibilityCalculator.MaxDebtServiceFraction:P0} of trailing 30-day net operating cash flow). " +
                         "Try a smaller amount, a longer term, or grow your revenue first.",
                monthlyPayment = eligibility.MonthlyPayment,
                maxMonthlyPayment = eligibility.MaxMonthlyPayment,
            });
        }

        var now = DateTimeOffset.UtcNow;
        var loan = new Loan
        {
            Id = Guid.NewGuid(),
            AirlineId = airline.Id,
            Principal = request.Amount,
            AnnualInterestRate = annualRatePct,
            TermMonths = request.TermMonths,
            MonthlyPayment = eligibility.MonthlyPayment,
            RemainingBalance = request.Amount,
            StartUtc = now,
            IsPaidOff = false,
            CreatedUtc = now,
        };

        db.Loans.Add(loan);
        db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            AirlineId = airline.Id,
            Utc = now,
            Category = LedgerCategory.LoanProceeds,
            Amount = request.Amount,
            Description = $"Loan proceeds ({request.TermMonths} months at {annualRatePct:0.##}%)",
        });

        await db.SaveChangesAsync(ct);

        return Results.Created("/api/v1/fleet/loans", new { loan, cashBalance = await CashBalanceAsync(db, airline.Id, ct) });
    }

    /// <summary>
    /// Fires the moment a fleet exceeds one aircraft. One airframe is always kept free for the
    /// human: opening the app to find your whole fleet booked out to virtual pilots is the fastest
    /// way to feel locked out of your own airline. Below two aircraft the rule does not apply at
    /// all, or a new airline's pilots could never fly anything. A brand-new
    /// airline's sole aircraft already starts reserved (AirlineEndpoints.CreateAsync), but this is
    /// the moment that reservation actually starts to matter (before now there was only ever one
    /// aircraft to fly anyway), so this defensively (re-)asserts it rather than trusting it was
    /// never since cleared. After this call, reservation is entirely player-controlled via
    /// PUT /fleet/{id}/reservation - nothing else in the app touches this flag again.
    /// </summary>
    private static async Task EnsureSoleAircraftIsReservedAsync(FsOpsDbContext db, Guid airlineId, CancellationToken ct)
    {
        var existingFleet = await db.FleetAircraft.Where(f => f.AirlineId == airlineId).ToListAsync(ct);
        if (existingFleet.Count == 1 && !existingFleet.Any(f => f.ReservedForPlayer))
        {
            existingFleet[0].ReservedForPlayer = true;
        }
    }

    /// <summary>
    /// Reservation is the sole gate on both sides (decided 2026-08-09): the
    /// player may only fly a reserved aircraft, and a reserved aircraft is never offered to the
    /// scheduler - so reserving one that a virtual pilot is already scheduled to fly would create
    /// exactly the unrepresentable conflict 3a exists to prevent. Releasing (reserved -&gt; not
    /// reserved) never has this problem - a reserved aircraft was never offered to the scheduler in
    /// the first place, so it cannot already carry scheduled legs to worry about.
    /// <para>
    /// So: reserving (<see cref="SetReservationRequest.Reserved"/> true) an aircraft that already
    /// has active scheduled legs is REFUSED by default (409), naming every offending leg (pilot,
    /// day, route) so the player knows exactly what stands in the way - never a silent drop. Passing
    /// <see cref="SetReservationRequest.ForceClearSchedule"/> true clears those legs (soft-deletes
    /// the entries) and then reserves the aircraft, with the cleared legs echoed back in the
    /// response so the consequence is stated plainly, not discovered later as missing revenue.
    /// </para>
    /// </summary>
    internal static async Task<IResult> SetReservationAsync(
        Guid id, SetReservationRequest request, FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline before managing the fleet." });
        }

        var aircraft = await db.FleetAircraft.FirstOrDefaultAsync(f => f.Id == id && f.AirlineId == airline.Id, ct);
        if (aircraft is null)
        {
            return Results.NotFound();
        }

        var clearedLegs = Array.Empty<object>() as IReadOnlyList<object>;

        if (request.Reserved && !aircraft.ReservedForPlayer)
        {
            var affectedEntries = await db.PilotScheduleEntries.Where(e => e.FleetAircraftId == aircraft.Id).ToListAsync(ct);
            if (affectedEntries.Count > 0)
            {
                var scheduleIds = affectedEntries.Select(e => e.PilotScheduleId).Distinct().ToList();
                var schedules = await db.PilotSchedules.Where(s => scheduleIds.Contains(s.Id)).ToListAsync(ct);
                var pilotIdByScheduleId = schedules.ToDictionary(s => s.Id, s => s.PilotId);
                var pilotIds = pilotIdByScheduleId.Values.Distinct().ToList();
                var pilotsById = await db.Pilots.Where(p => pilotIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
                var routeIds = affectedEntries.Select(e => e.RouteId).Distinct().ToList();
                var routesById = await db.Routes.Where(r => routeIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);

                object DescribeLeg(PilotScheduleEntry e)
                {
                    var pilotId = pilotIdByScheduleId.TryGetValue(e.PilotScheduleId, out var pid) ? pid : Guid.Empty;
                    pilotsById.TryGetValue(pilotId, out var pilot);
                    routesById.TryGetValue(e.RouteId, out var route);
                    return new
                    {
                        pilotId,
                        pilotName = pilot?.Name,
                        dayOfWeek = (int)e.DayOfWeek,
                        departureTimeUtc = e.DepartureTimeUtc.ToString(@"hh\:mm\:ss"),
                        routeId = e.RouteId,
                        departureIcao = route?.DepartureIcao,
                        arrivalIcao = route?.ArrivalIcao,
                    };
                }

                if (!request.ForceClearSchedule)
                {
                    return Results.Conflict(new
                    {
                        error = $"{aircraft.Registration} is scheduled to fly {affectedEntries.Count} leg(s) - reserving it for the player would leave those legs " +
                                 "with no aircraft. Clear them first, or resend with forceClearSchedule to clear them now.",
                        offendingLegs = affectedEntries.Select(DescribeLeg).ToList(),
                    });
                }

                var now = DateTimeOffset.UtcNow;
                var cleared = affectedEntries.Select(DescribeLeg).ToList();
                foreach (var entry in affectedEntries)
                {
                    entry.DeletedUtc = now;
                }

                clearedLegs = cleared;
            }
        }

        aircraft.ReservedForPlayer = request.Reserved;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { aircraft.Id, aircraft.Registration, aircraft.ReservedForPlayer, clearedLegs });
    }

    /// <summary>Cash is never a stored column - see the project convention. Same materialise-then-sum
    /// pattern as AirlineEndpoints.BuildSummaryAsync (SQLite can't translate SumAsync over decimal).</summary>
    private static async Task<decimal> CashBalanceAsync(FsOpsDbContext db, Guid airlineId, CancellationToken ct)
    {
        var amounts = await db.LedgerTransactions.Where(t => t.AirlineId == airlineId).Select(t => t.Amount).ToListAsync(ct);
        return amounts.Sum();
    }

    /// <summary>
    /// The player's own display currency, for formatting a money figure inside an error message -
    /// every stored figure stays in the base unit (GBP) forever (see <see cref="MoneyFormatter"/>'s
    /// own doc); only text shown back to the player should ever be converted. Falls back to the
    /// base currency if <see cref="UserSettings"/> somehow doesn't exist yet or holds an
    /// unrecognised code - never expected once an airline exists, but a refusal message must never
    /// throw over a missing settings row.
    /// </summary>
    private static async Task<Currency> ResolveCurrencyAsync(FsOpsDbContext db, Guid ownerUserId, CancellationToken ct)
    {
        var settings = await db.UserSettings.FirstOrDefaultAsync(s => s.OwnerUserId == ownerUserId, ct);
        return CurrencyCatalogue.TryGet(settings?.CurrencyCode) ?? CurrencyCatalogue.TryGet(CurrencyCatalogue.BaseCurrencyCode)!;
    }

    /// <summary>
    /// Trailing 30-day net cash flow, excluding <see cref="LedgerCategory.StartingCapital"/> and
    /// <see cref="LedgerCategory.LoanProceeds"/> - see <see cref="LoanEligibilityCalculator"/>'s class
    /// doc for why: both are one-off injections, not repeatable income, and either would let an
    /// airline borrow against money it didn't earn from flying. Materialised before filtering by date
    /// - SQLite's EF provider can't translate a DateTimeOffset comparison in the query itself.
    /// </summary>
    private static async Task<decimal> TrailingNetOperatingCashFlowAsync(FsOpsDbContext db, Guid airlineId, DateTimeOffset now, CancellationToken ct)
    {
        var cutoff = now.AddDays(-30);
        var transactions = await db.LedgerTransactions
            .Where(t => t.AirlineId == airlineId && t.Category != LedgerCategory.StartingCapital && t.Category != LedgerCategory.LoanProceeds)
            .ToListAsync(ct);

        return transactions.Where(t => t.Utc >= cutoff).Sum(t => t.Amount);
    }

    /// <summary>
    /// Live preview for the buy/lease dialog's "randomise" button and its pre-filled suggestion -
    /// the suggestion is shown pre-filled so that randomising is the zero-effort path and typing
    /// over it is the deliberate one. Read-only: never reserves the registration, since the player may never submit the
    /// purchase - the actual endpoints re-check uniqueness at commit time regardless.
    /// </summary>
    internal static async Task<IResult> GetRegistrationSuggestionAsync(FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline before buying or leasing aircraft." });
        }

        var (_, registration) = await ResolveRegistrationAsync(db, airline, requestedRegistration: null, ct);
        return Results.Ok(new { registration });
    }

    /// <summary>
    /// Repaints happen, so an existing aircraft can be renamed from the Fleet page, subject to the
    /// same uniqueness rule as a custom registration at acquisition. Light validation only, same as
    /// buying/leasing: never a country-format check.
    /// </summary>
    internal static async Task<IResult> RenameAsync(
        Guid id, RenameAircraftRequest request, FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline before managing the fleet." });
        }

        var aircraft = await db.FleetAircraft.FirstOrDefaultAsync(f => f.Id == id && f.AirlineId == airline.Id, ct);
        if (aircraft is null)
        {
            return Results.NotFound();
        }

        var normalized = NormalizeRegistration(request.Registration);
        if (!AircraftRegistrationGenerator.IsValidCustomRegistration(normalized))
        {
            return Results.BadRequest(new { error = "Registration must be 2-10 characters: letters, digits and hyphens only." });
        }

        var existing = (await db.FleetAircraft
                .Where(f => f.AirlineId == airline.Id && f.Id != id)
                .Select(f => f.Registration)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (existing.Contains(normalized))
        {
            return Results.BadRequest(new { error = $"'{normalized}' is already used elsewhere in your fleet." });
        }

        aircraft.Registration = normalized;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { aircraft.Id, aircraft.Registration });
    }

    private static string NormalizeRegistration(string? raw) => (raw ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>
    /// Resolves the registration for a newly acquired aircraft - either the player's own custom
    /// entry (validated lightly, never against a country's format: liveries vary wildly, and a
    /// player matching a specific repaint knows what they want better than a validator does, so
    /// only what would break the app is rejected) or a freshly generated one. The country comes from the airline's
    /// HOME AIRPORT at this exact moment (acquisition time), never from anywhere the aircraft might
    /// later fly. That is also how reality works: an aircraft is registered in its operator's home
    /// country, not wherever it happens to be parked, so one night-stopping in Spain keeps its
    /// G- tail.
    ///
    /// <para>Uniqueness is achieved by REGENERATING - calling <see cref="AircraftRegistrationGenerator.Generate"/>
    /// again with a fresh random draw - never by appending a numeric suffix, which is what produced
    /// invalid registrations like "G-OLAF1" before this fix.</para>
    /// </summary>
    private static async Task<(string? Error, string Registration)> ResolveRegistrationAsync(
        FsOpsDbContext db, Airline airline, string? requestedRegistration, CancellationToken ct)
    {
        var existing = (await db.FleetAircraft.Where(f => f.AirlineId == airline.Id).Select(f => f.Registration).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(requestedRegistration))
        {
            var normalized = NormalizeRegistration(requestedRegistration);
            if (!AircraftRegistrationGenerator.IsValidCustomRegistration(normalized))
            {
                return ("Registration must be 2-10 characters: letters, digits and hyphens only.", string.Empty);
            }

            if (existing.Contains(normalized))
            {
                return ($"'{normalized}' is already used in your fleet.", string.Empty);
            }

            return (null, normalized);
        }

        var homeAirport = await db.Airports.FirstOrDefaultAsync(a => a.Icao == airline.HomeAirportIcao, ct);

        for (var attempt = 0; attempt < 1000; attempt++)
        {
            var candidate = AircraftRegistrationGenerator.Generate(homeAirport?.Country);
            if (existing.Add(candidate))
            {
                return (null, candidate);
            }
        }

        // Astronomically unlikely (it would mean 1,000 freshly-generated candidates all collided
        // with an existing registration), but never return a colliding registration regardless.
        return (null, $"FS-{Guid.NewGuid().ToString("N")[..4].ToUpperInvariant()}");
    }
}

/// <summary>
/// <see cref="Registration"/> is the player's own custom entry (optional) - people want their tail
/// to match the livery they fly. Null or blank means "generate one".
/// </summary>
public record LeaseAircraftRequest(Guid? AircraftTypeId, string? Registration = null);

public record BuyAircraftRequest(Guid? AircraftTypeId, string? Condition, string? Registration = null);

/// <summary>See FleetEndpoints.RenameAsync.</summary>
public record RenameAircraftRequest(string? Registration);

/// <summary>
/// A mid-game loan request. Deliberately has NO rate field: a rate the player supplies can be set
/// to zero, which makes borrowing free. The rate is always computed by
/// <see cref="LoanRateCalculator"/>; there is nothing here for a caller to supply or for
/// <see cref="FleetEndpoints.TakeLoanAsync"/> to trust.
/// </summary>
public record TakeLoanRequest(decimal Amount, int TermMonths);

/// <summary>See FleetEndpoints.SetReservationAsync. <see cref="ForceClearSchedule"/> only matters
/// when <see cref="Reserved"/> is true and the aircraft already has scheduled legs - it confirms the
/// player has seen the consequence and wants those legs cleared rather than the request refused.</summary>
public record SetReservationRequest(bool Reserved, bool ForceClearSchedule = false);

/// <summary>One fleet aircraft, enriched for the Fleet page - see FleetEndpoints.ListAsync.</summary>
public record FleetAircraftSummary(
    Guid Id,
    string Registration,
    Guid AircraftTypeId,
    string AircraftTypeName,
    string Family,
    int PaxCapacity,
    string Ownership,
    string Status,
    string LocationIcao,
    double AirframeHours,
    double HoursSinceACheck,
    double HoursSinceCCheck,
    double HoursToNextACheck,
    double HoursToNextCCheck,
    double ConditionPercent,
    double FuelOnBoardKg,
    DateTimeOffset? GroundedUntilUtc,
    string? GroundedReason,
    bool ReservedForPlayer,
    DateTimeOffset CreatedUtc);

/// <summary>One buyable/leasable aircraft type, with new/used pricing and exactly what a used
/// example would start at - see FleetEndpoints.ListAircraftTypesAsync.</summary>
public record AircraftTypeOption(
    Guid Id,
    string IcaoType,
    string Family,
    string Manufacturer,
    string Name,
    string Category,
    int PaxCapacity,
    int RangeNm,
    decimal PurchasePriceNew,
    decimal PurchasePriceUsed,
    decimal MonthlyLeaseRate,
    double LeaseDepositMonths,
    double UsedAirframeHours,
    double UsedHoursSinceACheck,
    double UsedHoursSinceCCheck,
    double UsedConditionPercent,
    double ACheckIntervalHours,
    double CCheckIntervalHours);
