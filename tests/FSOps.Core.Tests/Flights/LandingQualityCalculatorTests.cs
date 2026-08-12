using FSOps.Core.Entities;
using FSOps.Core.Flights;

namespace FSOps.Core.Tests.Flights;

public class LandingQualityCalculatorTests
{
    private static Runway MakeRunway(double startLat, double startLon, double endLat, double endLon, double headingTrue) => new()
    {
        Id = Guid.NewGuid(),
        AirportIcao = "TEST",
        Designator = "09/27",
        HeadingTrue = headingTrue,
        LatitudeStart = startLat,
        LongitudeStart = startLon,
        LatitudeEnd = endLat,
        LongitudeEnd = endLon,
    };

    [Fact]
    public void CentrelineDeviation_TouchdownExactlyOnCentreline_IsZero()
    {
        // A short east-west runway near the equator so 1 degree of longitude is close to 1 degree
        // of latitude in metres, keeping the numbers easy to reason about.
        var runway = MakeRunway(0.0, 0.0, 0.0, 0.02, 90);
        var touchdownOnLine = (Lat: 0.0, Lon: 0.01);

        var deviation = LandingQualityCalculator.CentrelineDeviationMetres(
            new[] { runway }, touchdownOnLine.Lat, touchdownOnLine.Lon, trackHeadingDeg: 90);

        Assert.NotNull(deviation);
        Assert.InRange(deviation!.Value, 0, 0.5);
    }

    [Fact]
    public void CentrelineDeviation_TouchdownOffsetFromCentreline_ReportsApproximatelyTheOffsetDistance()
    {
        var runway = MakeRunway(0.0, 0.0, 0.0, 0.02, 90);
        // 0.001 degrees of latitude off the line is roughly 111 metres.
        var deviation = LandingQualityCalculator.CentrelineDeviationMetres(
            new[] { runway }, touchdownLatitudeDeg: 0.001, touchdownLongitudeDeg: 0.01, trackHeadingDeg: 90);

        Assert.NotNull(deviation);
        Assert.InRange(deviation!.Value, 100, 125);
    }

    [Fact]
    public void CentrelineDeviation_LandingFromTheOppositeDirection_StillMatchesTheSameRunway()
    {
        var runway = MakeRunway(0.0, 0.0, 0.0, 0.02, 90);

        var deviation = LandingQualityCalculator.CentrelineDeviationMetres(
            new[] { runway }, touchdownLatitudeDeg: 0.0, touchdownLongitudeDeg: 0.01, trackHeadingDeg: 270);

        Assert.NotNull(deviation);
        Assert.InRange(deviation!.Value, 0, 0.5);
    }

    [Fact]
    public void CentrelineDeviation_PicksTheRunwayWhoseHeadingBestMatchesTheTrack()
    {
        var runway09 = MakeRunway(0.0, 0.0, 0.0, 0.02, 90);
        var runway18 = MakeRunway(0.0, 0.0, 0.02, 0.0, 180);

        // Track close to 180 - should pick runway18 even though runway09 is listed first, and the
        // touchdown sits right on runway18's centreline (longitude 0) while well off runway09's.
        var deviation = LandingQualityCalculator.CentrelineDeviationMetres(
            new[] { runway09, runway18 }, touchdownLatitudeDeg: 0.01, touchdownLongitudeDeg: 0.0, trackHeadingDeg: 175);

        Assert.NotNull(deviation);
        Assert.InRange(deviation!.Value, 0, 0.5);
    }

    [Fact]
    public void CentrelineDeviation_TwoParallelRunwaysShareTheSameHeading_PicksThePhysicallyCloserOne()
    {
        // Reproduces the real LEBL defect (K33): 06L/24R and 06R/24L are two DIFFERENT physical
        // runways that OurAirports reports with an IDENTICAL rounded HeadingTrue (66.0/246.0), so
        // heading-match alone ties exactly between them - about 1,370 m apart in the real data.
        // "near" sits right on the near runway's centreline; "far" is the same heading, offset
        // sideways by roughly that real-world separation, standing in for the other parallel
        // runway. Track 250 matches both equally (undirected diff 4 degrees either way).
        var near = MakeRunway(41.293244, 2.067251, 41.305735, 2.103751, 66.0);
        var far = MakeRunway(41.282311, 2.074342, 41.292218, 2.103282, 66.0);

        var deviation = LandingQualityCalculator.CentrelineDeviationMetres(
            new[] { near, far }, touchdownLatitudeDeg: 41.2974, touchdownLongitudeDeg: 2.0833, trackHeadingDeg: 250);

        Assert.NotNull(deviation);
        Assert.InRange(deviation!.Value, 0, 300);
    }

    [Fact]
    public void CentrelineDeviation_TwoParallelRunwaysShareTheSameHeading_OrderInTheListDoesNotChangeWhichIsPicked()
    {
        // Same scenario as above but with the candidates in the opposite order - the bug this
        // guards against was exactly that .OrderBy(headingDiff).First() breaks an exact tie by
        // whatever order the caller's (unordered) database query happened to return, not by
        // anything about the landing. The result must be identical either way.
        var near = MakeRunway(41.293244, 2.067251, 41.305735, 2.103751, 66.0);
        var far = MakeRunway(41.282311, 2.074342, 41.292218, 2.103282, 66.0);

        var deviationNearFirst = LandingQualityCalculator.CentrelineDeviationMetres(
            new[] { near, far }, touchdownLatitudeDeg: 41.2974, touchdownLongitudeDeg: 2.0833, trackHeadingDeg: 250);
        var deviationFarFirst = LandingQualityCalculator.CentrelineDeviationMetres(
            new[] { far, near }, touchdownLatitudeDeg: 41.2974, touchdownLongitudeDeg: 2.0833, trackHeadingDeg: 250);

        Assert.NotNull(deviationNearFirst);
        Assert.NotNull(deviationFarFirst);
        Assert.Equal(deviationNearFirst!.Value, deviationFarFirst!.Value, precision: 6);
        // Both orderings must land on the near runway's small deviation, not the ~1,200 m gap to
        // the far one.
        Assert.InRange(deviationNearFirst.Value, 0, 300);
    }

    [Fact]
    public void CentrelineDeviation_NoRunwaysHaveCoordinates_ReturnsNullInsteadOfThrowing()
    {
        var runway = new Runway { Id = Guid.NewGuid(), AirportIcao = "TEST", HeadingTrue = 90 };

        var deviation = LandingQualityCalculator.CentrelineDeviationMetres(new[] { runway }, 0, 0, 90);

        Assert.Null(deviation);
    }

    [Fact]
    public void CentrelineDeviation_NoRunwaysAtAll_ReturnsNullInsteadOfThrowing()
    {
        var deviation = LandingQualityCalculator.CentrelineDeviationMetres(Array.Empty<Runway>(), 0, 0, 90);

        Assert.Null(deviation);
    }
}
