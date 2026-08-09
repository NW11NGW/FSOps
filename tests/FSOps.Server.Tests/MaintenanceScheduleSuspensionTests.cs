using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSOps.Server.Tests;

/// <summary>
/// docs/PLAN.md "A schedule option: suspend during maintenance and resume automatically" - the
/// fourteen-cancellation-fees bug this exists to prevent: a True-life C-check against a daily
/// schedule must not cancel every occurrence for the fourteen days the aircraft is grounded.
/// <see cref="PilotSchedule.AutoSuspendOnMaintenance"/> gates the behaviour and defaults true (see
/// its own doc and PilotScheduleConfiguration's explicit HasDefaultValue). Two tests either side of
/// that one boolean - on resolves to Suspended with no fee, off resolves to Cancelled with a real
/// fee under the exact same grounding - are both needed: the "on" test alone would still pass if
/// suspension had accidentally become unconditional, which would mean the flag isn't actually a
/// setting. A third test proves resumption needs no code of its own: the very next occurrence after
/// the grounding elapses just flies normally.
/// <para>
/// Same harness as VirtualFlightResolverServiceTests (isolated in-memory RouteTestContext,
/// FakeClock) - deliberately a separate file rather than edits to that one, since this is new
/// coverage for a new feature rather than a change to what the existing tests were already proving.
/// </para>
/// </summary>
public class MaintenanceScheduleSuspensionTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 4, 0, 0, 0, TimeSpan.Zero); // a Sunday

    [Fact]
    public async Task AutoSuspendOn_MaintenanceGrounded_ResolvesToSuspended_NoFeeUnderTrueLife()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        ctx.Airline.Playstyle = AirlinePlaystyle.TrueLife;
        await ctx.Db.SaveChangesAsync();

        var pilot = await SeedVirtualPilotAsync(ctx);
        var route = await SeedRouteAsync(ctx);
        var fleetAircraftId = await FleetAircraftIdAsync(ctx);
        var schedule = await SeedScheduleAsync(ctx, pilot, route, fleetAircraftId, autoSuspendOnMaintenance: true);
        await SeedEconomyStateAsync(ctx, Base);
        await GroundTheOnlyAircraftAsync(ctx, until: Base.AddDays(14)); // a C-check's worth of downtime

        var now = Base.AddHours(12);
        var service = CreateService(ctx, new FakeClock(now));

        var result = await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, result.FlightsSuspended);
        Assert.Equal(0, result.FlightsCancelled);
        Assert.Equal(0, result.FlightsSkipped);
        Assert.Equal(0, result.FlightsCompleted);

        var flight = await ctx.Db.Flights.SingleAsync(f => f.AirlineId == ctx.Airline.Id);
        Assert.Equal(FlightStatus.Suspended, flight.Status);
        Assert.NotNull(flight.UnflyableReason);
        Assert.Equal(0m, flight.Revenue);
        Assert.Equal(0m, flight.TotalCost);

        // The number that matters: no CancellationFee line at all, even though True-life's fee is
        // configured and positive - a fee here would be exactly the bug this feature exists to fix.
        var expectedFee = EconomyConfigCatalog.Default().Get(AirlinePlaystyle.TrueLife).UnflyableSchedule.CancellationFee;
        Assert.True(expectedFee > 0);
        Assert.Empty(await ctx.Db.LedgerTransactions.Where(t => t.AirlineId == ctx.Airline.Id).ToListAsync());

        Assert.True(schedule.AutoSuspendOnMaintenance);
    }

    [Fact]
    public async Task AutoSuspendOff_MaintenanceGrounded_StillResolvesToCancelled_PostsARealFeeUnderTrueLife()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        ctx.Airline.Playstyle = AirlinePlaystyle.TrueLife;
        await ctx.Db.SaveChangesAsync();

        var pilot = await SeedVirtualPilotAsync(ctx);
        var route = await SeedRouteAsync(ctx);
        var fleetAircraftId = await FleetAircraftIdAsync(ctx);
        // Same grounding as the test above, but the player has explicitly opted OUT of suspension -
        // proves the flag genuinely gates the behaviour rather than suspension being unconditional.
        await SeedScheduleAsync(ctx, pilot, route, fleetAircraftId, autoSuspendOnMaintenance: false);
        await SeedEconomyStateAsync(ctx, Base);
        await GroundTheOnlyAircraftAsync(ctx, until: Base.AddDays(14));

        var now = Base.AddHours(12);
        var service = CreateService(ctx, new FakeClock(now));

        var result = await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, result.FlightsSuspended);
        Assert.Equal(1, result.FlightsCancelled);

        var flight = await ctx.Db.Flights.SingleAsync(f => f.AirlineId == ctx.Airline.Id);
        Assert.Equal(FlightStatus.Cancelled, flight.Status);

        var expectedFee = EconomyConfigCatalog.Default().Get(AirlinePlaystyle.TrueLife).UnflyableSchedule.CancellationFee;
        var ledgerLine = await ctx.Db.LedgerTransactions.SingleAsync(t => t.AirlineId == ctx.Airline.Id);
        Assert.Equal(LedgerCategory.CancellationFee, ledgerLine.Category);
        Assert.Equal(-expectedFee, ledgerLine.Amount);
    }

    [Fact]
    public async Task Suspended_ResumesAutomatically_OnceTheGroundingElapses_NoPlayerActionNeeded()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var pilot = await SeedVirtualPilotAsync(ctx);
        var route = await SeedRouteAsync(ctx);
        var fleetAircraftId = await FleetAircraftIdAsync(ctx);
        await SeedScheduleAsync(ctx, pilot, route, fleetAircraftId, autoSuspendOnMaintenance: true);
        await SeedEconomyStateAsync(ctx, Base);
        // Grounded only for the first Sunday's occurrence - released well before the second week's.
        await GroundTheOnlyAircraftAsync(ctx, until: Base.AddDays(2));

        // Far enough ahead to resolve both this week's (suspended) occurrence and next week's
        // (should fly normally, once the grounding has long since elapsed).
        var now = Base.AddDays(8);
        var service = CreateService(ctx, new FakeClock(now));

        var result = await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, result.FlightsSuspended);
        Assert.Equal(1, result.FlightsCompleted);

        // Materialise first, then order in memory - SQLite's EF provider can't translate
        // OrderBy over DateTimeOffset (the project's own documented trap).
        var flights = (await ctx.Db.Flights.Where(f => f.AirlineId == ctx.Airline.Id).ToListAsync())
            .OrderBy(f => f.PlannedDepartureUtc)
            .ToList();
        Assert.Equal(2, flights.Count);
        Assert.Equal(FlightStatus.Suspended, flights[0].Status);
        Assert.Equal(FlightStatus.Completed, flights[1].Status);
        Assert.True(flights[1].Revenue > 0);

        var aircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == fleetAircraftId);
        Assert.Equal(FleetAircraftStatus.Active, aircraft.Status);
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
