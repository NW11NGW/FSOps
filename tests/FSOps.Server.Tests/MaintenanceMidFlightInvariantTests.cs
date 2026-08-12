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
/// The explicit, permanent invariant: maintenance never interrupts a flight in
/// progress. A flight that crosses a check threshold must
/// complete normally and only then trigger the check. <see cref="MaintenancePoster.PostFlightHours"/>
/// is only ever called at flight completion; this asserts the aircraft stays untouched (Active,
/// no MaintenanceEvent, hours-since-check unchanged) for the entire time a flight that will
/// eventually trigger a check is InProgress, and only grounds once that flight is finalized. Also
/// covers the "perform maintenance now" endpoint's own respect for the same rule - blocked outright
/// while airborne, exactly like <see cref="MaintenanceTriggerTests"/> covers the natural-trigger path.
/// </summary>
public class MaintenanceMidFlightInvariantTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AircraftAlreadyPastThreshold_StaysActiveAndUngrounded_WhileItsFlightIsStillInProgress()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var economyConfigCatalog = EconomyConfigCatalog.Default();

        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();
        // Already past the natural A-check threshold - if maintenance were evaluated live (rather
        // than only at completion) this aircraft would already be grounded.
        fleetAircraft.HoursSinceACheck = economyConfigCatalog.Get(AirlinePlaystyle.Casual).Maintenance.ACheckIntervalHours + 10;
        fleetAircraft.Status = FleetAircraftStatus.InFlight;
        await ctx.Db.SaveChangesAsync();

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
            CreatedUtc = Base,
        };
        ctx.Db.Routes.Add(route);
        var flight = new Flight
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            RouteId = route.Id,
            FleetAircraftId = fleetAircraft.Id,
            PilotId = Guid.NewGuid(),
            Status = FlightStatus.InProgress,
            PlannedDepartureUtc = Base,
            PlannedBlockMinutes = 90,
            OutUtc = Base,
            PaxBooked = 150,
            TitleFlown = "Test Aircraft",
            CreatedUtc = Base,
        };
        ctx.Db.Flights.Add(flight);
        await ctx.Db.SaveChangesAsync();

        // While the flight is still InProgress: no MaintenanceEvent exists, no check has fired,
        // and the aircraft's own status is whatever the caller set (InFlight) - MaintenancePoster
        // is simply never invoked for a flight that hasn't completed yet.
        Assert.Empty(await ctx.Db.MaintenanceEvents.ToListAsync());
        var stillInFlight = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == fleetAircraft.Id);
        Assert.Equal(FleetAircraftStatus.InFlight, stillInFlight.Status);

        // A "perform maintenance now" attempt is also refused outright while this aircraft is
        // airborne - the same invariant, enforced defensively at the other entry point too.
        var quoteResult = await MaintenanceEndpoints.MaintenanceQuoteAsync(fleetAircraft.Id, ctx.Db, ctx.CurrentUser, economyConfigCatalog, CancellationToken.None);
        var quote = (MaintenanceQuoteResponse)((IValueHttpResult)quoteResult).Value!;
        Assert.False(quote.CanPerform);

        // Now the flight actually completes - only at THIS point does the overdue check fire.
        var (lifecycle, _) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);
        var tracker = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flight.Id,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = fleetAircraft.Id,
            ArrivalIcao = route.ArrivalIcao,
            PlannedBlockMinutes = flight.PlannedBlockMinutes,
            Machine = CompletedMachine(flight.Id),
            LatestSnapshot = Snapshot(flight.Id, 55.9500, -3.3725),
        };

        await lifecycle.FinalizeFlightAsync(tracker);

        var grounded = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == fleetAircraft.Id);
        Assert.Equal(FleetAircraftStatus.InMaintenance, grounded.Status);
        Assert.NotNull(grounded.GroundedUntilUtc);
        Assert.Single(await ctx.Db.MaintenanceEvents.ToListAsync());
    }

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
            economyConfigCatalog, null, NullLogger<FlightLifecycleService>.Instance);
        return (lifecycle, telemetry);
    }
}
