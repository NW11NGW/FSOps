using FSOps.Core.Entities;

namespace FSOps.Core.Flights;

/// <summary>
/// Works out how far off the runway centreline a touchdown landed, in metres. Pure spherical/flat-
/// earth maths - no I/O - so it can never throw on bad or missing data; every failure mode (no
/// runways, no threshold coordinates, a degenerate runway) just returns null rather than crashing
/// a flight completion.
/// </summary>
public static class LandingQualityCalculator
{
    private const double MetresPerDegreeLatitude = 111_320.0;

    /// <param name="candidateRunways">Runways already filtered to the arrival airport.</param>
    /// <param name="touchdownLatitudeDeg">Where the aircraft made contact.</param>
    /// <param name="touchdownLongitudeDeg">Where the aircraft made contact.</param>
    /// <param name="trackHeadingDeg">Aircraft track at contact, used to pick the runway/end actually used.</param>
    /// <returns>Perpendicular distance from the runway centreline in metres, or null if it can't be determined.</returns>
    public static double? CentrelineDeviationMetres(
        IReadOnlyList<Runway> candidateRunways,
        double touchdownLatitudeDeg,
        double touchdownLongitudeDeg,
        double trackHeadingDeg)
    {
        var usable = candidateRunways
            .Where(r => r.LatitudeStart is not null && r.LongitudeStart is not null &&
                        r.LatitudeEnd is not null && r.LongitudeEnd is not null)
            .ToList();

        if (usable.Count == 0)
        {
            return null;
        }

        // A runway is landed on from either end, so compare against its orientation as an
        // undirected line (0-180 degrees) rather than requiring an exact heading/reciprocal match.
        var best = usable
            .OrderBy(r => UndirectedHeadingDifferenceDeg(r.HeadingTrue, trackHeadingDeg))
            .First();

        return PerpendicularDistanceMetres(
            best.LatitudeStart!.Value, best.LongitudeStart!.Value,
            best.LatitudeEnd!.Value, best.LongitudeEnd!.Value,
            touchdownLatitudeDeg, touchdownLongitudeDeg);
    }

    private static double UndirectedHeadingDifferenceDeg(double runwayHeadingDeg, double trackHeadingDeg)
    {
        var runwayLine = Normalise180(runwayHeadingDeg);
        var trackLine = Normalise180(trackHeadingDeg);
        var diff = Math.Abs(runwayLine - trackLine);
        return Math.Min(diff, 180 - diff);
    }

    private static double Normalise180(double headingDeg)
    {
        var h = headingDeg % 180.0;
        return h < 0 ? h + 180.0 : h;
    }

    private static double? PerpendicularDistanceMetres(
        double startLat, double startLon, double endLat, double endLon, double pointLat, double pointLon)
    {
        // Flat-earth projection centred on the runway threshold - fine at runway length/landing
        // dispersion scales (tens to low hundreds of metres), far below where earth curvature matters.
        var metresPerDegreeLongitude = MetresPerDegreeLatitude * Math.Cos(ToRadians(startLat));

        var x1 = 0.0;
        var y1 = 0.0;
        var x2 = (endLon - startLon) * metresPerDegreeLongitude;
        var y2 = (endLat - startLat) * MetresPerDegreeLatitude;
        var xp = (pointLon - startLon) * metresPerDegreeLongitude;
        var yp = (pointLat - startLat) * MetresPerDegreeLatitude;

        var dx = x2 - x1;
        var dy = y2 - y1;
        var lengthMetres = Math.Sqrt(dx * dx + dy * dy);

        if (lengthMetres < 1.0)
        {
            // Degenerate runway record (start == end) - fall back to straight-line distance to the
            // single point we do have rather than dividing by ~zero.
            return Math.Sqrt(xp * xp + yp * yp);
        }

        return Math.Abs((dy * xp) - (dx * yp)) / lengthMetres;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
