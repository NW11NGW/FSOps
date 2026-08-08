using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Data;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSOps.Server.Tests;

/// <summary>
/// Chunk E1's own stated verification (docs/PLAN.md "Wall-clock manipulation" and the E1 task
/// brief): the monthly cycle must post lease/salary/insurance lines exactly once per period no
/// matter how many times or how concurrently it is resolved, must catch up correctly after the app
/// was closed for days, must never move <see cref="EconomyState.LastProcessedUtc"/> backwards, and
/// must bound how much catch-up a single pass performs. Every test drives
/// <see cref="EconomyClockService.RunOnceAsync"/> directly against an isolated in-memory SQLite
/// database (see RouteTestContext) with a <see cref="FakeClock"/> - never the real database, never
/// a real wall-clock wait.
/// </summary>
public class EconomyClockServiceTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Period = EconomyClockService.PeriodLength;

    private const decimal LeaseRate = 30_000m;
    private const decimal PilotSalary = 9_000m;

    // EconomyConfig.Default().FleetFinance.MonthlyInsurancePerAircraft = 6,000m, so one aircraft +
    // one lease + one pilot totals exactly 45,000/month - the same figure
    // StartupTrajectoryTests.MonthlyFixedCostPerAircraft() asserts the balance against.
    private const decimal ExpectedMonthlyTotal = -(LeaseRate + PilotSalary + 6_000m);

    [Fact]
    public async Task ResolvedTwiceAtTheSameTime_PostsExactlyOneSetOfPostings()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await BackdateFleetAndSeedLeaseAndPilotAsync(ctx);
        await SeedEconomyStateAsync(ctx, lastProcessedUtc: Base);

        var now = Base + Period;
        var clock = new FakeClock(now);
        var service = CreateService(ctx, clock);

        var first = await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, first.PeriodsPosted);

        var linesAfterFirst = await ctx.Db.LedgerTransactions.Where(t => t.AirlineId == ctx.Airline.Id).ToListAsync();
        Assert.Equal(3, linesAfterFirst.Count); // lease + salary + insurance
        Assert.Equal(ExpectedMonthlyTotal, linesAfterFirst.Sum(t => t.Amount));

        // The same resolution, called again at the same clock time - exactly what two overlapping
        // ticks, or a retry after an ambiguous failure, would do. Must be a complete no-op.
        var second = await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, second.PeriodsPosted);

        var linesAfterSecond = await ctx.Db.LedgerTransactions.Where(t => t.AirlineId == ctx.Airline.Id).ToListAsync();
        Assert.Equal(linesAfterFirst.Count, linesAfterSecond.Count);
        Assert.Equal(linesAfterFirst.Sum(t => t.Amount), linesAfterSecond.Sum(t => t.Amount));
    }

    [Fact]
    public async Task ClosedForThreeMonths_ChargesThreeMonths_OnceEach_InOrder()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await BackdateFleetAndSeedLeaseAndPilotAsync(ctx);
        await SeedEconomyStateAsync(ctx, lastProcessedUtc: Base);

        var now = Base + Period * 3; // app reopened after three missed months
        var clock = new FakeClock(now);
        var service = CreateService(ctx, clock);

        var result = await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(3, result.PeriodsPosted);
        Assert.False(result.ClockMovedBackwards);
        Assert.False(result.MoreCatchUpRemaining);
        Assert.Equal(now, result.LastProcessedUtc);

        var lines = await ctx.Db.LedgerTransactions.Where(t => t.AirlineId == ctx.Airline.Id).ToListAsync();
        Assert.Equal(9, lines.Count); // 3 periods x (lease + salary + insurance)
        Assert.Equal(ExpectedMonthlyTotal * 3, lines.Sum(t => t.Amount));

        // Once each, in order: three distinct period timestamps, each exactly Period apart,
        // matching Base + Period, Base + 2*Period, Base + 3*Period.
        var periodTimestamps = lines.Select(l => l.Utc).Distinct().OrderBy(u => u).ToList();
        Assert.Equal(new[] { Base + Period, Base + Period * 2, Base + Period * 3 }, periodTimestamps);
        foreach (var timestamp in periodTimestamps)
        {
            Assert.Equal(3, lines.Count(l => l.Utc == timestamp)); // lease + salary + insurance every period
        }

        // AsNoTracking: RunOnceAsync updated the row through its own scoped DbContext against the
        // same connection, but ctx.Db still has the original row tracked from SeedEconomyStateAsync -
        // without this, the identity map hands back the stale in-memory instance instead of
        // re-reading what was actually committed.
        var state = await ctx.Db.EconomyStates.AsNoTracking().SingleAsync();
        Assert.Equal(now, state.LastProcessedUtc);
    }

    [Fact]
    public async Task RestartAfterCatchUp_ResumesFromThePersistedWatermark_NeverReCharges()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await BackdateFleetAndSeedLeaseAndPilotAsync(ctx);
        await SeedEconomyStateAsync(ctx, lastProcessedUtc: Base);

        var now = Base + Period * 2;
        var clock = new FakeClock(now);

        // First "process lifetime": one service instance catches up.
        var firstInstance = CreateService(ctx, clock);
        var firstResult = await firstInstance.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, firstResult.PeriodsPosted);

        // A crash/restart: a brand new EconomyClockService (no in-memory state carried over,
        // exactly as a fresh process would have) against the same database. Idempotency here can
        // only come from the persisted EconomyState row, not from anything held in memory.
        var secondInstance = CreateService(ctx, clock);
        var secondResult = await secondInstance.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, secondResult.PeriodsPosted);

        var lines = await ctx.Db.LedgerTransactions.Where(t => t.AirlineId == ctx.Airline.Id).ToListAsync();
        Assert.Equal(6, lines.Count); // 2 periods, not 4
        Assert.Equal(ExpectedMonthlyTotal * 2, lines.Sum(t => t.Amount));
    }

    [Fact]
    public async Task TwoTicksRacingConcurrently_StillPostsEachDuePeriodExactlyOnce()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await BackdateFleetAndSeedLeaseAndPilotAsync(ctx);
        await SeedEconomyStateAsync(ctx, lastProcessedUtc: Base);

        var now = Base + Period * 2;
        var clock = new FakeClock(now);
        var service = CreateService(ctx, clock);

        // Two overlapping calls, as two PeriodicTimer ticks racing (or a tick overlapping a
        // slow-running previous pass) would produce.
        var results = await Task.WhenAll(
            service.RunOnceAsync(CancellationToken.None),
            service.RunOnceAsync(CancellationToken.None));

        Assert.Equal(2, results.Sum(r => r.PeriodsPosted)); // the two due periods, split across the two calls, never duplicated

        var lines = await ctx.Db.LedgerTransactions.Where(t => t.AirlineId == ctx.Airline.Id).ToListAsync();
        Assert.Equal(6, lines.Count);
        Assert.Equal(ExpectedMonthlyTotal * 2, lines.Sum(t => t.Amount));
    }

    [Fact]
    public async Task ClockMovingBackwards_IsRecordedAndProcessesNothing_WatermarkNeverMovesBackwards()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await BackdateFleetAndSeedLeaseAndPilotAsync(ctx);
        await SeedEconomyStateAsync(ctx, lastProcessedUtc: Base + Period * 5);

        // The observed clock reports a time before the watermark - a backwards jump.
        var clock = new FakeClock(Base + Period * 5 - TimeSpan.FromDays(1));
        var service = CreateService(ctx, clock);

        var result = await service.RunOnceAsync(CancellationToken.None);

        Assert.True(result.ClockMovedBackwards);
        Assert.Equal(0, result.PeriodsPosted);
        Assert.Equal(Base + Period * 5, result.LastProcessedUtc); // unchanged, never moved backwards

        var state = await ctx.Db.EconomyStates.AsNoTracking().SingleAsync();
        Assert.Equal(Base + Period * 5, state.LastProcessedUtc);

        var lines = await ctx.Db.LedgerTransactions.Where(t => t.AirlineId == ctx.Airline.Id).ToListAsync();
        Assert.Empty(lines);
    }

    [Fact]
    public async Task EnormousApparentJump_IsBoundedPerPass_NotOneUnboundedBurst()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await BackdateFleetAndSeedLeaseAndPilotAsync(ctx);
        await SeedEconomyStateAsync(ctx, lastProcessedUtc: Base);

        // 100 months owed in one apparent jump (e.g. the system clock was wound forward, or the
        // app was genuinely closed for years).
        var now = Base + Period * 100;
        var clock = new FakeClock(now);
        var service = CreateService(ctx, clock);

        var firstPass = await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(24, firstPass.PeriodsPosted); // capped, not all 100 in one go
        Assert.True(firstPass.MoreCatchUpRemaining);
        Assert.Equal(Base + Period * 24, firstPass.LastProcessedUtc);

        var secondPass = await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(24, secondPass.PeriodsPosted); // continues at the same bounded rate
        Assert.True(secondPass.MoreCatchUpRemaining);
        Assert.Equal(Base + Period * 48, secondPass.LastProcessedUtc);

        var lines = await ctx.Db.LedgerTransactions.Where(t => t.AirlineId == ctx.Airline.Id).ToListAsync();
        Assert.Equal(48 * 3, lines.Count); // 48 periods processed so far across the two passes, none skipped, none doubled
    }

    /// <summary>
    /// The integration-gap regression test: insurance is a config-derived figure that now differs
    /// by <see cref="AirlinePlaystyle"/> (Casual 6,000 vs True-life 50,000 in
    /// EconomyConfigCatalog.Default()), so a monthly pass covering two airlines on different
    /// playstyles must bill each its own rate, never one shared figure. This is what would have
    /// caught the original bug (EconomyClockService reading a flat EconomyConfig, which always
    /// resolved to Casual) and is what keeps it from creeping back in.
    /// </summary>
    [Fact]
    public async Task TwoAirlinesOnDifferentPlaystyles_EachBilledItsOwnInsuranceRate()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await BackdateFleetAndSeedLeaseAndPilotAsync(ctx); // ctx.Airline defaults to AirlinePlaystyle.Casual

        var trueLifeAirline = new Airline
        {
            Id = Guid.NewGuid(),
            Name = "True Life Air",
            IcaoCode = "TLA",
            HomeAirportIcao = "EGGD",
            StrategyProfile = AirlineStrategyProfile.International,
            Playstyle = AirlinePlaystyle.TrueLife,
            OwnerUserId = Guid.NewGuid(),
            CreatedUtc = Base,
        };
        ctx.Db.Airlines.Add(trueLifeAirline);

        var trueLifeAircraft = new FleetAircraft
        {
            Id = Guid.NewGuid(),
            AirlineId = trueLifeAirline.Id,
            AircraftTypeId = ctx.AircraftType.Id,
            Registration = "G-TRUE",
            Ownership = AircraftOwnership.Leased,
            LocationIcao = "EGGD",
            Status = FleetAircraftStatus.Active,
            CreatedUtc = Base,
        };
        ctx.Db.FleetAircraft.Add(trueLifeAircraft);

        ctx.Db.Leases.Add(new Lease
        {
            Id = Guid.NewGuid(),
            AirlineId = trueLifeAirline.Id,
            FleetAircraftId = trueLifeAircraft.Id,
            MonthlyRate = 380_000m,
            StartUtc = Base,
            IsActive = true,
            CreatedUtc = Base,
        });
        ctx.Db.Pilots.Add(new Pilot
        {
            Id = Guid.NewGuid(),
            AirlineId = trueLifeAirline.Id,
            Name = "True Life Pilot",
            IsPlayer = true,
            MonthlySalary = 9_000m,
            SkillRating = 50,
            CreatedUtc = Base,
        });
        await ctx.Db.SaveChangesAsync();

        await SeedEconomyStateAsync(ctx, lastProcessedUtc: Base);

        var now = Base + Period;
        var service = CreateService(ctx, new FakeClock(now), EconomyConfigCatalog.Default());

        var result = await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, result.PeriodsPosted);

        var casualInsurance = await ctx.Db.LedgerTransactions
            .Where(t => t.AirlineId == ctx.Airline.Id && t.Category == LedgerCategory.Insurance)
            .SingleAsync();
        var trueLifeInsurance = await ctx.Db.LedgerTransactions
            .Where(t => t.AirlineId == trueLifeAirline.Id && t.Category == LedgerCategory.Insurance)
            .SingleAsync();

        Assert.Equal(-6_000m, casualInsurance.Amount);
        Assert.Equal(-50_000m, trueLifeInsurance.Amount);
    }

    /// <summary>
    /// Chunk E1's own stated verification: take a loan and show the ledger - specifically, that the
    /// monthly cycle actually amortises it (the one piece of the Chunk B loan feature that never
    /// existed before E1: a founding/mid-game loan's RemainingBalance never moved and no LoanPayment
    /// line was ever posted).
    /// </summary>
    [Fact]
    public async Task OutstandingLoan_IsAmortisedMonthly_InterestAndPrincipalSplit()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await BackdateFleetAndSeedLeaseAndPilotAsync(ctx);
        await SeedEconomyStateAsync(ctx, lastProcessedUtc: Base);

        const decimal principal = 200_000m;
        const double annualRate = 6.0;
        const int termMonths = 60;
        var monthlyPayment = FSOps.Core.Finance.LoanCalculator.MonthlyPayment(principal, annualRate, termMonths);

        var loan = new Loan
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Principal = principal,
            AnnualInterestRate = annualRate,
            TermMonths = termMonths,
            MonthlyPayment = monthlyPayment,
            RemainingBalance = principal,
            StartUtc = Base,
            IsPaidOff = false,
            CreatedUtc = Base,
        };
        ctx.Db.Loans.Add(loan);
        await ctx.Db.SaveChangesAsync();

        var now = Base + Period;
        var service = CreateService(ctx, new FakeClock(now));

        var result = await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, result.PeriodsPosted);

        var updatedLoan = await ctx.Db.Loans.AsNoTracking().SingleAsync();
        // First month's interest on 200,000 at 6% APR = 200,000 * 0.06/12 = 1,000 exactly.
        var expectedPrincipalPortion = monthlyPayment - 1_000m;
        Assert.Equal(principal - expectedPrincipalPortion, updatedLoan.RemainingBalance);
        Assert.False(updatedLoan.IsPaidOff);

        var loanPaymentLine = await ctx.Db.LedgerTransactions
            .SingleAsync(t => t.AirlineId == ctx.Airline.Id && t.Category == LedgerCategory.LoanPayment);
        Assert.Equal(-monthlyPayment, loanPaymentLine.Amount);
    }

    [Fact]
    public async Task LoanPaidOffPartwayThroughATerm_StopsBeingCharged_NeverGoesNegative()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await BackdateFleetAndSeedLeaseAndPilotAsync(ctx);
        await SeedEconomyStateAsync(ctx, lastProcessedUtc: Base);

        // A small remaining balance smaller than one level payment - due to be paid off on the
        // very next period.
        var loan = new Loan
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Principal = 10_000m,
            AnnualInterestRate = 6.0,
            TermMonths = 60,
            MonthlyPayment = 500m,
            RemainingBalance = 400m,
            StartUtc = Base,
            IsPaidOff = false,
            CreatedUtc = Base,
        };
        ctx.Db.Loans.Add(loan);
        await ctx.Db.SaveChangesAsync();

        var service = CreateService(ctx, new FakeClock(Base + Period * 2));
        await service.RunOnceAsync(CancellationToken.None);

        var afterFirstPeriod = await ctx.Db.Loans.AsNoTracking().SingleAsync();
        Assert.True(afterFirstPeriod.IsPaidOff);
        Assert.Equal(0m, afterFirstPeriod.RemainingBalance);

        // Only ONE LoanPayment line posted across the two periods this pass covered - once paid
        // off, the loan is never charged again, and the balance never goes negative.
        var loanPaymentLines = await ctx.Db.LedgerTransactions
            .Where(t => t.AirlineId == ctx.Airline.Id && t.Category == LedgerCategory.LoanPayment)
            .ToListAsync();
        Assert.Single(loanPaymentLines);
    }

    /// <summary>Backdates the fleet aircraft RouteTestContext already seeded to <see cref="Base"/>
    /// (it is otherwise stamped with the real wall-clock "now", which would fall after every
    /// period boundary these tests use) and adds a matching lease and pilot, all anchored at
    /// <see cref="Base"/> so every period from then on is charged consistently.</summary>
    private static async Task BackdateFleetAndSeedLeaseAndPilotAsync(RouteTestContext ctx)
    {
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();
        fleetAircraft.CreatedUtc = Base;

        ctx.Db.Leases.Add(new Lease
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = fleetAircraft.Id,
            MonthlyRate = LeaseRate,
            StartUtc = Base,
            IsActive = true,
            CreatedUtc = Base,
        });

        ctx.Db.Pilots.Add(new Pilot
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Name = "Test Pilot",
            IsPlayer = true,
            MonthlySalary = PilotSalary,
            SkillRating = 50,
            CreatedUtc = Base,
        });

        await ctx.Db.SaveChangesAsync();
    }

    private static async Task SeedEconomyStateAsync(RouteTestContext ctx, DateTimeOffset lastProcessedUtc)
    {
        ctx.Db.EconomyStates.Add(new EconomyState
        {
            Id = Guid.NewGuid(),
            LastProcessedUtc = lastProcessedUtc,
            WorldSeed = 1,
            FuelPricePerKg = 0m,
        });
        await ctx.Db.SaveChangesAsync();
    }

    /// <summary>A real EconomyClockService whose db-scope resolves a fresh FsOpsDbContext against
    /// the same live in-memory SQLite connection RouteTestContext seeded - same pattern as
    /// FlightLedgerPostingTests.CreateLifecycleAndTelemetry.</summary>
    private static EconomyClockService CreateService(RouteTestContext ctx, FakeClock clock, EconomyConfigCatalog? catalog = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<FsOpsDbContext>(o => o.UseSqlite(ctx.Connection));
        var provider = services.BuildServiceProvider();

        return new EconomyClockService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            catalog ?? EconomyConfigCatalog.Default(),
            clock,
            NullLogger<EconomyClockService>.Instance);
    }
}
