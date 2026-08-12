using FSOps.Data;
using FSOps.Server.Auth;
using FSOps.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Endpoints;

/// <summary>
/// Backs the dashboard's ATC layer - online VATSIM controllers plotted alongside the player's own
/// aircraft on the live operations map, answering "is anyone controlling where I am flying right
/// now". Deliberately narrower than the fuller VATSIM integration originally envisaged - which
/// also covered other people's live traffic on the map, verifying a tracked flight was genuinely
/// flown online against the player's own CID, and pre-filling the Fly screen from a filed plan.
/// This shows controllers only, never other pilots, and only controllers
/// relevant to the player - not a global VATSIM controller list.
///
/// <para><b>Two kinds of coverage, drawn differently on purpose, because they are different
/// claims.</b></para>
///
/// <para><i>Terminal</i> (<c>CoverageKind == "terminal"</c>) - tower, ground, delivery and
/// airport-named approach. These genuinely are airport-local, so the position is the airport,
/// resolved locally against FSOps' own Airports table by matching the callsign's leading
/// ICAO-shaped segment (EGLL_TWR -&gt; EGLL). The client draws the feed's own <c>visual_range</c>
/// as a circle around it. That circle is an approximation and is labelled as one: it is the range
/// the controller's client is set to see, not the shape of anything they control.</para>
///
/// <para><i>Sector</i> (<c>CoverageKind == "sector"</c>) - CTR and FSS. These have no airport and
/// never did, which is why FSOps used to drop every one of them. They now carry real published FIR
/// and UIR geometry from the bundled VAT-Spy data (see <see cref="IAtcBoundarySource"/>), so
/// en-route control can be shown at all for the first time. A sector controller has no latitude or
/// longitude and no marker - a polygon has no meaningful centre, and inventing one would put a pin
/// where nobody is.</para>
///
/// <para><b>What is still deliberately not shown, because the data does not support it.</b>
/// Non-airport TRACON callsigns (NY_APP, SCT_APP) resolve to neither an airport nor a published
/// boundary - no bundleable TRACON geometry exists - so they are left out exactly as before. There
/// are no vertical limits anywhere in this data, so nothing here claims an altitude band, and
/// top-down coverage (a centre working an airport with nobody local) is never inferred. A sector
/// polygon claims one thing only: this controller is working that published region, and that
/// region contains at least one airport you serve.</para>
///
/// <para><b>Two scopes, because two consumers are asking different questions.</b> The dashboard
/// map asks "what is on screen right now" - it must be able to show a controller wherever the user
/// pans, so hiding anything outside the player's own network would make the list disagree with the
/// map the user is looking at. It requests <c>scope=all</c> and filters to its own viewport on the
/// client, from data it already holds, so panning costs nothing and never touches the feed. The
/// in-game panel has no map at all and asks "who is controlling where I fly", so it takes the
/// default network scope - the right answer where there is no viewport to speak of.</para>
///
/// <para><see cref="VatsimAtcController.InNetwork"/> carries what used to be the filter: whether
/// the position covers an airport the player actually serves (for a sector, whether the boundary
/// geometrically contains one). Under <c>scope=all</c> that stops being a reason to hide anyone and
/// becomes emphasis instead - a controller at your own airport is more interesting than a
/// neighbouring one, but not to the exclusion of someone the user can plainly see on screen.</para>
/// </summary>
public static class VatsimEndpoints
{
    /// <summary>Facility ids from the VATSIM feed that work published airspace rather than an
    /// airport. Everything else is treated as airport-local.</summary>
    private const int FlightServiceStationFacilityId = 1;
    private const int CenterFacilityId = 6;

    /// <summary>Wire values for <see cref="VatsimAtcController.CoverageKind"/>. Strings rather
    /// than an enum so the JSON contract reads plainly on the client and adding a third kind later
    /// is not a breaking numeric change.</summary>
    internal const string TerminalCoverage = "terminal";
    internal const string SectorCoverage = "sector";

    public static void MapVatsimEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/operations/atc", GetAtcAsync);
    }

    /// <param name="geometry">Opt-in: boundary polygons are only serialised when a client actually
    /// intends to draw them. The dashboard's controller list and any future panel view want the
    /// same controller data without tens of kilobytes of coordinates they would throw away, and
    /// the in-game panel is deliberately map-free.</param>
    /// <param name="scope">"all" for every controller FSOps can place anywhere in the world - what
    /// a map needs, since the user can pan anywhere. Anything else (including absent) keeps the
    /// network-only scope, which is what a consumer with no viewport should get.</param>
    /// <remarks>
    /// <see cref="IAtcBoundarySource"/> is explicitly <c>[FromServices]</c>. Without it, minimal-API
    /// binding treats an unrecognised interface parameter as an inferred request body, and a GET
    /// may not have one - so the app throws "Body was inferred but the method does not allow
    /// inferred body parameters" **at endpoint-mapping time**, killing the whole server on startup
    /// rather than just failing this route. It compiles perfectly either way, which is precisely
    /// what makes it worth an attribute and a comment instead of a lesson re-learned.
    /// </remarks>
    internal static async Task<IResult> GetAtcAsync(
        [FromServices] IVatsimNetworkClient vatsim,
        [FromServices] IAtcBoundarySource boundarySource,
        [FromServices] FsOpsDbContext db,
        [FromServices] ICurrentUser currentUser,
        bool? geometry,
        string? scope,
        CancellationToken ct)
    {
        var worldScope = string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase);

        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Empty("ok", null);
        }

        // Materialise the route rows first, then flatten/de-duplicate in memory - EF Core cannot
        // translate SelectMany over an array literal, and against SQLite this throws at runtime
        // rather than failing to compile, so every airline with an active route would get a 500.
        // Same family as SumAsync over decimal and OrderBy over DateTimeOffset: read the rows, do
        // the set work in C#.
        var activeRoutes = await db.Routes
            .Where(r => r.AirlineId == airline.Id && r.IsActive)
            .Select(r => new { r.DepartureIcao, r.ArrivalIcao })
            .ToListAsync(ct);
        var networkIcaos = activeRoutes
            .SelectMany(r => new[] { r.DepartureIcao, r.ArrivalIcao })
            .Distinct()
            .ToList();

        if (networkIcaos.Count == 0)
        {
            // Nothing to show ATC coverage for yet - not the same outcome as VATSIM being
            // unreachable, and deliberately skips the fetch entirely (see VatsimNetworkClient's
            // class doc on backing off when nothing is listening). This holds under either scope:
            // with no routes the dashboard draws no map at all, so there is no viewport to fill.
            return Empty("ok", null);
        }

        var snapshot = await vatsim.GetSnapshotAsync(ct);

        if (!snapshot.Available)
        {
            return Empty("unavailable", snapshot.FetchedAtUtc);
        }

        var networkIcaoSet = networkIcaos.ToHashSet(StringComparer.Ordinal);

        // Airport-shaped callsign prefixes worth looking up. Under the network scope that is just
        // the network; under the world scope it is every airport the feed actually names, which is
        // a few hundred rows at busy times rather than the whole airport table.
        var candidateIcaos = networkIcaos.ToHashSet(StringComparer.Ordinal);
        if (worldScope)
        {
            foreach (var controller in snapshot.Controllers)
            {
                var icao = AirportIcaoFromCallsign(controller.Callsign);
                if (icao is not null) candidateIcaos.Add(icao);
            }
        }

        var candidateList = candidateIcaos.ToList();
        var airports = await db.Airports
            .Where(a => candidateList.Contains(a.Icao))
            .Select(a => new AirportLocation(a.Icao, a.Name, a.Latitude, a.Longitude))
            .ToListAsync(ct);
        var airportsByIcao = airports.ToDictionary(a => a.Icao, StringComparer.Ordinal);
        // Sector relevance is still measured against the player's own airports - that is what
        // InNetwork means - so this stays the network subset even under the world scope.
        var networkAirports = airports.Where(a => networkIcaoSet.Contains(a.Icao)).ToList();

        var controllers = new List<VatsimAtcController>();
        var boundaries = new Dictionary<string, double[][][][]>(StringComparer.Ordinal);

        foreach (var controller in snapshot.Controllers)
        {
            var resolved = controller.FacilityId is CenterFacilityId or FlightServiceStationFacilityId
                ? ResolveSector(controller, boundarySource, networkAirports)
                : ResolveTerminal(controller, airportsByIcao, networkIcaoSet);

            if (resolved is null)
            {
                // FSOps cannot place this controller at all - no airport it knows, no published
                // boundary. Left out rather than shown unplaced, under either scope: silence is
                // the honest representation of "we don't know". See the class doc.
                continue;
            }

            // Under the network scope, being outside the player's network is still a reason to
            // hide. Under the world scope it is only a reason to de-emphasise, because the user
            // can see the shape on the map and a list that omitted it would contradict the map.
            if (!worldScope && !resolved.Controller.InNetwork)
            {
                continue;
            }

            controllers.Add(resolved.Controller);

            // De-duplicated by boundary id: two controllers splitting one FIR (LON_N_CTR and
            // LON_S_CTR) share one published region, and sending it twice would both waste bytes
            // and invite the map into drawing two overlapping shapes where there is one.
            if (resolved.Boundary is not null && !boundaries.ContainsKey(resolved.Boundary.Id))
            {
                boundaries[resolved.Boundary.Id] = AtcBoundaryGeometry.ToMultiPolygonCoordinates(resolved.Boundary);
            }
        }

        var ordered = controllers.OrderBy(c => c.Callsign, StringComparer.Ordinal).ToList();
        var includeGeometry = geometry == true && boundaries.Count > 0;

        return Results.Ok(new VatsimAtcResponse(
            "ok",
            snapshot.FetchedAtUtc,
            ordered,
            includeGeometry ? boundaries : null));
    }

    private static IResult Empty(string status, DateTimeOffset? fetchedAtUtc) =>
        Results.Ok(new VatsimAtcResponse(status, fetchedAtUtc, Array.Empty<VatsimAtcController>(), null));

    /// <summary>Airport-local positions: the airport's own position plus the feed's visual range,
    /// which the client draws as an explicitly approximate circle.</summary>
    private static ResolvedController? ResolveTerminal(
        VatsimController controller,
        IReadOnlyDictionary<string, AirportLocation> airports,
        IReadOnlySet<string> networkIcaos)
    {
        var icao = AirportIcaoFromCallsign(controller.Callsign);
        if (icao is null || !airports.TryGetValue(icao, out var airport))
        {
            return null;
        }

        return new ResolvedController(
            new VatsimAtcController(
                controller.Callsign,
                FacilityLabel(controller.FacilityId),
                controller.Frequency,
                airport.Icao,
                airport.Name,
                airport.Latitude,
                airport.Longitude,
                controller.VisualRangeNm,
                controller.LogonTimeUtc,
                TerminalCoverage,
                null,
                null,
                networkIcaos.Contains(airport.Icao)),
            null);
    }

    /// <summary>
    /// En-route positions: resolve the published boundary. An unresolvable callsign returns null
    /// and the controller is dropped, which is precisely what FSOps did for every CTR/FSS before
    /// boundary data existed - "we do not know" must keep looking like silence, not like a shape.
    /// Whether the boundary contains one of the player's own airports decides emphasis, and under
    /// the network scope also inclusion, but never whether the geometry itself is trustworthy.
    /// </summary>
    private static ResolvedController? ResolveSector(
        VatsimController controller, IAtcBoundarySource boundarySource, IReadOnlyList<AirportLocation> networkAirports)
    {
        if (!boundarySource.Available)
        {
            return null;
        }

        var boundary = boundarySource.Resolve(controller.Callsign);
        if (boundary is null)
        {
            return null;
        }

        var inNetwork = networkAirports.Any(a => AtcBoundaryGeometry.Contains(boundary, a.Latitude, a.Longitude));

        return new ResolvedController(
            new VatsimAtcController(
                controller.Callsign,
                FacilityLabel(controller.FacilityId),
                controller.Frequency,
                // No airport and no coordinates: a sector is not at a point, and a marker would
                // put one where nobody is. The client keys its marker layer off these being null.
                null,
                null,
                null,
                null,
                controller.VisualRangeNm,
                controller.LogonTimeUtc,
                SectorCoverage,
                boundary.Id,
                boundary.Name,
                inNetwork),
            boundary);
    }

    /// <summary>The callsign segment before the first underscore, when it looks like an ICAO code
    /// (exactly four letters). TRACON callsigns ("NY_APP") don't match and correctly resolve to no
    /// position - see the class doc.</summary>
    private static string? AirportIcaoFromCallsign(string callsign)
    {
        var separator = callsign.IndexOf('_');
        var candidate = separator < 0 ? callsign : callsign[..separator];
        return candidate.Length == 4 && candidate.All(char.IsAsciiLetterUpper) ? candidate : null;
    }

    private static string FacilityLabel(int facilityId) => facilityId switch
    {
        1 => "Flight Service Station",
        2 => "Clearance Delivery",
        3 => "Ground",
        4 => "Tower",
        5 => "Approach/Departure",
        6 => "Center",
        _ => "Controller",
    };

    private sealed record AirportLocation(string Icao, string Name, double Latitude, double Longitude);

    /// <summary>A controller that survived filtering, plus the boundary geometry it needs (sector
    /// positions only) so the caller can collect and de-duplicate it.</summary>
    private sealed record ResolvedController(VatsimAtcController Controller, AtcBoundary? Boundary);
}

/// <param name="Boundaries">Boundary id -&gt; GeoJSON MultiPolygon <c>coordinates</c>, present only
/// when the caller asked for geometry and something actually has some. Sent once per boundary and
/// referenced by <see cref="VatsimAtcController.BoundaryId"/> rather than inlined per controller.</param>
public sealed record VatsimAtcResponse(
    string Status,
    DateTimeOffset? FetchedAtUtc,
    IReadOnlyList<VatsimAtcController> Controllers,
    IReadOnlyDictionary<string, double[][][][]>? Boundaries);

/// <param name="CoverageKind">"terminal" - airport-local, position is the airport and any circle
/// drawn from <paramref name="VisualRangeNm"/> is an approximation; "sector" - real published
/// boundary geometry, no position.</param>
/// <param name="InNetwork">Whether this position covers an airport the player actually serves - the
/// airport itself for terminal coverage, or a boundary containing one for a sector. Emphasis, not
/// a filter, under <c>scope=all</c>; under the default network scope nothing else is returned at
/// all, so it is always true there.</param>
public sealed record VatsimAtcController(
    string Callsign,
    string FacilityLabel,
    string Frequency,
    string? AirportIcao,
    string? AirportName,
    double? LatitudeDeg,
    double? LongitudeDeg,
    double? VisualRangeNm,
    DateTimeOffset LogonTimeUtc,
    string CoverageKind,
    string? BoundaryId,
    string? BoundaryName,
    bool InNetwork);
