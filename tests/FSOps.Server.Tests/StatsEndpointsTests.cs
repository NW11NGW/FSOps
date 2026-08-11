using System.Text.Json;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Server.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Tests;

/// <summary>
/// Backs the Stats page - see StatsEndpoints' own class doc. Covers the day-bucketed on-time/load
/// factor trend (including the SimRateElevated exclusion, so this page can never disagree with the
/// reputation card over the same window), fleet hours-flown/idle/check-proximity, and the pilot
/// logbook. Drives StatsEndpoints' handlers directly against an isolated in-memory
/// RouteTestContext, same convention as FinanceEndpointsTests. Results are JSON round-tripped into
/// probe records (rather than reflected on directly) because the handlers return anonymous types,
/// same reasoning as MaintenanceTriggerTests.OkValueOf.
/// </summary>
public class StatsEndpointsTests
{
    private static T OkValueOf<T>(IResult result)
    {
        var value = ((IValueHttpResult)result).Value;
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private sealed record PerformanceResponseProbe(int PeriodDays, List<PerformancePointProbe> Points);

    private sealed record PerformancePointProbe(string DateUtc, int SectorsFlown, double? OnTimePercent, double? LoadFactorPercent);

    private sealed record FleetResponseProbe(int PeriodDays, List<FleetAircraftProbe> Aircraft);

    private sealed record FleetAircraftProbe(
        Guid FleetAircraftId, string Registration, string AircraftTypeName, string Status, int SectorsFlown,
        double HoursFlownInPeriod, double IdleHoursInPeriod, double UtilisationPercent,
        double HoursSinceACheck, double HoursSinceCCheck, double HoursToNextACheck, double HoursToNextCCheck, double ConditionPercent);

    private sealed record PilotsResponseProbe(int PeriodDays, List<PilotLogbookProbe> Pilots);

    private sealed record PilotLogbookProbe(Guid PilotId, string Name, bool IsPlayer, int SectorsFlown, double HoursFlown, double? OnTimePercent, double? AverageLandingFpm);

    private static readonly DateTimeOffset Base = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

    private static Route SeedRoute(RouteTestContext ctx)
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
            CreatedUtc = Base,
        };
        ctx.Db.Routes.Add(route);
        return route;
    }

    private static Pilot SeedPilot(RouteTestContext ctx, string name, bool isPlayer)
    {
        var pilot = new Pilot
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Name = name,
            IsPlayer = isPlayer,
            MonthlySalary = 9000m,
            SkillRating = 50,
            CreatedUtc = Base,
        };
        ctx.Db.Pilots.Add(pilot);
        return pilot;
    }

    /// <summary>Builds a completed Flight row directly (no live tracker needed - these tests only
    /// exercise StatsEndpoints' own aggregation, same shortcut FinanceEndpointsTests takes for the
    /// ledger). arrivalDelayMinutes offsets In from the planned arrival to control on-time/late.</summary>
    private static Flight SeedCompletedFlight(
        RouteTestContext ctx, Route route, Guid fleetAircraftId, Guid pilotId, DateTimeOffset plannedDepartureUtc,
        int plannedBlockMinutes, double arrivalDelayMinutes, int paxFlown, bool simRateElevated = false, double? landingFpm = null)
    {
        var outUtc = plannedDepartureUtc;
        var inUtc = plannedDepartureUtc.AddMinutes(plannedBlockMinutes + arrivalDelayMinutes);

        var flight = new Flight
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            RouteId = route.Id,
            FleetAircraftId = fleetAircraftId,
            PilotId = pilotId,
            Status = FlightStatus.Completed,
            PlannedDepartureUtc = plannedDepartureUtc,
            PlannedBlockMinutes = plannedBlockMinutes,
            OutUtc = outUtc,
            InUtc = inUtc,
            PaxBooked = paxFlown,
            PaxFlown = paxFlown,
            RevenuePosted = true,
            SimRateElevated = simRateElevated,
            LandingFpmFirst = landingFpm,
            TitleFlown = "Test Aircraft",
            CreatedUtc = plannedDepartureUtc,
        };
        ctx.Db.Flights.Add(flight);
        return flight;
    }

    // ---------- Performance (on-time / load factor) ----------

    [Fact]
    public async Task Performance_NoCompletedFlights_ReturnsEmptyPoints()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();

        var result = await StatsEndpoints.PerformanceAsync(30, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        var body = OkValueOf<PerformanceResponseProbe>(result);
        Assert.Empty(body.Points);
    }

    [Fact]
    public async Task Performance_BucketsByDay_ComputesOnTimePercentAndLoadFactor()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = SeedRoute(ctx);
        var pilot = SeedPilot(ctx, "Player", isPlayer: true);
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();
        var day = DateTimeOffset.UtcNow.AddDays(-1);

        // On-time (2 minutes late, inside the default 5-minute tolerance), 90 of 180 seats = 50%.
        SeedCompletedFlight(ctx, route, fleetAircraft.Id, pilot.Id, day, plannedBlockMinutes: 90, arrivalDelayMinutes: 2, paxFlown: 90);
        // Late (60 minutes late), 180 of 180 seats = 100%.
        SeedCompletedFlight(ctx, route, fleetAircraft.Id, pilot.Id, day.AddHours(1), plannedBlockMinutes: 90, arrivalDelayMinutes: 60, paxFlown: 180);
        await ctx.Db.SaveChangesAsync();

        var result = await StatsEndpoints.PerformanceAsync(30, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);

        var body = OkValueOf<PerformanceResponseProbe>(result);
        var point = Assert.Single(body.Points);

        Assert.Equal(2, point.SectorsFlown);
        Assert.Equal(50.0, point.OnTimePercent);
        Assert.Equal(75.0, point.LoadFactorPercent);
    }

    [Fact]
    public async Task Performance_SimRateElevatedFlight_CountsAsSector_ButExcludedFromOnTime()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = SeedRoute(ctx);
        var pilot = SeedPilot(ctx, "Player", isPlayer: true);
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();
        var day = DateTimeOffset.UtcNow.AddDays(-1);

        // The only flight of the day - and it can't honestly measure on-time, so the day must show
        // a sector but a null (never a fabricated) on-time percent.
        SeedCompletedFlight(ctx, route, fleetAircraft.Id, pilot.Id, day, plannedBlockMinutes: 90, arrivalDelayMinutes: 0, paxFlown: 180, simRateElevated: true);
        await ctx.Db.SaveChangesAsync();

        var result = await StatsEndpoints.PerformanceAsync(30, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);

        var body = OkValueOf<PerformanceResponseProbe>(result);
        var point = Assert.Single(body.Points);
        Assert.Equal(1, point.SectorsFlown);
        Assert.Null(point.OnTimePercent);
        // Load factor is a telemetry-independent figure (pax vs seats), so it is still measured.
        Assert.Equal(100.0, point.LoadFactorPercent);
    }

    // ---------- Fleet utilisation ----------

    [Fact]
    public async Task Fleet_ComputesHoursFlownIdleTimeAndCheckProximity()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = SeedRoute(ctx);
        var pilot = SeedPilot(ctx, "Player", isPlayer: true);
        var catalog = EconomyConfigCatalog.Default();
        var economyConfig = catalog.Get(ctx.Airline.Playstyle);

        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();
        fleetAircraft.HoursSinceACheck = 100;
        fleetAircraft.HoursSinceCCheck = 100;
        await ctx.Db.SaveChangesAsync();

        // Exactly 3 hours of block time in the window.
        SeedCompletedFlight(ctx, route, fleetAircraft.Id, pilot.Id, DateTimeOffset.UtcNow.AddDays(-1), plannedBlockMinutes: 180, arrivalDelayMinutes: 0, paxFlown: 100);
        await ctx.Db.SaveChangesAsync();

        var result = await StatsEndpoints.FleetAsync(1, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        var body = OkValueOf<FleetResponseProbe>(result);
        var row = Assert.Single(body.Aircraft);

        Assert.Equal(3.0, row.HoursFlownInPeriod, precision: 3);
        // A 1-day window is 24 hours; 3 flown leaves 21 idle.
        Assert.Equal(21.0, row.IdleHoursInPeriod, precision: 3);
        Assert.Equal(economyConfig.Maintenance.ACheckIntervalHours - 100, row.HoursToNextACheck, precision: 3);
        Assert.Equal(economyConfig.Maintenance.CCheckIntervalHours - 100, row.HoursToNextCCheck, precision: 3);
    }

    // ---------- Pilot logbook ----------

    [Fact]
    public async Task Pilots_ComputesSectorsHoursOnTimeAndLandingFpm_PerPilot_IncludingThePlayer()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = SeedRoute(ctx);
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();
        var player = SeedPilot(ctx, "You", isPlayer: true);
        var virtualPilot = SeedPilot(ctx, "Robin Hayes", isPlayer: false);
        var day = DateTimeOffset.UtcNow.AddDays(-1);

        SeedCompletedFlight(ctx, route, fleetAircraft.Id, player.Id, day, plannedBlockMinutes: 120, arrivalDelayMinutes: 1, paxFlown: 150, landingFpm: -180);
        SeedCompletedFlight(ctx, route, fleetAircraft.Id, virtualPilot.Id, day.AddHours(2), plannedBlockMinutes: 60, arrivalDelayMinutes: 30, paxFlown: 150, landingFpm: -240);
        SeedCompletedFlight(ctx, route, fleetAircraft.Id, virtualPilot.Id, day.AddHours(4), plannedBlockMinutes: 60, arrivalDelayMinutes: 0, paxFlown: 150, landingFpm: -200);
        await ctx.Db.SaveChangesAsync();

        var result = await StatsEndpoints.PilotsAsync(30, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);

        var body = OkValueOf<PilotsResponseProbe>(result);
        Assert.Equal(2, body.Pilots.Count);

        var virtualRow = body.Pilots.Single(p => !p.IsPlayer);
        Assert.Equal(2, virtualRow.SectorsFlown);
        // Block hours are Out-to-In elapsed time (BlockTimeCalculator, the same authority
        // MaintenancePoster.PostFlightHours accrues airframe/pilot hours from) - NOT planned block
        // minutes. SeedCompletedFlight keeps OutUtc at the planned departure and pushes InUtc later
        // by arrivalDelayMinutes, so the in-flight delay is real elapsed block time here, exactly as
        // it would be for a sector that departed on time but ran long: (60+30)/60 + (60+0)/60 = 2.5h.
        Assert.Equal(2.5, virtualRow.HoursFlown, precision: 3);
        Assert.Equal(50.0, virtualRow.OnTimePercent);
        Assert.Equal(-220.0, virtualRow.AverageLandingFpm);

        var playerRow = body.Pilots.Single(p => p.IsPlayer);
        Assert.Equal(1, playerRow.SectorsFlown);
        Assert.Equal(100.0, playerRow.OnTimePercent);
    }
}
