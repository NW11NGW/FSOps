using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Finance;
using FSOps.Data;
using FSOps.Server.Auth;
using FSOps.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Endpoints;

/// <summary>
/// Backs the Finances page - see docs/PLAN.md "What the Finances page must contain (specified by
/// the user, 2026-08-08)": cash and the headline P&amp;L, leases with real payment dates and an
/// exit, loans with full/partial early repayment, per-pilot revenue vs cost, a fixed/variable cost
/// breakdown, and P&amp;L per route. Every figure here is read from posted
/// <see cref="LedgerTransaction"/>/<see cref="Flight"/> rows rather than recomputed from scratch, so
/// nothing shown here can ever disagree with what actually moved the cash balance - the plan's own
/// closing requirement for this page.
/// </summary>
public static class FinanceEndpoints
{
    public static void MapFinanceEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/finance/summary", SummaryAsync);
        group.MapGet("/finance/leases", LeasesAsync);
        group.MapGet("/finance/loans", LoansAsync);
        group.MapGet("/finance/loans/{id:guid}/settlement-quote", LoanSettlementQuoteAsync);
        group.MapPost("/finance/loans/{id:guid}/settle", SettleLoanAsync);
        group.MapPost("/finance/loans/{id:guid}/overpay", OverpayLoanAsync);
        group.MapGet("/finance/pilots", PilotsAsync);
        group.MapGet("/finance/costs", CostsAsync);
        group.MapGet("/finance/routes", RoutesAsync);
        group.MapGet("/finance/ledger", LedgerAsync);
    }

    private static readonly TimeSpan TrailingWindow = TimeSpan.FromDays(30);

    internal static async Task<IResult> SummaryAsync(FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(new
            {
                cashBalance = 0m,
                cashBalance30DaysAgo = 0m,
                currentPeriodStartUtc = (DateTimeOffset?)null,
                currentPeriodEndUtc = (DateTimeOffset?)null,
                currentPeriodIncome = 0m,
                currentPeriodExpenditure = 0m,
                currentPeriodNet = 0m,
            });
        }

        var now = DateTimeOffset.UtcNow;
        var transactions = await db.LedgerTransactions.Where(t => t.AirlineId == airline.Id).ToListAsync(ct);

        var cashBalance = transactions.Sum(t => t.Amount);
        var cashBalance30DaysAgo = cashBalance - transactions.Where(t => t.Utc >= now - TrailingWindow).Sum(t => t.Amount);

        var economyState = await db.EconomyStates.FirstOrDefaultAsync(ct);
        var periodStart = economyState?.LastProcessedUtc ?? now;
        var periodEnd = periodStart + EconomyClockService.PeriodLength;

        var periodTransactions = transactions.Where(t => t.Utc >= periodStart).ToList();
        var income = periodTransactions.Where(t => t.Amount > 0).Sum(t => t.Amount);
        var expenditure = periodTransactions.Where(t => t.Amount < 0).Sum(t => t.Amount);

        return Results.Ok(new
        {
            cashBalance,
            cashBalance30DaysAgo,
            currentPeriodStartUtc = periodStart,
            currentPeriodEndUtc = periodEnd,
            currentPeriodIncome = income,
            currentPeriodExpenditure = expenditure,
            currentPeriodNet = income + expenditure,
        });
    }

    internal static async Task<IResult> LeasesAsync(FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(new { leases = Array.Empty<object>(), totalMonthlyCommitment = 0m });
        }

        var leases = await db.Leases.Where(l => l.AirlineId == airline.Id && l.IsActive && l.DeletedUtc == null).ToListAsync(ct);
        if (leases.Count == 0)
        {
            return Results.Ok(new { leases = Array.Empty<object>(), totalMonthlyCommitment = 0m });
        }

        var economyState = await db.EconomyStates.FirstOrDefaultAsync(ct);
        var fleetIds = leases.Select(l => l.FleetAircraftId).ToList();
        var fleet = await db.FleetAircraft.Where(f => fleetIds.Contains(f.Id)).ToListAsync(ct);
        var typeIds = fleet.Select(f => f.AircraftTypeId).Distinct().ToList();
        var types = await db.AircraftTypes.Where(t => typeIds.Contains(t.Id)).ToListAsync(ct);
        var typeById = types.ToDictionary(t => t.Id);
        var fleetById = fleet.ToDictionary(f => f.Id);

        var result = leases.Select(l =>
        {
            var aircraft = fleetById.GetValueOrDefault(l.FleetAircraftId);
            var typeName = aircraft is not null && typeById.TryGetValue(aircraft.AircraftTypeId, out var type) ? type.Name : string.Empty;
            var periodStart = economyState?.LastProcessedUtc ?? l.StartUtc;

            return new
            {
                leaseId = l.Id,
                fleetAircraftId = l.FleetAircraftId,
                registration = aircraft?.Registration ?? string.Empty,
                aircraftTypeName = typeName,
                monthlyRate = l.MonthlyRate,
                startUtc = l.StartUtc,
                nextPaymentUtc = periodStart + EconomyClockService.PeriodLength,
            };
        }).OrderBy(l => l.registration).ToList();

        return Results.Ok(new { leases = result, totalMonthlyCommitment = leases.Sum(l => l.MonthlyRate) });
    }

    internal static async Task<IResult> LoansAsync(FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(new { loans = Array.Empty<object>() });
        }

        var loans = (await db.Loans.Where(l => l.AirlineId == airline.Id && l.DeletedUtc == null).ToListAsync(ct))
            .OrderByDescending(l => l.StartUtc)
            .ToList();

        var result = loans.Select(l =>
        {
            var (remainingTermMonths, totalInterestRemaining) = l.IsPaidOff
                ? (0, 0m)
                : LoanSettlementCalculator.SimulateRemainingAmortization(l.RemainingBalance, l.AnnualInterestRate, l.MonthlyPayment);

            return new
            {
                loanId = l.Id,
                principal = l.Principal,
                remainingBalance = l.RemainingBalance,
                annualInterestRate = l.AnnualInterestRate,
                monthlyPayment = l.MonthlyPayment,
                startUtc = l.StartUtc,
                termMonths = l.TermMonths,
                remainingTermMonths,
                totalInterestRemaining,
                isPaidOff = l.IsPaidOff,
            };
        }).ToList();

        return Results.Ok(new { loans = result });
    }

    internal static async Task<IResult> LoanSettlementQuoteAsync(Guid id, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var (loan, error) = await ResolveLoanAsync(db, currentUser, id, ct);
        if (error is not null)
        {
            return error;
        }

        if (loan!.IsPaidOff)
        {
            return Results.BadRequest(new { error = "This loan is already paid off." });
        }

        var airlinePlaystyle = (await db.Airlines.FirstAsync(a => a.Id == loan.AirlineId, ct)).Playstyle;
        var economyConfig = economyConfigCatalog.Get(airlinePlaystyle);
        var quote = LoanSettlementCalculator.ComputeFullPayoffQuote(loan.RemainingBalance, loan.AnnualInterestRate, loan.MonthlyPayment, economyConfig.LoanEarlySettlement);

        return Results.Ok(new
        {
            loanId = loan.Id,
            remainingBalance = quote.RemainingBalance,
            earlySettlementFee = quote.EarlySettlementFee,
            totalPayoff = quote.TotalPayoff,
            interestSaved = quote.InterestSaved,
        });
    }

    /// <summary>
    /// Settles the loan in full. Requires <see cref="SettleLoanRequest.ExpectedTotalPayoff"/> - the
    /// exact figure the player confirmed on <see cref="LoanSettlementQuoteAsync"/>. The payoff is
    /// recomputed here from the loan's CURRENT <see cref="Loan.RemainingBalance"/> and compared
    /// against it; on any mismatch nothing is posted and the new figure is returned instead. This
    /// guards against the same class of drift as <see cref="FleetDisposalEndpoints"/>'s sell/
    /// end-lease commits: RemainingBalance can move between the quote and the commit if
    /// <c>EconomyClockService</c>'s background billing tick posts a monthly payment against this
    /// loan in that window.
    /// </summary>
    internal static async Task<IResult> SettleLoanAsync(Guid id, SettleLoanRequest request, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var (loan, error) = await ResolveLoanAsync(db, currentUser, id, ct);
        if (error is not null)
        {
            return error;
        }

        if (loan!.IsPaidOff)
        {
            return Results.BadRequest(new { error = "This loan is already paid off." });
        }

        var airline = await db.Airlines.FirstAsync(a => a.Id == loan.AirlineId, ct);
        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);
        var quote = LoanSettlementCalculator.ComputeFullPayoffQuote(loan.RemainingBalance, loan.AnnualInterestRate, loan.MonthlyPayment, economyConfig.LoanEarlySettlement);

        if (quote.TotalPayoff != request.ExpectedTotalPayoff)
        {
            return Results.BadRequest(new
            {
                error = $"The payoff figure has changed since you last checked (was {request.ExpectedTotalPayoff:F2}, now {quote.TotalPayoff:F2}) - please confirm the new figure.",
                currentTotalPayoff = quote.TotalPayoff,
            });
        }

        var cashBalance = await CashBalanceAsync(db, airline.Id, ct);
        if (cashBalance < quote.TotalPayoff)
        {
            return Results.BadRequest(new { error = $"Settling this loan needs {quote.TotalPayoff:F2}, you have {cashBalance:F2}." });
        }

        var now = DateTimeOffset.UtcNow;
        loan.RemainingBalance = 0m;
        loan.IsPaidOff = true;

        db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            AirlineId = airline.Id,
            Utc = now,
            Category = LedgerCategory.LoanPayment,
            Amount = -quote.RemainingBalance,
            Description = "Loan settled early - remaining principal",
        });
        db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            AirlineId = airline.Id,
            Utc = now,
            Category = LedgerCategory.LoanPayment,
            Amount = -quote.EarlySettlementFee,
            Description = "Loan settled early - early-settlement fee",
        });

        await db.SaveChangesAsync(ct);

        return Results.Ok(new { loanId = loan.Id, totalPayoff = quote.TotalPayoff, cashBalance = await CashBalanceAsync(db, airline.Id, ct) });
    }

    internal static async Task<IResult> OverpayLoanAsync(Guid id, OverpayLoanRequest request, FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var (loan, error) = await ResolveLoanAsync(db, currentUser, id, ct);
        if (error is not null)
        {
            return error;
        }

        if (loan!.IsPaidOff)
        {
            return Results.BadRequest(new { error = "This loan is already paid off." });
        }

        if (request.Amount <= 0)
        {
            return Results.BadRequest(new { error = "amount must be greater than zero." });
        }

        if (request.Amount > loan.RemainingBalance)
        {
            return Results.BadRequest(new { error = $"amount ({request.Amount:F2}) exceeds the remaining balance ({loan.RemainingBalance:F2}) - use full settlement instead." });
        }

        var cashBalance = await CashBalanceAsync(db, loan.AirlineId, ct);
        if (cashBalance < request.Amount)
        {
            return Results.BadRequest(new { error = $"Overpaying {request.Amount:F2} needs that much cash, you have {cashBalance:F2}." });
        }

        var newBalance = LoanSettlementCalculator.ApplyOverpayment(loan.RemainingBalance, request.Amount);
        loan.RemainingBalance = newBalance;
        loan.IsPaidOff = newBalance <= 0.01m;
        if (loan.IsPaidOff)
        {
            loan.RemainingBalance = 0m;
        }

        var now = DateTimeOffset.UtcNow;
        db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            AirlineId = loan.AirlineId,
            Utc = now,
            Category = LedgerCategory.LoanPayment,
            Amount = -request.Amount,
            Description = "Loan overpayment against principal",
        });

        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            loanId = loan.Id,
            amountApplied = request.Amount,
            newRemainingBalance = loan.RemainingBalance,
            cashBalance = await CashBalanceAsync(db, loan.AirlineId, ct),
            note = "Overpayment reduces your remaining balance. Your monthly payment stays the same and the loan finishes sooner.",
        });
    }

    /// <summary>Ledger categories that make up a completed flight's operating cost - see
    /// <see cref="PilotsAsync"/>/<see cref="RoutesAsync"/>, both of which sum these straight off
    /// posted <see cref="LedgerTransaction"/> rows (joined by <see cref="LedgerTransaction.FlightId"/>)
    /// rather than off <see cref="Flight.TotalCost"/>/<see cref="Flight.Revenue"/> - those columns
    /// are FlightEconomicsPoster's own cache of the same figures and this app already has a rule
    /// that everything shown on the Finances page must come from posted ledger rows, never a
    /// recomputation, so these two endpoints go to the ledger directly instead of trusting a second
    /// copy of the number to stay in sync with it. Deliberately excludes
    /// <see cref="LedgerCategory.CancellationFee"/> - that's never posted against a completed,
    /// revenue-posted flight (the two are mutually exclusive), so it would never match anyway.</summary>
    private static readonly HashSet<LedgerCategory> FlightOperatingCostCategories = new()
    {
        LedgerCategory.Fuel, LedgerCategory.LandingFees, LedgerCategory.Handling, LedgerCategory.ParkingFees,
        LedgerCategory.PassengerCharges, LedgerCategory.TurnaroundFees, LedgerCategory.Maintenance, LedgerCategory.CrewCost,
    };

    /// <summary>
    /// Per-pilot revenue vs cost - docs/PLAN.md "Virtual pilots - are they worth it?". `revenue` and
    /// `operatingCost` are ledger-derived (summed straight from posted <see cref="LedgerTransaction"/>
    /// rows for the pilot's flights in the window - real money that actually moved). `totalCost`/
    /// `netContribution` are NOT: they add the pilot's <see cref="Pilot.MonthlySalary"/>, prorated to
    /// the requested window, and a prorated salary is never itself a ledger posting (the real
    /// monthly-salary line posts in full, on the billing cycle, wherever that falls relative to this
    /// window) - so these two are a run-rate for COMPARING pilots, not a claim about cash that moved
    /// in this exact window. The response fields are named accordingly
    /// (`estimatedTotalCost`/`estimatedNetContribution`) so nothing here can be mistaken for a
    /// ledger figure.
    /// </summary>
    internal static async Task<IResult> PilotsAsync(int? days, FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var periodDays = days is > 0 ? days.Value : 30;

        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(new { periodDays, pilots = Array.Empty<object>() });
        }

        var pilots = await db.Pilots.Where(p => p.AirlineId == airline.Id && p.DeletedUtc == null).ToListAsync(ct);
        if (pilots.Count == 0)
        {
            return Results.Ok(new { periodDays, pilots = Array.Empty<object>() });
        }

        var now = DateTimeOffset.UtcNow;
        var cutoff = now - TimeSpan.FromDays(periodDays);
        var pilotIds = pilots.Select(p => p.Id).ToList();

        var flights = (await db.Flights
                .Where(f => pilotIds.Contains(f.PilotId) && f.Status == FlightStatus.Completed && f.RevenuePosted && f.DeletedUtc == null)
                .ToListAsync(ct))
            .Where(f => (f.InUtc ?? f.CreatedUtc) >= cutoff)
            .ToList();

        var flightIds = flights.Select(f => f.Id).ToList();
        var ledgerLines = await db.LedgerTransactions.Where(t => t.FlightId != null && flightIds.Contains(t.FlightId.Value)).ToListAsync(ct);
        var revenueByFlight = ledgerLines.Where(t => t.Category == LedgerCategory.TicketRevenue)
            .GroupBy(t => t.FlightId!.Value).ToDictionary(g => g.Key, g => g.Sum(t => t.Amount));
        var costByFlight = ledgerLines.Where(t => FlightOperatingCostCategories.Contains(t.Category))
            .GroupBy(t => t.FlightId!.Value).ToDictionary(g => g.Key, g => -g.Sum(t => t.Amount)); // ledger cost lines are negative - flip to a positive cost figure

        var flightsByPilot = flights.GroupBy(f => f.PilotId).ToDictionary(g => g.Key, g => g.ToList());

        var result = pilots.Select(p =>
        {
            var pilotFlights = flightsByPilot.GetValueOrDefault(p.Id) ?? new List<Flight>();
            var revenue = pilotFlights.Sum(f => revenueByFlight.GetValueOrDefault(f.Id));
            var operatingCost = pilotFlights.Sum(f => costByFlight.GetValueOrDefault(f.Id));
            // The monthly salary is a fixed cost independent of the window - prorate it to the
            // requested period so the comparison figure below doesn't carry a full month's wage
            // against 7 days of revenue, which would make every pilot look artificially
            // unprofitable the shorter the window gets. This is explicitly an estimate - see the
            // method doc.
            var proratedSalary = Math.Round(p.MonthlySalary * (decimal)periodDays / 30m, 2, MidpointRounding.AwayFromZero);
            var estimatedTotalCost = operatingCost + proratedSalary;

            return new
            {
                pilotId = p.Id,
                name = p.Name,
                isPlayer = p.IsPlayer,
                monthlySalary = p.MonthlySalary,
                sectorsFlown = pilotFlights.Count,
                revenue,
                operatingCost,
                estimatedTotalCost,
                estimatedNetContribution = revenue - estimatedTotalCost,
            };
        }).OrderByDescending(p => p.estimatedNetContribution).ToList();

        return Results.Ok(new { periodDays, pilots = result });
    }

    internal static async Task<IResult> CostsAsync(int? days, FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var periodDays = days is > 0 ? days.Value : 30;

        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(new { periodDays, @fixed = default(object), variable = default(object), revenue = default(object), legacyDataNotice = (string?)null });
        }

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(periodDays);
        var transactions = (await db.LedgerTransactions.Where(t => t.AirlineId == airline.Id).ToListAsync(ct))
            .Where(t => t.Utc >= cutoff)
            .ToList();

        decimal Sum(LedgerCategory category) => transactions.Where(t => t.Category == category).Sum(t => t.Amount);
        decimal SumFiltered(LedgerCategory category, Func<LedgerTransaction, bool> predicate) => transactions.Where(t => t.Category == category).Where(predicate).Sum(t => t.Amount);

        // Aircraft transactions and the early-exit fees are posted under the same categories as the
        // regular recurring charge (see FleetDisposalEndpoints) - split by sign/description so a
        // sale doesn't get counted as a purchase and a termination fee doesn't inflate the ordinary
        // lease-payment line.
        var leasePayments = SumFiltered(LedgerCategory.LeasePayment, t => !t.Description.StartsWith("Early lease termination fee", StringComparison.Ordinal) && !t.Description.StartsWith("Lease pro-rata settlement", StringComparison.Ordinal));
        var leaseEarlyTermination = SumFiltered(LedgerCategory.LeasePayment, t => t.Description.StartsWith("Early lease termination fee", StringComparison.Ordinal) || t.Description.StartsWith("Lease pro-rata settlement", StringComparison.Ordinal));
        var loanPayments = SumFiltered(LedgerCategory.LoanPayment, t => !t.Description.StartsWith("Loan settled early", StringComparison.Ordinal) && !t.Description.StartsWith("Loan overpayment", StringComparison.Ordinal));
        var loanEarlySettlement = SumFiltered(LedgerCategory.LoanPayment, t => t.Description.StartsWith("Loan settled early", StringComparison.Ordinal) || t.Description.StartsWith("Loan overpayment", StringComparison.Ordinal));

        var fixedTotal = leasePayments + Sum(LedgerCategory.Salary) + Sum(LedgerCategory.Insurance) + loanPayments + leaseEarlyTermination + loanEarlySettlement;
        var variableTotal = Sum(LedgerCategory.Fuel) + Sum(LedgerCategory.LandingFees) + Sum(LedgerCategory.Handling) + Sum(LedgerCategory.ParkingFees)
            + Sum(LedgerCategory.PassengerCharges) + Sum(LedgerCategory.TurnaroundFees) + Sum(LedgerCategory.Maintenance) + Sum(LedgerCategory.CrewCost)
            + Sum(LedgerCategory.CancellationFee) + Sum(LedgerCategory.GsxServices);

        var aircraftSaleProceeds = SumFiltered(LedgerCategory.AircraftPurchase, t => t.Amount > 0);

        var legacySalaryRowCount = CountLegacySalaryRows(transactions);
        var legacyDataNotice = legacySalaryRowCount > 0
            ? $"{legacySalaryRowCount} historical crew-cost line(s) posted before this breakdown existed are still counted under salaries (fixed) rather than crew cost (variable), because the ledger didn't yet distinguish them at the time."
            : null;

        return Results.Ok(new
        {
            periodDays,
            @fixed = new
            {
                leasePayments,
                salaries = Sum(LedgerCategory.Salary),
                insurance = Sum(LedgerCategory.Insurance),
                loanPayments,
                leaseEarlyTermination,
                loanEarlySettlement,
                total = fixedTotal,
            },
            variable = new
            {
                fuel = Sum(LedgerCategory.Fuel),
                landingFees = Sum(LedgerCategory.LandingFees),
                handling = Sum(LedgerCategory.Handling),
                parkingFees = Sum(LedgerCategory.ParkingFees),
                passengerCharges = Sum(LedgerCategory.PassengerCharges),
                turnaroundFees = Sum(LedgerCategory.TurnaroundFees),
                maintenance = Sum(LedgerCategory.Maintenance),
                crew = Sum(LedgerCategory.CrewCost),
                cancellationFees = Sum(LedgerCategory.CancellationFee),
                other = Sum(LedgerCategory.GsxServices),
                total = variableTotal,
            },
            revenue = new
            {
                ticketRevenue = Sum(LedgerCategory.TicketRevenue),
                aircraftSaleProceeds,
                total = Sum(LedgerCategory.TicketRevenue) + aircraftSaleProceeds,
            },
            legacyDataNotice,
        });
    }

    /// <summary>
    /// Counts <see cref="LedgerCategory.Salary"/> rows in <paramref name="transactions"/> that
    /// predate the fixed/variable split - i.e. are a legacy per-sector crew-cost line rather than
    /// the monthly wage. Uses an exact structural discriminator, not description text:
    /// <see cref="FSOps.Server.Services.EconomyClockService"/>'s monthly wage posting is
    /// airline-level (<see cref="LedgerTransaction.FlightId"/> is always null), while
    /// <c>FlightEconomicsPoster.Post</c> always posts through a specific flight
    /// (<c>FlightId</c> always set) - before <see cref="LedgerCategory.CrewCost"/> existed, per-sector
    /// crew cost was posted as <see cref="LedgerCategory.Salary"/> this same way, so
    /// "Category == Salary AND FlightId != null" identifies exactly those legacy rows and nothing
    /// else, regardless of how any description is ever worded. Confirmed by inspection that no other
    /// code path posts a flight-scoped Salary row (only <c>EconomyClockService</c> posts Salary at
    /// all, and always airline-level).
    /// <para>
    /// Detection only, never used to move money between buckets - see
    /// <see cref="LedgerCostClassifier"/>'s class doc for why <see cref="CostsAsync"/> counts every
    /// Salary row as fixed regardless, and surfaces this count as a notice instead of silently
    /// reclassifying anything.
    /// </para>
    /// </summary>
    private static int CountLegacySalaryRows(IReadOnlyList<LedgerTransaction> transactions) =>
        transactions.Count(t => t.Category == LedgerCategory.Salary && t.FlightId is not null);

    /// <summary>
    /// P&amp;L per directional route - docs/PLAN.md "Profit and loss per route ... given every real
    /// cost". <c>revenue</c>/<c>cost</c>/<c>profit</c> are entirely ledger-derived: summed straight
    /// from posted <see cref="LedgerTransaction"/> rows for each route's flights in the window (see
    /// <see cref="FlightOperatingCostCategories"/>), never from the <see cref="Flight.Revenue"/>/
    /// <see cref="Flight.TotalCost"/> cache columns - so this can never disagree with what actually
    /// moved the cash balance. Unlike <see cref="PilotsAsync"/> there is no fixed cost to prorate
    /// here (a route has no monthly wage of its own), so every figure this endpoint returns is real
    /// money, not an estimate.
    /// </summary>
    internal static async Task<IResult> RoutesAsync(int? days, FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var periodDays = days is > 0 ? days.Value : 30;

        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(new { periodDays, routes = Array.Empty<object>() });
        }

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(periodDays);
        var flights = (await db.Flights
                .Where(f => f.AirlineId == airline.Id && f.Status == FlightStatus.Completed && f.RevenuePosted && f.DeletedUtc == null)
                .ToListAsync(ct))
            .Where(f => (f.InUtc ?? f.CreatedUtc) >= cutoff)
            .ToList();

        if (flights.Count == 0)
        {
            return Results.Ok(new { periodDays, routes = Array.Empty<object>() });
        }

        var routeIds = flights.Select(f => f.RouteId).Distinct().ToList();
        var routes = await db.Routes.Where(r => routeIds.Contains(r.Id)).ToListAsync(ct);
        var routeById = routes.ToDictionary(r => r.Id);

        var flightIds = flights.Select(f => f.Id).ToList();
        var ledgerLines = await db.LedgerTransactions.Where(t => t.FlightId != null && flightIds.Contains(t.FlightId.Value)).ToListAsync(ct);
        var revenueByFlight = ledgerLines.Where(t => t.Category == LedgerCategory.TicketRevenue)
            .GroupBy(t => t.FlightId!.Value).ToDictionary(g => g.Key, g => g.Sum(t => t.Amount));
        var costByFlight = ledgerLines.Where(t => FlightOperatingCostCategories.Contains(t.Category))
            .GroupBy(t => t.FlightId!.Value).ToDictionary(g => g.Key, g => -g.Sum(t => t.Amount));

        var result = flights.GroupBy(f => f.RouteId).Select(g =>
        {
            var route = routeById.GetValueOrDefault(g.Key);
            var revenue = g.Sum(f => revenueByFlight.GetValueOrDefault(f.Id));
            var cost = g.Sum(f => costByFlight.GetValueOrDefault(f.Id));

            return new
            {
                routeId = g.Key,
                departureIcao = route?.DepartureIcao ?? string.Empty,
                arrivalIcao = route?.ArrivalIcao ?? string.Empty,
                flightNumber = route?.FlightNumber,
                sectorsFlown = g.Count(),
                revenue,
                cost,
                profit = revenue - cost,
            };
        }).OrderByDescending(r => r.profit).ToList();

        return Results.Ok(new { periodDays, routes = result });
    }

    internal static async Task<IResult> LedgerAsync(
        string? category, int? skip, int? take, FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(new { total = 0, transactions = Array.Empty<object>() });
        }

        LedgerCategory? parsedCategory = null;
        if (!string.IsNullOrWhiteSpace(category))
        {
            if (!Enum.TryParse<LedgerCategory>(category, ignoreCase: true, out var parsed))
            {
                return Results.BadRequest(new { error = $"Unknown category '{category}'." });
            }

            parsedCategory = parsed;
        }

        var transactions = await db.LedgerTransactions.Where(t => t.AirlineId == airline.Id).ToListAsync(ct);
        if (parsedCategory is not null)
        {
            transactions = transactions.Where(t => t.Category == parsedCategory.Value).ToList();
        }

        var ordered = transactions.OrderByDescending(t => t.Utc).ToList();
        var effectiveSkip = Math.Max(0, skip ?? 0);
        var effectiveTake = take is > 0 and <= 500 ? take.Value : 50;

        var page = ordered.Skip(effectiveSkip).Take(effectiveTake).Select(t => new
        {
            id = t.Id,
            utc = t.Utc,
            category = t.Category.ToString(),
            amount = t.Amount,
            description = t.Description,
            flightId = t.FlightId,
        }).ToList();

        return Results.Ok(new { total = ordered.Count, transactions = page });
    }

    private static async Task<(Loan? Loan, IResult? Error)> ResolveLoanAsync(FsOpsDbContext db, ICurrentUser currentUser, Guid id, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return (null, Results.BadRequest(new { error = "Create an airline before managing loans." }));
        }

        var loan = await db.Loans.FirstOrDefaultAsync(l => l.Id == id && l.AirlineId == airline.Id && l.DeletedUtc == null, ct);
        if (loan is null)
        {
            return (null, Results.NotFound(new { error = "Loan not found." }));
        }

        return (loan, null);
    }

    private static async Task<decimal> CashBalanceAsync(FsOpsDbContext db, Guid airlineId, CancellationToken ct)
    {
        var amounts = await db.LedgerTransactions.Where(t => t.AirlineId == airlineId).Select(t => t.Amount).ToListAsync(ct);
        return amounts.Sum();
    }
}

/// <summary>Body for <see cref="FinanceEndpoints.OverpayLoanAsync"/> - a partial payment against
/// principal only, distinct from <see cref="FinanceEndpoints.SettleLoanAsync"/>'s full payoff.</summary>
public record OverpayLoanRequest(decimal Amount);

/// <summary>Body for <see cref="FinanceEndpoints.SettleLoanAsync"/> - carries the payoff figure the
/// player confirmed on the settlement quote, so the commit can detect drift rather than silently
/// posting a different figure. See that method's own doc.</summary>
public record SettleLoanRequest(decimal ExpectedTotalPayoff);
