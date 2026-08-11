using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Scheduling;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSOps.Server.Tests;

/// <summary>
/// Chunk E2's own stated verification: virtual flights resolve exactly once per occurrence no
/// matter how many times or how concurrently resolution runs, catch-up after being closed is
/// bounded per pass and never loses or duplicates an occurrence, the watermark never moves
/// backwards, a virtual flight is a full economic citizen (same ledger postings, aircraft movement
/// and airframe-hours accrual as a player flight), and an unflyable occurrence resolves to
/// Casual's quiet skip or True-life's cancellation-with-cost purely from the resolved
/// EconomyConfig - never an <c>if (playstyle)</c> branch. Every test drives
/// <see cref="VirtualFlightResolverService.RunOnceAsync"/> directly against an isolated in-memory
/// SQLite database (see RouteTestContext) with a <see cref="FakeClock"/> - same pattern as
/// EconomyClockServiceTests.
/// </summary>
public class VirtualFlightResolverServiceTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 4, 0, 0, 0, TimeSpan.Zero); // a Sunday

    [Fact]
    public async Task ResolvedTwiceAtTheSameTime_PostsExactlyOneFlightPerOccurrence()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var pilot = await SeedVirtualPilotAsync(ctx);
        var (outbound, inbound) = await SeedRoundTripRoutesAsync(ctx);
        await SeedRoundTripScheduleAsync(ctx, pilot, outbound, inbound);
        await SeedEconomyStateAsync(ctx, Base);

        // One day past the watermark - both legs of the first week's round trip (06:00/08:00
        // Sunday) have long since "landed" by this point, but the SECOND week's occurrence (7 days
        // later) has not, so this window captures exactly one round trip's worth.
        var now = Base.AddDays(1);
        var clock = new FakeClock(now);
        var service = CreateService(ctx, clock);

        var first = await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, first.FlightsCompleted); // outbound + inbound

        var flightsAfterFirst = await ctx.Db.Flights.Where(f => f.AirlineId == ctx.Airline.Id).ToListAsync();
        Assert.Equal(2, flightsAfterFirst.Count);
        var ledgerAfterFirst = await ctx.Db.LedgerTransactions.Where(t => t.AirlineId == ctx.Airline.Id).ToListAsync();

        var second = await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, second.OccurrencesProcessed);

        var flightsAfterSecond = await ctx.Db.Flights.Where(f => f.AirlineId == ctx.Airline.Id).ToListAsync();
        var ledgerAfterSecond = await ctx.Db.LedgerTransactions.Where(t => t.AirlineId == ctx.Airline.Id).ToListAsync();
        Assert.Equal(flightsAfterFirst.Count, flightsAfterSecond.Count);
        Assert.Equal(ledgerAfterFirst.Count, ledgerAfterSecond.Count);
        Assert.Equal(ledgerAfterFirst.Sum(t => t.Amount), ledgerAfterSecond.Sum(t => t.Amount));
    }

    [Fact]
    public async Task ClockMovingBackwards_ResolvesNothing_AndNeverMovesTheWatermarkBackwards()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var pilot = await SeedVirtualPilotAsync(ctx);
        var (outbound, inbound) = await SeedRoundTripRoutesAsync(ctx);
        await SeedRoundTripScheduleAsync(ctx, pilot, outbound, inbound);
        var watermark = Base.AddDays(30);
        await SeedEconomyStateAsync(ctx, watermark);

        var clock = new FakeClock(watermark.AddDays(-5)); // clock reports a time before the watermark
        var service = CreateService(ctx, clock);

        var result = await service.RunOnceAsync(CancellationToken.None);

        Assert.True(result.ClockMovedBackwards);
        Assert.Equal(0, result.OccurrencesProcessed);
        Assert.Equal(watermark, result.LastScheduleResolvedUtc);

        var state = await ctx.Db.EconomyStates.AsNoTracking().SingleAsync();
        Assert.Equal(watermark, state.LastScheduleResolvedUtc);
        Assert.Empty(await ctx.Db.Flights.Where(f => f.AirlineId == ctx.Airline.Id).ToListAsync());
    }

    [Fact]
    public async Task ClosedForALongTime_CapsOccurrencesPerPass_AndResumesUntilFullyCaughtUp()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var pilot = await SeedVirtualPilotAsync(ctx);
        var (outbound, inbound) = await SeedRoundTripRoutesAsync(ctx);

        // A round trip every day of the week (2 legs/day) rather than once a week, so a long
        // closure produces enough occurrences to exceed a single pass's cap.
        var schedule = new PilotSchedule { Id = Guid.NewGuid(), PilotId = pilot.Id, AirlineId = ctx.Airline.Id, CreatedUtc = Base };
        ctx.Db.PilotSchedules.Add(schedule);
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            ctx.Db.PilotScheduleEntries.Add(new PilotScheduleEntry
            {
                Id = Guid.NewGuid(), PilotScheduleId = schedule.Id, DayOfWeek = day,
                DepartureTimeUtc = new TimeSpan(6, 0, 0), RouteId = outbound.Id, FleetAircraftId = await FleetAircraftIdAsync(ctx), CreatedUtc = Base,
            });
            ctx.Db.PilotScheduleEntries.Add(new PilotScheduleEntry
            {
                Id = Guid.NewGuid(), PilotScheduleId = schedule.Id, DayOfWeek = day,
                DepartureTimeUtc = new TimeSpan(8, 0, 0), RouteId = inbound.Id, FleetAircraftId = await FleetAircraftIdAsync(ctx), CreatedUtc = Base,
            });
        }
        await ctx.Db.SaveChangesAsync();

        await SeedEconomyStateAsync(ctx, Base);
        var now = Base.AddDays(300); // 300 days closed x 2 legs/day is comfortably north of one pass's cap
        var clock = new FakeClock(now);
        var service = CreateService(ctx, clock);

        var first = await service.RunOnceAsync(CancellationToken.None);
        Assert.True(first.OccurrencesProcessed > 0);
        Assert.True(first.MoreCatchUpRemaining, "A 300-day, 2-legs/day backlog must not resolve in a single pass.");

        var totalProcessed = first.OccurrencesProcessed;
        var passes = 1;
        VirtualFlightResolutionResult last = first;
        while (last.MoreCatchUpRemaining && passes < 20)
        {
            last = await service.RunOnceAsync(CancellationToken.None);
            totalProcessed += last.OccurrencesProcessed;
            passes++;
        }

        Assert.False(last.MoreCatchUpRemaining);
        Assert.Equal(now, last.LastScheduleResolvedUtc);
        Assert.True(passes > 1, "Expected the backlog to require more than one pass, proving the per-pass cap actually engaged.");

        // Nothing lost, nothing duplicated: every Flight row this pilot's chain could have produced
        // exists exactly once.
        var flights = await ctx.Db.Flights.Where(f => f.AirlineId == ctx.Airline.Id).ToListAsync();
        Assert.Equal(totalProcessed, flights.Count);
        Assert.Equal(flights.Count, flights.Select(f => (f.ScheduleId, f.PlannedDepartureUtc)).Distinct().Count());
    }

    [Fact]
    public async Task FlyableOccurrence_IsAFullEconomicCitizen_LedgerPostedAircraftMovedHoursAccrued()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var pilot = await SeedVirtualPilotAsync(ctx);
        var (outbound, inbound) = await SeedRoundTripRoutesAsync(ctx);
        await SeedRoundTripScheduleAsync(ctx, pilot, outbound, inbound);
        await SeedEconomyStateAsync(ctx, Base);

        var now = Base.AddDays(1); // captures only the first week's round trip, not the next one too
        var clock = new FakeClock(now);
        var service = CreateService(ctx, clock);

        var result = await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, result.FlightsCompleted);
        Assert.Equal(0, result.FlightsSkipped);
        Assert.Equal(0, result.FlightsCancelled);

        var flights = (await ctx.Db.Flights.Where(f => f.AirlineId == ctx.Airline.Id).ToListAsync())
            .OrderBy(f => f.PlannedDepartureUtc)
            .ToList();
        Assert.All(flights, f =>
        {
            Assert.Equal(FlightStatus.Completed, f.Status);
            Assert.True(f.RevenuePosted);
            Assert.True(f.PaxBooked > 0);
            Assert.True(f.Revenue > 0);
            Assert.Null(f.TypeMismatch); // no aircraft-type mismatch check applies to a virtual pilot - unknown, not "matched"
            Assert.Null(f.UnflyableReason);
            Assert.Equal(pilot.Id, f.PilotId);
        });

        var ledger = await ctx.Db.LedgerTransactions.Where(t => t.AirlineId == ctx.Airline.Id).ToListAsync();
        var categories = ledger.Select(t => t.Category).ToHashSet();
        Assert.Contains(LedgerCategory.TicketRevenue, categories);
        Assert.Contains(LedgerCategory.Fuel, categories);
        Assert.Contains(LedgerCategory.LandingFees, categories);
        Assert.Contains(LedgerCategory.Maintenance, categories);
        Assert.Contains(LedgerCategory.CrewCost, categories);

        // Full round trip - the aircraft is back where it started.
        var fleetAircraftId = await FleetAircraftIdAsync(ctx);
        var aircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == fleetAircraftId);
        Assert.Equal("EGGD", aircraft.LocationIcao);
        Assert.True(aircraft.AirframeHours > 0);
        Assert.True(aircraft.ConditionPercent < 100);
    }

    [Fact]
    public async Task UnflyableOccurrence_UnderCasual_IsSkippedQuietly_RecordedButNoPenalty()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var pilot = await SeedVirtualPilotAsync(ctx);
        var (outbound, inbound) = await SeedRoundTripRoutesAsync(ctx);
        await SeedRoundTripScheduleAsync(ctx, pilot, outbound, inbound);
        await SeedEconomyStateAsync(ctx, Base);
        // Unflyable because the aircraft is somewhere it shouldn't be - a schedule the player built
        // badly, not maintenance the simulation imposed (see MisplaceTheOnlyAircraftAsync's doc).
        await MisplaceTheOnlyAircraftAsync(ctx, wrongIcao: "EGSS");

        // RouteTestContext's airline defaults to AirlinePlaystyle.Casual.
        var now = Base.AddDays(1); // captures only the first week's round trip, not the next one too
        var clock = new FakeClock(now);
        var service = CreateService(ctx, clock);

        var result = await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, result.FlightsSkipped);
        Assert.Equal(0, result.FlightsCompleted);
        Assert.Equal(0, result.FlightsCancelled);

        var flights = await ctx.Db.Flights.Where(f => f.AirlineId == ctx.Airline.Id).ToListAsync();
        Assert.Equal(2, flights.Count); // recorded, never a silent gap
        Assert.All(flights, f =>
        {
            Assert.Equal(FlightStatus.Skipped, f.Status);
            Assert.NotNull(f.UnflyableReason);
            Assert.Equal(0m, f.Revenue);
            Assert.Equal(0m, f.TotalCost);
            Assert.True(f.RevenuePosted);
        });

        // No penalty at all - not even a cancellation-fee ledger line.
        Assert.Empty(await ctx.Db.LedgerTransactions.Where(t => t.AirlineId == ctx.Airline.Id).ToListAsync());
    }

    [Fact]
    public async Task UnflyableOccurrence_UnderTrueLife_IsCancelledWithARealCost_ClearlyReported()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        ctx.Airline.Playstyle = AirlinePlaystyle.TrueLife;
        await ctx.Db.SaveChangesAsync();

        var pilot = await SeedVirtualPilotAsync(ctx);
        var (outbound, inbound) = await SeedRoundTripRoutesAsync(ctx);
        await SeedRoundTripScheduleAsync(ctx, pilot, outbound, inbound);
        await SeedEconomyStateAsync(ctx, Base);
        // Unflyable because the aircraft is somewhere it shouldn't be - a schedule the player built
        // badly, not maintenance the simulation imposed (see MisplaceTheOnlyAircraftAsync's doc).
        await MisplaceTheOnlyAircraftAsync(ctx, wrongIcao: "EGSS");

        var now = Base.AddDays(1); // captures only the first week's round trip, not the next one too
        var clock = new FakeClock(now);
        var service = CreateService(ctx, clock);

        var result = await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, result.FlightsCancelled);
        Assert.Equal(0, result.FlightsCompleted);
        Assert.Equal(0, result.FlightsSkipped);

        var flights = await ctx.Db.Flights.Where(f => f.AirlineId == ctx.Airline.Id).ToListAsync();
        Assert.Equal(2, flights.Count);

        var expectedFee = EconomyConfigCatalog.Default().Get(AirlinePlaystyle.TrueLife).UnflyableSchedule.CancellationFee;
        Assert.True(expectedFee > 0);

        Assert.All(flights, f =>
        {
            Assert.Equal(FlightStatus.Cancelled, f.Status);
            Assert.NotNull(f.UnflyableReason);
            Assert.Equal(0m, f.Revenue); // never any revenue for a sector that never flew
            Assert.Equal(expectedFee, f.TotalCost);
        });

        var ledger = await ctx.Db.LedgerTransactions.Where(t => t.AirlineId == ctx.Airline.Id).ToListAsync();
        Assert.Equal(2, ledger.Count);
        Assert.All(ledger, t =>
        {
            Assert.Equal(LedgerCategory.CancellationFee, t.Category);
            Assert.Equal(-expectedFee, t.Amount);
            Assert.Contains("still at", t.Description);
        });
    }

    [Fact]
    public async Task PlayerFlights_AreNeverTouchedByTheResolver()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var (outbound, _) = await SeedRoundTripRoutesAsync(ctx);
        await SeedEconomyStateAsync(ctx, Base);

        // A player-flown InProgress flight with no schedule entry at all - the resolver must never
        // touch it, since it can only ever complete via real tracking.
        var playerPilot = new Pilot
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Name = "You", IsPlayer = true,
            MonthlySalary = 9_000m, SkillRating = 50, CreatedUtc = Base,
        };
        ctx.Db.Pilots.Add(playerPilot);
        var playerFlight = new Flight
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, RouteId = outbound.Id, FleetAircraftId = await FleetAircraftIdAsync(ctx),
            PilotId = playerPilot.Id, Status = FlightStatus.InProgress, PlannedDepartureUtc = Base, PlannedBlockMinutes = 65,
            CreatedUtc = Base,
        };
        ctx.Db.Flights.Add(playerFlight);
        await ctx.Db.SaveChangesAsync();

        var clock = new FakeClock(Base.AddDays(30));
        var service = CreateService(ctx, clock);
        await service.RunOnceAsync(CancellationToken.None);

        var reloaded = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == playerFlight.Id);
        Assert.Equal(FlightStatus.InProgress, reloaded.Status);
        Assert.False(reloaded.RevenuePosted);
    }

    private static async Task<Pilot> SeedVirtualPilotAsync(RouteTestContext ctx)
    {
        var pilot = new Pilot
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Name = "First Officer Test",
            IsPlayer = false,
            MonthlySalary = 9_000m,
            SkillRating = 50,
            Status = PilotStatus.Available,
            CreatedUtc = Base,
        };
        ctx.Db.Pilots.Add(pilot);
        await ctx.Db.SaveChangesAsync();
        return pilot;
    }

    private static async Task<(Route Outbound, Route Inbound)> SeedRoundTripRoutesAsync(RouteTestContext ctx)
    {
        var outbound = new Route
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, DepartureIcao = "EGGD", ArrivalIcao = "EGPH",
            FlightNumber = "101", DistanceNm = 275.2, BaseFare = 89.00m, IsActive = true, CreatedUtc = Base,
        };
        var inbound = new Route
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, DepartureIcao = "EGPH", ArrivalIcao = "EGGD",
            FlightNumber = "102", DistanceNm = 275.2, BaseFare = 89.00m, IsActive = true, CreatedUtc = Base,
        };
        ctx.Db.Routes.AddRange(outbound, inbound);
        await ctx.Db.SaveChangesAsync();
        return (outbound, inbound);
    }

    /// <summary>A weekly there-and-back on Sunday: 06:00 out, 08:00 back - well inside default
    /// rest/duty/turnaround limits.</summary>
    private static async Task SeedRoundTripScheduleAsync(RouteTestContext ctx, Pilot pilot, Route outbound, Route inbound)
    {
        var schedule = new PilotSchedule { Id = Guid.NewGuid(), PilotId = pilot.Id, AirlineId = ctx.Airline.Id, CreatedUtc = Base };
        ctx.Db.PilotSchedules.Add(schedule);
        ctx.Db.PilotScheduleEntries.Add(new PilotScheduleEntry
        {
            Id = Guid.NewGuid(), PilotScheduleId = schedule.Id, DayOfWeek = DayOfWeek.Sunday,
            DepartureTimeUtc = new TimeSpan(6, 0, 0), RouteId = outbound.Id, FleetAircraftId = await FleetAircraftIdAsync(ctx), CreatedUtc = Base,
        });
        ctx.Db.PilotScheduleEntries.Add(new PilotScheduleEntry
        {
            Id = Guid.NewGuid(), PilotScheduleId = schedule.Id, DayOfWeek = DayOfWeek.Sunday,
            DepartureTimeUtc = new TimeSpan(8, 0, 0), RouteId = inbound.Id, FleetAircraftId = await FleetAircraftIdAsync(ctx), CreatedUtc = Base,
        });
        await ctx.Db.SaveChangesAsync();
    }

    private static async Task GroundTheOnlyAircraftAsync(RouteTestContext ctx, DateTimeOffset until)
    {
        var fleetAircraftId = await FleetAircraftIdAsync(ctx);
        var aircraft = await ctx.Db.FleetAircraft.SingleAsync(f => f.Id == fleetAircraftId);
        aircraft.Status = FleetAircraftStatus.InMaintenance;
        aircraft.GroundedUntilUtc = until;
        await ctx.Db.SaveChangesAsync();
    }

    /// <summary>
    /// Makes an occurrence unflyable for a schedule-quality reason (the aircraft is parked
    /// somewhere other than where this leg departs from) rather than a maintenance grounding - see
    /// MaintenanceScheduleSuspensionTests for the maintenance case, which resolves differently
    /// (Suspended, no fee under any playstyle) now that PilotSchedule.AutoSuspendOnMaintenance
    /// defaults on. The two Skipped/Cancelled tests below need a cause that stays Skipped/Cancelled
    /// regardless of that flag, since what they're actually proving is "a badly-planned schedule
    /// bites under True-life and doesn't under Casual" - not anything about maintenance.
    /// </summary>
    private static async Task MisplaceTheOnlyAircraftAsync(RouteTestContext ctx, string wrongIcao)
    {
        var fleetAircraftId = await FleetAircraftIdAsync(ctx);
        var aircraft = await ctx.Db.FleetAircraft.SingleAsync(f => f.Id == fleetAircraftId);
        aircraft.LocationIcao = wrongIcao;
        await ctx.Db.SaveChangesAsync();
    }

    private static async Task<Guid> FleetAircraftIdAsync(RouteTestContext ctx)
    {
        // RouteTestContext seeds exactly one fleet aircraft ("G-TEST" at EGGD) but doesn't expose
        // its id as a property, so it's fetched by its known registration instead of touching that
        // shared test fixture (used by many other test files) just for this.
        return await ctx.Db.FleetAircraft.Where(f => f.Registration == "G-TEST").Select(f => f.Id).SingleAsync();
    }

    private static async Task SeedEconomyStateAsync(RouteTestContext ctx, DateTimeOffset lastScheduleResolvedUtc)
    {
        ctx.Db.EconomyStates.Add(new EconomyState
        {
            Id = Guid.NewGuid(),
            LastProcessedUtc = lastScheduleResolvedUtc,
            LastScheduleResolvedUtc = lastScheduleResolvedUtc,
            WorldSeed = 1,
            FuelPricePerKg = 0m,
        });
        await ctx.Db.SaveChangesAsync();
    }

    private static VirtualFlightResolverService CreateService(RouteTestContext ctx, FakeClock clock, EconomyConfigCatalog? catalog = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<FSOps.Data.FsOpsDbContext>(o => o.UseSqlite(ctx.Connection));
        var provider = services.BuildServiceProvider();

        return new VirtualFlightResolverService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            catalog ?? EconomyConfigCatalog.Default(),
            clock,
            NullLogger<VirtualFlightResolverService>.Instance);
    }
}
