using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Server.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Tests;

/// <summary>
/// Backs the Finances page - docs/PLAN.md "What the Finances page must contain". Covers the loan
/// early-settlement exploit close, the fixed/variable cost split (including the new
/// CrewCost/ParkingFees/PassengerCharges/TurnaroundFees categories and the legacy-data notice), and
/// that per-pilot/per-route money is read from posted LedgerTransaction rows. Drives
/// FinanceEndpoints' handlers directly against an isolated in-memory RouteTestContext, same
/// convention as FleetEndpointsTests/FleetDisposalEndpointsTests.
/// </summary>
public class FinanceEndpointsTests
{
    private static async Task SeedStartingCashAsync(RouteTestContext ctx, decimal amount)
    {
        ctx.Db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Utc = DateTimeOffset.UtcNow,
            Category = LedgerCategory.StartingCapital,
            Amount = amount,
            Description = "Test seed capital",
        });
        await ctx.Db.SaveChangesAsync();
    }

    /// <summary>
    /// LoanEligibilityCalculator sizes borrowing capacity off trailing 30-day NET OPERATING cash
    /// flow, which deliberately EXCLUDES LedgerCategory.StartingCapital (see that calculator's own
    /// doc - otherwise a brand-new airline could borrow against money it didn't earn). A test that
    /// only seeds StartingCapital therefore has zero borrowing capacity and every TakeLoanAsync call
    /// is correctly rejected with 400 - this seeds genuine trading income instead, so loan tests can
    /// actually take a loan to then settle/overpay/exploit-test against.
    /// </summary>
    private static async Task SeedTradingIncomeAsync(RouteTestContext ctx, decimal amount)
    {
        ctx.Db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Utc = DateTimeOffset.UtcNow,
            Category = LedgerCategory.TicketRevenue,
            Amount = amount,
            Description = "Test seed trading income",
        });
        await ctx.Db.SaveChangesAsync();
    }

    private static async Task<decimal> CashBalanceAsync(RouteTestContext ctx)
    {
        var amounts = await ctx.Db.LedgerTransactions.Where(t => t.AirlineId == ctx.Airline.Id).Select(t => t.Amount).ToListAsync();
        return amounts.Sum();
    }

    private static int StatusCodeOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode!.Value;

    private static object BodyOf(IResult result) => Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IValueHttpResult>(result).Value!;

    private static T Prop<T>(object body, string name) => (T)body.GetType().GetProperty(name)!.GetValue(body)!;

    // ---------- Loans ----------

    [Fact]
    public async Task TakeALoan_ImmediatelySettleInFull_IsANetLoss_NeverFree()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedStartingCashAsync(ctx, 10_000_000m);
        await SeedTradingIncomeAsync(ctx, 10_000_000m); // gives the loan eligibility check real capacity to approve against
        var catalog = EconomyConfigCatalog.Default();

        var loanResult = await FleetEndpoints.TakeLoanAsync(new TakeLoanRequest(100_000m, 24), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(loanResult));

        var cashAfterBorrow = await CashBalanceAsync(ctx);
        var loan = await ctx.Db.Loans.SingleAsync(l => l.AirlineId == ctx.Airline.Id);

        var quoteResult = await FinanceEndpoints.LoanSettlementQuoteAsync(loan.Id, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        var totalPayoff = Prop<decimal>(BodyOf(quoteResult), "totalPayoff");

        var settleResult = await FinanceEndpoints.SettleLoanAsync(loan.Id, new SettleLoanRequest(totalPayoff), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(settleResult));

        var cashAfterSettle = await CashBalanceAsync(ctx);

        // Zero elapsed time means zero interest accrued - the ONLY thing that can make this cost
        // anything is the early-settlement fee, which must guarantee a loss regardless.
        Assert.True(cashAfterSettle < cashAfterBorrow, $"Borrow-then-settle should lose money: afterBorrow={cashAfterBorrow}, afterSettle={cashAfterSettle}");

        var reloaded = await ctx.Db.Loans.SingleAsync(l => l.Id == loan.Id);
        Assert.True(reloaded.IsPaidOff);
        Assert.Equal(0m, reloaded.RemainingBalance);
    }

    [Fact]
    public async Task Overpay_ReducesBalance_AndDoesNotChargeAFee()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedStartingCashAsync(ctx, 10_000_000m);
        await SeedTradingIncomeAsync(ctx, 10_000_000m); // gives the loan eligibility check real capacity to approve against
        var catalog = EconomyConfigCatalog.Default();

        await FleetEndpoints.TakeLoanAsync(new TakeLoanRequest(100_000m, 24), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        var loan = await ctx.Db.Loans.SingleAsync(l => l.AirlineId == ctx.Airline.Id);
        var cashBeforeOverpay = await CashBalanceAsync(ctx);

        var overpayResult = await FinanceEndpoints.OverpayLoanAsync(loan.Id, new OverpayLoanRequest(10_000m), ctx.Db, ctx.CurrentUser, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(overpayResult));

        var cashAfterOverpay = await CashBalanceAsync(ctx);
        // No fee on a partial overpayment - cash moves by exactly the amount applied.
        Assert.Equal(cashBeforeOverpay - 10_000m, cashAfterOverpay);

        var reloaded = await ctx.Db.Loans.SingleAsync(l => l.Id == loan.Id);
        Assert.Equal(90_000m, reloaded.RemainingBalance);
        Assert.False(reloaded.IsPaidOff);
    }

    [Fact]
    public async Task Overpay_ExceedingBalance_IsRejected()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedStartingCashAsync(ctx, 10_000_000m);
        await SeedTradingIncomeAsync(ctx, 10_000_000m); // gives the loan eligibility check real capacity to approve against
        var catalog = EconomyConfigCatalog.Default();

        await FleetEndpoints.TakeLoanAsync(new TakeLoanRequest(100_000m, 24), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        var loan = await ctx.Db.Loans.SingleAsync(l => l.AirlineId == ctx.Airline.Id);

        var result = await FinanceEndpoints.OverpayLoanAsync(loan.Id, new OverpayLoanRequest(200_000m), ctx.Db, ctx.CurrentUser, CancellationToken.None);
        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));

        var reloaded = await ctx.Db.Loans.SingleAsync(l => l.Id == loan.Id);
        Assert.Equal(100_000m, reloaded.RemainingBalance);
    }

    [Fact]
    public async Task Settle_BalanceMovesBetweenQuoteAndCommit_RefusesTheStaleFigure()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedStartingCashAsync(ctx, 10_000_000m);
        await SeedTradingIncomeAsync(ctx, 10_000_000m); // gives the loan eligibility check real capacity to approve against
        var catalog = EconomyConfigCatalog.Default();

        await FleetEndpoints.TakeLoanAsync(new TakeLoanRequest(100_000m, 24), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        var loan = await ctx.Db.Loans.SingleAsync(l => l.AirlineId == ctx.Airline.Id);

        var quoteResult = await FinanceEndpoints.LoanSettlementQuoteAsync(loan.Id, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        var quotedPayoff = Prop<decimal>(BodyOf(quoteResult), "totalPayoff");

        // Simulates EconomyClockService's background billing tick landing between the quote being
        // shown and the player confirming - exactly the drift this guard exists to catch.
        loan.RemainingBalance -= 4_000m;
        await ctx.Db.SaveChangesAsync();

        var staleCommit = await FinanceEndpoints.SettleLoanAsync(loan.Id, new SettleLoanRequest(quotedPayoff), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(staleCommit));
        var currentTotalPayoff = Prop<decimal>(BodyOf(staleCommit), "currentTotalPayoff");
        Assert.NotEqual(quotedPayoff, currentTotalPayoff);

        var stillOutstanding = await ctx.Db.Loans.SingleAsync(l => l.Id == loan.Id);
        Assert.False(stillOutstanding.IsPaidOff);

        var freshCommit = await FinanceEndpoints.SettleLoanAsync(loan.Id, new SettleLoanRequest(currentTotalPayoff), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(freshCommit));
    }

    [Fact]
    public async Task Settle_AlreadyPaidOffLoan_IsRejected()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedStartingCashAsync(ctx, 10_000_000m);
        await SeedTradingIncomeAsync(ctx, 10_000_000m); // gives the loan eligibility check real capacity to approve against
        var catalog = EconomyConfigCatalog.Default();

        await FleetEndpoints.TakeLoanAsync(new TakeLoanRequest(100_000m, 24), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        var loan = await ctx.Db.Loans.SingleAsync(l => l.AirlineId == ctx.Airline.Id);
        var firstQuote = await FinanceEndpoints.LoanSettlementQuoteAsync(loan.Id, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        var firstPayoff = Prop<decimal>(BodyOf(firstQuote), "totalPayoff");
        await FinanceEndpoints.SettleLoanAsync(loan.Id, new SettleLoanRequest(firstPayoff), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        var secondSettle = await FinanceEndpoints.SettleLoanAsync(loan.Id, new SettleLoanRequest(0m), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(secondSettle));
    }

    // ---------- Costs / fixed vs variable ----------

    [Fact]
    public async Task Costs_SplitsFixedFromVariable_UsingTheDedicatedCategories()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var now = DateTimeOffset.UtcNow;

        void Post(LedgerCategory category, decimal amount, Guid? flightId = null) => ctx.Db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Utc = now,
            Category = category,
            Amount = amount,
            FlightId = flightId,
            Description = "test",
        });

        Post(LedgerCategory.LeasePayment, -30_000m);
        Post(LedgerCategory.Salary, -9_000m); // airline-level monthly wage - no FlightId
        Post(LedgerCategory.Insurance, -6_000m);
        Post(LedgerCategory.LoanPayment, -4_000m);
        Post(LedgerCategory.Fuel, -1_200m);
        Post(LedgerCategory.LandingFees, -500m);
        Post(LedgerCategory.Handling, -300m);
        Post(LedgerCategory.ParkingFees, -100m);
        Post(LedgerCategory.PassengerCharges, -150m);
        Post(LedgerCategory.TurnaroundFees, -200m);
        Post(LedgerCategory.Maintenance, -900m);
        Post(LedgerCategory.CrewCost, -600m, flightId: Guid.NewGuid());
        Post(LedgerCategory.TicketRevenue, 12_000m);
        await ctx.Db.SaveChangesAsync();

        var result = await FinanceEndpoints.CostsAsync(days: 30, ctx.Db, ctx.CurrentUser, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));
        var body = BodyOf(result);

        var fixedBucket = body.GetType().GetProperty("fixed")!.GetValue(body)!;
        Assert.Equal(-30_000m, Prop<decimal>(fixedBucket, "leasePayments"));
        Assert.Equal(-9_000m, Prop<decimal>(fixedBucket, "salaries"));
        Assert.Equal(-6_000m, Prop<decimal>(fixedBucket, "insurance"));
        Assert.Equal(-4_000m, Prop<decimal>(fixedBucket, "loanPayments"));
        Assert.Equal(-49_000m, Prop<decimal>(fixedBucket, "total"));

        var variableBucket = body.GetType().GetProperty("variable")!.GetValue(body)!;
        Assert.Equal(-1_200m, Prop<decimal>(variableBucket, "fuel"));
        Assert.Equal(-100m, Prop<decimal>(variableBucket, "parkingFees"));
        Assert.Equal(-150m, Prop<decimal>(variableBucket, "passengerCharges"));
        Assert.Equal(-200m, Prop<decimal>(variableBucket, "turnaroundFees"));
        Assert.Equal(-600m, Prop<decimal>(variableBucket, "crew"));
        Assert.Equal(-1_200m - 500m - 300m - 100m - 150m - 200m - 900m - 600m, Prop<decimal>(variableBucket, "total"));

        var legacyDataNotice = Prop<string?>(body, "legacyDataNotice");
        Assert.Null(legacyDataNotice); // the CrewCost row above has a FlightId but is CrewCost, not Salary - not legacy.
    }

    [Fact]
    public async Task Costs_FlagsLegacySalaryRows_ByFlightIdNotDescription()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var now = DateTimeOffset.UtcNow;

        // A pre-split row: Category == Salary but posted against a specific flight - exactly what
        // FlightEconomicsPoster wrote for per-sector crew cost before CrewCost existed.
        ctx.Db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Utc = now,
            Category = LedgerCategory.Salary,
            Amount = -250m,
            FlightId = Guid.NewGuid(),
            Description = "Anything - the description is deliberately NOT checked",
        });
        // A genuine monthly wage row: Category == Salary, no FlightId.
        ctx.Db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Utc = now,
            Category = LedgerCategory.Salary,
            Amount = -9_000m,
            FlightId = null,
            Description = "Monthly salary: Test Pilot",
        });
        await ctx.Db.SaveChangesAsync();

        var result = await FinanceEndpoints.CostsAsync(days: 30, ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var body = BodyOf(result);

        var legacyDataNotice = Prop<string?>(body, "legacyDataNotice");
        Assert.NotNull(legacyDataNotice);
        Assert.Contains("1 historical", legacyDataNotice);

        // Both rows still count as fixed (the conservative, stated rule) - the notice is a caveat,
        // never a silent move of money between buckets.
        var fixedBucket = body.GetType().GetProperty("fixed")!.GetValue(body)!;
        Assert.Equal(-9_250m, Prop<decimal>(fixedBucket, "salaries"));
    }

    [Fact]
    public async Task Costs_SeparatesEarlyExitFeesFromOrdinaryLeaseAndLoanPayments()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var now = DateTimeOffset.UtcNow;

        ctx.Db.LedgerTransactions.Add(new LedgerTransaction { Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Utc = now, Category = LedgerCategory.LeasePayment, Amount = -30_000m, Description = "Monthly lease payment: G-TEST" });
        ctx.Db.LedgerTransactions.Add(new LedgerTransaction { Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Utc = now, Category = LedgerCategory.LeasePayment, Amount = -8_200m, Description = "Lease pro-rata settlement (early return): G-TEST, 8.2 day(s) of the current period" });
        ctx.Db.LedgerTransactions.Add(new LedgerTransaction { Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Utc = now, Category = LedgerCategory.LeasePayment, Amount = -15_000m, Description = "Early lease termination fee: G-TEST" });
        ctx.Db.LedgerTransactions.Add(new LedgerTransaction { Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Utc = now, Category = LedgerCategory.LoanPayment, Amount = -4_000m, Description = "Monthly loan payment (balance 96,000.00)" });
        ctx.Db.LedgerTransactions.Add(new LedgerTransaction { Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Utc = now, Category = LedgerCategory.LoanPayment, Amount = -2_000m, Description = "Loan settled early - early-settlement fee" });
        await ctx.Db.SaveChangesAsync();

        var result = await FinanceEndpoints.CostsAsync(days: 30, ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var body = BodyOf(result);
        var fixedBucket = body.GetType().GetProperty("fixed")!.GetValue(body)!;

        Assert.Equal(-30_000m, Prop<decimal>(fixedBucket, "leasePayments"));
        Assert.Equal(-23_200m, Prop<decimal>(fixedBucket, "leaseEarlyTermination"));
        Assert.Equal(-4_000m, Prop<decimal>(fixedBucket, "loanPayments"));
        Assert.Equal(-2_000m, Prop<decimal>(fixedBucket, "loanEarlySettlement"));
    }

    // ---------- Pilots / routes - ledger-derived money ----------

    [Fact]
    public async Task Pilots_RevenueAndOperatingCost_ComeFromLedgerRows_NotFlightColumns()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var fleetAircraft = await ctx.Db.FleetAircraft.SingleAsync();
        var route = new Route { Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, DepartureIcao = "EGGD", ArrivalIcao = "EGPH", DistanceNm = 280, BaseFare = 90m, IsActive = true, CreatedUtc = DateTimeOffset.UtcNow };
        ctx.Db.Routes.Add(route);
        var pilot = new Pilot { Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Name = "Test Pilot", IsPlayer = false, MonthlySalary = 9_000m, CreatedUtc = DateTimeOffset.UtcNow };
        ctx.Db.Pilots.Add(pilot);

        var flight = new Flight
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            RouteId = route.Id,
            FleetAircraftId = fleetAircraft.Id,
            PilotId = pilot.Id,
            Status = FlightStatus.Completed,
            RevenuePosted = true,
            PlannedDepartureUtc = DateTimeOffset.UtcNow.AddHours(-2),
            InUtc = DateTimeOffset.UtcNow.AddHours(-1),
            // Deliberately WRONG cached columns - if the endpoint reads these instead of the ledger,
            // the test numbers below will not match and the test fails, proving the ledger is the
            // real source.
            Revenue = 999_999m,
            TotalCost = 999_999m,
            CreatedUtc = DateTimeOffset.UtcNow.AddHours(-2),
        };
        ctx.Db.Flights.Add(flight);

        ctx.Db.LedgerTransactions.Add(new LedgerTransaction { Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Utc = DateTimeOffset.UtcNow, Category = LedgerCategory.TicketRevenue, Amount = 5_000m, FlightId = flight.Id, Description = "Ticket revenue" });
        ctx.Db.LedgerTransactions.Add(new LedgerTransaction { Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Utc = DateTimeOffset.UtcNow, Category = LedgerCategory.Fuel, Amount = -800m, FlightId = flight.Id, Description = "Fuel" });
        ctx.Db.LedgerTransactions.Add(new LedgerTransaction { Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Utc = DateTimeOffset.UtcNow, Category = LedgerCategory.CrewCost, Amount = -200m, FlightId = flight.Id, Description = "Crew cost (this sector)" });
        await ctx.Db.SaveChangesAsync();

        var result = await FinanceEndpoints.PilotsAsync(days: 30, ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var body = BodyOf(result);
        var pilots = (System.Collections.IEnumerable)body.GetType().GetProperty("pilots")!.GetValue(body)!;
        var pilotRow = pilots.Cast<object>().Single();

        Assert.Equal(5_000m, Prop<decimal>(pilotRow, "revenue"));
        Assert.Equal(1_000m, Prop<decimal>(pilotRow, "operatingCost")); // 800 fuel + 200 crew, sign-flipped positive
        Assert.Equal(1, Prop<int>(pilotRow, "sectorsFlown"));

        // 30-day window == full monthly salary, unprorated.
        Assert.Equal(1_000m + 9_000m, Prop<decimal>(pilotRow, "estimatedTotalCost"));
        Assert.Equal(5_000m - (1_000m + 9_000m), Prop<decimal>(pilotRow, "estimatedNetContribution"));
    }

    [Fact]
    public async Task Pilots_SevenDayWindow_ProratesSalaryToASeventhOfAMonth()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var pilot = new Pilot { Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Name = "Test Pilot", IsPlayer = false, MonthlySalary = 9_000m, CreatedUtc = DateTimeOffset.UtcNow };
        ctx.Db.Pilots.Add(pilot);
        await ctx.Db.SaveChangesAsync();

        var result = await FinanceEndpoints.PilotsAsync(days: 7, ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var body = BodyOf(result);
        var pilots = (System.Collections.IEnumerable)body.GetType().GetProperty("pilots")!.GetValue(body)!;
        var pilotRow = pilots.Cast<object>().Single();

        var expectedProratedSalary = Math.Round(9_000m * 7m / 30m, 2, MidpointRounding.AwayFromZero);
        Assert.Equal(9_000m, Prop<decimal>(pilotRow, "monthlySalary")); // unprorated reference figure
        Assert.Equal(expectedProratedSalary, Prop<decimal>(pilotRow, "estimatedTotalCost")); // no flights -> operatingCost is 0
        Assert.Equal(7, Prop<int>(body, "periodDays"));
    }

    // ---------- Ledger ----------

    [Fact]
    public async Task Ledger_FiltersByCategory_AndPaginates()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        for (var i = 0; i < 3; i++)
        {
            ctx.Db.LedgerTransactions.Add(new LedgerTransaction { Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Utc = DateTimeOffset.UtcNow.AddMinutes(-i), Category = LedgerCategory.Fuel, Amount = -100m, Description = $"Fuel {i}" });
        }
        ctx.Db.LedgerTransactions.Add(new LedgerTransaction { Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Utc = DateTimeOffset.UtcNow, Category = LedgerCategory.LandingFees, Amount = -50m, Description = "Landing" });
        await ctx.Db.SaveChangesAsync();

        var filtered = await FinanceEndpoints.LedgerAsync("fuel", skip: 0, take: 50, ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var body = BodyOf(filtered);
        Assert.Equal(3, Prop<int>(body, "total"));

        var paged = await FinanceEndpoints.LedgerAsync("fuel", skip: 1, take: 1, ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var pagedBody = BodyOf(paged);
        var transactions = (System.Collections.IEnumerable)pagedBody.GetType().GetProperty("transactions")!.GetValue(pagedBody)!;
        Assert.Single(transactions.Cast<object>());

        var badCategory = await FinanceEndpoints.LedgerAsync("NotACategory", skip: 0, take: 50, ctx.Db, ctx.CurrentUser, CancellationToken.None);
        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(badCategory));
    }
}
