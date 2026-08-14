using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Server.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Tests;

/// <summary>
/// Backs the Finances page. Covers the loan
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
    public async Task Costs_CountsRepositioningAsAVariableCost()
    {
        // A positioning fee buys nothing the airline still owns afterwards, so it belongs on the
        // variable side - never fixed (it is not owed unless the player chooses to move something)
        // and never capital, which is where an aircraft purchase goes.
        using var ctx = await RouteTestContext.CreateAsync();

        ctx.Db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Utc = DateTimeOffset.UtcNow,
            Category = LedgerCategory.AircraftRepositioning,
            Amount = -2_000m,
            Description = "Repositioned G-FSOA: EGGD to EGPH",
        });
        await ctx.Db.SaveChangesAsync();

        var result = await FinanceEndpoints.CostsAsync(days: 30, ctx.Db, ctx.CurrentUser, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));
        var body = BodyOf(result);

        var variableBucket = body.GetType().GetProperty("variable")!.GetValue(body)!;
        Assert.Equal(-2_000m, Prop<decimal>(variableBucket, "repositioning"));
        Assert.Equal(-2_000m, Prop<decimal>(variableBucket, "total"));

        // And nowhere near the fixed side.
        var fixedBucket = body.GetType().GetProperty("fixed")!.GetValue(body)!;
        Assert.Equal(0m, Prop<decimal>(fixedBucket, "total"));
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

    /// <summary>
    /// Regression for the 2026-08-14 defect: RoutesAsync counted only TicketRevenue as revenue, so a
    /// sector flown online earned a VatsimOnlineBonus line that appeared in NO route's P&amp;L - while
    /// the Logbook's "Net", which sums every line posted against the flight, happily included it. One
    /// flight, two screens, two different numbers, and no way for a player to tell which to believe.
    /// The bonus is flight-scoped (FlightEconomicsPoster.PostVatsimOnlineBonus stamps it with the
    /// flight's own FlightId and sizes it off that sector's ticket revenue), so it belongs to the
    /// route that earned it. Asserted by comparing the two endpoints against each other rather than
    /// against a hand-written constant, so this fails the moment they diverge again for ANY reason.
    /// </summary>
    [Fact]
    public async Task Routes_ProfitEqualsTheLogbooksNet_IncludingTheVatsimOnlineBonus()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var fleetAircraft = await ctx.Db.FleetAircraft.SingleAsync();
        var route = new Route { Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, DepartureIcao = "EGGD", ArrivalIcao = "EGPH", DistanceNm = 280, BaseFare = 90m, IsActive = true, CreatedUtc = DateTimeOffset.UtcNow };
        ctx.Db.Routes.Add(route);
        var pilot = new Pilot { Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Name = "Test Pilot", IsPlayer = true, MonthlySalary = 0m, CreatedUtc = DateTimeOffset.UtcNow };
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
            PaxFlown = 150,
            PlannedDepartureUtc = DateTimeOffset.UtcNow.AddHours(-2),
            OutUtc = DateTimeOffset.UtcNow.AddHours(-2),
            InUtc = DateTimeOffset.UtcNow.AddHours(-1),
            VatsimOnline = true,
            CreatedUtc = DateTimeOffset.UtcNow.AddHours(-2),
        };
        ctx.Db.Flights.Add(flight);

        void Post(LedgerCategory category, decimal amount) => ctx.Db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Utc = DateTimeOffset.UtcNow,
            Category = category,
            Amount = amount,
            FlightId = flight.Id,
            Description = category.ToString(),
        });

        // Every line FlightEconomicsPoster actually writes for a completed sector, so nothing can
        // pass this test by simply not being posted.
        Post(LedgerCategory.TicketRevenue, 13_500m);
        Post(LedgerCategory.VatsimOnlineBonus, 405m); // 3% uplift, posted against this very flight
        Post(LedgerCategory.Fuel, -2_400m);
        Post(LedgerCategory.LandingFees, -500m);
        Post(LedgerCategory.Handling, -300m);
        Post(LedgerCategory.ParkingFees, -100m);
        Post(LedgerCategory.PassengerCharges, -150m);
        Post(LedgerCategory.TurnaroundFees, -200m);
        Post(LedgerCategory.Maintenance, -900m);
        Post(LedgerCategory.CrewCost, -600m);
        await ctx.Db.SaveChangesAsync();

        var routesBody = BodyOf(await FinanceEndpoints.RoutesAsync(days: 30, ctx.Db, ctx.CurrentUser, CancellationToken.None));
        var routeRow = ((System.Collections.IEnumerable)routesBody.GetType().GetProperty("routes")!.GetValue(routesBody)!).Cast<object>().Single();

        Assert.Equal(13_500m + 405m, Prop<decimal>(routeRow, "revenue"));
        Assert.Equal(5_150m, Prop<decimal>(routeRow, "cost"));

        var logbookBody = BodyOf(await FlightEndpoints.LogbookAsync(ctx.Db, ctx.CurrentUser, CancellationToken.None));
        var sector = ((System.Collections.IEnumerable)logbookBody.GetType().GetProperty("sectors")!.GetValue(logbookBody)!).Cast<object>().Single();

        // The claim that matters: the one sector on this route contributes exactly what the logbook
        // says it netted. And both equal the ledger itself.
        Assert.Equal(Prop<decimal>(sector, "net"), Prop<decimal>(routeRow, "profit"));

        var everyLineForTheFlight = (await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync()).Sum(t => t.Amount);
        Assert.Equal(everyLineForTheFlight, Prop<decimal>(routeRow, "profit"));
    }

    /// <summary>
    /// The structural half of the same defect, and the test that turns this whole family of bugs
    /// into a build failure instead of a discovery. A LedgerCategory that no bucket claims is money
    /// the Finances page silently fails to report, which has now happened twice - once harmlessly
    /// (<c>Other</c>, which nothing ever wrote) and once for real (<c>VatsimOnlineBonus</c>, which
    /// FlightEconomicsPoster genuinely posts). Deliberately walks <c>Enum.GetValues</c> rather than a
    /// hand-maintained list, so a category added tomorrow fails here until someone decides where its
    /// money goes. There are deliberately NO exceptions: an unplaceable category is a finding to
    /// raise, never a line to add to an allow-list - three categories drifted out of the totals one
    /// at a time, each individually defensible.
    /// </summary>
    [Fact]
    public void EveryLedgerCategory_HasAPlaceOnTheFinancesPage()
    {
        var unplaced = new List<string>();

        foreach (var category in Enum.GetValues<LedgerCategory>())
        {
            try
            {
                _ = FinanceEndpoints.BucketFor(category);
            }
            catch (ArgumentOutOfRangeException)
            {
                unplaced.Add(category.ToString());
            }
        }

        Assert.True(
            unplaced.Count == 0,
            $"LedgerCategory {string.Join(", ", unplaced)} has no place on the Finances page. Assign it in " +
            "FinanceEndpoints.BucketFor to a fixed cost, a variable cost, operating revenue, or capital and " +
            "financing - or delete the category if nothing writes it (check `git log --all -S` first: the value " +
            "is persisted as TEXT, so one left in a real database fails to parse on read). A category in no " +
            "bucket is money the page reports nowhere while the cash balance counts it perfectly.");
    }

    /// <summary>
    /// The same guarantee from the other end: whatever <see cref="FinanceEndpoints.BucketFor"/> says,
    /// the totals the page actually renders have to add up to every row in the ledger. Nothing is
    /// excluded - the capital and financing rows land in their own total rather than being waved
    /// past, because "not operating profit" is not the same as "not money".
    /// </summary>
    [Fact]
    public async Task Costs_EveryRowInTheLedger_LandsInExactlyOneOfThePagesTotals()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var now = DateTimeOffset.UtcNow;

        // One row per category, all distinct amounts, so a category dropped from a bucket changes
        // that bucket's total by a unique figure rather than cancelling out against another.
        var amount = -1m;
        foreach (var category in Enum.GetValues<LedgerCategory>())
        {
            amount -= 7m;
            ctx.Db.LedgerTransactions.Add(new LedgerTransaction
            {
                Id = Guid.NewGuid(),
                AirlineId = ctx.Airline.Id,
                Utc = now,
                Category = category,
                Amount = category is LedgerCategory.TicketRevenue or LedgerCategory.VatsimOnlineBonus
                    or LedgerCategory.StartingCapital or LedgerCategory.LoanProceeds
                    ? -amount
                    : amount,
                Description = category.ToString(),
            });
        }

        await ctx.Db.SaveChangesAsync();

        var body = BodyOf(await FinanceEndpoints.CostsAsync(days: 30, ctx.Db, ctx.CurrentUser, CancellationToken.None));
        var fixedTotal = Prop<decimal>(body.GetType().GetProperty("fixed")!.GetValue(body)!, "total");
        var variableTotal = Prop<decimal>(body.GetType().GetProperty("variable")!.GetValue(body)!, "total");
        var revenueTotal = Prop<decimal>(body.GetType().GetProperty("revenue")!.GetValue(body)!, "total");

        var rows = await ctx.Db.LedgerTransactions.Where(t => t.AirlineId == ctx.Airline.Id).ToListAsync();

        // The page shows three cards, and the fourth bucket - capital and financing - is real money
        // that is deliberately not operating profit or loss. The revenue card already surfaces the
        // sale half of AircraftPurchase, so only the rest of that bucket is outstanding here.
        var capitalNotShownOnTheRevenueCard = rows
            .Where(t => FinanceEndpoints.BucketFor(t.Category) == FinanceEndpoints.FinanceBucket.CapitalAndFinancing)
            .Where(t => !(t.Category == LedgerCategory.AircraftPurchase && t.Amount > 0))
            .Sum(t => t.Amount);

        Assert.Equal(rows.Sum(t => t.Amount), fixedTotal + variableTotal + revenueTotal + capitalNotShownOnTheRevenueCard);

        // And each operating total really is its bucket, not a hand-written list that happens to
        // agree today.
        Assert.Equal(rows.Where(t => FinanceEndpoints.BucketFor(t.Category) == FinanceEndpoints.FinanceBucket.Fixed).Sum(t => t.Amount), fixedTotal);
        Assert.Equal(rows.Where(t => FinanceEndpoints.BucketFor(t.Category) == FinanceEndpoints.FinanceBucket.Variable).Sum(t => t.Amount), variableTotal);
    }

    /// <summary>
    /// The VATSIM online-flying uplift is real money, posted by FlightEconomicsPoster, and until
    /// 2026-08-14 it appeared in NO total on this page - not fixed, not variable, not revenue - while
    /// the cash balance (which simply sums the ledger) counted it perfectly. So a player flying
    /// online saw their Finances page disagree with their own cash figure and with their logbook.
    /// </summary>
    [Fact]
    public async Task Costs_CountsTheVatsimOnlineBonusAsRevenue_OnItsOwnLine()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var now = DateTimeOffset.UtcNow;

        ctx.Db.LedgerTransactions.Add(new LedgerTransaction { Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Utc = now, Category = LedgerCategory.TicketRevenue, Amount = 13_500m, FlightId = Guid.NewGuid(), Description = "Ticket revenue" });
        ctx.Db.LedgerTransactions.Add(new LedgerTransaction { Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Utc = now, Category = LedgerCategory.VatsimOnlineBonus, Amount = 405m, FlightId = Guid.NewGuid(), Description = "VATSIM online-flying bonus" });
        await ctx.Db.SaveChangesAsync();

        var body = BodyOf(await FinanceEndpoints.CostsAsync(days: 30, ctx.Db, ctx.CurrentUser, CancellationToken.None));
        var revenueBucket = body.GetType().GetProperty("revenue")!.GetValue(body)!;

        // Its own line, never folded into the fare - it is earned by flying online, not by pricing.
        Assert.Equal(13_500m, Prop<decimal>(revenueBucket, "ticketRevenue"));
        Assert.Equal(405m, Prop<decimal>(revenueBucket, "onlineFlyingBonus"));
        Assert.Equal(13_905m, Prop<decimal>(revenueBucket, "total"));

        // And nowhere near either cost side.
        Assert.Equal(0m, Prop<decimal>(body.GetType().GetProperty("fixed")!.GetValue(body)!, "total"));
        Assert.Equal(0m, Prop<decimal>(body.GetType().GetProperty("variable")!.GetValue(body)!, "total"));
    }

    /// <summary>
    /// The flight-scoped sets RoutesAsync/PilotsAsync use are narrower than the page buckets (a
    /// cancellation fee is a variable cost but never lands on a revenue-posted flight), but they must
    /// never contradict them - a category counted as revenue on one screen and cost on another is
    /// the disagreement this whole pass is about.
    /// </summary>
    [Fact]
    public void TheFlightScopedSets_AgreeWithThePageBuckets()
    {
        Assert.All(FinanceEndpoints.FlightRevenueCategories, c => Assert.Equal(FinanceEndpoints.FinanceBucket.OperatingRevenue, FinanceEndpoints.BucketFor(c)));
        Assert.All(FinanceEndpoints.FlightOperatingCostCategories, c => Assert.Equal(FinanceEndpoints.FinanceBucket.Variable, FinanceEndpoints.BucketFor(c)));
        Assert.Empty(FinanceEndpoints.FlightRevenueCategories.Intersect(FinanceEndpoints.FlightOperatingCostCategories));
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

        // The stale-input path for a REMOVED category, checked the same way the GsxServices removal
        // was: a bookmarked or cached "?category=Other" now answers with a plain 400 rather than
        // falling over or, worse, quietly returning the unfiltered list as though the filter applied.
        var retiredCategory = await FinanceEndpoints.LedgerAsync("Other", skip: 0, take: 50, ctx.Db, ctx.CurrentUser, CancellationToken.None);
        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(retiredCategory));
    }
}
