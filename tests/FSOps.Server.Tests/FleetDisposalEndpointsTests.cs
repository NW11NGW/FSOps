using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Finance;
using FSOps.Server.Endpoints;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Tests;

/// <summary>
/// Getting rid of an aircraft - returning a lease and selling. The stated verification for this feature is the two exploit tests
/// (<see cref="BuyThenSellImmediately_IsANetLoss"/>, <see cref="LeaseThenReturnImmediately_LeavesTheAirlineWorseOff_ThanNeverLeasing"/>)
/// plus the disposal-blocking rules and both halves of the end-lease guard - it accepts the first
/// click when only the clock has moved (<see cref="LeaseTermination_ClockAdvancesWithinTheSamePeriod_IsAcceptedOnTheFirstCommit"/>)
/// and still refuses when a rent payment has fallen due (<see cref="LeaseTermination_BillingTickBetweenQuoteAndCommit_IsRefused"/>). Drives
/// FleetDisposalEndpoints' handlers directly against an isolated in-memory RouteTestContext, same
/// convention as FleetEndpointsTests, with a <see cref="FakeClock"/> so time-dependent drift is
/// deterministic rather than a real-wall-clock race.
/// </summary>
public class FleetDisposalEndpointsTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

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

    private static int StatusCodeOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode!.Value;

    /// <summary>Extracts the value out of a Results.Ok(new {...}) response without depending on the
    /// anonymous type's exact compiled name - Results.Ok&lt;T&gt; is invariant, so a cast to
    /// Ok&lt;object&gt; fails for Ok&lt;{anonymous type}&gt; even though both implement
    /// IValueHttpResult non-generically.</summary>
    private static object BodyOf(IResult result) => Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IValueHttpResult>(result).Value!;

    private static T Prop<T>(object body, string name) => (T)body.GetType().GetProperty(name)!.GetValue(body)!;

    [Fact]
    public async Task BuyThenSellImmediately_IsANetLoss()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedStartingCashAsync(ctx, 200_000_000m);
        var catalog = EconomyConfigCatalog.Default();
        var clock = new FakeClock(Base);

        var cashBeforeBuy = await CashBalanceAsync(ctx);

        var buyResult = await FleetEndpoints.BuyAsync(new BuyAircraftRequest(ctx.AircraftType.Id, "New"), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(buyResult));

        var candidates = await ctx.Db.FleetAircraft.Where(f => f.AirlineId == ctx.Airline.Id && f.Ownership == AircraftOwnership.Owned && f.AirframeHours == 0).ToListAsync();
        var bought = candidates.OrderByDescending(f => f.CreatedUtc).First();

        var quoteResult = await FleetDisposalEndpoints.SaleQuoteAsync(bought.Id, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        var saleValue = Prop<decimal>(BodyOf(quoteResult), "saleValue");

        var sellResult = await FleetDisposalEndpoints.SellAsync(bought.Id, new SellAircraftRequest(saleValue), ctx.Db, ctx.CurrentUser, catalog, clock, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(sellResult));

        var cashAfterSell = await CashBalanceAsync(ctx);

        // The whole round trip - buy new, sell without ever flying it - must leave the airline
        // strictly worse off than never having bought it at all - the sale value is depreciated
        // below purchase price, so buying and immediately selling cannot be a free round trip.
        Assert.True(cashAfterSell < cashBeforeBuy, $"Round trip should lose money: before={cashBeforeBuy}, after={cashAfterSell}");

        // FleetAircraft carries a global query filter (DeletedUtc == null) - a soft-deleted row is
        // invisible to a normal query, which is the whole point of soft delete, so verifying the
        // DeletedUtc value itself needs IgnoreQueryFilters().
        var soldAircraft = await ctx.Db.FleetAircraft.IgnoreQueryFilters().FirstAsync(f => f.Id == bought.Id);
        Assert.NotNull(soldAircraft.DeletedUtc);

        var saleLine = await ctx.Db.LedgerTransactions.SingleAsync(t => t.AirlineId == ctx.Airline.Id && t.Category == LedgerCategory.AircraftPurchase && t.Amount > 0);
        var economyConfig = catalog.Get(ctx.Airline.Playstyle);
        var expectedSaleValue = economyConfig.PurchasePriceFor(ctx.AircraftType) * 0.80m; // NewAircraftResaleFactor, zero wear
        Assert.Equal(Math.Round(expectedSaleValue, 2, MidpointRounding.AwayFromZero), saleLine.Amount);
    }

    [Fact]
    public async Task BuyUsedThenSellImmediately_IsAlsoANetLoss()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedStartingCashAsync(ctx, 200_000_000m);
        var catalog = EconomyConfigCatalog.Default();
        var clock = new FakeClock(Base);

        var cashBeforeBuy = await CashBalanceAsync(ctx);

        var buyResult = await FleetEndpoints.BuyAsync(new BuyAircraftRequest(ctx.AircraftType.Id, "Used"), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(buyResult));

        var bought = await ctx.Db.FleetAircraft.Where(f => f.AirlineId == ctx.Airline.Id && f.Ownership == AircraftOwnership.Owned && f.AirframeHours > 0).SingleAsync();

        var quoteResult = await FleetDisposalEndpoints.SaleQuoteAsync(bought.Id, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        var saleValue = Prop<decimal>(BodyOf(quoteResult), "saleValue");

        var sellResult = await FleetDisposalEndpoints.SellAsync(bought.Id, new SellAircraftRequest(saleValue), ctx.Db, ctx.CurrentUser, catalog, clock, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(sellResult));

        var cashAfterSell = await CashBalanceAsync(ctx);

        // Even a used-aircraft flip - the cheapest possible entry point - must lose money against
        // what it cost to buy in the first place (NOT compared to cash right after buying, which
        // trivially rises again the moment anything at all is sold back for a positive amount).
        Assert.True(cashAfterSell < cashBeforeBuy, $"Used-aircraft flip should lose money overall: beforeBuy={cashBeforeBuy}, afterSell={cashAfterSell}");

        // Exact worked example - see the depreciation curve's own doc and the final report: a used
        // aircraft bought at the shipped 70%/70%/70% state and immediately resold values at 0.50 of
        // new against a 0.55-of-new purchase price, a ~9.09% loss on the flip.
        var economyConfig = catalog.Get(ctx.Airline.Playstyle);
        var newPrice = economyConfig.PurchasePriceFor(ctx.AircraftType);
        var usedPurchasePrice = Math.Round(newPrice * economyConfig.UsedAircraft.PriceMultiplier, 2, MidpointRounding.AwayFromZero);
        var expectedLoss = usedPurchasePrice - saleValue;
        Assert.Equal(cashBeforeBuy - expectedLoss, cashAfterSell);
    }

    [Fact]
    public async Task Sell_LeasedAircraft_IsRejected()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedStartingCashAsync(ctx, 10_000_000m);
        var catalog = EconomyConfigCatalog.Default();
        var clock = new FakeClock(Base);

        var leaseResult = await FleetEndpoints.LeaseAsync(new LeaseAircraftRequest(ctx.AircraftType.Id), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(leaseResult));

        var leased = await ctx.Db.FleetAircraft.SingleAsync(f => f.AirlineId == ctx.Airline.Id && f.Ownership == AircraftOwnership.Leased);

        var sellResult = await FleetDisposalEndpoints.SellAsync(leased.Id, new SellAircraftRequest(0m), ctx.Db, ctx.CurrentUser, catalog, clock, CancellationToken.None);
        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(sellResult));

        var stillThere = await ctx.Db.FleetAircraft.SingleAsync(f => f.Id == leased.Id);
        Assert.Null(stillThere.DeletedUtc);
    }

    [Fact]
    public async Task Sell_WhileFlying_IsRejected()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var clock = new FakeClock(Base);
        var aircraft = await ctx.Db.FleetAircraft.SingleAsync();
        aircraft.Status = FleetAircraftStatus.InFlight;
        await ctx.Db.SaveChangesAsync();

        var sellResult = await FleetDisposalEndpoints.SellAsync(aircraft.Id, new SellAircraftRequest(0m), ctx.Db, ctx.CurrentUser, catalog, clock, CancellationToken.None);
        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(sellResult));
    }

    [Fact]
    public async Task Sell_OnAStandingSchedule_IsRejected_AndNamesThePilot()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var clock = new FakeClock(Base);
        var aircraft = await ctx.Db.FleetAircraft.SingleAsync();

        var pilot = new Pilot { Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Name = "Test Pilot", IsPlayer = false, MonthlySalary = 9000m, CreatedUtc = DateTimeOffset.UtcNow };
        ctx.Db.Pilots.Add(pilot);
        var schedule = new PilotSchedule { Id = Guid.NewGuid(), PilotId = pilot.Id, AirlineId = ctx.Airline.Id, CreatedUtc = DateTimeOffset.UtcNow };
        ctx.Db.PilotSchedules.Add(schedule);
        ctx.Db.PilotScheduleEntries.Add(new PilotScheduleEntry
        {
            Id = Guid.NewGuid(),
            PilotScheduleId = schedule.Id,
            DayOfWeek = DayOfWeek.Monday,
            DepartureTimeUtc = TimeSpan.FromHours(8),
            RouteId = Guid.NewGuid(),
            FleetAircraftId = aircraft.Id,
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        await ctx.Db.SaveChangesAsync();

        var quote = await FleetDisposalEndpoints.SaleQuoteAsync(aircraft.Id, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        var body = BodyOf(quote);
        Assert.False(Prop<bool>(body, "canSell"));
        Assert.NotNull(body.GetType().GetProperty("standingScheduleConflict")!.GetValue(body));

        var sellResult = await FleetDisposalEndpoints.SellAsync(aircraft.Id, new SellAircraftRequest(0m), ctx.Db, ctx.CurrentUser, catalog, clock, CancellationToken.None);
        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(sellResult));
    }

    [Fact]
    public async Task LeaseThenReturnImmediately_LeavesTheAirlineWorseOff_ThanNeverLeasing()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedStartingCashAsync(ctx, 10_000_000m);
        var catalog = EconomyConfigCatalog.Default();
        var clock = new FakeClock(Base);

        var cashBeforeLease = await CashBalanceAsync(ctx);

        var leaseResult = await FleetEndpoints.LeaseAsync(new LeaseAircraftRequest(ctx.AircraftType.Id), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(leaseResult));

        var leased = await ctx.Db.FleetAircraft.SingleAsync(f => f.AirlineId == ctx.Airline.Id && f.Ownership == AircraftOwnership.Leased);

        // No EconomyState row exists yet - the endpoint must fall back to the lease's own StartUtc
        // as the period anchor.
        var quote = await FleetDisposalEndpoints.LeaseTerminationQuoteAsync(leased.Id, ctx.Db, ctx.CurrentUser, catalog, clock, CancellationToken.None);
        var totalCharge = Prop<decimal>(BodyOf(quote), "totalCharge");
        var periodStart = Prop<DateTimeOffset>(BodyOf(quote), "currentPeriodStartUtc");

        var endLeaseResult = await FleetDisposalEndpoints.EndLeaseAsync(leased.Id, new EndLeaseRequest(totalCharge, periodStart), ctx.Db, ctx.CurrentUser, catalog, clock, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(endLeaseResult));

        var cashAfterReturn = await CashBalanceAsync(ctx);

        // The core exploit test: lease, return immediately, airline must be worse
        // off than if it had never leased the aircraft at all - an early return carries a penalty
        // so the monthly charge can never be dodged by timing the return around the billing tick.
        Assert.True(cashAfterReturn < cashBeforeLease, $"Lease-then-return should lose money: before={cashBeforeLease}, after={cashAfterReturn}");

        var stillThere = await ctx.Db.FleetAircraft.FirstOrDefaultAsync(f => f.Id == leased.Id && f.DeletedUtc == null);
        Assert.Null(stillThere);

        var lease = await ctx.Db.Leases.SingleAsync(l => l.FleetAircraftId == leased.Id);
        Assert.False(lease.IsActive);
        Assert.NotNull(lease.EndUtc);
    }

    [Fact]
    public async Task EndLease_PartwayThroughPeriod_ChargesProRataPlusFee()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedStartingCashAsync(ctx, 10_000_000m);
        var catalog = EconomyConfigCatalog.Default();
        var clock = new FakeClock(Base);

        var leaseResult = await FleetEndpoints.LeaseAsync(new LeaseAircraftRequest(ctx.AircraftType.Id), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(leaseResult));
        var leased = await ctx.Db.FleetAircraft.SingleAsync(f => f.AirlineId == ctx.Airline.Id && f.Ownership == AircraftOwnership.Leased);
        var lease = await ctx.Db.Leases.SingleAsync(l => l.FleetAircraftId == leased.Id);

        // Anchor the billing period 8 days in the past, matching LeaseTerminationCalculatorTests'
        // partway-through-the-period case, so the pro-rata portion is exact and non-zero.
        var periodStart = Base.AddDays(-8);
        ctx.Db.EconomyStates.Add(new EconomyState
        {
            Id = Guid.NewGuid(),
            LastProcessedUtc = periodStart,
            WorldSeed = 1,
            FuelPricePerKg = 0.85m,
        });
        await ctx.Db.SaveChangesAsync();

        var cashBeforeEnd = await CashBalanceAsync(ctx);
        var quote = await FleetDisposalEndpoints.LeaseTerminationQuoteAsync(leased.Id, ctx.Db, ctx.CurrentUser, catalog, clock, CancellationToken.None);
        var totalCharge = Prop<decimal>(BodyOf(quote), "totalCharge");
        var quotedPeriodStart = Prop<DateTimeOffset>(BodyOf(quote), "currentPeriodStartUtc");

        var endLeaseResult = await FleetDisposalEndpoints.EndLeaseAsync(leased.Id, new EndLeaseRequest(totalCharge, quotedPeriodStart), ctx.Db, ctx.CurrentUser, catalog, clock, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(endLeaseResult));
        var cashAfterEnd = await CashBalanceAsync(ctx);

        var economyConfig = catalog.Get(ctx.Airline.Playstyle);
        var expectedProRata = Math.Round(lease.MonthlyRate * 8m / 30m, 2, MidpointRounding.AwayFromZero);
        var expectedFee = Math.Round(lease.MonthlyRate * (decimal)economyConfig.LeaseEarlyTermination.FeeMonths, 2, MidpointRounding.AwayFromZero);

        Assert.Equal(expectedProRata + expectedFee, totalCharge);
        Assert.Equal(cashBeforeEnd - (expectedProRata + expectedFee), cashAfterEnd);
    }

    /// <summary>
    /// The regression test for "you have to click nearly 3 times to end a lease". The pro-rata rent
    /// in a termination charge grows with the clock, so the commit used to compare the confirmed
    /// figure exactly and refuse the moment a second had passed - which is every real click, because
    /// a player has to read the dialog and type a registration first. The refusal re-quoted, the
    /// re-quote had already drifted too, and the loop never terminated.
    /// <para>
    /// So this asserts what ONE click does, not what three do: a single commit, five days after the
    /// quote it confirmed, and it goes through. If a future change reinstates a money comparison
    /// here, this test fails rather than the user discovering it again.
    /// </para>
    /// </summary>
    [Fact]
    public async Task LeaseTermination_ClockAdvancesWithinTheSamePeriod_IsAcceptedOnTheFirstCommit()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedStartingCashAsync(ctx, 10_000_000m);
        var catalog = EconomyConfigCatalog.Default();
        var clock = new FakeClock(Base);

        await FleetEndpoints.LeaseAsync(new LeaseAircraftRequest(ctx.AircraftType.Id), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        var leased = await ctx.Db.FleetAircraft.SingleAsync(f => f.AirlineId == ctx.Airline.Id && f.Ownership == AircraftOwnership.Leased);
        var lease = await ctx.Db.Leases.SingleAsync(l => l.FleetAircraftId == leased.Id);

        ctx.Db.EconomyStates.Add(new EconomyState { Id = Guid.NewGuid(), LastProcessedUtc = Base, WorldSeed = 1, FuelPricePerKg = 0.85m });
        await ctx.Db.SaveChangesAsync();

        // Player fetches the quote at Base...
        var quote = await FleetDisposalEndpoints.LeaseTerminationQuoteAsync(leased.Id, ctx.Db, ctx.CurrentUser, catalog, clock, CancellationToken.None);
        var quotedCharge = Prop<decimal>(BodyOf(quote), "totalCharge");
        var quotedPeriodStart = Prop<DateTimeOffset>(BodyOf(quote), "currentPeriodStartUtc");

        // ...and doesn't confirm until five days later. No rent has fallen due in that window - the
        // period is still the one they were quoted in - so the charge has drifted but nothing has
        // materially changed.
        clock.UtcNow = Base.AddDays(5);
        var cashBefore = await CashBalanceAsync(ctx);

        var commit = await FleetDisposalEndpoints.EndLeaseAsync(
            leased.Id, new EndLeaseRequest(quotedCharge, quotedPeriodStart), ctx.Db, ctx.CurrentUser, catalog, clock, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(commit));

        // And it charged the RECOMPUTED settlement - five days of rent - not the stale figure the
        // player confirmed. Charging the confirmed one would let anybody freeze the price by leaving
        // the dialog open while the rent kept accruing.
        var economyConfig = catalog.Get(ctx.Airline.Playstyle);
        var expectedProRata = Math.Round(lease.MonthlyRate * 5m / 30m, 2, MidpointRounding.AwayFromZero);
        var expectedFee = Math.Round(lease.MonthlyRate * (decimal)economyConfig.LeaseEarlyTermination.FeeMonths, 2, MidpointRounding.AwayFromZero);

        Assert.Equal(expectedProRata + expectedFee, Prop<decimal>(BodyOf(commit), "totalCharge"));
        Assert.True(expectedProRata + expectedFee > quotedCharge, "the five-day charge must exceed the figure quoted on day zero, or this test proves nothing");
        Assert.Equal(cashBefore - (expectedProRata + expectedFee), await CashBalanceAsync(ctx));
    }

    /// <summary>
    /// The other half of the guard: it still fires for the one thing it exists to catch. A rent
    /// payment falling due moves the lease into a new billing period and resets the pro-rata to
    /// nearly nothing - a change large enough that the player should re-read it rather than have it
    /// applied silently.
    /// </summary>
    [Fact]
    public async Task LeaseTermination_BillingTickBetweenQuoteAndCommit_IsRefused()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedStartingCashAsync(ctx, 10_000_000m);
        var catalog = EconomyConfigCatalog.Default();
        var clock = new FakeClock(Base.AddDays(20));

        await FleetEndpoints.LeaseAsync(new LeaseAircraftRequest(ctx.AircraftType.Id), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        var leased = await ctx.Db.FleetAircraft.SingleAsync(f => f.AirlineId == ctx.Airline.Id && f.Ownership == AircraftOwnership.Leased);

        var economyState = new EconomyState { Id = Guid.NewGuid(), LastProcessedUtc = Base, WorldSeed = 1, FuelPricePerKg = 0.85m };
        ctx.Db.EconomyStates.Add(economyState);
        await ctx.Db.SaveChangesAsync();

        // Quoted 20 days into the period, so the pro-rata portion is substantial.
        var quote = await FleetDisposalEndpoints.LeaseTerminationQuoteAsync(leased.Id, ctx.Db, ctx.CurrentUser, catalog, clock, CancellationToken.None);
        var quotedCharge = Prop<decimal>(BodyOf(quote), "totalCharge");
        var quotedPeriodStart = Prop<DateTimeOffset>(BodyOf(quote), "currentPeriodStartUtc");

        // EconomyClockService posts the due period and advances its watermark by exactly one whole
        // PeriodLength - the lease has now been billed for that month and a fresh period has begun.
        clock.UtcNow = Base + EconomyClockService.PeriodLength + TimeSpan.FromDays(1);
        economyState.LastProcessedUtc = Base + EconomyClockService.PeriodLength;
        await ctx.Db.SaveChangesAsync();

        var staleCommit = await FleetDisposalEndpoints.EndLeaseAsync(
            leased.Id, new EndLeaseRequest(quotedCharge, quotedPeriodStart), ctx.Db, ctx.CurrentUser, catalog, clock, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(staleCommit));
        var currentTotalCharge = Prop<decimal>(BodyOf(staleCommit), "currentTotalCharge");
        Assert.NotEqual(quotedCharge, currentTotalCharge);

        // Nothing was posted and the lease is still active - a refusal must never be a partial commit.
        Assert.True((await ctx.Db.Leases.SingleAsync(l => l.FleetAircraftId == leased.Id)).IsActive);

        // Re-confirming against the NEW period succeeds - the guard blocks stale data, not the action.
        var freshQuote = await FleetDisposalEndpoints.LeaseTerminationQuoteAsync(leased.Id, ctx.Db, ctx.CurrentUser, catalog, clock, CancellationToken.None);
        var freshCommit = await FleetDisposalEndpoints.EndLeaseAsync(
            leased.Id,
            new EndLeaseRequest(Prop<decimal>(BodyOf(freshQuote), "totalCharge"), Prop<DateTimeOffset>(BodyOf(freshQuote), "currentPeriodStartUtc")),
            ctx.Db, ctx.CurrentUser, catalog, clock, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(freshCommit));
    }

    /// <summary>A caller that sends no period at all must be refused, not quietly allowed through -
    /// the failure direction of a missing guard has to be "re-quote", never "charge something the
    /// player never confirmed".</summary>
    [Fact]
    public async Task LeaseTermination_CommitWithoutAPeriod_IsRefused()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedStartingCashAsync(ctx, 10_000_000m);
        var catalog = EconomyConfigCatalog.Default();
        var clock = new FakeClock(Base);

        await FleetEndpoints.LeaseAsync(new LeaseAircraftRequest(ctx.AircraftType.Id), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        var leased = await ctx.Db.FleetAircraft.SingleAsync(f => f.AirlineId == ctx.Airline.Id && f.Ownership == AircraftOwnership.Leased);

        ctx.Db.EconomyStates.Add(new EconomyState { Id = Guid.NewGuid(), LastProcessedUtc = Base, WorldSeed = 1, FuelPricePerKg = 0.85m });
        await ctx.Db.SaveChangesAsync();

        var quote = await FleetDisposalEndpoints.LeaseTerminationQuoteAsync(leased.Id, ctx.Db, ctx.CurrentUser, catalog, clock, CancellationToken.None);

        var commit = await FleetDisposalEndpoints.EndLeaseAsync(
            leased.Id, new EndLeaseRequest(Prop<decimal>(BodyOf(quote), "totalCharge"), null), ctx.Db, ctx.CurrentUser, catalog, clock, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(commit));
        Assert.True((await ctx.Db.Leases.SingleAsync(l => l.FleetAircraftId == leased.Id)).IsActive);
    }

    [Fact]
    public async Task SaleQuote_LastAircraft_IsFlagged_ButNotBlocked()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var aircraft = await ctx.Db.FleetAircraft.SingleAsync();

        var quote = await FleetDisposalEndpoints.SaleQuoteAsync(aircraft.Id, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        var body = BodyOf(quote);

        Assert.True(Prop<bool>(body, "isLastAircraft"));
        Assert.True(Prop<bool>(body, "canSell"));
    }
}
