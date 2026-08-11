using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Planning;
using FSOps.Server.Endpoints;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSOps.Server.Tests;

/// <summary>
/// GET /flights/plan-import (the SimBrief import hand-off) and StartAsync's enrichment of a
/// flight's planned figures. Every test here has no SimBriefPilotId configured (the default - no
/// UserSettings row is ever seeded by RouteTestContext), which SimBriefFlightPlanProvider itself
/// proves (SimBriefFlightPlanProviderTests) short-circuits before any HTTP call is made - so these
/// tests exercise the built-in fallback path exclusively and never reach simbrief.com, without
/// needing to fake FlightEndpoints' own HttpClient.
/// </summary>
public class FlightPlanImportEndpointTests
{
    private static int StatusCodeOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static (FlightLifecycleService Lifecycle, SimTelemetryService Telemetry) CreateLifecycleAndTelemetry()
    {
        var telemetry = new SimTelemetryService(new NoOpSimSource(), new NoOpHubContext(), NullLogger<SimTelemetryService>.Instance);
        var lifecycle = new FlightLifecycleService(null!, telemetry, new NoOpHubContext(), EconomyConfigCatalog.Default(), NullLogger<FlightLifecycleService>.Instance);
        return (lifecycle, telemetry);
    }

    private static async Task<Route> AddRouteAsync(RouteTestContext ctx, string departure, string arrival)
    {
        var route = new Route
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, DepartureIcao = departure, ArrivalIcao = arrival,
            DistanceNm = 275.2, BaseFare = 89.00m, IsActive = true, CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.Routes.Add(route);
        await ctx.Db.SaveChangesAsync();
        return route;
    }

    private static async Task SeedPlayerPilotAsync(RouteTestContext ctx)
    {
        ctx.Db.Pilots.Add(new Pilot
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Name = "You", IsPlayer = true,
            MonthlySalary = 9_000m, SkillRating = 50, CreatedUtc = DateTimeOffset.UtcNow,
        });
        await ctx.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task PlanImportAsync_UnknownRoute_ReturnsNotFound()
    {
        using var ctx = await RouteTestContext.CreateAsync();

        var result = await FlightEndpoints.PlanImportAsync(Guid.NewGuid(), null, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, StatusCodeOf(result));
    }

    [Fact]
    public async Task PlanImportAsync_NoPilotIdConfigured_FallsBackToBuiltInPlan()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = await AddRouteAsync(ctx, "EGGD", "EGPH");

        var result = await FlightEndpoints.PlanImportAsync(route.Id, null, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));
        var body = System.Text.Json.JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value!);
        Assert.True(body.GetProperty("available").GetBoolean());
        Assert.Equal("FSOps", body.GetProperty("source").GetString());
        Assert.False(body.GetProperty("fromSimBrief").GetBoolean());
        Assert.True(body.GetProperty("blockFuelKg").GetDouble() > 0);
        Assert.True(body.GetProperty("cruiseAltitudeFt").GetInt32() > 0);
        Assert.True(body.GetProperty("blockTimeMinutes").GetInt32() > 0);
        Assert.True(body.GetProperty("routeString").ValueKind is System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task PlanImportAsync_MatchesRoutePreviewCalculator_WhenNoSimBriefPlanAvailable()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = await AddRouteAsync(ctx, "EGGD", "EGPH");
        var departure = await ctx.Db.Airports.SingleAsync(a => a.Icao == "EGGD");
        var arrival = await ctx.Db.Airports.SingleAsync(a => a.Icao == "EGPH");
        var economyConfig = EconomyConfigCatalog.Default().Get(ctx.Airline.Playstyle);
        var expected = RoutePreviewCalculator.Calculate(economyConfig, departure, arrival, ctx.AircraftType, ctx.Airline.StrategyProfile);

        var result = await FlightEndpoints.PlanImportAsync(route.Id, null, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);

        var body = System.Text.Json.JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value!);
        Assert.Equal(expected.FuelBreakdown.TotalFuelKg, body.GetProperty("blockFuelKg").GetDouble());
        Assert.Equal(expected.CruiseAltitudeFt, body.GetProperty("cruiseAltitudeFt").GetInt32());
        Assert.Equal(expected.BlockTimeBreakdown.TotalMinutes, body.GetProperty("blockTimeMinutes").GetInt32());
    }

    [Fact]
    public async Task StartAsync_NoPilotIdConfigured_UsesBuiltInPlanAndSaysSo()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedPlayerPilotAsync(ctx);
        var aircraft = await ctx.Db.FleetAircraft.SingleAsync();
        aircraft.ReservedForPlayer = true;
        await ctx.Db.SaveChangesAsync();
        var route = await AddRouteAsync(ctx, "EGGD", "EGPH");
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry();

        var result = await FlightEndpoints.StartAsync(
            new StartFlightRequest(route.Id, aircraft.Id), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, EconomyConfigCatalog.Default(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(result));
        var body = System.Text.Json.JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value!);
        Assert.Equal("FSOps", body.GetProperty("planSource").GetString());

        // Never touches the fuel-uplift/reconciliation math, and the planned figures still match
        // exactly what RoutePreviewCalculator alone would have produced before this feature
        // existed - the enrichment is purely additive when nothing overrides it.
        var departure = await ctx.Db.Airports.SingleAsync(a => a.Icao == "EGGD");
        var arrival = await ctx.Db.Airports.SingleAsync(a => a.Icao == "EGPH");
        var economyConfig = EconomyConfigCatalog.Default().Get(ctx.Airline.Playstyle);
        var expected = RoutePreviewCalculator.Calculate(economyConfig, departure, arrival, ctx.AircraftType, ctx.Airline.StrategyProfile);
        var flight = await ctx.Db.Flights.SingleAsync();
        Assert.Equal(expected.BlockTimeBreakdown.TotalMinutes, flight.PlannedBlockMinutes);
    }

    // Regression for a real defect caught by manually exercising the live endpoint (see PR
    // notes): the built-in fallback plan used to silently discard SimBrief's own failure reason,
    // so a player with an unmatched/unknown Pilot ID saw "Using the built-in plan" with no
    // explanation at all. MergeFallback is the pure two-outcome merge FlightEndpoints.
    // ResolveFlightPlanAsync uses, exercised here without any HTTP call.
    [Fact]
    public void MergeFallback_PrimarySucceeds_ReturnsPrimaryUnchanged()
    {
        var plan = new FlightPlan(1000, 30000, 60, "DCT");
        var primary = FlightPlanOutcome.Succeeded("SimBrief", plan, "Plan imported from SimBrief.");
        var fallback = FlightPlanOutcome.Succeeded("FSOps", plan);

        var result = FlightEndpoints.MergeFallback(primary, fallback);

        Assert.Same(primary, result);
    }

    [Fact]
    public void MergeFallback_PrimaryFails_UsesFallbackPlanButKeepsPrimarysReason()
    {
        var fallbackPlan = new FlightPlan(2000, 36000, 90, null);
        var primary = FlightPlanOutcome.Failed("SimBrief", "SimBrief has no flight plan for this Pilot ID - using the built-in plan instead.");
        var fallback = FlightPlanOutcome.Succeeded("FSOps", fallbackPlan);

        var result = FlightEndpoints.MergeFallback(primary, fallback);

        Assert.True(result.Success);
        Assert.Equal("FSOps", result.ProviderName);
        Assert.Equal(fallbackPlan, result.Plan);
        Assert.Equal(primary.Message, result.Message);
    }
}
