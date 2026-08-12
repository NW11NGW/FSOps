using System.Text.Json;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Server.Endpoints;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSOps.Server.Tests;

/// <summary>
/// J28 - "can this aircraft use this airport" needed to check length AND surface, and needed to be
/// enforced somewhere an aircraft is actually chosen. Mirrors RouteRangeValidationTests' structure:
/// route planning guides without blocking, schedule-save and flight-start are hard refusals.
/// </summary>
public class RunwaySuitabilityValidationTests
{
    private static int StatusCodeOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static JsonElement JsonOf(IResult result) => JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value!);

    private static string ErrorOf(IResult result) => JsonOf(result).GetProperty("error").GetString() ?? string.Empty;

    /// <summary>A short, grass-only strip - too short for a widebody, and soft enough to refuse a
    /// heavy aircraft even if it somehow had the length. Close enough to EGGD (test fixture, ~51.4N
    /// -2.7W) that distance/range are never the reason anything is refused in these tests.</summary>
    private static async Task SeedGrassStripAsync(RouteTestContext ctx)
    {
        var airport = new Airport
        {
            Icao = "EGXG", Iata = null, Name = "Grass Field", Municipality = "Test", Country = "GB",
            Latitude = 51.40, Longitude = -2.75, ElevationFt = 100,
            SizeCategory = AirportSizeCategory.Small, HasScheduledService = false, LongestRunwayFt = 3000,
        };
        ctx.Db.Airports.Add(airport);
        ctx.Db.Runways.Add(new Runway
        {
            Id = Guid.NewGuid(), AirportIcao = "EGXG", Designator = "09", LengthFt = 3000, WidthFt = 60,
            Surface = "GRASS", HeadingTrue = 90,
        });
        await ctx.Db.SaveChangesAsync();
    }

    private static async Task<AircraftType> AddTypeAsync(RouteTestContext ctx, string name, int minRunwayFt, double mtowTonnes)
    {
        var type = new AircraftType
        {
            Id = Guid.NewGuid(), IcaoType = name[..4].ToUpperInvariant(), Family = name[..4].ToUpperInvariant(),
            Manufacturer = "Test", Name = name, PaxCapacity = 150, RangeNm = 3000, CruiseTasKts = 450,
            FuelBurnKgPerHour = 3000, MtowTonnes = mtowTonnes, MinRunwayFt = minRunwayFt, ServiceCeilingFt = 39000,
            PurchasePrice = 60_000_000m, MonthlyLeaseRate = 400_000m, MatchPatterns = "[]",
        };
        ctx.Db.AircraftTypes.Add(type);
        await ctx.Db.SaveChangesAsync();
        return type;
    }

    private static async Task<FleetAircraft> AddAircraftAsync(
        RouteTestContext ctx, AircraftType type, string registration, bool reserved, string locationIcao = "EGGD")
    {
        var aircraft = new FleetAircraft
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, AircraftTypeId = type.Id, Registration = registration,
            Ownership = AircraftOwnership.Owned, LocationIcao = locationIcao, Status = FleetAircraftStatus.Active,
            ReservedForPlayer = reserved, CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.FleetAircraft.Add(aircraft);
        await ctx.Db.SaveChangesAsync();
        return aircraft;
    }

    private static async Task ClearFleetAsync(RouteTestContext ctx)
    {
        ctx.Db.FleetAircraft.RemoveRange(await ctx.Db.FleetAircraft.ToListAsync());
        await ctx.Db.SaveChangesAsync();
    }

    // ---- Route creation - guidance, blocking only when nothing in the fleet fits ------------

    [Fact]
    public async Task CreateAsync_WhenOnlyAnUnreservedFleetAircraftFits_CreatesTheRoute()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedGrassStripAsync(ctx);
        await ClearFleetAsync(ctx);
        var heavy = await AddTypeAsync(ctx, "Heavy test type", minRunwayFt: 6000, mtowTonnes: 250);
        var light = await AddTypeAsync(ctx, "Light test type", minRunwayFt: 2500, mtowTonnes: 20);
        await AddAircraftAsync(ctx, heavy, "G-HEAVY", reserved: true);
        await AddAircraftAsync(ctx, light, "G-LIGHT", reserved: false);

        var result = await RouteEndpoints.CreateAsync(
            new CreateRouteRequest("EGGD", "EGXG", null, null, null), ctx.Db, ctx.CurrentUser,
            EconomyConfigCatalog.Default(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(result));
    }

    [Fact]
    public async Task CreateAsync_WhenNothingInTheFleetFits_IsRefused()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedGrassStripAsync(ctx);
        await ClearFleetAsync(ctx);
        var heavy = await AddTypeAsync(ctx, "Heavy test type", minRunwayFt: 6000, mtowTonnes: 250);
        await AddAircraftAsync(ctx, heavy, "G-HEAVY", reserved: true);

        var result = await RouteEndpoints.CreateAsync(
            new CreateRouteRequest("EGGD", "EGXG", null, null, null), ctx.Db, ctx.CurrentUser,
            EconomyConfigCatalog.Default(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        Assert.Contains("too short", ErrorOf(result));
        Assert.Empty(await ctx.Db.Routes.ToListAsync());
    }

    [Fact]
    public async Task PreviewAsync_WhenNothingInTheFleetFits_Blocks_ButStillReturnsAPreview()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedGrassStripAsync(ctx);
        await ClearFleetAsync(ctx);
        var heavy = await AddTypeAsync(ctx, "Heavy test type", minRunwayFt: 6000, mtowTonnes: 250);
        await AddAircraftAsync(ctx, heavy, "G-HEAVY", reserved: true);

        var previewMethod = typeof(RouteEndpoints).GetMethod(
            "PreviewAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var task = (Task<IResult>)previewMethod.Invoke(null, new object?[]
        {
            new RoutePreviewRequest("EGGD", "EGXG", null, null), ctx.Db, ctx.CurrentUser,
            EconomyConfigCatalog.Default(), CancellationToken.None,
        })!;
        var result = await task;

        // Guidance only - a preview must never fail outright, even when the runway verdict blocks.
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));
        var runway = JsonOf(result).GetProperty("validation").GetProperty("runway");
        Assert.Equal("BeyondFleet", runway.GetProperty("verdict").GetString());
        Assert.True(runway.GetProperty("blocking").GetBoolean());
    }

    // ---- Flight start - a hard refusal ------------------------------------------------------

    [Fact]
    public async Task StartAsync_HeavyAircraftOnAGrassStrip_IsRefused_EvenWhenTheClientAsksForItDirectly()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedGrassStripAsync(ctx);
        await ClearFleetAsync(ctx);
        var heavy = await AddTypeAsync(ctx, "Heavy test type", minRunwayFt: 2000, mtowTonnes: 250);
        var aircraft = await AddAircraftAsync(ctx, heavy, "G-HEAVY", reserved: true);
        var route = new Route
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, DepartureIcao = "EGGD", ArrivalIcao = "EGXG",
            DistanceNm = 5, BaseFare = 100m, IsActive = true, CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.Routes.Add(route);
        ctx.Db.Pilots.Add(new Pilot
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Name = "You", IsPlayer = true,
            MonthlySalary = 9_000m, SkillRating = 50, CreatedUtc = DateTimeOffset.UtcNow,
        });
        await ctx.Db.SaveChangesAsync();

        var telemetry = new SimTelemetryService(new NoOpSimSource(), new NoOpHubContext(), NullLogger<SimTelemetryService>.Instance);
        var lifecycle = new FlightLifecycleService(
            null!, telemetry, new NoOpHubContext(), EconomyConfigCatalog.Default(), null, NullLogger<FlightLifecycleService>.Instance);

        var result = await FlightEndpoints.StartAsync(
            new StartFlightRequest(route.Id, aircraft.Id), ctx.Db, ctx.CurrentUser, lifecycle, telemetry,
            EconomyConfigCatalog.Default(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        Assert.Contains("too heavy", ErrorOf(result));
        Assert.Empty(await ctx.Db.Flights.ToListAsync());
    }

    [Fact]
    public async Task StartAsync_AircraftTooLongForTheStrip_IsRefusedWithALengthReason()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedGrassStripAsync(ctx);
        await ClearFleetAsync(ctx);
        var big = await AddTypeAsync(ctx, "Big-field test type", minRunwayFt: 6000, mtowTonnes: 20);
        var aircraft = await AddAircraftAsync(ctx, big, "G-BIGF", reserved: true);
        var route = new Route
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, DepartureIcao = "EGGD", ArrivalIcao = "EGXG",
            DistanceNm = 5, BaseFare = 100m, IsActive = true, CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.Routes.Add(route);
        ctx.Db.Pilots.Add(new Pilot
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Name = "You", IsPlayer = true,
            MonthlySalary = 9_000m, SkillRating = 50, CreatedUtc = DateTimeOffset.UtcNow,
        });
        await ctx.Db.SaveChangesAsync();

        var telemetry = new SimTelemetryService(new NoOpSimSource(), new NoOpHubContext(), NullLogger<SimTelemetryService>.Instance);
        var lifecycle = new FlightLifecycleService(
            null!, telemetry, new NoOpHubContext(), EconomyConfigCatalog.Default(), null, NullLogger<FlightLifecycleService>.Instance);

        var result = await FlightEndpoints.StartAsync(
            new StartFlightRequest(route.Id, aircraft.Id), ctx.Db, ctx.CurrentUser, lifecycle, telemetry,
            EconomyConfigCatalog.Default(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        Assert.Contains("short of the 6,000 ft", ErrorOf(result));
        Assert.Empty(await ctx.Db.Flights.ToListAsync());
    }

    // ---- Scheduling a virtual pilot's leg - a hard refusal ----------------------------------

    [Fact]
    public async Task SaveScheduleAsync_HeavyAircraftOnAGrassLeg_IsRefusedWithOnePlainReason()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedGrassStripAsync(ctx);
        await ClearFleetAsync(ctx);
        var catalog = EconomyConfigCatalog.Default();
        var heavy = await AddTypeAsync(ctx, "Heavy test type", minRunwayFt: 2000, mtowTonnes: 250);
        var aircraft = await AddAircraftAsync(ctx, heavy, "G-HEAVY", reserved: false);

        var outbound = new Route
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, DepartureIcao = "EGGD", ArrivalIcao = "EGXG",
            DistanceNm = 5, BaseFare = 100m, IsActive = true, CreatedUtc = DateTimeOffset.UtcNow,
        };
        var inbound = new Route
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, DepartureIcao = "EGXG", ArrivalIcao = "EGGD",
            DistanceNm = 5, BaseFare = 100m, IsActive = true, CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.Routes.AddRange(outbound, inbound);
        await ctx.Db.SaveChangesAsync();

        var hireResult = await PilotEndpoints.HireAsync(new HirePilotRequest("Test FO"), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(hireResult));
        var pilot = await ctx.Db.Pilots.SingleAsync(p => p.AirlineId == ctx.Airline.Id && !p.IsPlayer);

        var request = new SaveScheduleRequest(new[]
        {
            new DutyDayRequest(0, aircraft.Id, new[]
            {
                new DutyLegRequest("06:00:00", outbound.Id),
                new DutyLegRequest("07:00:00", inbound.Id),
            }),
        });

        var result = await PilotEndpoints.SaveScheduleAsync(pilot.Id, request, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        var conflicts = JsonOf(result).GetProperty("conflicts").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.Contains(conflicts, c => c.Contains("too soft for G-HEAVY"));
        Assert.Empty(await ctx.Db.PilotScheduleEntries.ToListAsync());
    }
}
