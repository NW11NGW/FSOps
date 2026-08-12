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
/// FSOps bills a sector for what it actually burned, at the departure airport's price - never on
/// what was merely uplifted, and never at flight start (the old model this replaces charged fuel
/// when it was bought; see git history for FuelPersistenceTests, which this file supersedes).
/// Burn is measured from the first sample where the ENGINES are genuinely running, not from flight
/// start - see FuelBurnResolver.Measure and FlightLifecycleService.ProcessSample's own accumulation
/// logic - so the "spawn fuel" problem (MSFS's own default load, a menu fuel set, a GSX uplift
/// before startup) can never be read as burn. Proved end to end through the real paths a player
/// uses (<c>FlightEndpoints.StartAsync</c>/<c>AbandonAsync</c> and
/// <c>FlightLifecycleService.FinalizeFlightAsync</c>), never a calculator called in isolation.
/// </summary>
public class FuelBurnBillingTests
{
    private static readonly DateTimeOffset Base = new(2026, 4, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StartAsync_PostsNoFuelChargeAtAll_RegardlessOfWhatTelemetryReports()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var (outboundRoute, _) = await SeedRoundTripRoutesAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);

        var departure = await ctx.Db.Airports.SingleAsync(a => a.Icao == "EGGD");
        // A big rise in tank contents - exactly what used to trigger an uplift charge at start.
        telemetry.SetLastSampleForTests(MakeSample(DateTimeOffset.UtcNow, departure.Latitude, departure.Longitude, totalFuelKg: 9000, onGround: true, engineRunning: false));

        var cashBefore = await CashBalanceAsync(ctx);

        var startResult = await FlightEndpoints.StartAsync(
            new StartFlightRequest(outboundRoute.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startResult));

        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == outboundRoute.Id);
        Assert.Empty(await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync());
        Assert.Equal(0m, flight.TotalCost);

        var cashAfter = await CashBalanceAsync(ctx);
        Assert.Equal(cashBefore, cashAfter);

        // FuelOnBoardKg is still synced informationally from the telemetry reading - it just never
        // drives a charge any more.
        var fleetAircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync();
        Assert.Equal(9000, fleetAircraft.FuelOnBoardKg);
    }

    [Fact]
    public async Task MeasuredBurn_BilledAtDepartureAirportPrice_WhenTheSectorCompletes()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var (outboundRoute, _) = await SeedRoundTripRoutesAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var economyConfig = economyConfigCatalog.Get(ctx.Airline.Playstyle);
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);

        var startResult = await FlightEndpoints.StartAsync(
            new StartFlightRequest(outboundRoute.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startResult));

        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == outboundRoute.Id);
        var fleetAircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync();

        var tracker = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flight.Id,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = fleetAircraft.Id,
            ArrivalIcao = "EGPH",
            PlannedBlockMinutes = flight.PlannedBlockMinutes,
            Machine = CompletedMachine(flight.Id, flight.PlannedBlockMinutes),
            EngineStartFuelKg = 3000,
            AccumulatedBurnKg = 1800, // burned 1800 kg since the engines started.
            LastFuelKg = 1200,
            LatestSnapshot = Snapshot(flight.Id, 1200, flight.PlannedBlockMinutes),
        };

        await lifecycle.FinalizeFlightAsync(tracker);

        var updatedFlight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
        Assert.Equal(1800, updatedFlight.FuelUsedKg, 3);

        var ledgerLines = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync();
        var fuelLine = Assert.Single(ledgerLines.Where(t => t.Category == LedgerCategory.Fuel));
        Assert.Contains("EGGD", fuelLine.Description); // billed at the DEPARTURE airport, not EGPH.

        var expectedPrice = FuelPricing.PricePerKg(
            economyConfig.Fuel, "EGGD", "GB", tracker.Machine.InUtc!.Value, await FlightEconomicsPoster.ResolveWorldSeedAsync(ctx.Db, CancellationToken.None));
        var expectedCost = FlightCostCalculator.FuelBurnCost(1800, expectedPrice);
        Assert.Equal(-expectedCost, fuelLine.Amount);
    }

    [Fact]
    public async Task PreEngineStartFuelChange_IsExcludedFromBurn_NeverReadAsFuelBought()
    {
        // The exact "spawn fuel" problem this redesign exists to fix: the tank moves for reasons
        // that are not burn between "Start flight" and engine start (MSFS's own default load, a
        // menu fuel set, a GSX uplift before startup). Driven through the real ProcessSample path,
        // not hand-set tracker fields, so this proves the engine-gating itself, not just its result.
        using var ctx = await RouteTestContext.CreateAsync();
        var (outboundRoute, _) = await SeedRoundTripRoutesAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);

        var startResult = await FlightEndpoints.StartAsync(
            new StartFlightRequest(outboundRoute.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startResult));
        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == outboundRoute.Id);
        var fleetAircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync();
        var departure = await ctx.Db.Airports.SingleAsync(a => a.Icao == "EGGD");

        var tracker = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flight.Id,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = fleetAircraft.Id,
            ArrivalIcao = "EGPH",
            PlannedBlockMinutes = flight.PlannedBlockMinutes,
            Machine = new FlightPhaseStateMachine(),
        };

        // Engines off: MSFS spawns the aircraft with a default load (500 kg), then the player sets
        // a full tank from the fuel menu (4500 kg) - a huge apparent "rise", but none of it is
        // burn, since the engines never ran yet.
        lifecycle.ProcessSample(tracker, MakeSample(Base, departure.Latitude, departure.Longitude, totalFuelKg: 500, onGround: true, engineRunning: false));
        lifecycle.ProcessSample(tracker, MakeSample(Base + TimeSpan.FromSeconds(30), departure.Latitude, departure.Longitude, totalFuelKg: 4500, onGround: true, engineRunning: false));
        // Engines start - THIS reading (4480 kg, a trivial pre-start settle) becomes the baseline,
        // not 500 kg and not 4500 kg.
        lifecycle.ProcessSample(tracker, MakeSample(Base + TimeSpan.FromMinutes(1), departure.Latitude, departure.Longitude, totalFuelKg: 4480, onGround: true, engineRunning: true));
        // Taxi burn after engine start.
        lifecycle.ProcessSample(tracker, MakeSample(Base + TimeSpan.FromMinutes(4), departure.Latitude, departure.Longitude, totalFuelKg: 4380, onGround: true, engineRunning: true));

        await lifecycle.FinalizeFlightAsync(Finalized(tracker, 4380));

        var updatedFlight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
        // Only the post-engine-start taxi burn (4480 - 4380 = 100 kg) - never the 4000 kg pre-start
        // "rise" from the menu fuel set, and never a credit for it either.
        Assert.Equal(100, updatedFlight.FuelUsedKg, 3);
    }

    [Fact]
    public async Task MidSectorTopUp_AfterEngineStart_ContributesNothingToTheAccumulatedBurn()
    {
        // Proves the sum-of-decreases design directly: a rise anywhere after the baseline (a
        // ground top-up at an intermediate stop, say) must never reduce - let alone credit - the
        // running total, and must never be netted against the decreases either side of it.
        using var ctx = await RouteTestContext.CreateAsync();
        var (outboundRoute, _) = await SeedRoundTripRoutesAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);

        var startResult = await FlightEndpoints.StartAsync(
            new StartFlightRequest(outboundRoute.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startResult));
        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == outboundRoute.Id);
        var fleetAircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync();
        var departure = await ctx.Db.Airports.SingleAsync(a => a.Icao == "EGGD");

        var tracker = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flight.Id,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = fleetAircraft.Id,
            ArrivalIcao = "EGPH",
            PlannedBlockMinutes = flight.PlannedBlockMinutes,
            Machine = new FlightPhaseStateMachine(),
        };

        lifecycle.ProcessSample(tracker, MakeSample(Base, departure.Latitude, departure.Longitude, totalFuelKg: 2000, onGround: true, engineRunning: true)); // baseline.
        lifecycle.ProcessSample(tracker, MakeSample(Base + TimeSpan.FromMinutes(10), departure.Latitude, departure.Longitude, totalFuelKg: 1500, onGround: false, engineRunning: true)); // -500.
        lifecycle.ProcessSample(tracker, MakeSample(Base + TimeSpan.FromMinutes(20), departure.Latitude, departure.Longitude, totalFuelKg: 4000, onGround: true, engineRunning: true)); // a mid-sector top-up: +2500, must contribute 0.
        lifecycle.ProcessSample(tracker, MakeSample(Base + TimeSpan.FromMinutes(30), departure.Latitude, departure.Longitude, totalFuelKg: 3500, onGround: false, engineRunning: true)); // -500.

        await lifecycle.FinalizeFlightAsync(Finalized(tracker, 3500));

        var updatedFlight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
        // 500 + 500 = 1000, NOT (2000 - 3500) = -1500 that a naive start-minus-end subtraction
        // would have produced (and which FuelBurnResolver would then have had to fall back on).
        Assert.Equal(1000, updatedFlight.FuelUsedKg, 3);

        var ledgerLines = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync();
        var fuelLine = Assert.Single(ledgerLines.Where(t => t.Category == LedgerCategory.Fuel));
        Assert.True(fuelLine.Amount < 0);
    }

    /// <summary>
    /// A shutdown/restart mid-sector (a single-engine taxi stop, an intentional practice shutdown)
    /// does not reset the baseline or start a second one - it is only ever set once, at the FIRST
    /// sample where the engines are seen running - and accumulation resumes normally once the
    /// engines are running again. Nothing changes while shut down here (see the companion test
    /// below for what happens when the reading actually moves during that window).
    /// </summary>
    [Fact]
    public async Task EngineShutdownAndRestartMidSector_DoesNotResetTheBaseline()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var (outboundRoute, _) = await SeedRoundTripRoutesAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);

        var startResult = await FlightEndpoints.StartAsync(
            new StartFlightRequest(outboundRoute.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startResult));
        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == outboundRoute.Id);
        var fleetAircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync();
        var departure = await ctx.Db.Airports.SingleAsync(a => a.Icao == "EGGD");

        var tracker = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flight.Id,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = fleetAircraft.Id,
            ArrivalIcao = "EGPH",
            PlannedBlockMinutes = flight.PlannedBlockMinutes,
            Machine = new FlightPhaseStateMachine(),
        };

        lifecycle.ProcessSample(tracker, MakeSample(Base, departure.Latitude, departure.Longitude, totalFuelKg: 3000, onGround: true, engineRunning: true)); // baseline.
        lifecycle.ProcessSample(tracker, MakeSample(Base + TimeSpan.FromMinutes(2), departure.Latitude, departure.Longitude, totalFuelKg: 2950, onGround: true, engineRunning: true)); // -50.
        // Shut down for a single-engine taxi stop - fuel unchanged while off.
        lifecycle.ProcessSample(tracker, MakeSample(Base + TimeSpan.FromMinutes(3), departure.Latitude, departure.Longitude, totalFuelKg: 2950, onGround: true, engineRunning: false));
        // Restart - EngineStartFuelKg is untouched (already set), so this is not a second baseline.
        lifecycle.ProcessSample(tracker, MakeSample(Base + TimeSpan.FromMinutes(5), departure.Latitude, departure.Longitude, totalFuelKg: 2900, onGround: true, engineRunning: true)); // -50.

        await lifecycle.FinalizeFlightAsync(Finalized(tracker, 2900));

        var updatedFlight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
        Assert.Equal(100, updatedFlight.FuelUsedKg, 3); // 50 + 50, continuous through the shutdown.
    }

    /// <summary>
    /// The mirror of the pre-engine-start exclusion: once tracking HAS started, a decrease seen
    /// WHILE THE ENGINES ARE OFF is not burn either - a defuel, a menu change, or ground-crew
    /// activity during a turnaround stop - and must never be charged to the player as if it were.
    /// Wrongly charging for something that didn't happen is a worse failure than undercharging, so
    /// this is guarded explicitly rather than assumed safe.
    /// </summary>
    [Fact]
    public async Task DefuelWhileEnginesAreOff_IsExcludedFromBurn_NeverChargedToThePlayer()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var (outboundRoute, _) = await SeedRoundTripRoutesAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);

        var startResult = await FlightEndpoints.StartAsync(
            new StartFlightRequest(outboundRoute.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startResult));
        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == outboundRoute.Id);
        var fleetAircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync();
        var departure = await ctx.Db.Airports.SingleAsync(a => a.Icao == "EGGD");

        var tracker = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flight.Id,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = fleetAircraft.Id,
            ArrivalIcao = "EGPH",
            PlannedBlockMinutes = flight.PlannedBlockMinutes,
            Machine = new FlightPhaseStateMachine(),
        };

        lifecycle.ProcessSample(tracker, MakeSample(Base, departure.Latitude, departure.Longitude, totalFuelKg: 3000, onGround: true, engineRunning: true)); // baseline.
        lifecycle.ProcessSample(tracker, MakeSample(Base + TimeSpan.FromMinutes(2), departure.Latitude, departure.Longitude, totalFuelKg: 2950, onGround: true, engineRunning: true)); // -50, real burn.
        // Shut down for a turnaround stop and a real defuel happens - 450 kg removed by ground
        // crew, engines off throughout. Must never read as burn.
        lifecycle.ProcessSample(tracker, MakeSample(Base + TimeSpan.FromMinutes(20), departure.Latitude, departure.Longitude, totalFuelKg: 2500, onGround: true, engineRunning: false));
        // Restart with the tank already at the post-defuel level - no further change here.
        lifecycle.ProcessSample(tracker, MakeSample(Base + TimeSpan.FromMinutes(22), departure.Latitude, departure.Longitude, totalFuelKg: 2500, onGround: true, engineRunning: true));

        await lifecycle.FinalizeFlightAsync(Finalized(tracker, 2500));

        var updatedFlight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
        // Only the 50 kg burned before the shutdown - NOT 50 + 450 = 500, which a naive
        // engine-state-agnostic accumulator would have produced.
        Assert.Equal(50, updatedFlight.FuelUsedKg, 3);
    }

    [Fact]
    public async Task NoTelemetryEverObserved_FallsBackToTheSectorsOwnPlannedCharge()
    {
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
        // Nothing charged yet - fuel bills at completion now, never at start.
        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == outboundRoute.Id);
        Assert.Empty(await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync());

        var fleetAircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync();

        // Tracker built without ever running a sample through ProcessSample - every fuel field
        // stays at its default (null/0), exactly like a flight completed with no telemetry
        // connection at all.
        var tracker = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flight.Id,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = fleetAircraft.Id,
            ArrivalIcao = "EGPH",
            PlannedBlockMinutes = flight.PlannedBlockMinutes,
            Machine = CompletedMachine(flight.Id, flight.PlannedBlockMinutes),
            LatestSnapshot = Snapshot(flight.Id, 500, flight.PlannedBlockMinutes),
        };

        await lifecycle.FinalizeFlightAsync(tracker);

        var updatedFlight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
        Assert.Equal(plan.FuelBreakdown.ChargedFuelKg, updatedFlight.FuelUsedKg, 3);

        var ledgerLines = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync();
        var fuelLine = Assert.Single(ledgerLines.Where(t => t.Category == LedgerCategory.Fuel));
        var expectedCost = FlightCostCalculator.FuelBurnCost(
            plan.FuelBreakdown.ChargedFuelKg,
            FuelPricing.PricePerKg(economyConfig.Fuel, "EGGD", "GB", tracker.Machine.InUtc!.Value, await FlightEconomicsPoster.ResolveWorldSeedAsync(ctx.Db, CancellationToken.None)));
        Assert.Equal(-expectedCost, fuelLine.Amount);
    }

    [Fact]
    public async Task EnginesNeverObservedRunning_FallsBackToFirstSampleMinusLast()
    {
        // Tier 2 of FuelBurnResolver.Measure: a sim that connected late, or a telemetry gap that
        // never caught a genuine engine-start sample, still gets its best-available estimate -
        // never nothing, and never the sector's full planned charge either, as long as SOME
        // readings exist.
        using var ctx = await RouteTestContext.CreateAsync();
        var (outboundRoute, _) = await SeedRoundTripRoutesAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);

        var startResult = await FlightEndpoints.StartAsync(
            new StartFlightRequest(outboundRoute.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startResult));
        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == outboundRoute.Id);
        var fleetAircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync();
        var departure = await ctx.Db.Airports.SingleAsync(a => a.Icao == "EGGD");

        var tracker = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flight.Id,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = fleetAircraft.Id,
            ArrivalIcao = "EGPH",
            PlannedBlockMinutes = flight.PlannedBlockMinutes,
            Machine = new FlightPhaseStateMachine(),
        };

        // Every sample shows engines off - a telemetry gap that missed the real engine-start
        // moment, say.
        lifecycle.ProcessSample(tracker, MakeSample(Base, departure.Latitude, departure.Longitude, totalFuelKg: 2600, onGround: true, engineRunning: false));
        lifecycle.ProcessSample(tracker, MakeSample(Base + TimeSpan.FromMinutes(5), departure.Latitude, departure.Longitude, totalFuelKg: 2400, onGround: true, engineRunning: false));

        await lifecycle.FinalizeFlightAsync(Finalized(tracker, 2400));

        var updatedFlight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
        Assert.Equal(200, updatedFlight.FuelUsedKg, 3); // 2600 - 2400, the plain tier-2 subtraction.
    }

    [Fact]
    public async Task ImplausiblyLargeApparentBurn_FallsBackToPlanned_RatherThanAWildCharge()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var (outboundRoute, _) = await SeedRoundTripRoutesAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);

        var startResult = await FlightEndpoints.StartAsync(
            new StartFlightRequest(outboundRoute.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startResult));
        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == outboundRoute.Id);
        var fleetAircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync();

        var tracker = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flight.Id,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = fleetAircraft.Id,
            ArrivalIcao = "EGPH",
            PlannedBlockMinutes = flight.PlannedBlockMinutes,
            Machine = CompletedMachine(flight.Id, flight.PlannedBlockMinutes),
            // A sim-reset-shaped reading: an enormous apparent burn, way beyond what a short A320
            // hop could ever plausibly need - must never be billed at face value.
            EngineStartFuelKg = 90_000,
            AccumulatedBurnKg = 89_950,
            LastFuelKg = 50,
            LatestSnapshot = Snapshot(flight.Id, 50, flight.PlannedBlockMinutes),
        };

        await lifecycle.FinalizeFlightAsync(tracker);

        var updatedFlight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
        Assert.True(updatedFlight.FuelUsedKg < 10_000, $"an implausible reading must fall back to the sector's own modest planned figure, not {updatedFlight.FuelUsedKg:F0} kg.");
    }

    [Fact]
    public async Task SlewFlaggedSector_StillBilledForFuelBurned_DespiteEarningNoTicketRevenue()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var (outboundRoute, _) = await SeedRoundTripRoutesAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);

        var startResult = await FlightEndpoints.StartAsync(
            new StartFlightRequest(outboundRoute.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startResult));
        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == outboundRoute.Id);
        var fleetAircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync();

        var tracker = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flight.Id,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = fleetAircraft.Id,
            ArrivalIcao = "EGPH",
            PlannedBlockMinutes = flight.PlannedBlockMinutes,
            Machine = CompletedMachine(flight.Id, flight.PlannedBlockMinutes),
            EngineStartFuelKg = 3000,
            AccumulatedBurnKg = 1800,
            LastFuelKg = 1200,
            LatestSnapshot = Snapshot(flight.Id, 1200, flight.PlannedBlockMinutes),
        };
        tracker.IntegrityMonitor.Observe(new FlightTelemetrySample(
            Base, 55.9500, -3.3725, 0, 0, 0, 0, 0, 0, 0, false, true, false, 0, 0, 1200,
            "Test Aircraft", "Test Aircraft", "TEST", 1.0, true));

        await lifecycle.FinalizeFlightAsync(tracker);

        var updatedFlight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
        Assert.True(updatedFlight.SlewDetected);
        Assert.Equal(0m, updatedFlight.Revenue);

        var ledgerLines = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync();
        Assert.DoesNotContain(ledgerLines, t => t.Category == LedgerCategory.TicketRevenue);
        var fuelLine = Assert.Single(ledgerLines.Where(t => t.Category == LedgerCategory.Fuel));
        Assert.True(fuelLine.Amount < 0, "an invalid sector still burned real fuel, and still keeps that cost.");
    }

    [Fact]
    public async Task Abandon_BillsWhateverWasMeasurablyBurnedUpToTheAbandonPoint()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var (outboundRoute, _) = await SeedRoundTripRoutesAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);

        var startResult = await FlightEndpoints.StartAsync(
            new StartFlightRequest(outboundRoute.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startResult));
        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == outboundRoute.Id);
        var fleetAircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync();
        var departure = await ctx.Db.Airports.SingleAsync(a => a.Icao == "EGGD");

        // Drive a couple of real, ENGINE-RUNNING ground samples through ProcessSample so the
        // tracker genuinely holds a burn baseline, exactly as a live abandon mid-taxi would -
        // rather than hand-constructing internal tracker state, which the abandon path itself
        // never has access to.
        var tracker = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flight.Id,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = fleetAircraft.Id,
            ArrivalIcao = "EGPH",
            PlannedBlockMinutes = flight.PlannedBlockMinutes,
            Machine = new FlightPhaseStateMachine(),
        };
        lifecycle.SetActiveTrackerForTests(tracker);

        lifecycle.ProcessSample(tracker, MakeSample(Base, departure.Latitude, departure.Longitude, totalFuelKg: 2600, onGround: true, engineRunning: true));
        lifecycle.ProcessSample(tracker, MakeSample(Base + TimeSpan.FromMinutes(3), departure.Latitude, departure.Longitude, totalFuelKg: 2450, onGround: true, engineRunning: true));

        var abandonResult = await FlightEndpoints.AbandonAsync(flight.Id, ctx.Db, ctx.CurrentUser, lifecycle, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(abandonResult));

        var updatedFlight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
        Assert.Equal(FlightStatus.Abandoned, updatedFlight.Status);
        Assert.Equal(150, updatedFlight.FuelUsedKg, 3); // 2600 - 2450, taxiing before the abandon.

        var ledgerLines = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync();
        var fuelLine = Assert.Single(ledgerLines.Where(t => t.Category == LedgerCategory.Fuel));
        Assert.True(fuelLine.Amount < 0);
        Assert.Contains("EGGD", fuelLine.Description);
    }

    [Fact]
    public async Task Abandon_BeforeEnginesEverStarted_BillsApproximatelyNothing()
    {
        // A pre-start fuel change (spawn load, menu set) followed by an abandon before the engines
        // ever ran must never be billed - there is no engine-start baseline at all, so Measure's
        // tier 1 never applies, and there's no "first sample" fallback billing behaviour here
        // either (that only exists for a COMPLETED sector - see AbandonAsync's own fallback: 0,
        // not the sector's planned figure).
        using var ctx = await RouteTestContext.CreateAsync();
        var (outboundRoute, _) = await SeedRoundTripRoutesAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);

        var startResult = await FlightEndpoints.StartAsync(
            new StartFlightRequest(outboundRoute.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startResult));
        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == outboundRoute.Id);
        var fleetAircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync();
        var departure = await ctx.Db.Airports.SingleAsync(a => a.Icao == "EGGD");

        var tracker = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flight.Id,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = fleetAircraft.Id,
            ArrivalIcao = "EGPH",
            PlannedBlockMinutes = flight.PlannedBlockMinutes,
            Machine = new FlightPhaseStateMachine(),
        };
        lifecycle.SetActiveTrackerForTests(tracker);

        lifecycle.ProcessSample(tracker, MakeSample(Base, departure.Latitude, departure.Longitude, totalFuelKg: 500, onGround: true, engineRunning: false));
        lifecycle.ProcessSample(tracker, MakeSample(Base + TimeSpan.FromSeconds(20), departure.Latitude, departure.Longitude, totalFuelKg: 4500, onGround: true, engineRunning: false));

        var cashBefore = await CashBalanceAsync(ctx);
        var abandonResult = await FlightEndpoints.AbandonAsync(flight.Id, ctx.Db, ctx.CurrentUser, lifecycle, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(abandonResult));

        var ledgerLines = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync();
        Assert.Empty(ledgerLines);
        Assert.Equal(await CashBalanceAsync(ctx), cashBefore);

        var updatedFlight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
        Assert.Equal(0m, updatedFlight.TotalCost);
    }

    [Fact]
    public async Task Abandon_WithNoUsableFuelReadingAtAll_BillsNothing_RatherThanTheFullSectorFigure()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var (outboundRoute, _) = await SeedRoundTripRoutesAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);

        var startResult = await FlightEndpoints.StartAsync(
            new StartFlightRequest(outboundRoute.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startResult));
        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == outboundRoute.Id);

        var cashBefore = await CashBalanceAsync(ctx);

        // Abandoned immediately - no telemetry was ever tracked (no ProcessSample call, no tracker
        // even registered), so there is nothing to measure a burn from.
        var abandonResult = await FlightEndpoints.AbandonAsync(flight.Id, ctx.Db, ctx.CurrentUser, lifecycle, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(abandonResult));

        var ledgerLines = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync();
        Assert.Empty(ledgerLines); // unknown means bill nothing, never the sector's full planned figure.

        var cashAfter = await CashBalanceAsync(ctx);
        Assert.Equal(cashBefore, cashAfter);

        var updatedFlight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
        Assert.Equal(0m, updatedFlight.TotalCost);
    }

    [Fact]
    public async Task ReturnLeg_IsBilledForItsOwnBurn_RegardlessOfLeftoverFuelFromTheOutboundLeg()
    {
        // The old model's headline property was the opposite of this: a return leg flown on
        // leftover fuel posted no charge at all. That is deliberately gone now - a sector's own
        // burn is what it is billed for, independent of what happens to be in the tank.
        using var ctx = await RouteTestContext.CreateAsync();
        var (outboundRoute, returnRoute) = await SeedRoundTripRoutesAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);

        var startLeg1 = await FlightEndpoints.StartAsync(
            new StartFlightRequest(outboundRoute.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startLeg1));
        var flight1 = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == outboundRoute.Id);
        var fleetAircraftId = flight1.FleetAircraftId;

        var tracker1 = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flight1.Id,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = fleetAircraftId,
            ArrivalIcao = "EGPH",
            PlannedBlockMinutes = flight1.PlannedBlockMinutes,
            Machine = CompletedMachine(flight1.Id, flight1.PlannedBlockMinutes),
            EngineStartFuelKg = 4000,
            AccumulatedBurnKg = 1500,
            LastFuelKg = 2500, // lands with a real 2,500 kg still in the tank.
            LatestSnapshot = Snapshot(flight1.Id, 2500, flight1.PlannedBlockMinutes),
        };
        await lifecycle.FinalizeFlightAsync(tracker1);

        var fleetAircraftAfterLeg1 = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == fleetAircraftId);
        Assert.Equal(2500, fleetAircraftAfterLeg1.FuelOnBoardKg, 3); // informational carry-forward still happens.

        var trackedAircraft = await ctx.Db.FleetAircraft.FirstAsync(f => f.Id == fleetAircraftId);
        await ctx.Db.Entry(trackedAircraft).ReloadAsync();

        var startLeg2 = await FlightEndpoints.StartAsync(
            new StartFlightRequest(returnRoute.Id, fleetAircraftId), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startLeg2));
        var flight2 = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == returnRoute.Id);

        // Starting leg 2 still posts nothing (fuel bills at completion, not start) - the interesting
        // assertion is what happens once leg 2 itself completes.
        Assert.Empty(await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight2.Id).ToListAsync());

        var tracker2 = new FlightLifecycleService.ActiveFlightTracker
        {
            FlightId = flight2.Id,
            AirlineId = ctx.Airline.Id,
            FleetAircraftId = fleetAircraftId,
            ArrivalIcao = "EGGD",
            PlannedBlockMinutes = flight2.PlannedBlockMinutes,
            Machine = CompletedMachine(flight2.Id, flight2.PlannedBlockMinutes),
            EngineStartFuelKg = 2500, // the leftover from leg 1.
            AccumulatedBurnKg = 1600, // burned 1,600 kg flying leg 2 - billed regardless of the leftover.
            LastFuelKg = 900,
            LatestSnapshot = Snapshot(flight2.Id, 900, flight2.PlannedBlockMinutes),
        };
        await lifecycle.FinalizeFlightAsync(tracker2);

        var updatedFlight2 = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight2.Id);
        Assert.Equal(1600, updatedFlight2.FuelUsedKg, 3);

        var leg2LedgerLines = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight2.Id).ToListAsync();
        var leg2FuelLine = Assert.Single(leg2LedgerLines.Where(t => t.Category == LedgerCategory.Fuel));
        Assert.True(leg2FuelLine.Amount < 0, "the return leg burns real fuel and is billed for it, leftover tank contents notwithstanding.");
        Assert.Contains("EGPH", leg2FuelLine.Description); // billed at EGPH - leg 2's own departure airport.
    }

    /// <summary>
    /// Carries a tracker's accumulated fuel-tracking state (built up via real ProcessSample calls)
    /// onto a fresh tracker with a completed phase machine and a final snapshot, ready for
    /// FinalizeFlightAsync - needed because <see cref="FlightLifecycleService.ActiveFlightTracker.Machine"/>
    /// is init-only and can't simply be swapped on the original tracker once samples have been
    /// driven through it with an in-progress machine.
    /// </summary>
    private static FlightLifecycleService.ActiveFlightTracker Finalized(FlightLifecycleService.ActiveFlightTracker tracker, double finalFuelKg) => new()
    {
        FlightId = tracker.FlightId,
        AirlineId = tracker.AirlineId,
        FleetAircraftId = tracker.FleetAircraftId,
        ArrivalIcao = tracker.ArrivalIcao,
        PlannedBlockMinutes = tracker.PlannedBlockMinutes,
        Machine = CompletedMachine(tracker.FlightId, tracker.PlannedBlockMinutes),
        EngineStartFuelKg = tracker.EngineStartFuelKg,
        AccumulatedBurnKg = tracker.AccumulatedBurnKg,
        FirstSampleFuelKg = tracker.FirstSampleFuelKg,
        LastFuelKg = tracker.LastFuelKg,
        LatestSnapshot = Snapshot(tracker.FlightId, finalFuelKg, tracker.PlannedBlockMinutes),
    };

    private static int StatusCodeOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static TelemetrySample MakeSample(DateTimeOffset utc, double latitudeDeg, double longitudeDeg, double totalFuelKg, bool onGround, bool engineRunning) => new(
        utc, latitudeDeg, longitudeDeg, AltitudeMslFt: onGround ? 0 : 35000, AltitudeAglFt: onGround ? 0 : 35000,
        IndicatedAirspeedKt: 0, GroundSpeedKt: 0, VerticalSpeedFpm: 0, TrueHeadingDeg: 0, MagneticHeadingDeg: 0,
        OnGround: onGround, EngineRunning: engineRunning, ParkingBrakeSet: onGround, GForce: 1.0, TouchdownNormalVelocityFps: 0,
        TotalFuelKg: totalFuelKg, AircraftTitle: "Test Aircraft", AtcModel: "Test Aircraft", AtcType: "TEST",
        SimulationRate: 1.0, IsSlewActive: false);

    private static FlightPhaseStateMachine CompletedMachine(Guid flightId, int plannedBlockMinutes) => FlightPhaseStateMachine.RestoreFrom(new[]
    {
        PhaseChangeEvent(flightId, Base, FlightPhase.Preflight, FlightPhase.TaxiOut),
        PhaseChangeEvent(flightId, Base + TimeSpan.FromMinutes(plannedBlockMinutes), FlightPhase.TaxiIn, FlightPhase.Shutdown),
    });

    private static FlightEvent PhaseChangeEvent(Guid flightId, DateTimeOffset utc, FlightPhase from, FlightPhase to) => new()
    {
        Id = Guid.NewGuid(),
        FlightId = flightId,
        Utc = utc,
        Type = FlightEventType.PhaseChange,
        PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PhaseChangePayload(from.ToString(), to.ToString(), false)),
    };

    private static LiveFlightSnapshot Snapshot(Guid flightId, double fuelRemainingKg, int plannedBlockMinutes) => new(
        flightId, FlightPhase.Shutdown.ToString(), 55.9500, -3.3725,
        AltitudeMslFt: 0, AltitudeAglFt: 0, IndicatedAirspeedKt: 0, GroundSpeedKt: 0, VerticalSpeedFpm: 0,
        FuelRemainingKg: fuelRemainingKg, ElapsedBlockMinutes: plannedBlockMinutes, PlannedBlockMinutes: plannedBlockMinutes,
        AwaitingSimReconnect: false, TimestampUtc: Base + TimeSpan.FromMinutes(plannedBlockMinutes));

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
