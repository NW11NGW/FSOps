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
/// GET /flights/options and POST /flights/start under the redesigned reservation invariant
/// (docs/PLAN.md "3a") - the player may only fly a RESERVED aircraft, and every aircraft physically
/// at the departure airport is listed (never silently dropped), each with its own eligibility and
/// reason. Regression coverage for the 2026-08-09 real-use defect: an EGLL->EGPH route offering
/// only one of four airframes actually parked at EGLL, because the old endpoint picked a single
/// `.FirstOrDefault()` candidate per ROUTE regardless of how many aircraft were present.
/// </summary>
public class FlightOptionsEndpointTests
{
    private static int StatusCodeOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static (FlightLifecycleService Lifecycle, SimTelemetryService Telemetry) CreateLifecycleAndTelemetry()
    {
        var telemetry = new SimTelemetryService(new NoOpSimSource(), new NoOpHubContext(), NullLogger<SimTelemetryService>.Instance);
        var lifecycle = new FlightLifecycleService(null!, telemetry, new NoOpHubContext(), EconomyConfigCatalog.Default(), NullLogger<FlightLifecycleService>.Instance);
        return (lifecycle, telemetry);
    }

    private static async Task<FleetAircraft> AddAircraftAsync(RouteTestContext ctx, string registration, string locationIcao, bool reserved, FleetAircraftStatus status = FleetAircraftStatus.Active)
    {
        var aircraft = new FleetAircraft
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            AircraftTypeId = ctx.AircraftType.Id,
            Registration = registration,
            Ownership = AircraftOwnership.Owned,
            LocationIcao = locationIcao,
            Status = status,
            ReservedForPlayer = reserved,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.FleetAircraft.Add(aircraft);
        await ctx.Db.SaveChangesAsync();
        return aircraft;
    }

    /// <summary>FlightEndpoints.StartAsync requires a pilot on record - not part of RouteTestContext
    /// itself (see FlightLedgerPostingTests' own equivalent helper).</summary>
    private static async Task SeedPlayerPilotAsync(RouteTestContext ctx)
    {
        ctx.Db.Pilots.Add(new Pilot
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Name = "You", IsPlayer = true,
            MonthlySalary = 9_000m, SkillRating = 50, CreatedUtc = DateTimeOffset.UtcNow,
        });
        await ctx.Db.SaveChangesAsync();
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

    /// <summary>
    /// Regression for the exact user-reported shape: several aircraft sitting at the departure
    /// airport, only some of them reserved. Every one of them must appear in the response - none
    /// silently dropped - with the reserved-and-serviceable ones flyable and the rest carrying their
    /// own single reason.
    /// </summary>
    [Fact]
    public async Task OptionsAsync_ListsEveryAircraftAtDeparture_NotJustOne()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var founding = await ctx.Db.FleetAircraft.SingleAsync(); // G-TEST at EGGD, reserved by fixture default
        founding.ReservedForPlayer = false; // this test wants an explicitly UNRESERVED example
        await ctx.Db.SaveChangesAsync();
        var reserved = await AddAircraftAsync(ctx, "G-RSVD", "EGGD", reserved: true);
        var inMaintenance = await AddAircraftAsync(ctx, "G-MTCE", "EGGD", reserved: true, status: FleetAircraftStatus.InMaintenance);
        await AddRouteAsync(ctx, "EGGD", "EGPH");

        var result = await FlightEndpoints.OptionsAsync(ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

        var options = Assert.IsAssignableFrom<IEnumerable<object>>(((IValueHttpResult)result).Value!).Cast<object>().ToList();
        var route = Assert.Single(options);
        var routeJson = System.Text.Json.JsonSerializer.SerializeToElement(route);

        var aircraftOptions = routeJson.GetProperty("aircraftOptions").EnumerateArray().ToList();
        Assert.Equal(3, aircraftOptions.Count); // founding + reserved + inMaintenance - none dropped

        var foundingOption = aircraftOptions.Single(a => a.GetProperty("fleetAircraftId").GetGuid() == founding.Id);
        Assert.False(foundingOption.GetProperty("isFlyable").GetBoolean());
        Assert.Contains("Not reserved to you", foundingOption.GetProperty("reason").GetString());

        var reservedOption = aircraftOptions.Single(a => a.GetProperty("fleetAircraftId").GetGuid() == reserved.Id);
        Assert.True(reservedOption.GetProperty("isFlyable").GetBoolean());
        Assert.True(reservedOption.GetProperty("reason").ValueKind is System.Text.Json.JsonValueKind.Null);

        var maintenanceOption = aircraftOptions.Single(a => a.GetProperty("fleetAircraftId").GetGuid() == inMaintenance.Id);
        Assert.False(maintenanceOption.GetProperty("isFlyable").GetBoolean());
        Assert.Contains("maintenance", maintenanceOption.GetProperty("reason").GetString());

        Assert.True(routeJson.GetProperty("isFlyable").GetBoolean()); // at least one aircraft (reserved) is flyable
    }

    [Fact]
    public async Task OptionsAsync_NoAircraftAtDeparture_IsUnflyable_WithARouteLevelReason()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        // Founding aircraft is at EGGD; route departs somewhere nothing is parked.
        await AddRouteAsync(ctx, "EGPF", "EGGD");

        var result = await FlightEndpoints.OptionsAsync(ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);
        var options = Assert.IsAssignableFrom<IEnumerable<object>>(((IValueHttpResult)result).Value!).Cast<object>().ToList();
        var route = Assert.Single(options);
        var routeJson = System.Text.Json.JsonSerializer.SerializeToElement(route);

        Assert.False(routeJson.GetProperty("isFlyable").GetBoolean());
        Assert.Empty(routeJson.GetProperty("aircraftOptions").EnumerateArray());
        Assert.Contains("No aircraft at EGPF", routeJson.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task StartAsync_UnreservedAircraft_IsRejected()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var aircraft = await ctx.Db.FleetAircraft.SingleAsync();
        aircraft.ReservedForPlayer = false;
        await ctx.Db.SaveChangesAsync();
        var route = await AddRouteAsync(ctx, "EGGD", "EGPH");
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry();

        var result = await FlightEndpoints.StartAsync(
            new StartFlightRequest(route.Id, aircraft.Id), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, EconomyConfigCatalog.Default(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        Assert.Empty(await ctx.Db.Flights.ToListAsync());
    }

    [Fact]
    public async Task StartAsync_ReservedButAtTheWrongAirport_IsRejected()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var aircraft = await ctx.Db.FleetAircraft.SingleAsync();
        aircraft.ReservedForPlayer = true;
        aircraft.LocationIcao = "EGPF"; // not EGGD, the route's departure
        await ctx.Db.SaveChangesAsync();
        var route = await AddRouteAsync(ctx, "EGGD", "EGPH");
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry();

        var result = await FlightEndpoints.StartAsync(
            new StartFlightRequest(route.Id, aircraft.Id), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, EconomyConfigCatalog.Default(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        Assert.Empty(await ctx.Db.Flights.ToListAsync());
    }

    [Fact]
    public async Task StartAsync_ReservedAndAtDeparture_Succeeds()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedPlayerPilotAsync(ctx);
        var aircraft = await ctx.Db.FleetAircraft.SingleAsync();
        aircraft.ReservedForPlayer = true; // already at EGGD from the fixture
        await ctx.Db.SaveChangesAsync();
        var route = await AddRouteAsync(ctx, "EGGD", "EGPH");
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry();

        var result = await FlightEndpoints.StartAsync(
            new StartFlightRequest(route.Id, aircraft.Id), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, EconomyConfigCatalog.Default(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(result));
        Assert.Single(await ctx.Db.Flights.ToListAsync());
    }

    [Fact]
    public async Task StartAsync_NoAircraftIdGiven_AutoPicksOnlyAReservedOneAtDeparture()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedPlayerPilotAsync(ctx);
        var founding = await ctx.Db.FleetAircraft.SingleAsync();
        founding.ReservedForPlayer = false; // unreserved - must never be auto-picked
        await ctx.Db.SaveChangesAsync();
        var reserved = await AddAircraftAsync(ctx, "G-RSVD", "EGGD", reserved: true);
        var route = await AddRouteAsync(ctx, "EGGD", "EGPH");
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry();

        var result = await FlightEndpoints.StartAsync(
            new StartFlightRequest(route.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, EconomyConfigCatalog.Default(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(result));
        var flight = await ctx.Db.Flights.SingleAsync();
        Assert.Equal(reserved.Id, flight.FleetAircraftId);
    }
}
