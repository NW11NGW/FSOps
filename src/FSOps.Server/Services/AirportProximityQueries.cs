using FSOps.Core.Entities;
using FSOps.Data;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Services;

/// <summary>
/// Loads a cheap, SQL-filtered candidate set of airports around a position before handing them to
/// <see cref="FSOps.Core.Flights.LandingAirportResolver"/> for the exact great-circle check - keeps
/// flight completion from ever pulling the whole world airport table into memory just to find the
/// one nearest a landing.
/// </summary>
internal static class AirportProximityQueries
{
    // Comfortably wider than LandingAirportResolver.SearchRadiusNm so the cheap SQL box never
    // excludes a candidate the exact distance check would have accepted - one degree of latitude
    // is about 60 nm.
    private const double BoxDegrees = 1.0;

    public static async Task<List<Airport>> NearbyAsync(FsOpsDbContext db, double latitudeDeg, double longitudeDeg, CancellationToken ct)
    {
        // Longitude degrees shrink towards the poles, so widen the box to compensate; capped so it
        // never blows up near +/-90.
        var lonScale = Math.Max(0.2, Math.Cos(latitudeDeg * Math.PI / 180.0));
        var lonBoxDegrees = BoxDegrees / lonScale;

        return await db.Airports
            .Where(a => a.Latitude >= latitudeDeg - BoxDegrees && a.Latitude <= latitudeDeg + BoxDegrees &&
                        a.Longitude >= longitudeDeg - lonBoxDegrees && a.Longitude <= longitudeDeg + lonBoxDegrees)
            .ToListAsync(ct);
    }
}
