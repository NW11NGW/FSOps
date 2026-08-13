namespace FSOps.Server.Services;

/// <summary>A single lon/lat position in degrees. Lon first, matching GeoJSON's own axis order so
/// nothing has to be flipped on the way to the map.</summary>
public readonly record struct GeoPoint(double Lon, double Lat);

/// <summary>
/// One closed area: ring 0 is the outer ring, any further rings are holes (RFC7946 order). Holes
/// are rare in FIR data but they do occur - a region delegated out of the middle of a larger one -
/// and treating a hole as solid would claim coverage that is explicitly not there.
/// </summary>
public sealed record AtcBoundaryPolygon(IReadOnlyList<IReadOnlyList<GeoPoint>> Rings);

/// <summary>
/// The controlled airspace behind one en-route callsign, as real published geometry rather than a
/// stand-in shape. <see cref="Polygons"/> is a multi-polygon because two separate things both need
/// it: an FIR that is genuinely split into disjoint areas (or split at the antimeridian, which
/// RFC7946 requires), and a UIR callsign whose coverage is the union of several FIR boundaries.
///
/// What this deliberately does NOT carry, because the source data does not contain it: any
/// vertical extent. A boundary here is a lateral footprint and nothing else. Nothing built on it
/// may claim an altitude band.
/// </summary>
public sealed record AtcBoundary(string Id, string Name, IReadOnlyList<AtcBoundaryPolygon> Polygons);

/// <summary>
/// Resolves an en-route controller callsign to the published boundary it is working, using data
/// bundled with FSOps rather than a network call - FSOps must work with no internet at all, so
/// anything fetched at runtime would simply be absent when it is most wanted.
///
/// <para><see cref="Resolve"/> returning null is an ordinary, expected outcome and means exactly
/// "FSOps does not know what this callsign covers". Callers must then leave the controller out
/// entirely, which is what FSOps did for every en-route position before this existed. Guessing a
/// shape - a circle, a bounding box, the nearest airport - would look like knowledge and be
/// none.</para>
/// </summary>
public interface IAtcBoundarySource
{
    /// <summary>False when the bundled boundary data is missing or unreadable (a stripped
    /// deployment, a corrupt file). Callers degrade to "no en-route controllers shown", never to
    /// an error - this must never disadvantage anyone, and least of all someone flying offline.</summary>
    bool Available { get; }

    /// <summary>The boundary this callsign is working, or null when it cannot be resolved.</summary>
    AtcBoundary? Resolve(string callsign);
}

/// <summary>
/// Pure geometry helpers over <see cref="AtcBoundary"/> - no I/O, no data source, so they are
/// testable against a hand-written fixture shape with exact expected answers.
/// </summary>
public static class AtcBoundaryGeometry
{
    /// <summary>
    /// Whether a point lies inside the boundary: inside some polygon's outer ring and not inside
    /// any of that polygon's holes.
    ///
    /// <para>Even-odd ray casting in the plain lon/lat plane. That is correct here because RFC7946
    /// requires geometry crossing the antimeridian to be split into separate polygons on each side,
    /// so no single ring is expected to span the seam; a ring that did would be the one case this
    /// gets wrong, and the honest consequence is a missed match (a controller not shown) rather
    /// than a fabricated one.</para>
    /// </summary>
    public static bool Contains(AtcBoundary boundary, double latitude, double longitude)
    {
        foreach (var polygon in boundary.Polygons)
        {
            if (polygon.Rings.Count == 0 || !RingContains(polygon.Rings[0], latitude, longitude))
            {
                continue;
            }

            var inHole = false;
            for (var i = 1; i < polygon.Rings.Count; i++)
            {
                if (RingContains(polygon.Rings[i], latitude, longitude))
                {
                    inHole = true;
                    break;
                }
            }

            if (!inHole)
            {
                return true;
            }
        }

        return false;
    }

    private static bool RingContains(IReadOnlyList<GeoPoint> ring, double latitude, double longitude)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[i];
            var b = ring[j];
            // Half-open latitude test (a.Lat > lat) != (b.Lat > lat) counts each crossing once, so
            // a point exactly level with a shared vertex isn't double-counted back to "outside".
            if ((a.Lat > latitude) != (b.Lat > latitude))
            {
                var crossingLon = (b.Lon - a.Lon) * (latitude - a.Lat) / (b.Lat - a.Lat) + a.Lon;
                if (longitude < crossingLon)
                {
                    inside = !inside;
                }
            }
        }

        return inside;
    }

    /// <summary>
    /// The boundary as GeoJSON MultiPolygon <c>coordinates</c> (polygon -> ring -> position ->
    /// [lon, lat]), ready to hand straight to MapLibre without reshaping on the client.
    ///
    /// <para>Coordinates are rounded to <paramref name="decimals"/> places purely to keep the
    /// response small - four places is about 11 metres, which is invisible against a boundary
    /// hundreds of kilometres across but removes a lot of bytes. This is a display payload, never
    /// navigation data.</para>
    /// </summary>
    public static double[][][][] ToMultiPolygonCoordinates(AtcBoundary boundary, int decimals = 4)
    {
        var polygons = new double[boundary.Polygons.Count][][][];
        for (var p = 0; p < boundary.Polygons.Count; p++)
        {
            var rings = boundary.Polygons[p].Rings;
            var ringArrays = new double[rings.Count][][];
            for (var r = 0; r < rings.Count; r++)
            {
                var ring = rings[r];
                var positions = new double[ring.Count][];
                for (var i = 0; i < ring.Count; i++)
                {
                    positions[i] = new[]
                    {
                        Math.Round(ring[i].Lon, decimals),
                        Math.Round(ring[i].Lat, decimals),
                    };
                }

                ringArrays[r] = positions;
            }

            polygons[p] = ringArrays;
        }

        return polygons;
    }
}
