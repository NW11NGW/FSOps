using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSOps.Server.Tests;

/// <summary>
/// docs/PLAN.md "Progression - reputation and pilot skill", wired through
/// <see cref="VirtualFlightResolverService"/> - the same isolated-database harness as
/// VirtualFlightResolverServiceTests and MaintenanceScheduleSuspensionTests. Covers the properties
/// those files don't already exercise: a maintenance-Suspended occurrence never touches reputation,
/// a Skipped/Cancelled one always does, a flown one grows the pilot's skill and stamps
/// LastFlewUtc, duplicate resolution never moves reputation twice, and idle decay reaches a
/// schedule-less pilot but never a player pilot.
/// </summary>
public class ReputationAndPilotSkillProgressionTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 4, 0, 0, 0, TimeSpan.Zero); // a Sunday

    [Fact]
    public async Task SuspendedOccurrence_NeverTouchesReputation()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        ctx.Airline.Playstyle = AirlinePlaystyle.TrueLife; // a positive cancellation fee would exist here if this were merely Skipped/Cancelled
        await ctx.Db.SaveChangesAsync();

        var pilot = await SeedVirtualPilotAsync(ctx);
        var route = await SeedRouteAsync(ctx);
        var fleetAircraftId = await FleetAircraftIdAsync(ctx);
        await SeedScheduleAsync(ctx, pilot, route, fleetAircraftId, autoSuspendOnMaintenance: true);
        await SeedEconomyStateAsync(ctx, Base);
        await GroundTheOnlyAircraftAsync(ctx, until: Base.AddDays(14));

        var service = CreateService(ctx, new FakeClock(Base.AddHours(12)));
        var result = await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, result.FlightsSuspended);

        var airline = await ctx.Db.Airlines.AsNoTracking().SingleAsync(a => a.Id == ctx.Airline.Id);
        Assert.Equal(50.0, airline.ReputationScore);
    }

    [Fact]
    public async Task SkippedOccurrence_UnderCasual_LowersReputation()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        // RouteTestContext's airline defaults to AirlinePlaystyle.Casual (Skipped, not Cancelled).
        var pilot = await SeedVirtualPilotAsync(ctx);
        var route = await SeedRouteAsync(ctx);
        var fleetAircraftId = await FleetAircraftIdAsync(ctx);
        await SeedScheduleAsync(ctx, pilot, route, fleetAircraftId, autoSuspendOnMaintenance: true);
        await SeedEconomyStateAsync(ctx, Base);
        // Unflyable because the aircraft is somewhere it shouldn't be - a schedule-quality problem,
        // not maintenance, so it resolves Skipped rather than Suspended even with the flag on.
        await MisplaceTheOnlyAircraftAsync(ctx, wrongIcao: "EGSS");

        var service = CreateService(ctx, new FakeClock(Base.AddHours(12)));
        var result = await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, result.FlightsSkipped);

        var airline = await ctx.Db.Airlines.AsNoTracking().SingleAsync(a => a.Id == ctx.Airline.Id);
        Assert.True(airline.ReputationScore < 50.0, $"A skipped sector must cost reputation even under Casual (no fee), was {airline.ReputationScore}.");
    }

    [Fact]
    public async Task FlownOccurrence_MovesReputation_GrowsPilotSkill_AndStampsLastFlewUtc()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var pilot = await SeedVirtualPilotAsync(ctx);
        var route = await SeedRouteAsync(ctx);
        var fleetAircraftId = await FleetAircraftIdAsync(ctx);
        await SeedScheduleAsync(ctx, pilot, route, fleetAircraftId, autoSuspendOnMaintenance: true);
        await SeedEconomyStateAsync(ctx, Base);
        Assert.Null(pilot.LastFlewUtc);

        var now = Base.AddHours(12);
        var service = CreateService(ctx, new FakeClock(now));
        var result = await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, result.FlightsCompleted);

        var airline = await ctx.Db.Airlines.AsNoTracking().SingleAsync(a => a.Id == ctx.Airline.Id);
        Assert.NotEqual(50.0, airline.ReputationScore); // moved - either direction, depending on the deterministic performance draw

        var reloadedPilot = await ctx.Db.Pilots.AsNoTracking().SingleAsync(p => p.Id == pilot.Id);
        Assert.True(reloadedPilot.HoursFlown > 0);
        Assert.True(reloadedPilot.SkillRating > 50.0, $"Skill must have grown off a fresh pilot's first sector, was {reloadedPilot.SkillRating}.");
        Assert.NotNull(reloadedPilot.LastFlewUtc);
    }

    [Fact]
    public async Task ResolvedTwiceAtTheSameTime_NeverMovesReputationTwice()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var pilot = await SeedVirtualPilotAsync(ctx);
        var route = await SeedRouteAsync(ctx);
        var fleetAircraftId = await FleetAircraftIdAsync(ctx);
        await SeedScheduleAsync(ctx, pilot, route, fleetAircraftId, autoSuspendOnMaintenance: true);
        await SeedEconomyStateAsync(ctx, Base);

        var now = Base.AddHours(12);
        var service = CreateService(ctx, new FakeClock(now));

        await service.RunOnceAsync(CancellationToken.None);
        var afterFirst = (await ctx.Db.Airlines.AsNoTracking().SingleAsync(a => a.Id == ctx.Airline.Id)).ReputationScore;

        var second = await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, second.OccurrencesProcessed); // nothing new to resolve - the watermark already covers this occurrence

        var afterSecond = (await ctx.Db.Airlines.AsNoTracking().SingleAsync(a => a.Id == ctx.Airline.Id)).ReputationScore;
        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    public async Task IdlePilotWithNoSchedule_DecaysOverRealTime_ButAPlayerPilotNeverDecays()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedEconomyStateAsync(ctx, Base);

        var idleVirtualPilot = new Pilot
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Name = "Idle FO", IsPlayer = false,
            MonthlySalary = 9_000m, HoursFlown = 2000, SkillRating = 80, LastFlewUtc = Base, CreatedUtc = Base,
        };
        var idlePlayerPilot = new Pilot
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Name = "You", IsPlayer = true,
            MonthlySalary = 9_000m, HoursFlown = 2000, SkillRating = 80, LastFlewUtc = Base, CreatedUtc = Base,
        };
        ctx.Db.Pilots.AddRange(idleVirtualPilot, idlePlayerPilot);
        await ctx.Db.SaveChangesAsync();

        // No schedule at all for either pilot - nothing is ever due for resolution; only the
        // periodic idle-decay pass has anything to do on this tick. Far enough past both the grace
        // period and several decay half-lives that a real erosion must be visible.
        var config = EconomyConfigCatalog.Default().Get(AirlinePlaystyle.Casual).PilotSkill;
        var farFuture = Base.AddHours(config.IdleGracePeriodHours + config.IdleDecayHalfLifeHours * 3);
        var service = CreateService(ctx, new FakeClock(farFuture));

        await service.RunOnceAsync(CancellationToken.None);

        var reloadedVirtual = await ctx.Db.Pilots.AsNoTracking().SingleAsync(p => p.Id == idleVirtualPilot.Id);
        var reloadedPlayer = await ctx.Db.Pilots.AsNoTracking().SingleAsync(p => p.Id == idlePlayerPilot.Id);

        Assert.True(reloadedVirtual.SkillRating < 80.0, $"An idle virtual pilot with no schedule must decay, was {reloadedVirtual.SkillRating}.");
        Assert.Equal(80.0, reloadedPlayer.SkillRating); // the player's own record never decays, no matter how idle
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

    private static async Task<Route> SeedRouteAsync(RouteTestContext ctx)
    {
        var route = new Route
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            DepartureIcao = "EGGD",
            ArrivalIcao = "EGPH",
            FlightNumber = "101",
            DistanceNm = 275.2,
            BaseFare = 89.00m,
            IsActive = true,
            CreatedUtc = Base,
        };
        ctx.Db.Routes.Add(route);
        await ctx.Db.SaveChangesAsync();
        return route;
    }

    private static async Task<PilotSchedule> SeedScheduleAsync(RouteTestContext ctx, Pilot pilot, Route route, Guid fleetAircraftId, bool autoSuspendOnMaintenance)
    {
        var schedule = new PilotSchedule
        {
            Id = Guid.NewGuid(),
            PilotId = pilot.Id,
            AirlineId = ctx.Airline.Id,
            AutoSuspendOnMaintenance = autoSuspendOnMaintenance,
            CreatedUtc = Base,
        };
        ctx.Db.PilotSchedules.Add(schedule);
        ctx.Db.PilotScheduleEntries.Add(new PilotScheduleEntry
        {
            Id = Guid.NewGuid(),
            PilotScheduleId = schedule.Id,
            DayOfWeek = DayOfWeek.Sunday,
            DepartureTimeUtc = new TimeSpan(6, 0, 0),
            RouteId = route.Id,
            FleetAircraftId = fleetAircraftId,
            CreatedUtc = Base,
        });
        await ctx.Db.SaveChangesAsync();
        return schedule;
    }

    private static async Task GroundTheOnlyAircraftAsync(RouteTestContext ctx, DateTimeOffset until)
    {
        var fleetAircraftId = await FleetAircraftIdAsync(ctx);
        var aircraft = await ctx.Db.FleetAircraft.SingleAsync(f => f.Id == fleetAircraftId);
        aircraft.Status = FleetAircraftStatus.InMaintenance;
        aircraft.GroundedUntilUtc = until;
        await ctx.Db.SaveChangesAsync();
    }

    private static async Task MisplaceTheOnlyAircraftAsync(RouteTestContext ctx, string wrongIcao)
    {
        var fleetAircraftId = await FleetAircraftIdAsync(ctx);
        var aircraft = await ctx.Db.FleetAircraft.SingleAsync(f => f.Id == fleetAircraftId);
        aircraft.LocationIcao = wrongIcao;
        await ctx.Db.SaveChangesAsync();
    }

    private static async Task<Guid> FleetAircraftIdAsync(RouteTestContext ctx)
    {
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

    private static VirtualFlightResolverService CreateService(RouteTestContext ctx, FakeClock clock)
    {
        var services = new ServiceCollection();
        services.AddDbContext<FSOps.Data.FsOpsDbContext>(o => o.UseSqlite(ctx.Connection));
        var provider = services.BuildServiceProvider();

        return new VirtualFlightResolverService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            EconomyConfigCatalog.Default(),
            clock,
            NullLogger<VirtualFlightResolverService>.Instance);
    }
}
