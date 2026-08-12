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
/// J23/J24 - range must ask the right question at every point it is asked.
/// <para>
/// The reported defect: creating a long route was refused with "This route (3761 nm) is beyond the
/// Airbus A320's practical operating range", naming one arbitrarily-chosen aircraft type as though
/// it were the whole airline. It blocked routes the player could actually fly, and named an aircraft
/// that might have nothing to do with the answer. Route creation is now guidance that blocks ONLY
/// when nothing in the fleet can do it; scheduling a virtual pilot's leg and starting a flight are
/// hard refusals about one specific airframe.
/// </para>
/// </summary>
public class RouteRangeValidationTests
{
    private static int StatusCodeOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static JsonElement JsonOf(IResult result) => JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value!);

    private static string ErrorOf(IResult result) => JsonOf(result).GetProperty("error").GetString() ?? string.Empty;

    /// <summary>Bristol -> JFK is ~2,900 nm: comfortably beyond the short type below (850 nm
    /// operational) and comfortably within the long one (5,100 nm), so neither branch is a boundary
    /// case that could flip on a rounding change.</summary>
    private static async Task SeedTransatlanticAirportAsync(RouteTestContext ctx)
    {
        ctx.Db.Airports.Add(new Airport
        {
            Icao = "KJFK", Iata = "JFK", Name = "John F Kennedy International", Municipality = "New York",
            Country = "US", Latitude = 40.6413, Longitude = -73.7781, ElevationFt = 13,
            SizeCategory = AirportSizeCategory.Large, HasScheduledService = true, LongestRunwayFt = 14511,
        });
        await ctx.Db.SaveChangesAsync();
    }

    private static async Task<AircraftType> AddTypeAsync(RouteTestContext ctx, string name, int rangeNm)
    {
        var type = new AircraftType
        {
            Id = Guid.NewGuid(), IcaoType = name[..4].ToUpperInvariant(), Family = name[..4].ToUpperInvariant(),
            Manufacturer = "Test", Name = name, PaxCapacity = 200, RangeNm = rangeNm, CruiseTasKts = 470,
            FuelBurnKgPerHour = 5000, MtowTonnes = 200, MinRunwayFt = 5500, ServiceCeilingFt = 41000,
            PurchasePrice = 100_000_000m, MonthlyLeaseRate = 500_000m, MatchPatterns = "[]",
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

    /// <summary>Removes the fixture's founding A320 so each test's fleet is exactly what it seeds.</summary>
    private static async Task ClearFleetAsync(RouteTestContext ctx)
    {
        ctx.Db.FleetAircraft.RemoveRange(await ctx.Db.FleetAircraft.ToListAsync());
        await ctx.Db.SaveChangesAsync();
    }

    // ---- Route creation --------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_WhenOnlyAnUnreservedFleetAircraftHasTheRange_CreatesTheRoute()
    {
        // THE reported dead end. The airline owns something that can fly this; it simply isn't
        // reserved to the player. That is not a reason to refuse to create the route.
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedTransatlanticAirportAsync(ctx);
        await ClearFleetAsync(ctx);
        var shortRange = await AddTypeAsync(ctx, "Short-range type", rangeNm: 1000);
        var longRange = await AddTypeAsync(ctx, "Long-range type", rangeNm: 6000);
        await AddAircraftAsync(ctx, shortRange, "G-SHRT", reserved: true);
        await AddAircraftAsync(ctx, longRange, "G-LONG", reserved: false);

        var result = await RouteEndpoints.CreateAsync(
            new CreateRouteRequest("EGGD", "KJFK", null, null, null), ctx.Db, ctx.CurrentUser,
            EconomyConfigCatalog.Default(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(result));
        Assert.Equal(2, await ctx.Db.Routes.CountAsync()); // there-and-back pair, as always
    }

    [Fact]
    public async Task CreateAsync_WhenNothingInTheFleetHasTheRange_IsRefused_AndPointsAtAcquiringOne()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedTransatlanticAirportAsync(ctx);
        await ClearFleetAsync(ctx);
        var shortRange = await AddTypeAsync(ctx, "Short-range type", rangeNm: 1000);
        await AddAircraftAsync(ctx, shortRange, "G-SHRT", reserved: true);
        await AddAircraftAsync(ctx, shortRange, "G-SHR2", reserved: false);

        var result = await RouteEndpoints.CreateAsync(
            new CreateRouteRequest("EGGD", "KJFK", null, null, null), ctx.Db, ctx.CurrentUser,
            EconomyConfigCatalog.Default(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        var error = ErrorOf(result);
        Assert.Contains("beyond every aircraft in your fleet", error);
        Assert.Contains("Fleet page", error);
        // Never in the name of a single type, as though it were the airline's whole capability.
        Assert.DoesNotContain("Short-range type's", error);
        Assert.Empty(await ctx.Db.Routes.ToListAsync());
    }

    [Fact]
    public async Task CreateAsync_WhenAReservedAircraftHasTheRange_CreatesTheRoute()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedTransatlanticAirportAsync(ctx);
        await ClearFleetAsync(ctx);
        var longRange = await AddTypeAsync(ctx, "Long-range type", rangeNm: 6000);
        await AddAircraftAsync(ctx, longRange, "G-LONG", reserved: true);

        var result = await RouteEndpoints.CreateAsync(
            new CreateRouteRequest("EGGD", "KJFK", null, null, null), ctx.Db, ctx.CurrentUser,
            EconomyConfigCatalog.Default(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(result));
    }

    // ---- Route preview ---------------------------------------------------------------------

    [Fact]
    public async Task PreviewAsync_WhenOnlyAnUnreservedFleetAircraftHasTheRange_GuidesWithoutBlocking()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedTransatlanticAirportAsync(ctx);
        await ClearFleetAsync(ctx);
        var shortRange = await AddTypeAsync(ctx, "Short-range type", rangeNm: 1000);
        var longRange = await AddTypeAsync(ctx, "Long-range type", rangeNm: 6000);
        await AddAircraftAsync(ctx, shortRange, "G-SHRT", reserved: true);
        await AddAircraftAsync(ctx, longRange, "G-LONG", reserved: false);

        var preview = await PreviewAsync(ctx, "EGGD", "KJFK");
        var range = preview.GetProperty("validation").GetProperty("range");

        Assert.Equal("RequiresReservation", range.GetProperty("verdict").GetString());
        Assert.False(range.GetProperty("blocking").GetBoolean());
        Assert.Equal("G-LONG", range.GetProperty("aircraftRegistration").GetString());
        Assert.Contains("reserve it", range.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task PreviewAsync_WhenAReservedAircraftHasTheRange_SaysNothingAboutRange()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedTransatlanticAirportAsync(ctx);
        await ClearFleetAsync(ctx);
        var longRange = await AddTypeAsync(ctx, "Long-range type", rangeNm: 6000);
        await AddAircraftAsync(ctx, longRange, "G-LONG", reserved: true);

        var preview = await PreviewAsync(ctx, "EGGD", "KJFK");
        var validation = preview.GetProperty("validation");

        Assert.Equal("ReservedCanFly", validation.GetProperty("range").GetProperty("verdict").GetString());
        Assert.Equal(JsonValueKind.Null, validation.GetProperty("range").GetProperty("message").ValueKind);
        Assert.DoesNotContain(
            validation.GetProperty("warnings").EnumerateArray(),
            w => w.GetString()!.Contains("range", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PreviewAsync_WhenNothingInTheFleetHasTheRange_Blocks()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedTransatlanticAirportAsync(ctx);
        await ClearFleetAsync(ctx);
        var shortRange = await AddTypeAsync(ctx, "Short-range type", rangeNm: 1000);
        await AddAircraftAsync(ctx, shortRange, "G-SHRT", reserved: true);

        var range = (await PreviewAsync(ctx, "EGGD", "KJFK")).GetProperty("validation").GetProperty("range");

        Assert.Equal("BeyondFleet", range.GetProperty("verdict").GetString());
        Assert.True(range.GetProperty("blocking").GetBoolean());
        Assert.Equal(850, range.GetProperty("operationalRangeNm").GetDouble());
    }

    private static async Task<JsonElement> PreviewAsync(RouteTestContext ctx, string departure, string arrival)
    {
        var previewMethod = typeof(RouteEndpoints).GetMethod(
            "PreviewAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var task = (Task<IResult>)previewMethod.Invoke(null, new object?[]
        {
            new RoutePreviewRequest(departure, arrival, null, null), ctx.Db, ctx.CurrentUser,
            EconomyConfigCatalog.Default(), CancellationToken.None,
        })!;
        return JsonOf(await task);
    }

    // ---- Fly screen ------------------------------------------------------------------------

    [Fact]
    public async Task OptionsAsync_AircraftThatCannotReachTheDestination_IsListedButNotFlyable()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedTransatlanticAirportAsync(ctx);
        await ClearFleetAsync(ctx);
        var shortRange = await AddTypeAsync(ctx, "Short-range type", rangeNm: 1000);
        var longRange = await AddTypeAsync(ctx, "Long-range type", rangeNm: 6000);
        var tooShort = await AddAircraftAsync(ctx, shortRange, "G-SHRT", reserved: true);
        var capable = await AddAircraftAsync(ctx, longRange, "G-LONG", reserved: true);
        ctx.Db.Routes.Add(new Route
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, DepartureIcao = "EGGD", ArrivalIcao = "KJFK",
            DistanceNm = 2900, BaseFare = 400m, IsActive = true, CreatedUtc = DateTimeOffset.UtcNow,
        });
        await ctx.Db.SaveChangesAsync();

        var result = await FlightEndpoints.OptionsAsync(ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);
        var options = JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value!);
        var aircraftOptions = options[0].GetProperty("aircraftOptions").EnumerateArray().ToList();

        // Listed, never silently dropped - the player has to be able to see why it isn't offered.
        var tooShortOption = aircraftOptions.Single(a => a.GetProperty("fleetAircraftId").GetGuid() == tooShort.Id);
        Assert.False(tooShortOption.GetProperty("isFlyable").GetBoolean());
        Assert.Contains("can't reach KJFK", tooShortOption.GetProperty("reason").GetString()!);

        var capableOption = aircraftOptions.Single(a => a.GetProperty("fleetAircraftId").GetGuid() == capable.Id);
        Assert.True(capableOption.GetProperty("isFlyable").GetBoolean());
    }

    [Fact]
    public async Task OptionsAsync_RangeIsReportedAheadOfReservation_BecauseReservingWouldNotHelp()
    {
        // The existing "only say 'not reserved' when reserving genuinely is sufficient" rule: an
        // airframe that cannot reach the destination will not become flyable by being reserved.
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedTransatlanticAirportAsync(ctx);
        await ClearFleetAsync(ctx);
        var shortRange = await AddTypeAsync(ctx, "Short-range type", rangeNm: 1000);
        await AddAircraftAsync(ctx, shortRange, "G-SHRT", reserved: false);
        ctx.Db.Routes.Add(new Route
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, DepartureIcao = "EGGD", ArrivalIcao = "KJFK",
            DistanceNm = 2900, BaseFare = 400m, IsActive = true, CreatedUtc = DateTimeOffset.UtcNow,
        });
        await ctx.Db.SaveChangesAsync();

        var result = await FlightEndpoints.OptionsAsync(ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);
        var options = JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value!);
        var reason = options[0].GetProperty("aircraftOptions")[0].GetProperty("reason").GetString()!;

        Assert.Contains("can't reach", reason);
        Assert.DoesNotContain("Not reserved", reason);
    }

    [Fact]
    public async Task StartAsync_OverRangeSector_IsRefusedEvenWhenTheClientAsksForItDirectly()
    {
        // Filtering the options list is presentation; the endpoint has to be the thing that refuses.
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedTransatlanticAirportAsync(ctx);
        await ClearFleetAsync(ctx);
        var shortRange = await AddTypeAsync(ctx, "Short-range type", rangeNm: 1000);
        var aircraft = await AddAircraftAsync(ctx, shortRange, "G-SHRT", reserved: true);
        var route = new Route
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, DepartureIcao = "EGGD", ArrivalIcao = "KJFK",
            DistanceNm = 2900, BaseFare = 400m, IsActive = true, CreatedUtc = DateTimeOffset.UtcNow,
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
        Assert.Contains("can't reach KJFK", ErrorOf(result));
        Assert.Empty(await ctx.Db.Flights.ToListAsync());
    }

    // ---- Scheduling a virtual pilot's leg ---------------------------------------------------

    [Fact]
    public async Task SaveScheduleAsync_OverRangeLeg_IsRefusedWithOnePlainReason()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedTransatlanticAirportAsync(ctx);
        await ClearFleetAsync(ctx);
        var catalog = EconomyConfigCatalog.Default();
        var shortRange = await AddTypeAsync(ctx, "Short-range type", rangeNm: 1000);
        var aircraft = await AddAircraftAsync(ctx, shortRange, "G-SHRT", reserved: false);
        var (outbound, inbound) = await SeedTransatlanticRoundTripAsync(ctx);
        var pilot = await HirePilotAsync(ctx, catalog);

        var request = new SaveScheduleRequest(new[]
        {
            new DutyDayRequest(0, aircraft.Id, new[]
            {
                new DutyLegRequest("06:00:00", outbound.Id),
                new DutyLegRequest("18:00:00", inbound.Id),
            }),
        });

        var result = await PilotEndpoints.SaveScheduleAsync(pilot.Id, request, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        var conflicts = JsonOf(result).GetProperty("conflicts").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.Contains(conflicts, c => c.Contains("beyond G-SHRT's") && c.Contains("850 nm"));
        Assert.Empty(await ctx.Db.PilotScheduleEntries.ToListAsync());
    }

    [Fact]
    public async Task GetLegOptionsAsync_OverRangeRoute_IsIllegalWithARangeReason()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedTransatlanticAirportAsync(ctx);
        await ClearFleetAsync(ctx);
        var catalog = EconomyConfigCatalog.Default();
        var shortRange = await AddTypeAsync(ctx, "Short-range type", rangeNm: 1000);
        var aircraft = await AddAircraftAsync(ctx, shortRange, "G-SHRT", reserved: false);
        var (outbound, _) = await SeedTransatlanticRoundTripAsync(ctx);
        var pilot = await HirePilotAsync(ctx, catalog);

        var result = await PilotEndpoints.GetLegOptionsAsync(
            pilot.Id, new LegOptionsRequest(0, "06:00", aircraft.Id, null), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);

        var json = JsonOf(result);
        Assert.Empty(json.GetProperty("legal").EnumerateArray());
        var illegal = json.GetProperty("illegal").EnumerateArray()
            .Single(i => i.GetProperty("routeId").GetGuid() == outbound.Id);
        Assert.Contains("beyond G-SHRT's", illegal.GetProperty("reason").GetString()!);
    }

    private static async Task<(Route Outbound, Route Inbound)> SeedTransatlanticRoundTripAsync(RouteTestContext ctx)
    {
        var outbound = new Route
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, DepartureIcao = "EGGD", ArrivalIcao = "KJFK",
            DistanceNm = 2900, BaseFare = 400m, IsActive = true, CreatedUtc = DateTimeOffset.UtcNow,
        };
        var inbound = new Route
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, DepartureIcao = "KJFK", ArrivalIcao = "EGGD",
            DistanceNm = 2900, BaseFare = 400m, IsActive = true, CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.Routes.AddRange(outbound, inbound);
        await ctx.Db.SaveChangesAsync();
        return (outbound, inbound);
    }

    private static async Task<Pilot> HirePilotAsync(RouteTestContext ctx, EconomyConfigCatalog catalog)
    {
        var result = await PilotEndpoints.HireAsync(new HirePilotRequest("Test FO"), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(result));
        return await ctx.Db.Pilots.SingleAsync(p => p.AirlineId == ctx.Airline.Id && !p.IsPlayer);
    }

    [Fact]
    public async Task StartAsync_WithNoAircraftNamed_NeverAutoPicksOneThatCannotReach()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedTransatlanticAirportAsync(ctx);
        await ClearFleetAsync(ctx);
        var shortRange = await AddTypeAsync(ctx, "Short-range type", rangeNm: 1000);
        var longRange = await AddTypeAsync(ctx, "Long-range type", rangeNm: 6000);
        // Oldest first, so the naive ".OrderBy(CreatedUtc).First()" would pick the wrong one.
        await AddAircraftAsync(ctx, shortRange, "G-SHRT", reserved: true);
        var capable = await AddAircraftAsync(ctx, longRange, "G-LONG", reserved: true);
        var route = new Route
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, DepartureIcao = "EGGD", ArrivalIcao = "KJFK",
            DistanceNm = 2900, BaseFare = 400m, IsActive = true, CreatedUtc = DateTimeOffset.UtcNow,
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
            new StartFlightRequest(route.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry,
            EconomyConfigCatalog.Default(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(result));
        var flight = await ctx.Db.Flights.SingleAsync();
        Assert.Equal(capable.Id, flight.FleetAircraftId);
    }
}
