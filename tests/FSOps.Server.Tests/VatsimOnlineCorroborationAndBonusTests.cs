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
/// G8 (recording whether a flight was corroborated online) and G12 (the resulting bonus), driven
/// directly through <see cref="FlightLifecycleService.FinalizeFlightAsync"/> with a synthetic
/// <see cref="FlightLifecycleService.ActiveFlightTracker"/> whose VATSIM fields are set by hand -
/// exactly the pattern <c>FlightLedgerPostingTests</c> already uses to test finalize without
/// driving a full telemetry sample loop. The corroboration checks themselves (matching logic,
/// distance, controller filtering) are covered separately by
/// <c>VatsimFlightCorroborationServiceTests</c> - this file is about what FinalizeFlightAsync does
/// with the tracker's already-accumulated tallies.
/// </summary>
public class VatsimOnlineCorroborationAndBonusTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FinalizeFlightAsync_NeverCorroborated_LeavesEveryVatsimFieldNull()
    {
        // No CID was ever resolved (VatsimCid stays null, VatsimChecksTotal stays 0) - the default
        // shape of a tracker for a flight where the feature was never engaged at all.
        using var ctx = await RouteTestContext.CreateAsync();
        var route = await SeedRouteAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var (lifecycle, _) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);
        var flight = await SeedInProgressFlightAsync(ctx, route);
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();

        var tracker = BaseTracker(flight.Id, ctx.Airline.Id, fleetAircraft.Id, route.ArrivalIcao, flight.PlannedBlockMinutes);
        await lifecycle.FinalizeFlightAsync(tracker);

        var updated = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
        Assert.Null(updated.VatsimOnline);
        Assert.Null(updated.VatsimOnlineFraction);
        Assert.Null(updated.VatsimCallsign);
        Assert.Null(updated.VatsimControllersWorked);

        var ledgerLines = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync();
        Assert.DoesNotContain(ledgerLines, l => l.Category == LedgerCategory.VatsimOnlineBonus);
    }

    [Fact]
    public async Task FinalizeFlightAsync_CheckedButNeverMatched_RecordsFalseNotNull()
    {
        // A CID was configured and checks ran, but the CID was never seen near FSOps' own
        // telemetry - "we checked and it was offline" is a different, more informative fact than
        // "we never checked", and must not collapse to the same null.
        using var ctx = await RouteTestContext.CreateAsync();
        var route = await SeedRouteAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var (lifecycle, _) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);
        var flight = await SeedInProgressFlightAsync(ctx, route);
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();

        var tracker = BaseTracker(flight.Id, ctx.Airline.Id, fleetAircraft.Id, route.ArrivalIcao, flight.PlannedBlockMinutes);
        tracker.VatsimCid = 123456;
        tracker.VatsimChecksTotal = 3;
        tracker.VatsimChecksMatched = 0;

        await lifecycle.FinalizeFlightAsync(tracker);

        var updated = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
        Assert.False(updated.VatsimOnline);
        Assert.Equal(0.0, updated.VatsimOnlineFraction);
        Assert.Null(updated.VatsimCallsign);

        var ledgerLines = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync();
        Assert.DoesNotContain(ledgerLines, l => l.Category == LedgerCategory.VatsimOnlineBonus);
    }

    [Fact]
    public async Task FinalizeFlightAsync_QualifyingCorroboration_RecordsOutcomeAndPostsModestBonus()
    {
        // 3 of 4 checks matched (75%), comfortably above the default 50% minimum - qualifies for
        // the G12 bonus.
        using var ctx = await RouteTestContext.CreateAsync();
        var route = await SeedRouteAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var (lifecycle, _) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);
        var flight = await SeedInProgressFlightAsync(ctx, route);
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();

        var tracker = BaseTracker(flight.Id, ctx.Airline.Id, fleetAircraft.Id, route.ArrivalIcao, flight.PlannedBlockMinutes);
        tracker.VatsimCid = 123456;
        tracker.VatsimChecksTotal = 4;
        tracker.VatsimChecksMatched = 3;
        tracker.VatsimLastCallsign = "BAW123";
        tracker.VatsimControllersWorked.Add("EGPH_TWR");
        tracker.VatsimControllersWorked.Add("EGGD_GND");

        var airlineBefore = await ctx.Db.Airlines.AsNoTracking().SingleAsync(a => a.Id == ctx.Airline.Id);

        await lifecycle.FinalizeFlightAsync(tracker);

        var updated = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
        Assert.True(updated.VatsimOnline);
        Assert.Equal(0.75, updated.VatsimOnlineFraction);
        Assert.Equal("BAW123", updated.VatsimCallsign);
        Assert.Equal("EGGD_GND, EGPH_TWR", updated.VatsimControllersWorked);

        var ledgerLines = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync();
        var ticketRevenue = ledgerLines.Single(l => l.Category == LedgerCategory.TicketRevenue).Amount;
        var bonusLine = Assert.Single(ledgerLines, l => l.Category == LedgerCategory.VatsimOnlineBonus);

        // The bonus is a fraction of THIS sector's own ticket revenue, per EconomyConfig's
        // VatsimOnlineBonus.RevenueUpliftFraction (0.03 by default) - never a flat figure.
        var expectedBonus = Math.Round(ticketRevenue * (decimal)economyConfigCatalog.Get(AirlinePlaystyle.Casual).VatsimOnlineBonus.RevenueUpliftFraction, 2, MidpointRounding.AwayFromZero);
        Assert.Equal(expectedBonus, bonusLine.Amount);
        Assert.True(bonusLine.Amount > 0);

        // Flight.Revenue folds the bonus in, so the report card's headline figure already includes
        // it rather than needing the client to sum ledger lines to see the true total.
        Assert.Equal(ticketRevenue + bonusLine.Amount, updated.Revenue);

        // Reputation moved further than an otherwise-identical non-online flight would have (see
        // the modest-uplift test below for the direct comparison) - checked here just as "moved
        // upward from baseline at all", since PostCompletedFlight's own ordinary step already does
        // that on its own.
        var airlineAfter = await ctx.Db.Airlines.AsNoTracking().SingleAsync(a => a.Id == ctx.Airline.Id);
        Assert.True(airlineAfter.ReputationScore > airlineBefore.ReputationScore);
    }

    [Fact]
    public async Task FinalizeFlightAsync_QualifyingOnlineFlight_EarnsMoreReputationThanAnIdenticalOfflineFlight()
    {
        // Same flight shape, same landing, same everything except VATSIM corroboration - isolates
        // the G12 reputation uplift from the ordinary on-time/landing credit every completed sector
        // already earns.
        using var offlineCtx = await RouteTestContext.CreateAsync();
        var offlineRoute = await SeedRouteAsync(offlineCtx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var (offlineLifecycle, _) = CreateLifecycleAndTelemetry(offlineCtx, economyConfigCatalog);
        var offlineFlight = await SeedInProgressFlightAsync(offlineCtx, offlineRoute);
        var offlineAircraft = await offlineCtx.Db.FleetAircraft.FirstAsync();
        var offlineTracker = BaseTracker(offlineFlight.Id, offlineCtx.Airline.Id, offlineAircraft.Id, offlineRoute.ArrivalIcao, offlineFlight.PlannedBlockMinutes);
        await offlineLifecycle.FinalizeFlightAsync(offlineTracker);
        var offlineAirlineAfter = await offlineCtx.Db.Airlines.AsNoTracking().SingleAsync(a => a.Id == offlineCtx.Airline.Id);

        using var onlineCtx = await RouteTestContext.CreateAsync();
        var onlineRoute = await SeedRouteAsync(onlineCtx);
        var (onlineLifecycle, _) = CreateLifecycleAndTelemetry(onlineCtx, economyConfigCatalog);
        var onlineFlight = await SeedInProgressFlightAsync(onlineCtx, onlineRoute);
        var onlineAircraft = await onlineCtx.Db.FleetAircraft.FirstAsync();
        var onlineTracker = BaseTracker(onlineFlight.Id, onlineCtx.Airline.Id, onlineAircraft.Id, onlineRoute.ArrivalIcao, onlineFlight.PlannedBlockMinutes);
        onlineTracker.VatsimCid = 123456;
        onlineTracker.VatsimChecksTotal = 2;
        onlineTracker.VatsimChecksMatched = 2;
        onlineTracker.VatsimLastCallsign = "BAW123";
        await onlineLifecycle.FinalizeFlightAsync(onlineTracker);
        var onlineAirlineAfter = await onlineCtx.Db.Airlines.AsNoTracking().SingleAsync(a => a.Id == onlineCtx.Airline.Id);

        Assert.True(onlineAirlineAfter.ReputationScore > offlineAirlineAfter.ReputationScore);

        // "Modest" is a real requirement, not just a description - the online bonus's own step
        // must not swamp the ordinary sector's ~0.69-point best-case reputation step.
        var delta = onlineAirlineAfter.ReputationScore - offlineAirlineAfter.ReputationScore;
        Assert.True(delta < 0.3, $"Expected the VATSIM reputation bonus to be modest (<0.3 points), was {delta}.");
    }

    [Fact]
    public async Task FinalizeFlightAsync_CorroborationBelowMinimumFraction_RecordsOnlineButPostsNoBonus()
    {
        // Matched once out of four checks (25%) - below the default 50% minimum. VatsimOnline is
        // still true (it WAS corroborated at least once), but briefly touching the network doesn't
        // earn the reward meant for genuinely flying the sector online.
        using var ctx = await RouteTestContext.CreateAsync();
        var route = await SeedRouteAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var (lifecycle, _) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog);
        var flight = await SeedInProgressFlightAsync(ctx, route);
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();

        var tracker = BaseTracker(flight.Id, ctx.Airline.Id, fleetAircraft.Id, route.ArrivalIcao, flight.PlannedBlockMinutes);
        tracker.VatsimCid = 123456;
        tracker.VatsimChecksTotal = 4;
        tracker.VatsimChecksMatched = 1;
        tracker.VatsimLastCallsign = "BAW123";

        await lifecycle.FinalizeFlightAsync(tracker);

        var updated = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
        Assert.True(updated.VatsimOnline);
        Assert.Equal(0.25, updated.VatsimOnlineFraction);

        var ledgerLines = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync();
        Assert.DoesNotContain(ledgerLines, l => l.Category == LedgerCategory.VatsimOnlineBonus);
    }

    // ---------------------------------------------------------------------------------------
    // Shared fixtures - mirrors FlightLedgerPostingTests' own helpers.
    // ---------------------------------------------------------------------------------------

    private static FlightLifecycleService.ActiveFlightTracker BaseTracker(
        Guid flightId, Guid airlineId, Guid fleetAircraftId, string arrivalIcao, int plannedBlockMinutes) => new()
    {
        FlightId = flightId,
        AirlineId = airlineId,
        FleetAircraftId = fleetAircraftId,
        ArrivalIcao = arrivalIcao,
        PlannedBlockMinutes = plannedBlockMinutes,
        Machine = CompletedMachine(flightId),
        LatestSnapshot = Snapshot(flightId, latitudeDeg: 55.9500, longitudeDeg: -3.3725),
    };

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

    private static FlightPhaseStateMachine CompletedMachine(Guid flightId) => FlightPhaseStateMachine.RestoreFrom(new[]
    {
        PhaseChangeEvent(flightId, 60, FlightPhase.Preflight, FlightPhase.TaxiOut),
        PhaseChangeEvent(flightId, 420, FlightPhase.TakeoffRoll, FlightPhase.Climb),
        PhaseChangeEvent(flightId, 5270, FlightPhase.Descent, FlightPhase.Approach),
        PhaseChangeEvent(flightId, 5398, FlightPhase.Approach, FlightPhase.Landed),
        PhaseChangeEvent(flightId, 5442, FlightPhase.Landed, FlightPhase.TaxiIn),
        PhaseChangeEvent(flightId, 5550, FlightPhase.TaxiIn, FlightPhase.Shutdown),
    });

    /// <summary>Seeds a route and the pilot FlightLifecycleService's finalize path needs - neither
    /// is part of RouteTestContext's own baseline seed.</summary>
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

    private static async Task<Flight> SeedInProgressFlightAsync(RouteTestContext ctx, Route route)
    {
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();
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
            PaxBooked = 150,
            FuelPlannedKg = 3000,
            TitleFlown = "Test Aircraft",
            CreatedUtc = Base,
        };
        ctx.Db.Flights.Add(flight);
        await ctx.Db.SaveChangesAsync();
        return flight;
    }

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
