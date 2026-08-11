using FSOps.Data;
using FSOps.Server.Auth;
using FSOps.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Endpoints;

/// <summary>
/// Backs the dashboard's ATC layer - online VATSIM controllers plotted alongside the player's own
/// aircraft on the live operations map, answering "is anyone controlling where I am flying right
/// now". Deliberately narrower than the full network-traffic integration sketched in docs/PLAN.md
/// "VATSIM integration": this shows controllers only, never other pilots, and only controllers
/// relevant to the player - not a global VATSIM controller list.
///
/// "Relevant" means covering an airport in the player's own route network (the same active-route
/// airport set /operations/live already draws as the network on this map) - matching the empty
/// state's own wording, "no controllers online near your network". Position is resolved locally
/// against FSOps' own Airports table by matching the callsign's leading ICAO-shaped segment
/// (EGLL_TWR -> EGLL). En-route/oceanic callsigns (CTR/FSS, and TRACON callsigns like NY_APP) never
/// map to a single airport, so they are left out entirely rather than shown unplaced or guessed at
/// - FSOps holds no FIR/sector boundary data that would let it say whether one of those actually
/// covers the player's network.
/// </summary>
public static class VatsimEndpoints
{
    public static void MapVatsimEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/operations/atc", GetAtcAsync);
    }

    internal static async Task<IResult> GetAtcAsync(
        IVatsimNetworkClient vatsim, FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(new VatsimAtcResponse("ok", null, Array.Empty<VatsimAtcController>()));
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
            // class doc on backing off when nothing is listening).
            return Results.Ok(new VatsimAtcResponse("ok", null, Array.Empty<VatsimAtcController>()));
        }

        var snapshot = await vatsim.GetSnapshotAsync(ct);

        if (!snapshot.Available)
        {
            return Results.Ok(new VatsimAtcResponse("unavailable", snapshot.FetchedAtUtc, Array.Empty<VatsimAtcController>()));
        }

        var networkIcaoSet = networkIcaos.ToHashSet(StringComparer.Ordinal);

        // Only controllers whose callsign resolves to one of the player's own network airports
        // survive this filter - see the class doc for why CTR/FSS and non-network airports are
        // dropped here rather than passed through unplaced.
        var relevant = snapshot.Controllers
            .Select(c => (Controller: c, Icao: AirportIcaoFromCallsign(c.Callsign)))
            .Where(x => x.Icao is not null && networkIcaoSet.Contains(x.Icao))
            .ToList();

        if (relevant.Count == 0)
        {
            return Results.Ok(new VatsimAtcResponse("ok", snapshot.FetchedAtUtc, Array.Empty<VatsimAtcController>()));
        }

        // Batch-resolve every candidate ICAO in one query rather than one round-trip per
        // controller.
        var candidateIcaos = relevant.Select(x => x.Icao!).Distinct().ToList();
        var airportsByIcao = await db.Airports
            .Where(a => candidateIcaos.Contains(a.Icao))
            .Select(a => new AirportLocation(a.Icao, a.Name, a.Latitude, a.Longitude))
            .ToDictionaryAsync(a => a.Icao, ct);

        var controllers = relevant
            .Select(x =>
            {
                var airport = airportsByIcao.TryGetValue(x.Icao!, out var found) ? found : null;

                return new VatsimAtcController(
                    x.Controller.Callsign,
                    FacilityLabel(x.Controller.FacilityId),
                    x.Controller.Frequency,
                    airport?.Icao,
                    airport?.Name,
                    airport?.Latitude,
                    airport?.Longitude,
                    x.Controller.VisualRangeNm,
                    x.Controller.LogonTimeUtc);
            })
            .OrderBy(c => c.Callsign, StringComparer.Ordinal)
            .ToList();

        return Results.Ok(new VatsimAtcResponse("ok", snapshot.FetchedAtUtc, controllers));
    }

    /// <summary>The callsign segment before the first underscore, when it looks like an ICAO code
    /// (exactly four letters). TRACON/FIR callsigns ("NY_APP", "LON_CTR", "SHANWICK_FSS") don't
    /// match and correctly resolve to no position - see the class doc.</summary>
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
}

public sealed record VatsimAtcResponse(string Status, DateTimeOffset? FetchedAtUtc, IReadOnlyList<VatsimAtcController> Controllers);

public sealed record VatsimAtcController(
    string Callsign,
    string FacilityLabel,
    string Frequency,
    string? AirportIcao,
    string? AirportName,
    double? LatitudeDeg,
    double? LongitudeDeg,
    double? VisualRangeNm,
    DateTimeOffset LogonTimeUtc);
