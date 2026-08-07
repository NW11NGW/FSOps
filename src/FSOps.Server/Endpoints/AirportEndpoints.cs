using FSOps.Core.Airports;
using FSOps.Core.Entities;
using FSOps.Data;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Endpoints;

public static class AirportEndpoints
{
    private const int MaxQueryLength = 200;
    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;
    private const int CandidatePoolSize = 500;

    public static void MapAirportEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/airports/search", SearchAsync);
        group.MapGet("/airports/{icao}", GetByIcaoAsync);
    }

    private static async Task<IResult> SearchAsync(string? q, int? limit, FsOpsDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return Results.BadRequest(new { error = "q is required" });
        }

        q = q.Trim();
        if (q.Length > MaxQueryLength)
        {
            return Results.BadRequest(new { error = $"q must be {MaxQueryLength} characters or fewer" });
        }

        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        var prefixPattern = $"{EscapeLike(q)}%";
        var containsPattern = $"%{EscapeLike(q)}%";

        // Pull a bounded, broadly-matching candidate set straight from SQLite (case-insensitive
        // by default for ASCII LIKE), then apply the real ranking rules in memory - that logic
        // is shared with the unit tests and easier to reason about than hand-written SQL ORDER BY.
        var candidates = await db.Airports
            .Where(a =>
                EF.Functions.Like(a.Icao, prefixPattern, "\\") ||
                (a.Iata != null && EF.Functions.Like(a.Iata, prefixPattern, "\\")) ||
                EF.Functions.Like(a.Name, containsPattern, "\\") ||
                EF.Functions.Like(a.Municipality, containsPattern, "\\"))
            .Take(CandidatePoolSize)
            .ToListAsync(ct);

        var results = AirportSearchRanking.Order(candidates, q).Take(take).Select(ToSummary);
        return Results.Ok(results);
    }

    private static async Task<IResult> GetByIcaoAsync(string icao, FsOpsDbContext db, CancellationToken ct)
    {
        var code = icao.Trim().ToUpperInvariant();
        var airport = await db.Airports
            .Include(a => a.Runways)
            .FirstOrDefaultAsync(a => a.Icao == code, ct);

        if (airport is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(new
        {
            airport.Icao,
            airport.Iata,
            airport.Name,
            airport.Municipality,
            airport.Country,
            airport.Latitude,
            airport.Longitude,
            airport.ElevationFt,
            airport.SizeCategory,
            airport.HasScheduledService,
            airport.LongestRunwayFt,
            Runways = airport.Runways.Select(r => new
            {
                r.Designator,
                r.LengthFt,
                r.WidthFt,
                r.Surface,
                r.HeadingTrue,
                r.IsLighted,
                r.IsClosed,
            }),
        });
    }

    private static object ToSummary(Airport a) => new
    {
        a.Icao,
        a.Iata,
        a.Name,
        a.Municipality,
        a.Country,
        a.Latitude,
        a.Longitude,
        a.ElevationFt,
        a.SizeCategory,
        a.HasScheduledService,
        a.LongestRunwayFt,
    };

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
