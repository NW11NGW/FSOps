using FSOps.Core.Entities;
using FSOps.Server.Endpoints;
using FSOps.Server.Services;
using Microsoft.AspNetCore.Http;

namespace FSOps.Server.Tests;

/// <summary>
/// Drives VatsimEndpoints.GetAtcAsync directly against an isolated RouteTestContext (which seeds
/// EGGD/EGPH/EGSS/EGPF), same convention as MaintenanceEndpointsTests. VatsimNetworkClient itself
/// is swapped for FakeVatsimNetworkClient here - these tests are about the endpoint's own logic
/// (network-relevance filtering, airport resolution from callsigns, facility labels, unavailable
/// passthrough), not about the HTTP fetch, which VatsimNetworkClientTests covers on its own.
/// </summary>
public class VatsimEndpointsTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static T ValueOf<T>(IResult result) => (T)Assert.IsAssignableFrom<IValueHttpResult>(result).Value!;

    private sealed class FakeVatsimNetworkClient : IVatsimNetworkClient
    {
        private readonly VatsimSnapshot _snapshot;
        public int CallCount { get; private set; }
        public FakeVatsimNetworkClient(VatsimSnapshot snapshot) => _snapshot = snapshot;
        public Task<VatsimSnapshot> GetSnapshotAsync(CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_snapshot);
        }
    }

    /// <summary>Gives the seeded airline one active route EGGD-EGPH, so "network" = {EGGD, EGPH}
    /// for every test below unless noted otherwise.</summary>
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

    [Fact]
    public async Task GetAtcAsync_TowerCallsignAtNetworkAirport_ResolvesAirportPosition()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx); // network = EGGD, EGPH
        var snapshot = new VatsimSnapshot(true, Base, new[]
        {
            new VatsimController("EGPH_TWR", 1, "Alice", "118.700", 4, 40, Base),
        });

        var result = await VatsimEndpoints.GetAtcAsync(new FakeVatsimNetworkClient(snapshot), ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var response = ValueOf<VatsimAtcResponse>(result);

        Assert.Equal("ok", response.Status);
        var controller = Assert.Single(response.Controllers);
        Assert.Equal("EGPH", controller.AirportIcao);
        Assert.Equal("Edinburgh Airport", controller.AirportName);
        Assert.Equal(55.9500, controller.LatitudeDeg);
        Assert.Equal(-3.3725, controller.LongitudeDeg);
        Assert.Equal("Tower", controller.FacilityLabel);
        Assert.Equal(40, controller.VisualRangeNm);
    }

    [Fact]
    public async Task GetAtcAsync_TowerCallsignAtAirportOutsideNetwork_IsFilteredOut()
    {
        // EGPF (Glasgow) is a real, seeded airport - just not one this airline flies to, so a
        // controller there is not "near your network" and must not appear.
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx); // network = EGGD, EGPH
        var snapshot = new VatsimSnapshot(true, Base, new[]
        {
            new VatsimController("EGPF_TWR", 1, "Alice", "118.800", 4, 40, Base),
        });

        var result = await VatsimEndpoints.GetAtcAsync(new FakeVatsimNetworkClient(snapshot), ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var response = ValueOf<VatsimAtcResponse>(result);

        Assert.Equal("ok", response.Status);
        Assert.Empty(response.Controllers);
    }

    [Fact]
    public async Task GetAtcAsync_CenterCallsignAtNetworkAirportPrefix_NeverMatchesAndIsFilteredOut()
    {
        // A CTR/FSS callsign never resolves to a single airport regardless of what its prefix
        // looks like, so it can never satisfy the network filter - see the class doc.
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx); // network = EGGD, EGPH
        var snapshot = new VatsimSnapshot(true, Base, new[]
        {
            new VatsimController("LON_CTR", 2, "Bob", "127.100", 6, 300, Base),
        });

        var result = await VatsimEndpoints.GetAtcAsync(new FakeVatsimNetworkClient(snapshot), ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var response = ValueOf<VatsimAtcResponse>(result);

        Assert.Empty(response.Controllers);
    }

    [Fact]
    public async Task GetAtcAsync_NoActiveRoutes_ReturnsOkEmpty_AndNeverCallsTheVatsimClient()
    {
        // No network yet (brand-new airline) - nothing to show, and the feed must not even be
        // fetched: see VatsimNetworkClient's "back off when nothing is listening" doc.
        using var ctx = await RouteTestContext.CreateAsync();
        var client = new FakeVatsimNetworkClient(new VatsimSnapshot(true, Base, new[]
        {
            new VatsimController("EGGD_TWR", 1, "Alice", "118.300", 4, 40, Base),
        }));

        var result = await VatsimEndpoints.GetAtcAsync(client, ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var response = ValueOf<VatsimAtcResponse>(result);

        Assert.Equal("ok", response.Status);
        Assert.Empty(response.Controllers);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task GetAtcAsync_CallsignForUnknownIcao_IsFilteredOut()
    {
        // EGXX looks ICAO-shaped but isn't a network airport (or in FSOps' airport database at
        // all) - must degrade to "not shown", not throw or fabricate coordinates.
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx);
        var snapshot = new VatsimSnapshot(true, Base, new[]
        {
            new VatsimController("EGXX_GND", 3, "Carl", "121.900", 3, 20, Base),
        });

        var result = await VatsimEndpoints.GetAtcAsync(new FakeVatsimNetworkClient(snapshot), ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var response = ValueOf<VatsimAtcResponse>(result);

        Assert.Empty(response.Controllers);
    }

    [Fact]
    public async Task GetAtcAsync_FeedUnavailable_ReturnsUnavailableStatusWithEmptyList()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx);
        var client = new FakeVatsimNetworkClient(new VatsimSnapshot(false, Base, Array.Empty<VatsimController>()));

        var result = await VatsimEndpoints.GetAtcAsync(client, ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var response = ValueOf<VatsimAtcResponse>(result);

        Assert.Equal("unavailable", response.Status);
        Assert.Empty(response.Controllers);
    }

    [Fact]
    public async Task GetAtcAsync_EmptyControllerList_ReturnsOkStatusWithEmptyList()
    {
        // Distinct from "unavailable" - the feed answered fine, nobody happens to be controlling
        // the network's airports right now. The frontend must tell these two states apart.
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx);
        var client = new FakeVatsimNetworkClient(new VatsimSnapshot(true, Base, Array.Empty<VatsimController>()));

        var result = await VatsimEndpoints.GetAtcAsync(client, ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var response = ValueOf<VatsimAtcResponse>(result);

        Assert.Equal("ok", response.Status);
        Assert.Empty(response.Controllers);
    }

    [Fact]
    public async Task GetAtcAsync_MultipleControllersAtNetworkAirports_OrderedByCallsign()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx, "EGGD", "EGSS");
        var snapshot = new VatsimSnapshot(true, Base, new[]
        {
            new VatsimController("EGSS_APP", 1, "A", "129.400", 5, 60, Base),
            new VatsimController("EGGD_DEL", 2, "B", "121.925", 2, 10, Base),
            new VatsimController("EGPF_TWR", 3, "C", "118.800", 4, 40, Base), // not in network - dropped
        });

        var result = await VatsimEndpoints.GetAtcAsync(new FakeVatsimNetworkClient(snapshot), ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var response = ValueOf<VatsimAtcResponse>(result);

        Assert.Equal(new[] { "EGGD_DEL", "EGSS_APP" }, response.Controllers.Select(c => c.Callsign));
    }
}
