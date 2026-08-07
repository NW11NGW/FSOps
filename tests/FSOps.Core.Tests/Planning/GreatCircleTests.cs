using FSOps.Core.Planning;

namespace FSOps.Core.Tests.Planning;

public class GreatCircleTests
{
    // Airport coordinates (WGS84 lat/lon in degrees).
    private const double EgllLat = 51.4706;
    private const double EgllLon = -0.4619;
    private const double KjfkLat = 40.6413;
    private const double KjfkLon = -73.7781;
    private const double EgkkLat = 51.1481;
    private const double EgkkLon = -0.1903;
    private const double LeblLat = 41.2971;
    private const double LeblLon = 2.0785;

    [Fact]
    public void DistanceNm_SamePoint_IsExactlyZero()
    {
        Assert.Equal(0, GreatCircle.DistanceNm(51.5, -0.5, 51.5, -0.5));
    }

    [Fact]
    public void DistanceNm_QuarterOfEquator_MatchesExactGeometry()
    {
        // A quarter of the equator's great circle is exactly (pi * R) / 2 nm, independent of
        // the haversine implementation - straightforward spherical geometry.
        const double earthRadiusNm = 3440.065;
        var expected = Math.PI * earthRadiusNm / 2;

        var actual = GreatCircle.DistanceNm(0, 0, 0, 90);

        Assert.Equal(expected, actual, 6);
    }

    [Fact]
    public void InitialBearingDeg_DueEastAlongEquator_IsExactly90()
    {
        Assert.Equal(90, GreatCircle.InitialBearingDeg(0, 0, 0, 10), 6);
    }

    [Fact]
    public void InitialBearingDeg_DueNorthAlongMeridian_IsExactly0()
    {
        Assert.Equal(0, GreatCircle.InitialBearingDeg(0, 0, 10, 0), 6);
    }

    [Fact]
    public void InitialBearingDeg_DueSouthAlongMeridian_IsExactly180()
    {
        Assert.Equal(180, GreatCircle.InitialBearingDeg(10, 0, 0, 0), 6);
    }

    [Fact]
    public void DistanceNm_EgllToKjfk_IsRoughlyThreeThousandNm()
    {
        var distance = GreatCircle.DistanceNm(EgllLat, EgllLon, KjfkLat, KjfkLon);

        Assert.InRange(distance, 2900, 3100);
    }

    [Fact]
    public void DistanceNm_EgkkToLebl_IsRoughlySixHundredNm()
    {
        var distance = GreatCircle.DistanceNm(EgkkLat, EgkkLon, LeblLat, LeblLon);

        Assert.InRange(distance, 550, 700);
    }

    [Fact]
    public void SamplePath_ReturnsRequestedNumberOfPoints_StartingAndEndingAtInputs()
    {
        var path = GreatCircle.SamplePath(EgllLat, EgllLon, KjfkLat, KjfkLon, 64);

        Assert.Equal(64, path.Count);
        Assert.Equal(EgllLon, path[0].Lon, 3);
        Assert.Equal(EgllLat, path[0].Lat, 3);
        Assert.Equal(KjfkLon, path[^1].Lon, 3);
        Assert.Equal(KjfkLat, path[^1].Lat, 3);
    }

    [Fact]
    public void SamplePath_CoincidentPoints_DoesNotThrowAndReturnsThatPointRepeated()
    {
        var path = GreatCircle.SamplePath(EgllLat, EgllLon, EgllLat, EgllLon, 10);

        Assert.Equal(10, path.Count);
        Assert.All(path, p =>
        {
            Assert.Equal(EgllLon, p.Lon, 6);
            Assert.Equal(EgllLat, p.Lat, 6);
        });
    }

    [Fact]
    public void SamplePath_FewerThanTwoSamplesRequested_StillReturnsAtLeastTwoPoints()
    {
        var path = GreatCircle.SamplePath(EgkkLat, EgkkLon, LeblLat, LeblLon, 1);

        Assert.True(path.Count >= 2);
    }
}
