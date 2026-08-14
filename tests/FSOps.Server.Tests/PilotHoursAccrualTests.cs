using System.Text.Json;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Flights;
using FSOps.Core.Scheduling;
using FSOps.Data;
using FSOps.Server.Endpoints;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSOps.Server.Tests;

/// <summary>
/// Regression coverage for the "Pilot.HoursFlown never accrues from a player flight" bug
/// (found 2026-08-08: E2 added accrual for virtual pilots inside VirtualFlightResolverService, but
/// the player's own pilot record kept accruing nothing no matter how much they flew).
/// <see cref="MaintenancePoster.PostFlightHours"/> is the one place airframe hours accrue for all
/// three flight-completion paths - it now also accrues the flying pilot's
/// <see cref="Pilot.HoursFlown"/> in the same call, so a fourth caller cannot reintroduce the split
/// the bug came from. One test per call site: the real-telemetry completion path
/// (<see cref="FlightLifecycleService.FinalizeFlightAsync"/>), the manual-completion path
/// (<see cref="FlightEndpoints.CompleteManualAsync"/>), and the virtual-pilot path
/// (<see cref="VirtualFlightResolverService"/>) - exactly the three places that went out of sync
/// before this fix.
/// </summary>
public class PilotHoursAccrualTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 4, 0, 0, 0, TimeSpan.Zero); // a Sunday

    [Fact]
    public async Task FinalizeFlightAsync_AccruesThePlayerPilotsHoursFlown_AlongsideAirframeHours()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = await SeedRouteAsync(ctx);
        var pilot = await SeedPlayerPilotAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();

        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();

        var flight = new Flight
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            RouteId = route.Id,
            FleetAircraftId = fleetAircraft.Id,
            PilotId = pilot.Id,
            Status = FlightStatus.InProgress,
            PlannedDepartureUtc = Base,
            PlannedBlockMinutes = 90,
            PaxBooked = 150,
            FuelPlannedKg = 3000,
            TitleFlown = "Test Aircraft",
            CreatedUtc = Base,
        };
        ctx.Db.Flights.Add(flight);
        await ctx.Db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddDbContext<FsOpsDbContext>(o => o.UseSqlite(ctx.Connection));
        var provider = services.BuildServiceProvider();

        var telemetry = new SimTelemetryService(new NoOpSimSource(), new NoOpHubContext(), NullLogger<SimTelemetryService>.Instance);
        var lifecycle = new FlightLifecycleService(
            provider.GetRequiredService<IServiceScopeFactory>(), telemetry, new NoOpHubContext(),
            economyConfigCatalog, null, NullLogger<FlightLifecycleService>.Instance);

        // Same completed-flight shape as MaintenanceTriggerTests: Out at t=60s, In at t=5550s - a
        // block time of exactly 5490s = 1.525h.
        var tracker = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flight.Id,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = fleetAircraft.Id,
            ArrivalIcao = route.ArrivalIcao,
            PlannedBlockMinutes = flight.PlannedBlockMinutes,
            Machine = CompletedMachine(flight.Id),
            LatestSnapshot = Snapshot(flight.Id, latitudeDeg: 55.9500, longitudeDeg: -3.3725),
        };

        await lifecycle.FinalizeFlightAsync(tracker);

        var updatedAircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == fleetAircraft.Id);
        var updatedPilot = await ctx.Db.Pilots.AsNoTracking().SingleAsync(p => p.Id == pilot.Id);

        Assert.Equal(1.525, updatedAircraft.AirframeHours, precision: 3);
        Assert.Equal(1.525, updatedPilot.HoursFlown, precision: 3);
    }

    [Fact]
    public async Task CompleteManualAsync_AccruesThePlayerPilotsHoursFlown_FromThePlannedBlockTime()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();
        fleetAircraft.Status = FleetAircraftStatus.InFlight;

        var route = await SeedRouteAsync(ctx);
        var pilot = await SeedPlayerPilotAsync(ctx);

        var outUtc = DateTimeOffset.UtcNow.AddHours(-2);
        var flight = new Flight
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            RouteId = route.Id,
            FleetAircraftId = fleetAircraft.Id,
            PilotId = pilot.Id,
            Status = FlightStatus.InProgress,
            PlannedDepartureUtc = outUtc,
            PlannedBlockMinutes = 120,
            OutUtc = outUtc,
            PaxBooked = 150,
            FuelPlannedKg = 3000,
            TitleFlown = "Test Aircraft",
            CreatedUtc = outUtc,
        };
        ctx.Db.Flights.Add(flight);
        await ctx.Db.SaveChangesAsync();

        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var lifecycle = new FlightLifecycleService(null!, null!, null!, economyConfigCatalog, null, null!);
        var result = await FlightEndpoints.CompleteManualAsync(flight.Id, ctx.Db, ctx.CurrentUser, lifecycle, economyConfigCatalog, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ((IStatusCodeHttpResult)result).StatusCode);

        var updatedPilot = await ctx.Db.Pilots.AsNoTracking().SingleAsync(p => p.Id == pilot.Id);
        // Manual completion always uses the PLANNED block time (120 min = 2h), never the real
        // wall-clock gap - same rule FlightManualCompletionAndAbandonTests already covers for
        // airframe hours; pilot hours must follow the identical basis.
        Assert.Equal(2.0, updatedPilot.HoursFlown, precision: 3);
    }

    [Fact]
    public async Task VirtualFlightResolver_AccruesTheFlyingPilotsHoursFlown_ForEachCompletedOccurrence()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var pilot = await SeedVirtualPilotAsync(ctx);
        var outbound = await SeedSingleRouteAsync(ctx);
        var fleetAircraftId = await ctx.Db.FleetAircraft.Where(f => f.Registration == "G-TEST").Select(f => f.Id).SingleAsync();

        var schedule = new PilotSchedule { Id = Guid.NewGuid(), PilotId = pilot.Id, AirlineId = ctx.Airline.Id, CreatedUtc = Base };
        ctx.Db.PilotSchedules.Add(schedule);
        ctx.Db.PilotScheduleEntries.Add(new PilotScheduleEntry
        {
            Id = Guid.NewGuid(),
            PilotScheduleId = schedule.Id,
            DayOfWeek = DayOfWeek.Sunday,
            DepartureTimeUtc = new TimeSpan(6, 0, 0),
            RouteId = outbound.Id,
            FleetAircraftId = fleetAircraftId,
            CreatedUtc = Base,
        });

        ctx.Db.EconomyStates.Add(new EconomyState
        {
            Id = Guid.NewGuid(),
            LastProcessedUtc = Base,
            LastScheduleResolvedUtc = Base,
            WorldSeed = 1,
            FuelPricePerKg = 0m,
        });
        await ctx.Db.SaveChangesAsync();

        var now = Base.AddHours(12); // well past the 06:00 departure plus any realistic block time
        var services = new ServiceCollection();
        services.AddDbContext<FsOpsDbContext>(o => o.UseSqlite(ctx.Connection));
        var provider = services.BuildServiceProvider();
        var service = new VirtualFlightResolverService(
            provider.GetRequiredService<IServiceScopeFactory>(), EconomyConfigCatalog.Default(), new FakeClock(now),
            NullLogger<VirtualFlightResolverService>.Instance);

        var result = await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, result.FlightsCompleted);

        var updatedPilot = await ctx.Db.Pilots.AsNoTracking().SingleAsync(p => p.Id == pilot.Id);
        var flownFlight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.AirlineId == ctx.Airline.Id);

        Assert.True(updatedPilot.HoursFlown > 0, "Virtual pilot should have accrued hours for the completed occurrence.");
        // Same basis as the aircraft's own airframe-hours accrual for this occurrence - both come
        // from MaintenancePoster.PostFlightHours's single flightHours argument.
        var updatedAircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == fleetAircraftId);
        Assert.Equal(updatedAircraft.AirframeHours, updatedPilot.HoursFlown, precision: 6);
        Assert.Equal(FlightStatus.Completed, flownFlight.Status);
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
            DistanceNm = 280,
            BaseFare = 90m,
            IsActive = true,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.Routes.Add(route);
        await ctx.Db.SaveChangesAsync();
        return route;
    }

    private static async Task<Route> SeedSingleRouteAsync(RouteTestContext ctx)
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

    private static async Task<Pilot> SeedPlayerPilotAsync(RouteTestContext ctx)
    {
        var pilot = new Pilot
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Name = "Test Player Pilot",
            IsPlayer = true,
            MonthlySalary = 0m,
            SkillRating = 50,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.Pilots.Add(pilot);
        await ctx.Db.SaveChangesAsync();
        return pilot;
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
            CreatedUtc = Base,
        };
        ctx.Db.Pilots.Add(pilot);
        await ctx.Db.SaveChangesAsync();
        return pilot;
    }

    /// <summary>Same completed-flight shape as MaintenanceTriggerTests: Out at t=60s, In at
    /// t=5550s - a block time of exactly 5490s = 1.525h.</summary>
    private static FlightPhaseStateMachine CompletedMachine(Guid flightId) => FlightPhaseStateMachine.RestoreFrom(new[]
    {
        PhaseChangeEvent(flightId, 60, FlightPhase.Preflight, FlightPhase.TaxiOut),
        PhaseChangeEvent(flightId, 420, FlightPhase.TakeoffRoll, FlightPhase.Climb),
        PhaseChangeEvent(flightId, 5270, FlightPhase.Descent, FlightPhase.Approach),
        PhaseChangeEvent(flightId, 5398, FlightPhase.Approach, FlightPhase.Landed),
        PhaseChangeEvent(flightId, 5442, FlightPhase.Landed, FlightPhase.TaxiIn),
        PhaseChangeEvent(flightId, 5550, FlightPhase.TaxiIn, FlightPhase.Shutdown),
    });

    private static FlightEvent PhaseChangeEvent(Guid flightId, double t, FlightPhase from, FlightPhase to) => new()
    {
        Id = Guid.NewGuid(),
        FlightId = flightId,
        Utc = Base + TimeSpan.FromSeconds(t),
        Type = FlightEventType.PhaseChange,
        PayloadJson = JsonSerializer.Serialize(new PhaseChangePayload(from.ToString(), to.ToString(), false)),
    };

    private static LiveFlightSnapshot Snapshot(Guid flightId, double latitudeDeg, double longitudeDeg) => new(
        flightId, FlightPhase.Shutdown.ToString(), latitudeDeg, longitudeDeg,
        AltitudeMslFt: 0, AltitudeAglFt: 0, IndicatedAirspeedKt: 0, GroundSpeedKt: 0, VerticalSpeedFpm: 0,
        FuelRemainingKg: 2000, ElapsedBlockMinutes: 92, PlannedBlockMinutes: 90, AwaitingSimReconnect: false,
        TimestampUtc: Base + TimeSpan.FromSeconds(5550));
}
