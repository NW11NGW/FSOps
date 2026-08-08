using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Finance;
using FSOps.Server.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Tests;

/// <summary>
/// Chunk E1's own stated verification: buy a used aircraft and show its condition/hours differ
/// from new, and take a loan and show the ledger. Drives FleetEndpoints' handlers directly against
/// an isolated in-memory RouteTestContext - never the real database.
/// </summary>
public class FleetEndpointsTests
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

    private static async Task<decimal> CashBalanceAsync(RouteTestContext ctx)
    {
        var amounts = await ctx.Db.LedgerTransactions.Where(t => t.AirlineId == ctx.Airline.Id).Select(t => t.Amount).ToListAsync();
        return amounts.Sum();
    }

    [Fact]
    public async Task BuyUsed_IsCheaperThanNew_AndStartsWornIn()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedStartingCashAsync(ctx, 200_000_000m);
        var catalog = EconomyConfigCatalog.Default();

        var buyNewResult = await FleetEndpoints.BuyAsync(
            new BuyAircraftRequest(ctx.AircraftType.Id, "New"), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(buyNewResult));

        var buyUsedResult = await FleetEndpoints.BuyAsync(
            new BuyAircraftRequest(ctx.AircraftType.Id, "Used"), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(buyUsedResult));

        // RouteTestContext already seeds one Owned founding aircraft at 100% condition, so the two
        // just-bought aircraft plus that one makes three - distinguish the two new purchases by
        // AirframeHours instead (the seeded aircraft and the new purchase are both 0h/100%, but only
        // the used purchase has non-zero hours and sub-100 condition).
        var fleet = await ctx.Db.FleetAircraft.Where(f => f.AirlineId == ctx.Airline.Id && f.Ownership == AircraftOwnership.Owned).ToListAsync();
        Assert.Equal(3, fleet.Count);

        var usedAircraft = fleet.Single(f => f.AirframeHours > 0);
        var newAircraft = fleet.First(f => f.Id != usedAircraft.Id && f.ConditionPercent == 100);

        Assert.Equal(0, newAircraft.HoursSinceACheck);
        Assert.Equal(0, newAircraft.AirframeHours);

        var economyConfig = catalog.Get(ctx.Airline.Playstyle);
        Assert.True(usedAircraft.ConditionPercent < newAircraft.ConditionPercent);
        Assert.True(usedAircraft.HoursSinceACheck > newAircraft.HoursSinceACheck);
        Assert.True(usedAircraft.AirframeHours > 0);
        Assert.Equal(economyConfig.UsedAircraft.StartingConditionPercent, usedAircraft.ConditionPercent);

        // Two purchases posted, used strictly cheaper than new for the same type.
        var purchases = await ctx.Db.LedgerTransactions
            .Where(t => t.AirlineId == ctx.Airline.Id && t.Category == LedgerCategory.AircraftPurchase)
            .ToListAsync();
        Assert.Equal(2, purchases.Count);
        var newPurchase = purchases.Single(p => p.Description.Contains("new"));
        var usedPurchase = purchases.Single(p => p.Description.Contains("used"));
        Assert.True(Math.Abs(usedPurchase.Amount) < Math.Abs(newPurchase.Amount));
        Assert.Equal(ctx.AircraftType.PurchasePrice * economyConfig.UsedAircraft.PriceMultiplier, -usedPurchase.Amount);
    }

    [Fact]
    public async Task Buy_InsufficientFunds_IsRejected_CashUnchanged()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedStartingCashAsync(ctx, 1_000m); // far short of any aircraft price
        var catalog = EconomyConfigCatalog.Default();
        var cashBefore = await CashBalanceAsync(ctx);

        var result = await FleetEndpoints.BuyAsync(
            new BuyAircraftRequest(ctx.AircraftType.Id, "New"), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        Assert.Equal(cashBefore, await CashBalanceAsync(ctx));
        Assert.Equal(1, await ctx.Db.FleetAircraft.CountAsync()); // only the seeded founding aircraft
    }

    [Fact]
    public async Task Lease_ChargesDepositAndCreatesActiveLease()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedStartingCashAsync(ctx, 2_000_000m);
        var catalog = EconomyConfigCatalog.Default();
        var economyConfig = catalog.Get(ctx.Airline.Playstyle);
        // Priced from economy-config.json's LeaseRates, keyed by ICAO type - never
        // ctx.AircraftType.MonthlyLeaseRate (seeded to an arbitrary 500,000 specifically so this
        // test cannot pass by accidentally reading that column - see
        // Lease_ChargesThePlaystylesOwnRate_NotTheCatalogueColumn below for the dedicated
        // regression test).
        var expectedRate = economyConfig.LeaseRateFor(ctx.AircraftType.IcaoType);
        var expectedDeposit = expectedRate * (decimal)economyConfig.AirlineStartup.LeaseDepositMonths;
        var cashBefore = await CashBalanceAsync(ctx);

        var result = await FleetEndpoints.LeaseAsync(
            new LeaseAircraftRequest(ctx.AircraftType.Id), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(result));
        Assert.Equal(cashBefore - expectedDeposit, await CashBalanceAsync(ctx));

        var leased = await ctx.Db.FleetAircraft.SingleAsync(f => f.Ownership == AircraftOwnership.Leased);
        Assert.Equal(100, leased.ConditionPercent);
        Assert.Equal(FleetAircraftStatus.Active, leased.Status);

        var lease = await ctx.Db.Leases.SingleAsync(l => l.FleetAircraftId == leased.Id);
        Assert.True(lease.IsActive);
        Assert.Equal(expectedRate, lease.MonthlyRate);
        Assert.NotEqual(ctx.AircraftType.MonthlyLeaseRate, lease.MonthlyRate);
    }

    /// <summary>
    /// The regression test for the Chunk E1 bug: a True-life airline leasing an additional A320
    /// must be charged the True-life rate and a Casual airline the Casual rate, and neither must
    /// ever be charged AircraftType.MonthlyLeaseRate (seeded here to 500,000 - not the Casual
    /// 30,000 or True-life 380,000 figure) regardless of what value happens to be sitting in that
    /// database column. This is what makes "a True-life airline can lease a second A320 at the
    /// Casual rate" and "pricing depends on when the database was seeded" impossible to reintroduce.
    /// </summary>
    [Fact]
    public async Task Lease_ChargesThePlaystylesOwnRate_NotTheCatalogueColumn()
    {
        var catalog = EconomyConfigCatalog.Default();

        using var casualCtx = await RouteTestContext.CreateAsync();
        await SeedStartingCashAsync(casualCtx, 5_000_000m);
        casualCtx.Airline.Playstyle = AirlinePlaystyle.Casual;
        await casualCtx.Db.SaveChangesAsync();

        var casualResult = await FleetEndpoints.LeaseAsync(
            new LeaseAircraftRequest(casualCtx.AircraftType.Id), casualCtx.Db, casualCtx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(casualResult));
        var casualLease = await casualCtx.Db.Leases.SingleAsync(l => l.AirlineId == casualCtx.Airline.Id);

        using var trueLifeCtx = await RouteTestContext.CreateAsync();
        await SeedStartingCashAsync(trueLifeCtx, 5_000_000m);
        trueLifeCtx.Airline.Playstyle = AirlinePlaystyle.TrueLife;
        await trueLifeCtx.Db.SaveChangesAsync();

        var trueLifeResult = await FleetEndpoints.LeaseAsync(
            new LeaseAircraftRequest(trueLifeCtx.AircraftType.Id), trueLifeCtx.Db, trueLifeCtx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(trueLifeResult));
        var trueLifeLease = await trueLifeCtx.Db.Leases.SingleAsync(l => l.AirlineId == trueLifeCtx.Airline.Id);

        Assert.Equal(30_000m, casualLease.MonthlyRate);
        Assert.Equal(380_000m, trueLifeLease.MonthlyRate);
        Assert.NotEqual(casualLease.MonthlyRate, trueLifeLease.MonthlyRate);
        Assert.NotEqual(casualCtx.AircraftType.MonthlyLeaseRate, casualLease.MonthlyRate);
        Assert.NotEqual(trueLifeCtx.AircraftType.MonthlyLeaseRate, trueLifeLease.MonthlyRate);
    }

    [Fact]
    public async Task Lease_TwoAircraftOfTheSameType_GetDifferentRegistrations()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedStartingCashAsync(ctx, 5_000_000m);
        var catalog = EconomyConfigCatalog.Default();

        await FleetEndpoints.LeaseAsync(
            new LeaseAircraftRequest(ctx.AircraftType.Id), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        await FleetEndpoints.LeaseAsync(
            new LeaseAircraftRequest(ctx.AircraftType.Id), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        var registrations = await ctx.Db.FleetAircraft.Select(f => f.Registration).ToListAsync();
        Assert.Equal(registrations.Count, registrations.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task TakeLoan_WithinEligibility_CreatesLoanAndPostsProceeds()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        // Trailing net operating cash flow is built from non-StartingCapital/LoanProceeds lines -
        // simulate a healthy month of ticket revenue.
        ctx.Db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Utc = DateTimeOffset.UtcNow,
            Category = LedgerCategory.TicketRevenue, Amount = 100_000m, Description = "Simulated revenue",
        });
        await ctx.Db.SaveChangesAsync();

        var cashBefore = await CashBalanceAsync(ctx);

        // 100,000 trailing net cash flow -> max monthly payment 30,000. A small, clearly
        // affordable loan. No rate is supplied - TakeLoanRequest has no rate field at all (see
        // docs/PLAN.md "Loan interest is set by the simulation, never by the player").
        var result = await FleetEndpoints.TakeLoanAsync(
            new TakeLoanRequest(200_000m, 60), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(result));
        Assert.Equal(cashBefore + 200_000m, await CashBalanceAsync(ctx));

        var loan = await ctx.Db.Loans.SingleAsync();
        Assert.Equal(200_000m, loan.Principal);
        Assert.Equal(200_000m, loan.RemainingBalance);
        Assert.False(loan.IsPaidOff);

        // The rate is computed, not hardcoded - re-derive it the same way TakeLoanAsync does
        // (LoanRateCalculator against this airline's own playstyle and trailing cash flow) so this
        // assertion can never silently pass by coincidence.
        var economyConfig = catalog.Get(ctx.Airline.Playstyle);
        var expectedRate = LoanRateCalculator.ComputeAnnualRatePct(200_000m, 60, 100_000m, economyConfig.Loan);
        Assert.Equal(expectedRate, loan.AnnualInterestRate);
        Assert.Equal(LoanCalculator.MonthlyPayment(200_000m, expectedRate, 60), loan.MonthlyPayment);

        var proceedsLine = await ctx.Db.LedgerTransactions.SingleAsync(t => t.Category == LedgerCategory.LoanProceeds);
        Assert.Equal(200_000m, proceedsLine.Amount);
    }

    [Fact]
    public async Task TakeLoan_ExceedingEligibility_IsRejected_NoLoanCreated()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        ctx.Db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Utc = DateTimeOffset.UtcNow,
            Category = LedgerCategory.TicketRevenue, Amount = 45_000m, Description = "Casual-scale monthly net",
        });
        await ctx.Db.SaveChangesAsync();

        // A used-A320-scale loan against a single casual aircraft's cash flow - exactly the
        // scenario LoanEligibilityCalculator's class doc says must fail, at whatever rate this
        // ends up priced at (even the cap rate's payment on 20,000,000 is nowhere near affordable).
        var result = await FleetEndpoints.TakeLoanAsync(
            new TakeLoanRequest(20_000_000m, 60), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        Assert.Empty(await ctx.Db.Loans.ToListAsync());
        Assert.DoesNotContain(await ctx.Db.LedgerTransactions.ToListAsync(), t => t.Category == LedgerCategory.LoanProceeds);
    }

    [Fact]
    public async Task TakeLoan_CannotBootstrapEligibilityFromItsOwnStartingCapitalOrPriorLoanProceeds()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        // A huge one-off starting-capital injection and a prior loan's proceeds - neither should
        // count toward "trailing net operating cash flow" for a NEW loan's eligibility.
        ctx.Db.LedgerTransactions.AddRange(
            new LedgerTransaction
            {
                Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Utc = DateTimeOffset.UtcNow,
                Category = LedgerCategory.StartingCapital, Amount = 2_000_000m, Description = "Starting capital",
            },
            new LedgerTransaction
            {
                Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Utc = DateTimeOffset.UtcNow,
                Category = LedgerCategory.LoanProceeds, Amount = 5_000_000m, Description = "Prior loan proceeds",
            });
        await ctx.Db.SaveChangesAsync();

        var result = await FleetEndpoints.TakeLoanAsync(
            new TakeLoanRequest(1_000_000m, 60), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
    }

    /// <summary>
    /// The Chunk E1 exploit fix's own regression test: TakeLoanRequest has no rate field at all,
    /// so there is no way for a caller to smuggle a 0% (or any other) rate into a loan - see
    /// docs/PLAN.md "Loan interest is set by the simulation, never by the player". A healthy cash
    /// flow but a tiny loan (barely touches borrowing capacity) must still be priced at the
    /// playstyle's BASE rate, never 0%.
    /// </summary>
    [Fact]
    public async Task TakeLoan_NeverChargesZeroPercent_EvenForATinyLoanAgainstHealthyCashFlow()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        ctx.Db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Utc = DateTimeOffset.UtcNow,
            Category = LedgerCategory.TicketRevenue, Amount = 1_000_000m, Description = "Very healthy month",
        });
        await ctx.Db.SaveChangesAsync();

        var result = await FleetEndpoints.TakeLoanAsync(
            new TakeLoanRequest(1_000m, 60), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(result));
        var loan = await ctx.Db.Loans.SingleAsync();

        // A tiny loan against a very healthy cash flow consumes almost none of the borrowing
        // capacity, so it should sit very close to (but never below) the playstyle's base rate -
        // and, above all, never at or near 0%, which is the exploit this whole feature closes.
        var economyConfig = catalog.Get(ctx.Airline.Playstyle);
        Assert.True(loan.AnnualInterestRate > 0);
        Assert.True(loan.AnnualInterestRate >= economyConfig.Loan.BaseAnnualRatePct);
        Assert.True(loan.AnnualInterestRate < economyConfig.Loan.BaseAnnualRatePct + 0.01);
        Assert.True(loan.AnnualInterestRate <= economyConfig.Loan.CapAnnualRatePct);
    }

    /// <summary>
    /// A loan's rate is fixed for its life at the rate computed when it was taken - see
    /// docs/PLAN.md "A loan's rate is fixed for its life". Taking a second, larger loan later
    /// (against a materially different trailing cash flow, so it necessarily prices at a different
    /// rate) must not retroactively touch the first loan's stored AnnualInterestRate.
    /// </summary>
    [Fact]
    public async Task TakeLoan_ALaterLoanAtADifferentRate_DoesNotChangeAnExistingLoansRate()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        ctx.Db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Utc = DateTimeOffset.UtcNow,
            Category = LedgerCategory.TicketRevenue, Amount = 500_000m, Description = "Healthy month",
        });
        await ctx.Db.SaveChangesAsync();

        // A small first loan - prices close to the base rate.
        var firstResult = await FleetEndpoints.TakeLoanAsync(
            new TakeLoanRequest(5_000m, 60), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(firstResult));
        var firstLoan = await ctx.Db.Loans.SingleAsync();
        var firstLoanRateAfterFirstTake = firstLoan.AnnualInterestRate;

        // A second, much larger loan against the same (now slightly reduced by the first loan's
        // ledger entry, but still healthy) cash flow - consumes far more of the remaining capacity,
        // so it must price at a materially higher rate than the first loan did.
        var secondResult = await FleetEndpoints.TakeLoanAsync(
            new TakeLoanRequest(120_000m, 60), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(secondResult));

        // SQLite's EF provider can't translate an OrderBy over DateTimeOffset - materialise first,
        // then order client-side (project convention).
        var loansAfter = (await ctx.Db.Loans.ToListAsync()).OrderBy(l => l.CreatedUtc).ToList();
        Assert.Equal(2, loansAfter.Count);
        var firstLoanAfter = loansAfter[0];
        var secondLoanAfter = loansAfter[1];

        // The first loan's row is completely untouched by the second loan's own pricing.
        Assert.Equal(firstLoan.Id, firstLoanAfter.Id);
        Assert.Equal(firstLoanRateAfterFirstTake, firstLoanAfter.AnnualInterestRate);
        Assert.Equal(5_000m, firstLoanAfter.Principal);

        // And the two loans genuinely differ in rate - proves this isn't a vacuous pass where both
        // loans happened to price identically.
        Assert.True(secondLoanAfter.AnnualInterestRate > firstLoanAfter.AnnualInterestRate);
    }

    private static int StatusCodeOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;
}
