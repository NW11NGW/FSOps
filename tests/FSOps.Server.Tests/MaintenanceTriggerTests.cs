using System.Text.Json;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Flights;
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
/// Chunk E1's own stated verification: a check fires at the right hours, grounds the aircraft, and
/// posts the right cost - end to end, through the real flight-completion path
/// (FlightLifecycleService.FinalizeFlightAsync -> MaintenancePoster -> MaintenanceScheduler), not
/// just the pure Core-level MaintenanceSchedulerTests. Same harness as FlightLedgerPostingTests.
/// </summary>
public class MaintenanceTriggerTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FlightThatCrossesFiveHundredHours_TriggersACheck_GroundsAircraft_PostsCost()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = await SeedRouteAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var economyConfig = economyConfigCatalog.Get(AirlinePlaystyle.Casual);

        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();
        // This completed flight's block time is exactly 1.525h (5550-60=5490s) - see
        // CompletedMachine below - so starting 1 hour short of the A-check threshold guarantees
        // this single flight crosses it.
        fleetAircraft.HoursSinceACheck = economyConfig.Maintenance.ACheckIntervalHours - 1.0;
        fleetAircraft.HoursSinceCCheck = 100;
        fleetAircraft.ConditionPercent = 50;
        await ctx.Db.SaveChangesAsync();

        var (lifecycle, _) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);
        var flight = await SeedInProgressFlightAsync(ctx, route, fleetAircraft.Id);

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
        Assert.Equal(FleetAircraftStatus.InMaintenance, updatedAircraft.Status);
        Assert.NotNull(updatedAircraft.GroundedUntilUtc);
        Assert.Equal(0, updatedAircraft.HoursSinceACheck);
        // C-check clock is untouched by an A-check.
        Assert.Equal(101.525, updatedAircraft.HoursSinceCCheck, precision: 3);

        var maintenanceEvent = await ctx.Db.MaintenanceEvents.AsNoTracking().SingleAsync(m => m.FleetAircraftId == fleetAircraft.Id);
        Assert.Equal(MaintenanceEventType.ACheck, maintenanceEvent.Type);
        Assert.Equal(economyConfig.Maintenance.ACheckCost, maintenanceEvent.Cost);
        Assert.Equal(maintenanceEvent.StartUtc.AddHours(economyConfig.Maintenance.ACheckDowntimeHours), maintenanceEvent.EndUtc);
        Assert.Equal(updatedAircraft.GroundedUntilUtc, maintenanceEvent.EndUtc);

        var maintenanceLedgerLines = await ctx.Db.LedgerTransactions
            .Where(t => t.AirlineId == ctx.Airline.Id && t.Category == LedgerCategory.Maintenance)
            .ToListAsync();
        Assert.Contains(maintenanceLedgerLines, t => t.Amount == -economyConfig.Maintenance.ACheckCost && t.Description.Contains("ACheck"));
    }

    [Fact]
    public async Task GroundedAircraft_IsExcludedFromFlyOptions_WithReasonAndUntilWhen()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = await SeedRouteAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();

        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();
        var groundedUntil = DateTimeOffset.UtcNow.AddHours(4);
        fleetAircraft.Status = FleetAircraftStatus.InMaintenance;
        fleetAircraft.GroundedUntilUtc = groundedUntil;
        await ctx.Db.SaveChangesAsync();

        var optionsResult = await FlightEndpoints.OptionsAsync(ctx.Db, ctx.CurrentUser, economyConfigCatalog, CancellationToken.None);
        var options = OkValueOf<List<FlightOptionProbe>>(optionsResult);

        var option = Assert.Single(options);
        Assert.False(option.IsFlyable);
        // The route-level reason is only set when NO aircraft is present at the departure airport
        // at all - with the fleet's one aircraft physically there but grounded, the reason lives
        // per-aircraft under aircraftOptions (see docs/PLAN.md "2b"/"3a" - the Fly screen now lists
        // every aircraft present, never just one, each with its own reason).
        var aircraftOption = Assert.Single(option.AircraftOptions);
        Assert.False(aircraftOption.IsFlyable);
        Assert.NotNull(aircraftOption.Reason);
        Assert.Contains("maintenance", aircraftOption.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("until", aircraftOption.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MaintenanceReleaser_ReleasesAircraftOnceDowntimeHasElapsed()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();
        fleetAircraft.Status = FleetAircraftStatus.InMaintenance;
        fleetAircraft.GroundedUntilUtc = Base;
        await ctx.Db.SaveChangesAsync();

        // Before the grounding period ends: still grounded.
        var releasedBefore = await MaintenanceReleaser.ReleaseDueAsync(ctx.Db, ctx.Airline.Id, Base - TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(0, releasedBefore);
        Assert.Equal(FleetAircraftStatus.InMaintenance, (await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == fleetAircraft.Id)).Status);

        // At/after the grounding period ends: released.
        var releasedAfter = await MaintenanceReleaser.ReleaseDueAsync(ctx.Db, ctx.Airline.Id, Base, CancellationToken.None);
        Assert.Equal(1, releasedAfter);
        var released = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == fleetAircraft.Id);
        Assert.Equal(FleetAircraftStatus.Active, released.Status);
        Assert.Null(released.GroundedUntilUtc);
    }

    private static T OkValueOf<T>(IResult result)
    {
        var value = ((IValueHttpResult)result).Value;
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private sealed record FlightOptionProbe(bool IsFlyable, string? Reason, List<AircraftOptionProbe> AircraftOptions);

    private sealed record AircraftOptionProbe(bool IsFlyable, string? Reason);

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

        if (!await ctx.Db.Pilots.AnyAsync(p => p.AirlineId == ctx.Airline.Id))
        {
            ctx.Db.Pilots.Add(new Pilot
            {
                Id = Guid.NewGuid(),
                AirlineId = ctx.Airline.Id,
                Name = "Test Pilot",
                IsPlayer = true,
                MonthlySalary = 9000m,
                SkillRating = 50,
                CreatedUtc = DateTimeOffset.UtcNow,
            });
        }

        await ctx.Db.SaveChangesAsync();
        return route;
    }

    private static async Task<Flight> SeedInProgressFlightAsync(RouteTestContext ctx, Route route, Guid fleetAircraftId)
    {
        var flight = new Flight
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            RouteId = route.Id,
            FleetAircraftId = fleetAircraftId,
            PilotId = Guid.NewGuid(),
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
        return flight;
    }

    /// <summary>Same completed-flight shape as FlightLedgerPostingTests: Out at t=60s, In at
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

    private static (FlightLifecycleService Lifecycle, SimTelemetryService Telemetry) CreateLifecycleAndTelemetry(
        RouteTestContext ctx, EconomyConfigCatalog economyConfigCatalog)
    {
        var services = new ServiceCollection();
        services.AddDbContext<FsOpsDbContext>(o => o.UseSqlite(ctx.Connection));
        var provider = services.BuildServiceProvider();

        var telemetry = new SimTelemetryService(new NoOpSimSource(), new NoOpHubContext(), NullLogger<SimTelemetryService>.Instance);
        var lifecycle = new FlightLifecycleService(
            provider.GetRequiredService<IServiceScopeFactory>(), telemetry, new NoOpHubContext(),
            economyConfigCatalog, NullLogger<FlightLifecycleService>.Instance);
        return (lifecycle, telemetry);
    }
}
