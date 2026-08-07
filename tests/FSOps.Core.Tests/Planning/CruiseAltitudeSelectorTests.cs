using FSOps.Core.Planning;

namespace FSOps.Core.Tests.Planning;

public class CruiseAltitudeSelectorTests
{
    [Fact]
    public void SelectCruiseAltitudeFt_Eastbound_ReturnsOddFlightLevel()
    {
        var altitude = CruiseAltitudeSelector.SelectCruiseAltitudeFt(distanceNm: 620, initialBearingDeg: 140, serviceCeilingFt: 39000);

        Assert.Equal(35000, altitude);
        Assert.True(altitude / 1000 % 2 != 0, "eastbound flight levels must be odd (in thousands of feet)");
    }

    [Fact]
    public void SelectCruiseAltitudeFt_Westbound_ReturnsEvenFlightLevel()
    {
        var altitude = CruiseAltitudeSelector.SelectCruiseAltitudeFt(distanceNm: 3000, initialBearingDeg: 288, serviceCeilingFt: 39000);

        Assert.Equal(36000, altitude);
        Assert.True(altitude / 1000 % 2 == 0, "westbound flight levels must be even (in thousands of feet)");
    }

    [Fact]
    public void SelectCruiseAltitudeFt_NeverExceedsServiceCeiling()
    {
        var altitude = CruiseAltitudeSelector.SelectCruiseAltitudeFt(distanceNm: 3000, initialBearingDeg: 90, serviceCeilingFt: 20000);

        Assert.True(altitude <= 20000);
    }

    [Fact]
    public void SelectCruiseAltitudeFt_ShortHop_IsLowerThanLongHaul()
    {
        var shortHop = CruiseAltitudeSelector.SelectCruiseAltitudeFt(distanceNm: 50, initialBearingDeg: 90, serviceCeilingFt: 39000);
        var longHaul = CruiseAltitudeSelector.SelectCruiseAltitudeFt(distanceNm: 3000, initialBearingDeg: 90, serviceCeilingFt: 39000);

        Assert.True(shortHop < longHaul);
    }

    [Fact]
    public void SelectCruiseAltitudeFt_BearingWrapsCorrectly_AtExactly180()
    {
        // 180 is the first westbound degree under the semicircular rule (0-179 eastbound).
        var altitude = CruiseAltitudeSelector.SelectCruiseAltitudeFt(distanceNm: 3000, initialBearingDeg: 180, serviceCeilingFt: 39000);

        Assert.True(altitude / 1000 % 2 == 0);
    }
}
