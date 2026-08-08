using System.Text.Json;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Flights;
using FSOps.Data;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSOps.Server.Tests;

/// <summary>
/// Regression coverage for the bug where a fleet aircraft's <see cref="FleetAircraft.LocationIcao"/>
/// was set once at fleet creation and never updated again, so GET /flights/options only ever
/// offered routes departing from the original airport. <see cref="FlightLifecycleService.FinalizeFlightAsync"/>
/// is exercised directly (it's internal for exactly this reason) with a synthetic
/// <see cref="FlightLifecycleService.ActiveFlightTracker"/>, so these tests never need a live
/// telemetry pump or SignalR connection - only a real, isolated in-memory database, per
/// RouteTestContext.
/// </summary>
public class FlightCompletionAircraftLocationTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FinalizeFlightAsync_NormalLanding_MovesAircraftToArrivalAndAddsAirframeHours()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();
        var flight = await SeedInProgressFlightAsync(ctx, fleetAircraft.Id);

        var lifecycle = await CreateLifecycleAsync(ctx);
        var tracker = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flight.Id,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = fleetAircraft.Id,
            ArrivalIcao = "EGPH",
            PlannedBlockMinutes = 60,
            Machine = CompletedMachine(flight.Id),
            // The last telemetry position ever seen for this flight - parked right at EGPH, an
            // ordinary, undiverted landing.
            LatestSnapshot = Snapshot(flight.Id, latitudeDeg: 55.9500, longitudeDeg: -3.3725),
        };

        await lifecycle.FinalizeFlightAsync(tracker);

        var updatedAircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == fleetAircraft.Id);
        Assert.Equal("EGPH", updatedAircraft.LocationIcao);
        Assert.Equal(FleetAircraftStatus.Active, updatedAircraft.Status);
        // OutUtc=60s, InUtc=5550s -> 5490 seconds = 1.525 hours of block time.
        Assert.Equal(1.525, updatedAircraft.AirframeHours, precision: 3);

        var updatedFlight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
        Assert.Equal(FlightStatus.Completed, updatedFlight.Status);
    }

    [Fact]
    public async Task FinalizeFlightAsync_DivertedLanding_MovesAircraftToWhereItActuallyParked()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();
        var flight = await SeedInProgressFlightAsync(ctx, fleetAircraft.Id);

        var lifecycle = await CreateLifecycleAsync(ctx);
        var tracker = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flight.Id,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = fleetAircraft.Id,
            // Planned to land at EGPH...
            ArrivalIcao = "EGPH",
            PlannedBlockMinutes = 60,
            Machine = CompletedMachine(flight.Id),
            // ...but the last tracked position is right at EGPF (Glasgow) instead - a diversion.
            LatestSnapshot = Snapshot(flight.Id, latitudeDeg: 55.8719, longitudeDeg: -4.4331),
        };

        await lifecycle.FinalizeFlightAsync(tracker);

        var updatedAircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == fleetAircraft.Id);
        Assert.Equal("EGPF", updatedAircraft.LocationIcao);
        Assert.Equal(FleetAircraftStatus.Active, updatedAircraft.Status);
    }

    [Fact]
    public async Task FinalizeFlightAsync_NoTelemetryEverReceived_FallsBackToPlannedArrival()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();
        var flight = await SeedInProgressFlightAsync(ctx, fleetAircraft.Id);

        var lifecycle = await CreateLifecycleAsync(ctx);
        var tracker = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flight.Id,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = fleetAircraft.Id,
            ArrivalIcao = "EGPH",
            PlannedBlockMinutes = 60,
            Machine = CompletedMachine(flight.Id),
            // No position snapshot was ever recorded (e.g. a very short rehydrate window).
            LatestSnapshot = null,
        };

        await lifecycle.FinalizeFlightAsync(tracker);

        var updatedAircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == fleetAircraft.Id);
        Assert.Equal("EGPH", updatedAircraft.LocationIcao);
    }

    private static async Task<Flight> SeedInProgressFlightAsync(RouteTestContext ctx, Guid fleetAircraftId)
    {
        var flight = new Flight
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            RouteId = Guid.NewGuid(),
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

    /// <summary>Builds a phase machine that has run a complete, ordinary flight through to
    /// Shutdown - Out at t=60s, In at t=5550s - by replaying the same event shape the live
    /// tracker would have queued, matching FlightPhaseRehydrationTests' pattern.</summary>
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

    /// <summary>
    /// A real FlightLifecycleService whose db-scope resolves a fresh FsOpsDbContext against the
    /// same live in-memory SQLite connection RouteTestContext seeded - exactly like the production
    /// IServiceScopeFactory does against the app's real connection - so FinalizeFlightAsync's own
    /// `using var scope = _scopeFactory.CreateScope()` sees the seeded data. Telemetry is never
    /// used by FinalizeFlightAsync, so it's left null; the hub is a no-op fake since nothing in
    /// these tests listens for the broadcast.
    /// </summary>
    private static Task<FlightLifecycleService> CreateLifecycleAsync(RouteTestContext ctx)
    {
        var services = new ServiceCollection();
        services.AddDbContext<FsOpsDbContext>(o => o.UseSqlite(ctx.Connection));
        var provider = services.BuildServiceProvider();

        return Task.FromResult(new FlightLifecycleService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            null!,
            new NoOpHubContext(),
            EconomyConfig.Default(),
            NullLogger<FlightLifecycleService>.Instance));
    }
}
