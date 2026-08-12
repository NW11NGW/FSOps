using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Flights;
using FSOps.Core.Planning;
using FSOps.Data;
using FSOps.Server.Endpoints;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using FSOps.Sim;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Route = FSOps.Core.Entities.Route;

namespace FSOps.Server.Tests;

/// <summary>
/// Fuel as a persisted asset on the aircraft, not a per-flight expense (Chunk E1). The
/// headline property: an aircraft that lands with fuel remaining starts its next flight on that
/// fuel, so a return leg flown on it costs nothing further - proved end to end through the real
/// path a player uses (<c>FlightEndpoints.StartAsync</c>'s reconciliation and
/// <c>FlightLifecycleService.FinalizeFlightAsync</c>'s persistence), never a calculator called in
/// isolation. Also covers reconciliation of a change FSOps wasn't watching, and live ground-uplift
/// detection (<c>FlightLifecycleService.HandleGroundFuelChangeAsync</c>, normally reached from
/// <c>ProcessSample</c> - see <c>FuelUpliftDetectorTests</c> in FSOps.Core.Tests for the pure
/// classification logic that decides when this fires).
/// </summary>
public class FuelPersistenceTests
{
    private static readonly DateTimeOffset Base = new(2026, 4, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HeadlineProperty_ReturnLegOnThePreviousLegsFuel_IncursZeroFurtherFuelCost()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        ctx.Airline.StrategyProfile = AirlineStrategyProfile.Domestic;
        var (outboundRoute, returnRoute) = await SeedRoundTripRoutesAsync(ctx);

        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var economyConfig = economyConfigCatalog.Get(ctx.Airline.Playstyle);
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);

        var fleetAircraftBefore = await ctx.Db.FleetAircraft.AsNoTracking().FirstAsync();
        Assert.Equal(0, fleetAircraftBefore.FuelOnBoardKg); // fresh aircraft, empty tank - see FleetAircraft.FuelOnBoardKg's doc.

        var departure = await ctx.Db.Airports.SingleAsync(a => a.Icao == "EGGD");
        var arrival = await ctx.Db.Airports.SingleAsync(a => a.Icao == "EGPH");
        var aircraftType = await ctx.Db.AircraftTypes.SingleAsync();
        var plan = RoutePreviewCalculator.Calculate(economyConfig, departure, arrival, aircraftType, ctx.Airline.StrategyProfile);
        var fullBlockFuelKg = plan.FuelBreakdown.TotalFuelKg;

        // --- LEG 1: EGGD -> EGPH. The sim reports a full, realistic tank (as if the pilot loaded
        // it via the EFB/GSX before pressing "start flight" here) - StartAsync's reconciliation
        // observes this and charges the FULL rise as an uplift, at EGGD's price. Timestamped at
        // real UtcNow (not the fixed Base date used for the completed-flight timeline below) -
        // StartAsync's reconciliation only trusts a sample within its recency window of the real
        // wall clock it calls DateTimeOffset.UtcNow against.
        telemetry.SetLastSampleForTests(MakeSample(DateTimeOffset.UtcNow, departure.Latitude, departure.Longitude, fullBlockFuelKg, onGround: true));

        var startLeg1 = await FlightEndpoints.StartAsync(
            new StartFlightRequest(outboundRoute.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startLeg1));

        var flight1 = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == outboundRoute.Id);
        var fleetAircraftId = flight1.FleetAircraftId;

        var leg1LedgerAfterStart = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight1.Id).ToListAsync();
        var leg1FuelLine = Assert.Single(leg1LedgerAfterStart.Where(t => t.Category == LedgerCategory.Fuel));
        Assert.True(leg1FuelLine.Amount < 0);
        Assert.Contains("EGGD", leg1FuelLine.Description);

        var fleetAircraftAfterLeg1Start = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == fleetAircraftId);
        Assert.Equal(fullBlockFuelKg, fleetAircraftAfterLeg1Start.FuelOnBoardKg, 3);

        // Complete leg 1: a normal (non-diverted) sector burns trip+taxi+contingency and lands
        // with the alternate/final-reserve allowances still on board - a real, plausible leftover,
        // not a contrived one.
        var leftoverAfterLeg1Kg = plan.FuelBreakdown.AlternateFuelKg + plan.FuelBreakdown.FinalReserveFuelKg + plan.FuelBreakdown.TaxiFuelKg;
        var blockMinutes = plan.BlockTimeBreakdown.TotalMinutes;
        var tracker1 = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flight1.Id,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = fleetAircraftId,
            ArrivalIcao = "EGPH",
            PlannedBlockMinutes = flight1.PlannedBlockMinutes,
            Machine = FlightPhaseStateMachine.RestoreFrom(new[]
            {
                PhaseChangeEvent(flight1.Id, Base, FlightPhase.Preflight, FlightPhase.TaxiOut),
                PhaseChangeEvent(flight1.Id, Base + TimeSpan.FromMinutes(blockMinutes), FlightPhase.TaxiIn, FlightPhase.Shutdown),
            }),
            LatestSnapshot = new LiveFlightSnapshot(
                flight1.Id, FlightPhase.Shutdown.ToString(), arrival.Latitude, arrival.Longitude,
                AltitudeMslFt: 0, AltitudeAglFt: 0, IndicatedAirspeedKt: 0, GroundSpeedKt: 0, VerticalSpeedFpm: 0,
                FuelRemainingKg: leftoverAfterLeg1Kg, ElapsedBlockMinutes: blockMinutes, PlannedBlockMinutes: flight1.PlannedBlockMinutes,
                AwaitingSimReconnect: false, TimestampUtc: Base + TimeSpan.FromMinutes(blockMinutes)),
        };

        await lifecycle.FinalizeFlightAsync(tracker1);

        var fleetAircraftAfterLeg1 = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == fleetAircraftId);
        Assert.Equal(leftoverAfterLeg1Kg, fleetAircraftAfterLeg1.FuelOnBoardKg, 3);
        Assert.True(fleetAircraftAfterLeg1.FuelOnBoardKg > 0, "the aircraft must land with real fuel still on board for this scenario to prove anything");

        // --- LEG 2 (the return leg): EGPH -> EGGD. Nobody touched the tank - the sim now reports
        // EXACTLY what's already tracked (fleetAircraftAfterLeg1.FuelOnBoardKg). Reconciliation
        // must see zero change and post NO fuel charge whatsoever. Again timestamped at real
        // UtcNow so StartAsync's recency window accepts it.
        telemetry.SetLastSampleForTests(MakeSample(DateTimeOffset.UtcNow, arrival.Latitude, arrival.Longitude, fleetAircraftAfterLeg1.FuelOnBoardKg, onGround: true));

        var cashBeforeLeg2 = await CashBalanceAsync(ctx);

        // ctx.Db's tracked FleetAircraft instance is stale at this point: leg 1's StartAsync
        // loaded and mutated it through THIS context, but FinalizeFlightAsync just updated the same
        // row through lifecycle's OWN separate DbContext (correctly mirroring production, where a
        // background service resolves through its own DI scope). In production this can never bite
        // StartAsync specifically - every HTTP request gets a brand-new scoped DbContext, so a
        // second request's StartAsync would never see a first request's tracked entities. Here,
        // though, ctx.Db is deliberately reused for both StartAsync calls to keep this test simple,
        // so its identity map would otherwise hand back the SAME stale in-memory object (still
        // showing EGPH's departure aircraft parked at EGGD, InFlight) instead of the fresh row
        // FinalizeFlightAsync just committed. Reload it explicitly so this test's DbContext usage
        // matches what a fresh per-request context would actually observe.
        var trackedAircraft = await ctx.Db.FleetAircraft.FirstAsync(f => f.Id == fleetAircraftId);
        await ctx.Db.Entry(trackedAircraft).ReloadAsync();

        var startLeg2 = await FlightEndpoints.StartAsync(
            new StartFlightRequest(returnRoute.Id, fleetAircraftId), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startLeg2));

        var flight2 = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == returnRoute.Id);

        // THE HEADLINE PROPERTY: no Fuel ledger line at all for the return leg, TotalCost carries
        // no fuel charge, and cash didn't move for fuel.
        var leg2LedgerAfterStart = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight2.Id).ToListAsync();
        Assert.DoesNotContain(leg2LedgerAfterStart, t => t.Category == LedgerCategory.Fuel);
        Assert.Equal(0m, flight2.TotalCost);

        var cashAfterLeg2Start = await CashBalanceAsync(ctx);
        Assert.Equal(cashBeforeLeg2, cashAfterLeg2Start);

        var fleetAircraftAfterLeg2Start = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == fleetAircraftId);
        Assert.Equal(fleetAircraftAfterLeg1.FuelOnBoardKg, fleetAircraftAfterLeg2Start.FuelOnBoardKg, 6);
    }

    [Fact]
    public async Task Reconciliation_UnexplainedRiseAtFlightStart_ChargesAtCurrentAirportPrice_AndSyncsTheTrackedFigure()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var (outboundRoute, _) = await SeedRoundTripRoutesAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);

        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();
        fleetAircraft.FuelOnBoardKg = 1000; // whatever FSOps last knew about.
        await ctx.Db.SaveChangesAsync();

        // The sim reports MORE than that - fuel changed while FSOps wasn't watching (a menu fuel
        // set, a sim restart, an untracked flight) - reconciliation treats an unexplained
        // increase as an uplift at the current airport's price.
        // Real UtcNow so StartAsync's recency window trusts this sample.
        var departure = await ctx.Db.Airports.SingleAsync(a => a.Icao == "EGGD");
        telemetry.SetLastSampleForTests(MakeSample(DateTimeOffset.UtcNow, departure.Latitude, departure.Longitude, totalFuelKg: 4000, onGround: true));

        var startResult = await FlightEndpoints.StartAsync(
            new StartFlightRequest(outboundRoute.Id, fleetAircraft.Id), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startResult));

        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == outboundRoute.Id);
        var ledgerLines = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync();
        var fuelLine = Assert.Single(ledgerLines.Where(t => t.Category == LedgerCategory.Fuel));
        Assert.Contains("EGGD", fuelLine.Description);
        Assert.Contains("3000", fuelLine.Description); // exactly the unexplained delta (4000 - 1000).

        var updatedAircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == fleetAircraft.Id);
        Assert.Equal(4000, updatedAircraft.FuelOnBoardKg); // never left drifting from the sim's own figure.
    }

    [Fact]
    public async Task Reconciliation_UnexplainedFallAtFlightStart_IsSilentlyAbsorbed_NeverCredited()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var (outboundRoute, _) = await SeedRoundTripRoutesAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);

        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();
        fleetAircraft.FuelOnBoardKg = 5000;
        await ctx.Db.SaveChangesAsync();

        var departure = await ctx.Db.Airports.SingleAsync(a => a.Icao == "EGGD");
        // The sim now reports LESS - defuelled, or consumed by an untracked flight. Policy
        // (decided this chunk): a decrease is treated as consumed, never a credit.
        // Real UtcNow so StartAsync's recency window trusts this sample.
        telemetry.SetLastSampleForTests(MakeSample(DateTimeOffset.UtcNow, departure.Latitude, departure.Longitude, totalFuelKg: 2000, onGround: true));

        var cashBefore = await CashBalanceAsync(ctx);

        var startResult = await FlightEndpoints.StartAsync(
            new StartFlightRequest(outboundRoute.Id, fleetAircraft.Id), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startResult));

        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == outboundRoute.Id);
        var ledgerLines = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync();
        Assert.DoesNotContain(ledgerLines, t => t.Category == LedgerCategory.Fuel);

        var cashAfter = await CashBalanceAsync(ctx);
        Assert.Equal(cashBefore, cashAfter); // never a credit.

        var updatedAircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == fleetAircraft.Id);
        Assert.Equal(2000, updatedAircraft.FuelOnBoardKg); // still synced to the sim's real figure.
    }

    [Fact]
    public async Task NoTelemetryAvailable_FallsBackToChargedFuelKg_MatchingThePreExistingBalanceFigures()
    {
        // No SetLastSampleForTests call at all - exactly the existing FlightFuelChargeBalanceTests/
        // FlightLedgerPostingTests situation (NoOpSimSource, never a sample). Must reproduce the
        // pre-persisted-fuel behaviour exactly, or every existing balance figure would move - see
        // FlightEndpoints.StartAsync's own doc for why.
        using var ctx = await RouteTestContext.CreateAsync();
        var (outboundRoute, _) = await SeedRoundTripRoutesAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var economyConfig = economyConfigCatalog.Get(ctx.Airline.Playstyle);
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);

        var departure = await ctx.Db.Airports.SingleAsync(a => a.Icao == "EGGD");
        var arrival = await ctx.Db.Airports.SingleAsync(a => a.Icao == "EGPH");
        var aircraftType = await ctx.Db.AircraftTypes.SingleAsync();
        var plan = RoutePreviewCalculator.Calculate(economyConfig, departure, arrival, aircraftType, ctx.Airline.StrategyProfile);

        var startResult = await FlightEndpoints.StartAsync(
            new StartFlightRequest(outboundRoute.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startResult));

        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == outboundRoute.Id);
        var ledgerLines = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync();
        var fuelLine = Assert.Single(ledgerLines.Where(t => t.Category == LedgerCategory.Fuel));

        var expectedCost = FlightCostCalculator.FuelUpliftCost(
            plan.FuelBreakdown.ChargedFuelKg,
            FuelPricing.PricePerKg(economyConfig.Fuel, "EGGD", "GB", flight.CreatedUtc, await FlightEconomicsPoster.ResolveWorldSeedAsync(ctx.Db, CancellationToken.None)));

        Assert.Equal(-expectedCost, fuelLine.Amount);

        var fleetAircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync();
        Assert.Equal(plan.FuelBreakdown.ChargedFuelKg, fleetAircraft.FuelOnBoardKg, 3);
    }

    [Fact]
    public async Task HandleGroundFuelChangeAsync_Uplift_PostsAtTheResolvedAirport_AndUpdatesFuelOnBoard()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var (outboundRoute, _) = await SeedRoundTripRoutesAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);

        var departure = await ctx.Db.Airports.SingleAsync(a => a.Icao == "EGGD");

        // Report the aircraft's own (fresh, empty) tank back at start, so reconciliation sees no
        // change and posts nothing - keeps this test isolated to the ONE ledger line the ground
        // detection below produces, rather than also picking up the no-telemetry fallback's charge.
        telemetry.SetLastSampleForTests(MakeSample(DateTimeOffset.UtcNow, departure.Latitude, departure.Longitude, totalFuelKg: 0, onGround: true));

        var startResult = await FlightEndpoints.StartAsync(
            new StartFlightRequest(outboundRoute.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startResult));
        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == outboundRoute.Id);
        var fleetAircraftBefore = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync();
        Assert.Empty(await ctx.Db.LedgerTransactions.Where(t => t.Category == LedgerCategory.Fuel).ToListAsync());

        var tracker = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flight.Id,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = flight.FleetAircraftId,
            ArrivalIcao = "EGPH",
            PlannedBlockMinutes = flight.PlannedBlockMinutes,
            Machine = new FlightPhaseStateMachine(),
        };

        var sample = MakeSample(Base + TimeSpan.FromMinutes(5), departure.Latitude, departure.Longitude, totalFuelKg: 5000, onGround: true);
        var deltaKg = 5000 - fleetAircraftBefore.FuelOnBoardKg;

        await lifecycle.HandleGroundFuelChangeAsync(tracker, sample, GroundFuelChangeKind.Uplift, deltaKg);

        var ledgerLines = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id && t.Category == LedgerCategory.Fuel).ToListAsync();
        var groundFuelLine = Assert.Single(ledgerLines.Where(t => t.Description.Contains("EGGD")));
        Assert.True(groundFuelLine.Amount < 0);

        var fleetAircraftAfter = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync();
        Assert.Equal(5000, fleetAircraftAfter.FuelOnBoardKg, 3);
    }

    [Fact]
    public async Task HandleGroundFuelChangeAsync_Defuel_NoLedgerLine_ButFuelOnBoardStillSynced()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var (outboundRoute, _) = await SeedRoundTripRoutesAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);

        var startResult = await FlightEndpoints.StartAsync(
            new StartFlightRequest(outboundRoute.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startResult));
        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == outboundRoute.Id);
        var fleetAircraftBefore = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync();
        Assert.True(fleetAircraftBefore.FuelOnBoardKg > 500, "the no-telemetry fallback should have topped the tank up already");

        var departure = await ctx.Db.Airports.SingleAsync(a => a.Icao == "EGGD");
        var tracker = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flight.Id,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = flight.FleetAircraftId,
            ArrivalIcao = "EGPH",
            PlannedBlockMinutes = flight.PlannedBlockMinutes,
            Machine = new FlightPhaseStateMachine(),
        };

        var lowerFuelKg = fleetAircraftBefore.FuelOnBoardKg - 500;
        var sample = MakeSample(Base + TimeSpan.FromMinutes(5), departure.Latitude, departure.Longitude, lowerFuelKg, onGround: true);

        var cashBefore = await CashBalanceAsync(ctx);
        await lifecycle.HandleGroundFuelChangeAsync(tracker, sample, GroundFuelChangeKind.Defuel, 500);
        var cashAfter = await CashBalanceAsync(ctx);

        Assert.Equal(cashBefore, cashAfter); // no ledger line, no credit.
        var fleetAircraftAfter = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync();
        Assert.Equal(lowerFuelKg, fleetAircraftAfter.FuelOnBoardKg, 3);
    }

    private static int StatusCodeOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static TelemetrySample MakeSample(DateTimeOffset utc, double latitudeDeg, double longitudeDeg, double totalFuelKg, bool onGround) => new(
        utc, latitudeDeg, longitudeDeg, AltitudeMslFt: onGround ? 0 : 35000, AltitudeAglFt: onGround ? 0 : 35000,
        IndicatedAirspeedKt: 0, GroundSpeedKt: 0, VerticalSpeedFpm: 0, TrueHeadingDeg: 0, MagneticHeadingDeg: 0,
        OnGround: onGround, EngineRunning: !onGround, ParkingBrakeSet: onGround, GForce: 1.0, TouchdownNormalVelocityFps: 0,
        TotalFuelKg: totalFuelKg, AircraftTitle: "Test Aircraft", AtcModel: "Test Aircraft", AtcType: "TEST",
        SimulationRate: 1.0, IsSlewActive: false);

    private static FlightEvent PhaseChangeEvent(Guid flightId, DateTimeOffset utc, FlightPhase from, FlightPhase to) => new()
    {
        Id = Guid.NewGuid(),
        FlightId = flightId,
        Utc = utc,
        Type = FlightEventType.PhaseChange,
        PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PhaseChangePayload(from.ToString(), to.ToString(), false)),
    };

    /// <summary>SQLite can't translate SumAsync over decimal - materialise the rows first.</summary>
    private static async Task<decimal> CashBalanceAsync(RouteTestContext ctx) =>
        (await ctx.Db.LedgerTransactions.Where(t => t.AirlineId == ctx.Airline.Id).ToListAsync()).Sum(t => t.Amount);

    /// <summary>Seeds both directions of an EGGD&lt;-&gt;EGPH round trip (routes are always
    /// bidirectional pairs) plus the pilot FlightEndpoints.StartAsync requires.</summary>
    private static async Task<(Route Outbound, Route Return)> SeedRoundTripRoutesAsync(RouteTestContext ctx)
    {
        var outbound = new Route
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            DepartureIcao = "EGGD",
            ArrivalIcao = "EGPH",
            FlightNumber = "101",
            DistanceNm = 274,
            BaseFare = 90m,
            IsActive = true,
            CreatedUtc = Base,
        };
        var inbound = new Route
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            DepartureIcao = "EGPH",
            ArrivalIcao = "EGGD",
            FlightNumber = "102",
            DistanceNm = 274,
            BaseFare = 90m,
            IsActive = true,
            CreatedUtc = Base,
        };
        ctx.Db.Routes.AddRange(outbound, inbound);

        ctx.Db.Pilots.Add(new Pilot
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Name = "Test Pilot",
            IsPlayer = true,
            MonthlySalary = 9000m,
            SkillRating = 50,
            CreatedUtc = Base,
        });

        await ctx.Db.SaveChangesAsync();
        return (outbound, inbound);
    }

    /// <summary>Same construction FlightLedgerPostingTests/FlightFuelChargeBalanceTests use: a real
    /// FlightLifecycleService (and the SimTelemetryService it needs) whose db-scope resolves a
    /// fresh FsOpsDbContext against the same live in-memory SQLite connection RouteTestContext
    /// seeded. NoOpSimSource never produces a real sample on its own -
    /// SimTelemetryService.SetLastSampleForTests is what lets these tests script a reading.</summary>
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
