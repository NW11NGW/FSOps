namespace FSOps.Core.Planning;

/// <summary>
/// Pure spherical-earth navigation maths - no I/O, no entity dependencies. Distances are in
/// nautical miles throughout, matching how the rest of FSOps thinks about range and speed.
/// </summary>
public static class GreatCircle
{
    private const double EarthRadiusNm = 3440.065;

    public static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
    {
        var phi1 = ToRadians(lat1);
        var phi2 = ToRadians(lat2);
        var deltaPhi = ToRadians(lat2 - lat1);
        var deltaLambda = ToRadians(lon2 - lon1);

        var a = Math.Sin(deltaPhi / 2) * Math.Sin(deltaPhi / 2) +
                Math.Cos(phi1) * Math.Cos(phi2) *
                Math.Sin(deltaLambda / 2) * Math.Sin(deltaLambda / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return EarthRadiusNm * c;
    }

    public static double InitialBearingDeg(double lat1, double lon1, double lat2, double lon2)
    {
        var phi1 = ToRadians(lat1);
        var phi2 = ToRadians(lat2);
        var deltaLambda = ToRadians(lon2 - lon1);

        var y = Math.Sin(deltaLambda) * Math.Cos(phi2);
        var x = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(deltaLambda);
        var thetaDeg = ToDegrees(Math.Atan2(y, x));

        return (thetaDeg + 360) % 360;
    }

    /// <summary>
    /// Samples points along the true great-circle path between two points, for drawing curved
    /// route arcs on a map. Returns raw longitudes, which can jump across +/-180 degrees when
    /// the path crosses the antimeridian - callers drawing this on a map must split the line
    /// into separate segments wherever consecutive longitudes differ by more than 180 degrees,
    /// rather than expecting this method to normalise that itself.
    /// </summary>
    public static IReadOnlyList<(double Lon, double Lat)> SamplePath(
        double lat1, double lon1, double lat2, double lon2, int sampleCount)
    {
        if (sampleCount < 2)
        {
            sampleCount = 2;
        }

        var points = new List<(double Lon, double Lat)>(sampleCount);

        var phi1 = ToRadians(lat1);
        var lambda1 = ToRadians(lon1);
        var phi2 = ToRadians(lat2);
        var lambda2 = ToRadians(lon2);

        var distanceNm = DistanceNm(lat1, lon1, lat2, lon2);
        var angularDistance = distanceNm / EarthRadiusNm;
        var sinAngular = Math.Sin(angularDistance);

        for (var i = 0; i < sampleCount; i++)
        {
            var f = (double)i / (sampleCount - 1);

            // Coincident or (extremely unlikely) near-antipodal endpoints make the slerp
            // formula divide by ~zero - fall straight to linear interpolation instead of
            // crashing, since a route preview must never throw.
            if (Math.Abs(sinAngular) < 1e-9)
            {
                points.Add((Lerp(lon1, lon2, f), Lerp(lat1, lat2, f)));
                continue;
            }

            var a = Math.Sin((1 - f) * angularDistance) / sinAngular;
            var b = Math.Sin(f * angularDistance) / sinAngular;

            var x = a * Math.Cos(phi1) * Math.Cos(lambda1) + b * Math.Cos(phi2) * Math.Cos(lambda2);
            var y = a * Math.Cos(phi1) * Math.Sin(lambda1) + b * Math.Cos(phi2) * Math.Sin(lambda2);
            var z = a * Math.Sin(phi1) + b * Math.Sin(phi2);

            var lat = Math.Atan2(z, Math.Sqrt(x * x + y * y));
            var lon = Math.Atan2(y, x);

            points.Add((ToDegrees(lon), ToDegrees(lat)));
        }

        return points;
    }

    private static double Lerp(double a, double b, double f) => a + ((b - a) * f);

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static double ToDegrees(double radians) => radians * 180.0 / Math.PI;
}
