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
/// Chunk D's own stated verification (docs/PLAN.md "Economy design"/"Integrity"): a completed
/// flight's cash balance moves by exactly the sum of its posted ledger lines, ledger posting is
/// idempotent, a slew-flagged flight never posts ticket revenue but keeps the fuel it already
/// bought, and an abandoned flight nets strictly negative once a real cost has been incurred.
/// Drives <c>FlightEndpoints.StartAsync</c> for real (fuel is posted there) and
/// <c>FlightLifecycleService.FinalizeFlightAsync</c> directly with a synthetic
/// <see cref="FlightLifecycleService.ActiveFlightTracker"/> - see
/// FlightCompletionAircraftLocationTests for why that's exposed for tests. Uses the same isolated
/// in-memory RouteTestContext as the other flight tests - never the real database.
/// </summary>
public class FlightLedgerPostingTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CompletedFlight_CashBalanceMovesByExactlyTheSumOfItsPostedLedgerLines()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = await SeedRouteAsync(ctx);
        // ctx.Airline defaults to AirlinePlaystyle.Casual (never set explicitly by RouteTestContext).
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);

        var startResult = await FlightEndpoints.StartAsync(
            new StartFlightRequest(route.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startResult));

        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == route.Id);
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();

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

        var ledgerLines = await ctx.Db.LedgerTransactions.Where(t => t.AirlineId == ctx.Airline.Id).ToListAsync();
        var ledgerSum = ledgerLines.Sum(t => t.Amount);

        var updatedFlight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
        Assert.Equal(FlightStatus.Completed, updatedFlight.Status);
        Assert.True(updatedFlight.RevenuePosted);

        // Nothing else touches the ledger in this test, so the airline's whole cash balance is
        // exactly this one flight's posted lines - the plan's own stated verification for this
        // phase, checked against Flight.Revenue/TotalCost (an independently maintained cache of
        // the same postings) rather than against itself.
        Assert.Equal(ledgerSum, updatedFlight.Revenue - updatedFlight.TotalCost);

        Assert.Contains(ledgerLines, t => t.Category == LedgerCategory.TicketRevenue && t.Amount > 0);
        Assert.Contains(ledgerLines, t => t.Category == LedgerCategory.Fuel && t.Amount < 0);
        Assert.Contains(ledgerLines, t => t.Category == LedgerCategory.LandingFees && t.Amount < 0);
        Assert.Contains(ledgerLines, t => t.Category == LedgerCategory.Maintenance && t.Amount < 0);
        Assert.Contains(ledgerLines, t => t.Category == LedgerCategory.CrewCost && t.Amount < 0);

        // Each airport charge posts under its own category. They were all posted as Handling until
        // the Finances page needed to separate variable cost from fixed and found the categories
        // indistinguishable - the only way to tell them apart was the description text, which is
        // display wording and must never be what the accounts are built on.
        Assert.Contains(ledgerLines, t => t.Category == LedgerCategory.Handling && t.Amount < 0);
        Assert.Contains(ledgerLines, t => t.Category == LedgerCategory.ParkingFees && t.Amount < 0);
        Assert.Contains(ledgerLines, t => t.Category == LedgerCategory.PassengerCharges && t.Amount < 0);
        Assert.Contains(ledgerLines, t => t.Category == LedgerCategory.TurnaroundFees && t.Amount < 0);

        // Per-sector crew cost is not the monthly wage, and a sector posts no monthly wage at all.
        Assert.DoesNotContain(ledgerLines, t => t.Category == LedgerCategory.Salary);
    }

    [Fact]
    public async Task FinalizeFlightAsync_CalledTwiceForTheSameFlight_PostsLedgerLinesExactlyOnce()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = await SeedRouteAsync(ctx);
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();
        var flight = await SeedInProgressFlightAsync(ctx, route, fleetAircraft.Id);

        // ctx.Airline defaults to AirlinePlaystyle.Casual (never set explicitly by RouteTestContext).
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var (lifecycle, _) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);

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
        var linesAfterFirstCall = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync();
        Assert.NotEmpty(linesAfterFirstCall);

        // The same tracker, finalized again - exactly what a reconnect or crash rehydration would
        // do. Must be a complete no-op: no new ledger rows, same cash.
        await lifecycle.FinalizeFlightAsync(tracker);
        var linesAfterSecondCall = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync();

        Assert.Equal(linesAfterFirstCall.Count, linesAfterSecondCall.Count);
        Assert.Equal(linesAfterFirstCall.Sum(t => t.Amount), linesAfterSecondCall.Sum(t => t.Amount));
    }

    [Fact]
    public async Task SlewFlaggedFlight_PostsNoTicketRevenue_ButKeepsTheFuelAlreadyBought()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = await SeedRouteAsync(ctx);
        // ctx.Airline defaults to AirlinePlaystyle.Casual (never set explicitly by RouteTestContext).
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);

        var startResult = await FlightEndpoints.StartAsync(
            new StartFlightRequest(route.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startResult));

        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == route.Id);
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();

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

        // A slew sample, exactly as ProcessSample would have fed the monitor while tracking live.
        tracker.IntegrityMonitor.Observe(new FlightTelemetrySample(
            Base, 55.9500, -3.3725, 0, 0, 0, 0, 0, 0, 0, false, true, false, 0, 0, 2000,
            "Test Aircraft", "Test Aircraft", "TEST", 1.0, true));

        await lifecycle.FinalizeFlightAsync(tracker);

        var ledgerLines = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync();
        Assert.DoesNotContain(ledgerLines, t => t.Category == LedgerCategory.TicketRevenue);
        Assert.Contains(ledgerLines, t => t.Category == LedgerCategory.Fuel && t.Amount < 0);
        // Nothing else touches this flight's ledger once the sector isn't payable - it's exactly
        // the fuel line already bought at departure, so the net is strictly negative.
        Assert.True(ledgerLines.Sum(t => t.Amount) < 0);

        var updatedFlight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
        Assert.True(updatedFlight.SlewDetected);
        Assert.True(updatedFlight.RevenuePosted);
        Assert.Equal(0m, updatedFlight.Revenue);
    }

    [Fact]
    public async Task AbandonedFlight_NetsStrictlyNegative_OnceFuelWasAlreadyBought()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = await SeedRouteAsync(ctx);
        // ctx.Airline defaults to AirlinePlaystyle.Casual (never set explicitly by RouteTestContext).
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);

        var startResult = await FlightEndpoints.StartAsync(
            new StartFlightRequest(route.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startResult));

        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == route.Id);

        var linesAfterStart = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync();
        Assert.Single(linesAfterStart);
        Assert.Equal(LedgerCategory.Fuel, linesAfterStart[0].Category);
        Assert.True(linesAfterStart[0].Amount < 0);

        var abandonResult = await FlightEndpoints.AbandonAsync(flight.Id, ctx.Db, ctx.CurrentUser, lifecycle, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(abandonResult));

        // Abandoning never refunds a sunk cost - the fuel line from start is still the only thing
        // on this airline's ledger, so the net is strictly negative.
        var ledgerLines = await ctx.Db.LedgerTransactions.Where(t => t.AirlineId == ctx.Airline.Id).ToListAsync();
        Assert.True(ledgerLines.Sum(t => t.Amount) < 0);

        var updatedFlight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
        Assert.Equal(FlightStatus.Abandoned, updatedFlight.Status);
    }

    private static int StatusCodeOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    /// <summary>Seeds a route and the pilot FlightEndpoints.StartAsync requires - neither is part
    /// of RouteTestContext's own baseline seed.</summary>
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

    /// <summary>Builds a phase machine that has run a complete, ordinary flight through to
    /// Shutdown - Out at t=60s, In at t=5550s - matching FlightCompletionAircraftLocationTests'
    /// pattern.</summary>
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
    /// A real FlightLifecycleService (and the SimTelemetryService it needs a reference to) whose
    /// db-scope resolves a fresh FsOpsDbContext against the same live in-memory SQLite connection
    /// RouteTestContext seeded - exactly like the production IServiceScopeFactory does against the
    /// app's real connection. The telemetry source is a no-op fake (see NoOpSimSource) since
    /// nothing here needs a real simulator or replay file; the hub is a no-op fake since nothing
    /// in these tests listens for the broadcast.
    /// </summary>
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
