using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Finance;
using FSOps.Data;
using FSOps.Server.Auth;
using FSOps.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Endpoints;

/// <summary>
/// Fleet finance and the Fleet page's backing data - see docs/PLAN.md's E1 brief ("Fleet finance",
/// "The Fleet page"). Buying/leasing additional aircraft, buying used (cheap to buy, expensive to
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
        group.MapPost("/fleet/lease", LeaseAsync);
        group.MapPost("/fleet/buy", BuyAsync);
        group.MapGet("/fleet/loans", ListLoansAsync);
        group.MapGet("/fleet/loan-eligibility", GetLoanEligibilityAsync);
        group.MapGet("/fleet/loan-quote", GetLoanQuoteAsync);
        group.MapPost("/fleet/loans", TakeLoanAsync);
    }

    /// <summary>
    /// Everything the Fleet page shows per aircraft: identity, location, ownership, condition/hours
    /// and - for a grounded aircraft - why and until when, never just "in maintenance" (docs/PLAN.md's
    /// E1 brief). Releases any aircraft whose downtime has already elapsed first, same as the Fly
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
                f.CreatedUtc);
        }).ToList();

        return Results.Ok(summaries);
    }

    /// <summary>
    /// The buy/lease picker's catalogue: every seeded aircraft type with its new purchase price,
    /// lease rate, and - because the trade-off must be informed before purchase, never a surprise
    /// (docs/PLAN.md "Used aircraft") - exactly what a used example of this type would cost and what
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
            .OrderBy(t => t.PurchasePrice)
            .Select(t =>
            {
                var used = MaintenanceScheduler.ResolveUsedAircraftState(t.PurchasePrice, economyConfig);
                // economyConfig.LeaseRateFor, never t.MonthlyLeaseRate - see EconomyConfig.LeaseRates'
                // doc comment for why the DB column is never read for pricing or display.
                return new AircraftTypeOption(
                    t.Id,
                    t.IcaoType,
                    t.Family,
                    t.Manufacturer,
                    t.Name,
                    t.PaxCapacity,
                    t.RangeNm,
                    t.PurchasePrice,
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
            .ToList();

        return Results.Ok(types);
    }

    /// <summary>
    /// Leases an additional aircraft, same shape as the founding lease (AirlineEndpoints.CreateAsync):
    /// a deposit charged up-front, a recurring Lease row EconomyClockService bills monthly. Always a
    /// fresh airframe - leasing an already-worn example isn't offered (see docs/PLAN.md, which frames
    /// the condition/age trade-off as a BUYING decision, not a leasing one).
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
            return Results.BadRequest(new
            {
                error = $"Insufficient funds - leasing a {aircraftType.Name} needs a deposit of {deposit:F2}, you have {cashBalance:F2}.",
            });
        }

        var now = DateTimeOffset.UtcNow;
        var registration = await GenerateUniqueRegistrationAsync(db, airline, ct);

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
            CreatedUtc = now,
        };

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
    /// computes (docs/PLAN.md "Used aircraft - cheap to buy, expensive to run"). Genuine milestone
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
        var usedState = MaintenanceScheduler.ResolveUsedAircraftState(aircraftType.PurchasePrice, economyConfig);
        var price = isUsed ? usedState.PurchasePrice : aircraftType.PurchasePrice;

        var cashBalance = await CashBalanceAsync(db, airline.Id, ct);
        if (cashBalance < price)
        {
            return Results.BadRequest(new
            {
                error = $"Insufficient funds - a {condition.ToLowerInvariant()} {aircraftType.Name} costs {price:F2}, you have {cashBalance:F2}.",
            });
        }

        var now = DateTimeOffset.UtcNow;
        var registration = await GenerateUniqueRegistrationAsync(db, airline, ct);

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
            CreatedUtc = now,
        };

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
    /// Live preview for the loan dialog - docs/PLAN.md "Show the rate before the player commits,
    /// along with the monthly repayment and total interest over the term". Runs the exact same
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
    /// Mid-game borrowing - docs/PLAN.md "Loans accelerate it": extends the Loan entity/annuity
    /// amortisation Chunk B built for airline creation to any point in an airline's life. Bounded by
    /// <see cref="LoanEligibilityCalculator"/> so borrowing accelerates progression rather than
    /// trivialising it. Repaid monthly through EconomyClockService, same as lease/salary/insurance.
    /// The rate is ALWAYS computed by <see cref="LoanRateCalculator"/>, exactly like
    /// <see cref="GetLoanQuoteAsync"/> previews it - see docs/PLAN.md "Loan interest is set by the
    /// simulation, never by the player". TakeLoanRequest has no rate field at all, so there is
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
            return Results.BadRequest(new
            {
                error = $"This loan's monthly payment ({eligibility.MonthlyPayment:F2}) exceeds what your airline's recent cash flow can service " +
                         $"(max {eligibility.MaxMonthlyPayment:F2}/month, {LoanEligibilityCalculator.MaxDebtServiceFraction:P0} of trailing 30-day net operating cash flow). " +
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

    /// <summary>Cash is never a stored column - see the project convention. Same materialise-then-sum
    /// pattern as AirlineEndpoints.BuildSummaryAsync (SQLite can't translate SumAsync over decimal).</summary>
    private static async Task<decimal> CashBalanceAsync(FsOpsDbContext db, Guid airlineId, CancellationToken ct)
    {
        var amounts = await db.LedgerTransactions.Where(t => t.AirlineId == airlineId).Select(t => t.Amount).ToListAsync(ct);
        return amounts.Sum();
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
    /// AircraftRegistrationGenerator.Generate is deterministic purely from the airline's ICAO code,
    /// so a second call for the same airline produces the exact same tail number as the founding
    /// aircraft - fine when it is only ever called once (at creation), not fine now that the fleet
    /// screen can add aircraft repeatedly. Appends a numeric suffix until the result is unique within
    /// this airline's fleet, so two aircraft never end up sharing a registration.
    /// </summary>
    private static async Task<string> GenerateUniqueRegistrationAsync(FsOpsDbContext db, Airline airline, CancellationToken ct)
    {
        var homeAirport = await db.Airports.FirstOrDefaultAsync(a => a.Icao == airline.HomeAirportIcao, ct);
        var baseRegistration = Core.Airlines.AircraftRegistrationGenerator.Generate(homeAirport?.Country, airline.IcaoCode);

        var existing = (await db.FleetAircraft.Where(f => f.AirlineId == airline.Id).Select(f => f.Registration).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!existing.Contains(baseRegistration))
        {
            return baseRegistration;
        }

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            var candidate = $"{baseRegistration}{suffix}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }

        // Astronomically unlikely (it would mean 998 aircraft of the same registration prefix
        // already exist), but never return a colliding registration under any circumstances.
        return $"{baseRegistration}{Guid.NewGuid().ToString("N")[..4].ToUpperInvariant()}";
    }
}

public record LeaseAircraftRequest(Guid? AircraftTypeId);

public record BuyAircraftRequest(Guid? AircraftTypeId, string? Condition);

/// <summary>
/// A mid-game loan request. Deliberately has NO rate field - see docs/PLAN.md "Loan interest is set
/// by the simulation, never by the player". The rate is always computed by
/// <see cref="LoanRateCalculator"/>; there is nothing here for a caller to supply or for
/// <see cref="FleetEndpoints.TakeLoanAsync"/> to trust.
/// </summary>
public record TakeLoanRequest(decimal Amount, int TermMonths);

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
    DateTimeOffset CreatedUtc);

/// <summary>One buyable/leasable aircraft type, with new/used pricing and exactly what a used
/// example would start at - see FleetEndpoints.ListAircraftTypesAsync.</summary>
public record AircraftTypeOption(
    Guid Id,
    string IcaoType,
    string Family,
    string Manufacturer,
    string Name,
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
