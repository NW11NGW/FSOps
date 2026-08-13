using FSOps.Core.Entities;
using FSOps.Server.Endpoints;
using FSOps.Server.Services;
using Microsoft.AspNetCore.Http;

namespace FSOps.Server.Tests;

/// <summary>
/// Drives VatsimEndpoints.GetAtcAsync directly against an isolated RouteTestContext (which seeds
/// EGGD/EGPH/EGSS/EGPF), same convention as MaintenanceEndpointsTests. Both the VATSIM feed and
/// the boundary data are faked here - these tests are about the endpoint's own logic (which
/// controllers are relevant, terminal versus sector treatment, geometry de-duplication and opt-in),
/// not about the HTTP fetch or the real data files, which VatsimNetworkClientTests and
/// VatSpyBoundarySourceTests cover separately.
///
/// The fixture geometry is hand-written: two plain lat/lon boxes with known contents, so every
/// expectation below is arithmetic anyone can check by eye rather than a fact about VAT-Spy.
/// </summary>
public class VatsimEndpointsTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const int FssFacility = 1;
    private const int DeliveryFacility = 2;
    private const int GroundFacility = 3;
    private const int TowerFacility = 4;
    private const int ApproachFacility = 5;
    private const int CenterFacility = 6;

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

    /// <summary>Resolves the leading callsign segment against a hand-supplied table. Standing in
    /// for VatSpyBoundarySource, whose own prefix-matching rules are tested where they live.</summary>
    private sealed class FakeAtcBoundarySource : IAtcBoundarySource
    {
        private readonly Dictionary<string, AtcBoundary> _byPrefix;

        private FakeAtcBoundarySource(bool available, Dictionary<string, AtcBoundary> byPrefix)
        {
            Available = available;
            _byPrefix = byPrefix;
        }

        public bool Available { get; }

        public AtcBoundary? Resolve(string callsign)
        {
            var separator = callsign.IndexOf('_');
            var prefix = separator < 0 ? callsign : callsign[..separator];
            return _byPrefix.TryGetValue(prefix, out var boundary) ? boundary : null;
        }

        /// <summary>The state on a machine where the bundled data is missing or unreadable - and
        /// the state FSOps was permanently in before it existed.</summary>
        public static FakeAtcBoundarySource Unavailable() => new(false, new Dictionary<string, AtcBoundary>());

        /// <summary>Available, but knows nothing about any callsign - a real outcome for any
        /// position VAT-Spy does not list.</summary>
        public static FakeAtcBoundarySource KnowsNothing() => new(true, new Dictionary<string, AtcBoundary>());

        public static FakeAtcBoundarySource With(params (string Prefix, AtcBoundary Boundary)[] entries) =>
            new(true, entries.ToDictionary(e => e.Prefix, e => e.Boundary));
    }

    /// <summary>An axis-aligned box, closed, wound anticlockwise.</summary>
    private static AtcBoundary Box(string id, string name, double minLat, double maxLat, double minLon, double maxLon)
    {
        var ring = new[]
        {
            new GeoPoint(minLon, minLat),
            new GeoPoint(maxLon, minLat),
            new GeoPoint(maxLon, maxLat),
            new GeoPoint(minLon, maxLat),
            new GeoPoint(minLon, minLat),
        };
        return new AtcBoundary(id, name, new[] { new AtcBoundaryPolygon(new[] { (IReadOnlyList<GeoPoint>)ring }) });
    }

    /// <summary>Covers EGPH (55.95N 3.37W) and EGPF (55.87N 4.43W); excludes EGGD (51.38N 2.72W)
    /// and EGSS (51.89N 0.24E).</summary>
    private static AtcBoundary ScottishBox() => Box("EGPX", "Scottish", 54.0, 59.0, -8.0, 0.0);

    /// <summary>Open Atlantic - contains none of the four seeded airports.</summary>
    private static AtcBoundary OceanicBox() => Box("EGGX", "Shanwick Oceanic", 45.0, 61.0, -30.0, -15.0);

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

    private static Task<IResult> InvokeAsync(
        RouteTestContext ctx,
        IVatsimNetworkClient vatsim,
        IAtcBoundarySource boundaries,
        bool? geometry = null,
        string? scope = null) =>
        VatsimEndpoints.GetAtcAsync(vatsim, boundaries, ctx.Db, ctx.CurrentUser, geometry, scope, CancellationToken.None);

    // ---------------------------------------------------------------------------------------
    // Terminal positions - airport-local, unchanged behaviour
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetAtcAsync_TowerCallsignAtNetworkAirport_ResolvesAirportPositionAsTerminalCoverage()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx); // network = EGGD, EGPH
        var snapshot = new VatsimSnapshot(true, Base, new[]
        {
            new VatsimController("EGPH_TWR", 1, "Alice", "118.700", TowerFacility, 40, Base),
        }, Array.Empty<VatsimPilot>());

        var result = await InvokeAsync(ctx, new FakeVatsimNetworkClient(snapshot), FakeAtcBoundarySource.KnowsNothing());
        var response = ValueOf<VatsimAtcResponse>(result);

        Assert.Equal("ok", response.Status);
        var controller = Assert.Single(response.Controllers);
        Assert.Equal("EGPH", controller.AirportIcao);
        Assert.Equal("Edinburgh Airport", controller.AirportName);
        Assert.Equal(55.9500, controller.LatitudeDeg);
        Assert.Equal(-3.3725, controller.LongitudeDeg);
        Assert.Equal("Tower", controller.FacilityLabel);
        Assert.Equal(40, controller.VisualRangeNm);

        // A tower genuinely is at a point, so it keeps the circle - but it is labelled as the
        // approximation it is, and carries no boundary.
        Assert.Equal("terminal", controller.CoverageKind);
        Assert.Null(controller.BoundaryId);
        Assert.Null(controller.BoundaryName);
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
            new VatsimController("EGPF_TWR", 1, "Alice", "118.800", TowerFacility, 40, Base),
        }, Array.Empty<VatsimPilot>());

        var result = await InvokeAsync(ctx, new FakeVatsimNetworkClient(snapshot), FakeAtcBoundarySource.KnowsNothing());
        var response = ValueOf<VatsimAtcResponse>(result);

        Assert.Equal("ok", response.Status);
        Assert.Empty(response.Controllers);
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
            new VatsimController("EGXX_GND", 3, "Carl", "121.900", GroundFacility, 20, Base),
        }, Array.Empty<VatsimPilot>());

        var result = await InvokeAsync(ctx, new FakeVatsimNetworkClient(snapshot), FakeAtcBoundarySource.KnowsNothing());
        var response = ValueOf<VatsimAtcResponse>(result);

        Assert.Empty(response.Controllers);
    }

    [Fact]
    public async Task GetAtcAsync_TraconApproachCallsign_IsStillFilteredOutEntirely()
    {
        // "NY_APP" is a TRACON: not an airport, and there is no bundleable TRACON boundary data
        // anywhere - VAT-Spy publishes FIR/UIR geometry only. So this stays dropped, and that is
        // the deliberate answer rather than an oversight. An invented shape here would read as
        // authoritative and be wrong.
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx);
        var snapshot = new VatsimSnapshot(true, Base, new[]
        {
            new VatsimController("NY_APP", 1, "Dana", "127.400", ApproachFacility, 150, Base),
        }, Array.Empty<VatsimPilot>());

        var result = await InvokeAsync(
            ctx, new FakeVatsimNetworkClient(snapshot), FakeAtcBoundarySource.With(("SCO", ScottishBox())));
        var response = ValueOf<VatsimAtcResponse>(result);

        Assert.Empty(response.Controllers);
    }

    // ---------------------------------------------------------------------------------------
    // Sector positions - en-route, shown at all for the first time
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetAtcAsync_CenterCallsign_WhenNoBoundaryDataIsAvailable_IsStillFilteredOut()
    {
        // The pre-boundary-data behaviour, kept honest: with nothing to say what LON_CTR covers,
        // it is left out rather than drawn at a guessed position. This is also exactly what
        // happens on an install whose data/vatspy files are missing.
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx); // network = EGGD, EGPH
        var snapshot = new VatsimSnapshot(true, Base, new[]
        {
            new VatsimController("LON_CTR", 2, "Bob", "127.100", CenterFacility, 300, Base),
        }, Array.Empty<VatsimPilot>());

        var result = await InvokeAsync(ctx, new FakeVatsimNetworkClient(snapshot), FakeAtcBoundarySource.Unavailable());
        var response = ValueOf<VatsimAtcResponse>(result);

        Assert.Empty(response.Controllers);
        Assert.Null(response.Boundaries);
    }

    [Fact]
    public async Task GetAtcAsync_CenterCallsignWithNoKnownBoundary_IsFilteredOut()
    {
        // Boundary data loaded fine, but this particular callsign isn't in it. "We don't know"
        // must keep looking like silence.
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx);
        var snapshot = new VatsimSnapshot(true, Base, new[]
        {
            new VatsimController("ZZZZ_CTR", 2, "Bob", "127.100", CenterFacility, 300, Base),
        }, Array.Empty<VatsimPilot>());

        var result = await InvokeAsync(
            ctx, new FakeVatsimNetworkClient(snapshot), FakeAtcBoundarySource.With(("SCO", ScottishBox())));
        var response = ValueOf<VatsimAtcResponse>(result);

        Assert.Empty(response.Controllers);
    }

    [Fact]
    public async Task GetAtcAsync_CenterCallsignWhoseBoundaryContainsANetworkAirport_IsShownAsASectorWithNoPosition()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx); // network = EGGD (outside the box), EGPH (inside it)
        var snapshot = new VatsimSnapshot(true, Base, new[]
        {
            new VatsimController("SCO_CTR", 2, "Bob", "129.225", CenterFacility, 300, Base),
        }, Array.Empty<VatsimPilot>());

        var result = await InvokeAsync(
            ctx, new FakeVatsimNetworkClient(snapshot), FakeAtcBoundarySource.With(("SCO", ScottishBox())));
        var response = ValueOf<VatsimAtcResponse>(result);

        var controller = Assert.Single(response.Controllers);
        Assert.Equal("sector", controller.CoverageKind);
        Assert.Equal("EGPX", controller.BoundaryId);
        Assert.Equal("Scottish", controller.BoundaryName);
        Assert.Equal("Center", controller.FacilityLabel);

        // No point, no airport: a sector is an area, and a marker would put a pin where nobody is.
        Assert.Null(controller.LatitudeDeg);
        Assert.Null(controller.LongitudeDeg);
        Assert.Null(controller.AirportIcao);
        Assert.Null(controller.AirportName);
    }

    [Fact]
    public async Task GetAtcAsync_FssCallsignIsTreatedAsASectorTheSameWayAsCenter()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx);
        var snapshot = new VatsimSnapshot(true, Base, new[]
        {
            new VatsimController("SCO_FSS", 2, "Bob", "127.650", FssFacility, 600, Base),
        }, Array.Empty<VatsimPilot>());

        var result = await InvokeAsync(
            ctx, new FakeVatsimNetworkClient(snapshot), FakeAtcBoundarySource.With(("SCO", ScottishBox())));
        var response = ValueOf<VatsimAtcResponse>(result);

        var controller = Assert.Single(response.Controllers);
        Assert.Equal("sector", controller.CoverageKind);
        Assert.Equal("Flight Service Station", controller.FacilityLabel);
    }

    [Fact]
    public async Task GetAtcAsync_CenterCallsignWhoseBoundaryMissesTheWholeNetwork_IsFilteredOut()
    {
        // Shanwick is online and its geometry is known perfectly well - it just doesn't contain
        // anywhere this airline flies, so it is not "near your network".
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx); // EGGD, EGPH - both well east of the oceanic box
        var snapshot = new VatsimSnapshot(true, Base, new[]
        {
            new VatsimController("EGGX_CTR", 2, "Bob", "127.100", CenterFacility, 600, Base),
        }, Array.Empty<VatsimPilot>());

        var result = await InvokeAsync(
            ctx, new FakeVatsimNetworkClient(snapshot), FakeAtcBoundarySource.With(("EGGX", OceanicBox())));
        var response = ValueOf<VatsimAtcResponse>(result);

        Assert.Empty(response.Controllers);
    }

    // ---------------------------------------------------------------------------------------
    // Geometry payload
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetAtcAsync_BoundaryGeometry_IsOmittedUnlessTheCallerAsksForIt()
    {
        // The controller list and the map share one endpoint, and the list draws no boundaries -
        // it should not be paying for coordinates it discards.
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx);
        var snapshot = new VatsimSnapshot(true, Base, new[]
        {
            new VatsimController("SCO_CTR", 2, "Bob", "129.225", CenterFacility, 300, Base),
        }, Array.Empty<VatsimPilot>());
        var boundaries = FakeAtcBoundarySource.With(("SCO", ScottishBox()));

        var withoutGeometry = ValueOf<VatsimAtcResponse>(
            await InvokeAsync(ctx, new FakeVatsimNetworkClient(snapshot), boundaries));
        Assert.Single(withoutGeometry.Controllers);
        Assert.Null(withoutGeometry.Boundaries);

        var explicitlyOff = ValueOf<VatsimAtcResponse>(
            await InvokeAsync(ctx, new FakeVatsimNetworkClient(snapshot), boundaries, geometry: false));
        Assert.Null(explicitlyOff.Boundaries);

        var withGeometry = ValueOf<VatsimAtcResponse>(
            await InvokeAsync(ctx, new FakeVatsimNetworkClient(snapshot), boundaries, geometry: true));
        var sent = Assert.Single(withGeometry.Boundaries!);
        Assert.Equal("EGPX", sent.Key);

        // GeoJSON MultiPolygon coordinates: one polygon, one ring, five closed positions of [lon, lat].
        var polygon = Assert.Single(sent.Value);
        var ring = Assert.Single(polygon);
        Assert.Equal(5, ring.Length);
        Assert.Equal(new[] { -8.0, 54.0 }, ring[0]);
        Assert.Equal(ring[0], ring[^1]);
    }

    [Fact]
    public async Task GetAtcAsync_TwoControllersSharingOneBoundary_SendItsGeometryOnce()
    {
        // LON_N_CTR and LON_S_CTR split one published region between them. Sending the polygon
        // twice would waste bytes and invite the map into drawing two overlapping shapes where
        // there is one.
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx);
        var snapshot = new VatsimSnapshot(true, Base, new[]
        {
            new VatsimController("SCO_N_CTR", 1, "A", "129.225", CenterFacility, 300, Base),
            new VatsimController("SCO_S_CTR", 2, "B", "128.500", CenterFacility, 300, Base),
        }, Array.Empty<VatsimPilot>());

        var result = await InvokeAsync(
            ctx, new FakeVatsimNetworkClient(snapshot), FakeAtcBoundarySource.With(("SCO", ScottishBox())), geometry: true);
        var response = ValueOf<VatsimAtcResponse>(result);

        Assert.Equal(2, response.Controllers.Count);
        Assert.All(response.Controllers, c => Assert.Equal("EGPX", c.BoundaryId));
        Assert.Single(response.Boundaries!);
    }

    [Fact]
    public async Task GetAtcAsync_OnlyTerminalControllersOnline_SendsNoBoundariesEvenWhenGeometryIsAsked()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx);
        var snapshot = new VatsimSnapshot(true, Base, new[]
        {
            new VatsimController("EGPH_TWR", 1, "Alice", "118.700", TowerFacility, 40, Base),
        }, Array.Empty<VatsimPilot>());

        var result = await InvokeAsync(
            ctx, new FakeVatsimNetworkClient(snapshot), FakeAtcBoundarySource.With(("SCO", ScottishBox())), geometry: true);
        var response = ValueOf<VatsimAtcResponse>(result);

        Assert.Single(response.Controllers);
        Assert.Null(response.Boundaries);
    }

    // ---------------------------------------------------------------------------------------
    // Feed and network states
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetAtcAsync_NoActiveRoutes_ReturnsOkEmpty_AndNeverCallsTheVatsimClient()
    {
        // No network yet (brand-new airline) - nothing to show, and the feed must not even be
        // fetched: see VatsimNetworkClient's "back off when nothing is listening" doc.
        using var ctx = await RouteTestContext.CreateAsync();
        var client = new FakeVatsimNetworkClient(new VatsimSnapshot(true, Base, new[]
        {
            new VatsimController("EGGD_TWR", 1, "Alice", "118.300", TowerFacility, 40, Base),
        }, Array.Empty<VatsimPilot>()));

        var result = await InvokeAsync(ctx, client, FakeAtcBoundarySource.With(("SCO", ScottishBox())), geometry: true);
        var response = ValueOf<VatsimAtcResponse>(result);

        Assert.Equal("ok", response.Status);
        Assert.Empty(response.Controllers);
        Assert.Null(response.Boundaries);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task GetAtcAsync_FeedUnavailable_ReturnsUnavailableStatusWithEmptyList()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx);
        var client = new FakeVatsimNetworkClient(new VatsimSnapshot(false, Base, Array.Empty<VatsimController>(), Array.Empty<VatsimPilot>()));

        var result = await InvokeAsync(ctx, client, FakeAtcBoundarySource.With(("SCO", ScottishBox())), geometry: true);
        var response = ValueOf<VatsimAtcResponse>(result);

        Assert.Equal("unavailable", response.Status);
        Assert.Empty(response.Controllers);
        Assert.Null(response.Boundaries);
    }

    [Fact]
    public async Task GetAtcAsync_EmptyControllerList_ReturnsOkStatusWithEmptyList()
    {
        // Distinct from "unavailable" - the feed answered fine, nobody happens to be controlling
        // the network's airports right now. The frontend must tell these two states apart.
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx);
        var client = new FakeVatsimNetworkClient(new VatsimSnapshot(true, Base, Array.Empty<VatsimController>(), Array.Empty<VatsimPilot>()));

        var result = await InvokeAsync(ctx, client, FakeAtcBoundarySource.With(("SCO", ScottishBox())));
        var response = ValueOf<VatsimAtcResponse>(result);

        Assert.Equal("ok", response.Status);
        Assert.Empty(response.Controllers);
    }

    // ---------------------------------------------------------------------------------------
    // Scope - what the map needs versus what a viewport-free consumer needs
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetAtcAsync_WorldScope_ReturnsControllersOutsideTheNetworkFlaggedAsSuch()
    {
        // The map must be able to show a controller wherever the user pans, so being outside the
        // network stops being a reason to hide someone the user can plainly see on screen. EGPF
        // (Glasgow) is a real airport this airline just doesn't serve.
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx); // network = EGGD, EGPH
        var snapshot = new VatsimSnapshot(true, Base, new[]
        {
            new VatsimController("EGPF_TWR", 1, "A", "118.800", TowerFacility, 40, Base),
            new VatsimController("EGPH_TWR", 2, "B", "118.700", TowerFacility, 40, Base),
        }, Array.Empty<VatsimPilot>());

        var result = await InvokeAsync(
            ctx, new FakeVatsimNetworkClient(snapshot), FakeAtcBoundarySource.KnowsNothing(), scope: "all");
        var response = ValueOf<VatsimAtcResponse>(result);

        Assert.Equal(new[] { "EGPF_TWR", "EGPH_TWR" }, response.Controllers.Select(c => c.Callsign));
        Assert.False(response.Controllers.Single(c => c.Callsign == "EGPF_TWR").InNetwork);
        Assert.True(response.Controllers.Single(c => c.Callsign == "EGPH_TWR").InNetwork);
    }

    [Fact]
    public async Task GetAtcAsync_WorldScope_ReturnsSectorsThatMissTheNetworkEntirely()
    {
        // Shanwick contains none of this airline's airports, but if the user pans out over the
        // Atlantic they will see its polygon - and a list that omitted it would contradict the map
        // they are looking at.
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx);
        var snapshot = new VatsimSnapshot(true, Base, new[]
        {
            new VatsimController("EGGX_CTR", 1, "A", "127.100", CenterFacility, 600, Base),
        }, Array.Empty<VatsimPilot>());

        var result = await InvokeAsync(
            ctx,
            new FakeVatsimNetworkClient(snapshot),
            FakeAtcBoundarySource.With(("EGGX", OceanicBox())),
            geometry: true,
            scope: "all");
        var response = ValueOf<VatsimAtcResponse>(result);

        var controller = Assert.Single(response.Controllers);
        Assert.Equal("sector", controller.CoverageKind);
        Assert.False(controller.InNetwork);
        Assert.Contains("EGGX", response.Boundaries!.Keys);
    }

    [Fact]
    public async Task GetAtcAsync_WorldScope_StillDropsControllersItCannotPlaceAtAll()
    {
        // Widening the scope must not widen what FSOps is willing to invent. A TRACON and an
        // unknown-to-FSOps airport stay out under every scope.
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx);
        var snapshot = new VatsimSnapshot(true, Base, new[]
        {
            new VatsimController("NY_APP", 1, "A", "127.400", ApproachFacility, 150, Base),
            new VatsimController("ZZZZ_CTR", 2, "B", "127.100", CenterFacility, 300, Base),
            new VatsimController("EGXX_GND", 3, "C", "121.900", GroundFacility, 20, Base),
        }, Array.Empty<VatsimPilot>());

        var result = await InvokeAsync(
            ctx, new FakeVatsimNetworkClient(snapshot), FakeAtcBoundarySource.With(("SCO", ScottishBox())), scope: "all");
        var response = ValueOf<VatsimAtcResponse>(result);

        Assert.Empty(response.Controllers);
    }

    [Fact]
    public async Task GetAtcAsync_DefaultScope_IsNetworkOnly_SoAViewportFreeConsumerIsUnaffected()
    {
        // A consumer with no map asks a different question: who is controlling where I fly.
        // Absent scope, and any unrecognised scope, must keep that answer.
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx);
        var snapshot = new VatsimSnapshot(true, Base, new[]
        {
            new VatsimController("EGPF_TWR", 1, "A", "118.800", TowerFacility, 40, Base),
            new VatsimController("EGPH_TWR", 2, "B", "118.700", TowerFacility, 40, Base),
        }, Array.Empty<VatsimPilot>());

        foreach (var scope in new string?[] { null, "network", "nonsense" })
        {
            var response = ValueOf<VatsimAtcResponse>(
                await InvokeAsync(ctx, new FakeVatsimNetworkClient(snapshot), FakeAtcBoundarySource.KnowsNothing(), scope: scope));

            var controller = Assert.Single(response.Controllers);
            Assert.Equal("EGPH_TWR", controller.Callsign);
            Assert.True(controller.InNetwork);
        }
    }

    [Fact]
    public async Task GetAtcAsync_NoActiveRoutes_SkipsTheFeedUnderEveryScope()
    {
        // With no routes the dashboard draws no map at all, so there is no viewport to fill and
        // no reason to wake the feed - the "back off when nothing is listening" rule survives the
        // wider scope intact.
        using var ctx = await RouteTestContext.CreateAsync();
        var client = new FakeVatsimNetworkClient(new VatsimSnapshot(true, Base, new[]
        {
            new VatsimController("EGPF_TWR", 1, "A", "118.800", TowerFacility, 40, Base),
        }, Array.Empty<VatsimPilot>()));

        var response = ValueOf<VatsimAtcResponse>(
            await InvokeAsync(ctx, client, FakeAtcBoundarySource.KnowsNothing(), geometry: true, scope: "all"));

        Assert.Empty(response.Controllers);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task GetAtcAsync_MixedTerminalAndSectorControllers_OrderedByCallsign()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await AddActiveRouteAsync(ctx, "EGGD", "EGPH");
        var snapshot = new VatsimSnapshot(true, Base, new[]
        {
            new VatsimController("SCO_CTR", 1, "A", "129.225", CenterFacility, 300, Base),
            new VatsimController("EGGD_DEL", 2, "B", "121.925", DeliveryFacility, 10, Base),
            new VatsimController("EGPF_TWR", 3, "C", "118.800", TowerFacility, 40, Base), // not in network - dropped
            new VatsimController("EGGX_CTR", 4, "D", "127.100", CenterFacility, 600, Base), // misses the network - dropped
        }, Array.Empty<VatsimPilot>());

        var result = await InvokeAsync(
            ctx,
            new FakeVatsimNetworkClient(snapshot),
            FakeAtcBoundarySource.With(("SCO", ScottishBox()), ("EGGX", OceanicBox())));
        var response = ValueOf<VatsimAtcResponse>(result);

        Assert.Equal(new[] { "EGGD_DEL", "SCO_CTR" }, response.Controllers.Select(c => c.Callsign));
        Assert.Equal(new[] { "terminal", "sector" }, response.Controllers.Select(c => c.CoverageKind));
    }
}
