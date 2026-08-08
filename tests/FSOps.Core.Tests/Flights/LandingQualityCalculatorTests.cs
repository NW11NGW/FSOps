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
