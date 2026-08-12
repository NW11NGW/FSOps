using FSOps.Server.Services;

namespace FSOps.Server.Tests;

/// <summary>
/// Point-in-boundary and the wire conversion, against hand-written shapes with answers anyone can
/// check by eye. This is the arithmetic that decides whether an en-route controller is shown at
/// all, so it is worth being exactly right rather than approximately right.
/// </summary>
public class AtcBoundaryGeometryTests
{
    private static IReadOnlyList<GeoPoint> Ring(params (double Lon, double Lat)[] points) =>
        points.Select(p => new GeoPoint(p.Lon, p.Lat)).ToArray();

    /// <summary>A 10x10 degree box from (0,0) to (10,10).</summary>
    private static AtcBoundary Square(string id = "BOX") => new(
        id,
        "Test Box",
        new[]
        {
            new AtcBoundaryPolygon(new[]
            {
                Ring((0, 0), (10, 0), (10, 10), (0, 10), (0, 0)),
            }),
        });

    [Theory]
    [InlineData(5, 5)]      // dead centre
    [InlineData(0.01, 0.01)] // just inside a corner
    [InlineData(9.99, 5)]    // just inside an edge
    public void Contains_PointInsideTheOuterRing_IsTrue(double latitude, double longitude)
    {
        Assert.True(AtcBoundaryGeometry.Contains(Square(), latitude, longitude));
    }

    [Theory]
    [InlineData(5, 10.5)]
    [InlineData(-0.5, 5)]
    [InlineData(50, 50)]
    [InlineData(5, -0.01)]
    public void Contains_PointOutsideTheOuterRing_IsFalse(double latitude, double longitude)
    {
        Assert.False(AtcBoundaryGeometry.Contains(Square(), latitude, longitude));
    }

    [Fact]
    public void Contains_PointInsideAHole_IsFalse()
    {
        // A region delegated out of the middle of a larger one. Treating the hole as solid would
        // claim coverage that is explicitly not there.
        var withHole = new AtcBoundary("HOLED", "Box with a hole", new[]
        {
            new AtcBoundaryPolygon(new[]
            {
                Ring((0, 0), (10, 0), (10, 10), (0, 10), (0, 0)),
                Ring((4, 4), (6, 4), (6, 6), (4, 6), (4, 4)),
            }),
        });

        Assert.False(AtcBoundaryGeometry.Contains(withHole, 5, 5));   // in the hole
        Assert.True(AtcBoundaryGeometry.Contains(withHole, 2, 2));    // in the ring, outside the hole
        Assert.True(AtcBoundaryGeometry.Contains(withHole, 8.5, 8.5));
    }

    [Fact]
    public void Contains_MultiPolygon_MatchesAnyOfItsParts()
    {
        // Two disjoint areas under one callsign - a genuinely split FIR, a UIR made of several
        // FIRs, or the two halves of a region that RFC7946 requires be split at the antimeridian.
        var split = new AtcBoundary("SPLIT", "Two parts", new[]
        {
            new AtcBoundaryPolygon(new[] { Ring((0, 0), (10, 0), (10, 10), (0, 10), (0, 0)) }),
            new AtcBoundaryPolygon(new[] { Ring((50, 50), (60, 50), (60, 60), (50, 60), (50, 50)) }),
        });

        Assert.True(AtcBoundaryGeometry.Contains(split, 5, 5));
        Assert.True(AtcBoundaryGeometry.Contains(split, 55, 55));
        Assert.False(AtcBoundaryGeometry.Contains(split, 30, 30)); // between the two parts
    }

    [Fact]
    public void Contains_ConcaveShape_DoesNotClaimTheNotchBetweenItsArms()
    {
        // A bounding-box shortcut would wrongly claim the notch. Real FIRs are full of these.
        var uShape = new AtcBoundary("U", "U shape", new[]
        {
            new AtcBoundaryPolygon(new[]
            {
                Ring((0, 0), (10, 0), (10, 10), (7, 10), (7, 3), (3, 3), (3, 10), (0, 10), (0, 0)),
            }),
        });

        Assert.True(AtcBoundaryGeometry.Contains(uShape, 1, 5));   // the base
        Assert.True(AtcBoundaryGeometry.Contains(uShape, 8, 1.5)); // the left arm
        Assert.True(AtcBoundaryGeometry.Contains(uShape, 8, 8.5)); // the right arm
        Assert.False(AtcBoundaryGeometry.Contains(uShape, 8, 5));  // the notch between them
    }

    [Fact]
    public void Contains_NegativeLongitudes_BehaveTheSame()
    {
        // Most of what FSOps' own test airports sit in is west of Greenwich, so this is not an
        // edge case so much as the common case.
        var westerly = new AtcBoundary("EGPX", "Scottish", new[]
        {
            new AtcBoundaryPolygon(new[] { Ring((-8, 54), (0, 54), (0, 59), (-8, 59), (-8, 54)) }),
        });

        Assert.True(AtcBoundaryGeometry.Contains(westerly, 55.95, -3.3725));  // EGPH
        Assert.True(AtcBoundaryGeometry.Contains(westerly, 55.8719, -4.4331)); // EGPF
        Assert.False(AtcBoundaryGeometry.Contains(westerly, 51.3827, -2.7191)); // EGGD, too far south
        Assert.False(AtcBoundaryGeometry.Contains(westerly, 51.8860, 0.2389));  // EGSS
    }

    [Fact]
    public void Contains_BoundaryWithNoGeometry_IsFalseRatherThanThrowing()
    {
        var empty = new AtcBoundary("EMPTY", "Nothing", Array.Empty<AtcBoundaryPolygon>());
        Assert.False(AtcBoundaryGeometry.Contains(empty, 5, 5));

        var emptyRings = new AtcBoundary("EMPTY", "Nothing", new[]
        {
            new AtcBoundaryPolygon(Array.Empty<IReadOnlyList<GeoPoint>>()),
        });
        Assert.False(AtcBoundaryGeometry.Contains(emptyRings, 5, 5));
    }

    [Fact]
    public void ToMultiPolygonCoordinates_ProducesGeoJsonShapeInLonLatOrder()
    {
        var coordinates = AtcBoundaryGeometry.ToMultiPolygonCoordinates(Square());

        var polygon = Assert.Single(coordinates);
        var ring = Assert.Single(polygon);
        Assert.Equal(5, ring.Length);
        // Lon first, matching GeoJSON, so nothing has to be flipped on the client.
        Assert.Equal(new[] { 0.0, 0.0 }, ring[0]);
        Assert.Equal(new[] { 10.0, 0.0 }, ring[1]);
        Assert.Equal(new[] { 10.0, 10.0 }, ring[2]);
        Assert.Equal(ring[0], ring[^1]);
    }

    [Fact]
    public void ToMultiPolygonCoordinates_RoundsToKeepTheResponseSmall()
    {
        var precise = new AtcBoundary("P", "Precise", new[]
        {
            new AtcBoundaryPolygon(new[]
            {
                Ring((1.234567891, 2.345678912), (3, 2.345678912), (3, 4), (1.234567891, 4), (1.234567891, 2.345678912)),
            }),
        });

        var ring = AtcBoundaryGeometry.ToMultiPolygonCoordinates(precise)[0][0];

        // Four places is about 11 metres - invisible against a boundary hundreds of kilometres
        // across, and a lot of bytes saved. This is display payload, never navigation data.
        Assert.Equal(new[] { 1.2346, 2.3457 }, ring[0]);
    }

    [Fact]
    public void ToMultiPolygonCoordinates_KeepsHolesAndSeparatePolygons()
    {
        var complex = new AtcBoundary("C", "Complex", new[]
        {
            new AtcBoundaryPolygon(new[]
            {
                Ring((0, 0), (10, 0), (10, 10), (0, 10), (0, 0)),
                Ring((4, 4), (6, 4), (6, 6), (4, 6), (4, 4)),
            }),
            new AtcBoundaryPolygon(new[] { Ring((50, 50), (60, 50), (60, 60), (50, 60), (50, 50)) }),
        });

        var coordinates = AtcBoundaryGeometry.ToMultiPolygonCoordinates(complex);

        Assert.Equal(2, coordinates.Length);
        Assert.Equal(2, coordinates[0].Length); // outer ring plus the hole, in that order
        Assert.Single(coordinates[1]);
    }
}
