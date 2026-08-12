using FSOps.Core.Entities;
using FSOps.Server.Endpoints;
using FSOps.Server.Services;
using Microsoft.AspNetCore.Http;

namespace FSOps.Server.Tests;

/// <summary>
/// Drives VatsimEndpoints.GetTrafficAsync (G11) and GetHistoryAsync (G9) directly against an
/// isolated RouteTestContext, same convention as VatsimEndpointsTests. GetHistoryAsync never
/// touches the VATSIM feed at all - it is built entirely from FSOps' own Flight rows - so those
/// tests need no fake network client.
/// </summary>
public class VatsimTrafficAndHistoryEndpointsTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static T ValueOf<T>(IResult result) => (T)Assert.IsAssignableFrom<IValueHttpResult>(result).Value!;

    private sealed class FakeVatsimNetworkClient : IVatsimNetworkClient
    {
        private readonly VatsimSnapshot _snapshot;
        public FakeVatsimNetworkClient(VatsimSnapshot snapshot) => _snapshot = snapshot;
        public Task<VatsimSnapshot> GetSnapshotAsync(CancellationToken ct) => Task.FromResult(_snapshot);
    }

    private static VatsimPilot Pilot(int cid, string callsign, double lat, double lon) =>
        new(callsign, cid, "Test Pilot", lat, lon, AltitudeFt: 35000, GroundSpeedKt: 450, HeadingDeg: 90, null, null, Base, Base);

    private static async Task AddActiveRouteAsync(RouteTestContext ctx, string departure = "EGGD", string arrival = "EGPH")
    {
        ctx.Db.Routes.Add(new Route
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            DepartureIcao = departure,
            ArrivalIcao = arrival,
            IsActive = true,
            CreatedUtc = Base,
        });
        await ctx.Db.SaveChangesAsync();
    }

    // ---------------------------------------------------------------------------------------
    // G11 - GetTrafficAsync
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetTrafficAsync_PilotNearNetworkAirport_IsIncluded()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx); // network = EGGD, EGPH
        var client = new FakeVatsimNetworkClient(new VatsimSnapshot(
            true, Base, Array.Empty<VatsimController>(), new[] { Pilot(1, "EZY123", 55.9500, -3.3725) })); // at EGPH

        var result = await VatsimEndpoints.GetTrafficAsync(client, ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var response = ValueOf<VatsimTrafficResponse>(result);

        Assert.Equal("ok", response.Status);
        var pilot = Assert.Single(response.Pilots);
        Assert.Equal("EZY123", pilot.Callsign);
    }

    [Fact]
    public async Task GetTrafficAsync_PilotFarFromNetwork_IsExcluded()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx); // network = EGGD, EGPH
        // Sydney - nowhere near a UK domestic network.
        var client = new FakeVatsimNetworkClient(new VatsimSnapshot(
            true, Base, Array.Empty<VatsimController>(), new[] { Pilot(1, "QFA1", -33.8688, 151.2093) }));

        var result = await VatsimEndpoints.GetTrafficAsync(client, ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var response = ValueOf<VatsimTrafficResponse>(result);

        Assert.Empty(response.Pilots);
    }

    [Fact]
    public async Task GetTrafficAsync_ExcludesThePlayersOwnConfiguredCid()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx);
        ctx.Db.UserSettings.Add(new UserSettings { Id = Guid.NewGuid(), OwnerUserId = ctx.CurrentUser.UserId, VatsimCid = "123456" });
        await ctx.Db.SaveChangesAsync();

        var client = new FakeVatsimNetworkClient(new VatsimSnapshot(
            true, Base, Array.Empty<VatsimController>(), new[]
            {
                Pilot(123456, "OWN1", 55.9500, -3.3725), // the player's own CID - must not appear as "other traffic"
                Pilot(654321, "OTHER1", 55.9500, -3.3725),
            }));

        var result = await VatsimEndpoints.GetTrafficAsync(client, ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var response = ValueOf<VatsimTrafficResponse>(result);

        var pilot = Assert.Single(response.Pilots);
        Assert.Equal("OTHER1", pilot.Callsign);
    }

    [Fact]
    public async Task GetTrafficAsync_FeedUnavailable_ReturnsUnavailableStatus()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx);
        var client = new FakeVatsimNetworkClient(new VatsimSnapshot(false, Base, Array.Empty<VatsimController>(), Array.Empty<VatsimPilot>()));

        var result = await VatsimEndpoints.GetTrafficAsync(client, ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var response = ValueOf<VatsimTrafficResponse>(result);

        Assert.Equal("unavailable", response.Status);
        Assert.Empty(response.Pilots);
    }

    [Fact]
    public async Task GetTrafficAsync_NoActiveRoutes_ReturnsOkEmpty_AndNeverCallsTheVatsimClient()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var client = new FakeVatsimNetworkClient(new VatsimSnapshot(true, Base, Array.Empty<VatsimController>(), Array.Empty<VatsimPilot>()));

        var result = await VatsimEndpoints.GetTrafficAsync(client, ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var response = ValueOf<VatsimTrafficResponse>(result);

        Assert.Equal("ok", response.Status);
        Assert.Empty(response.Pilots);
    }

    // ---------------------------------------------------------------------------------------
    // G9 - GetHistoryAsync (built entirely from FSOps' own Flight rows - no VATSIM feed involved)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetHistoryAsync_NoCidConfigured_ReportsCidConfiguredFalse()
    {
        using var ctx = await RouteTestContext.CreateAsync();

        var result = await VatsimEndpoints.GetHistoryAsync(ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var response = ValueOf<VatsimHistoryResponse>(result);

        Assert.False(response.CidConfigured);
        Assert.Empty(response.Flights);
    }

    [Fact]
    public async Task GetHistoryAsync_OnlyIncludesFlightsThatWereActuallyChecked()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        ctx.Db.UserSettings.Add(new UserSettings { Id = Guid.NewGuid(), OwnerUserId = ctx.CurrentUser.UserId, VatsimCid = "123456" });
        var route = new Route
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, DepartureIcao = "EGGD", ArrivalIcao = "EGPH",
            FlightNumber = "101", DistanceNm = 280, BaseFare = 90m, IsActive = true, CreatedUtc = Base,
        };
        ctx.Db.Routes.Add(route);

        var checkedOnline = NewFlight(ctx.Airline.Id, route.Id, vatsimOnline: true, fraction: 0.8, callsign: "BAW1");
        var checkedOffline = NewFlight(ctx.Airline.Id, route.Id, vatsimOnline: false, fraction: 0.0, callsign: null);
        var neverChecked = NewFlight(ctx.Airline.Id, route.Id, vatsimOnline: null, fraction: null, callsign: null);
        ctx.Db.Flights.AddRange(checkedOnline, checkedOffline, neverChecked);
        await ctx.Db.SaveChangesAsync();

        var result = await VatsimEndpoints.GetHistoryAsync(ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var response = ValueOf<VatsimHistoryResponse>(result);

        Assert.True(response.CidConfigured);
        Assert.Equal(2, response.Flights.Count);
        Assert.DoesNotContain(response.Flights, f => f.FlightId == neverChecked.Id);
        Assert.Contains(response.Flights, f => f.FlightId == checkedOnline.Id && f.Online && f.Callsign == "BAW1");
        Assert.Contains(response.Flights, f => f.FlightId == checkedOffline.Id && !f.Online);
    }

    private static Flight NewFlight(Guid airlineId, Guid routeId, bool? vatsimOnline, double? fraction, string? callsign) => new()
    {
        Id = Guid.NewGuid(),
        AirlineId = airlineId,
        RouteId = routeId,
        FleetAircraftId = Guid.NewGuid(),
        PilotId = Guid.NewGuid(),
        Status = FlightStatus.Completed,
        PlannedDepartureUtc = Base,
        PlannedBlockMinutes = 90,
        InUtc = Base.AddMinutes(95),
        PaxBooked = 150,
        FuelPlannedKg = 3000,
        TitleFlown = "Test Aircraft",
        CreatedUtc = Base,
        VatsimOnline = vatsimOnline,
        VatsimOnlineFraction = fraction,
        VatsimCallsign = callsign,
    };
}
