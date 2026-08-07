using FSOps.Core.Planning;

namespace FSOps.Core.Tests.Planning;

public class BlockTimeEstimatorTests
{
    [Fact]
    public void Estimate_LongHaul_MatchesHandCalculatedBreakdown()
    {
        // 1000 nm at 500 kt: climb 70nm/15min, descent 75nm/15min fixed, remaining
        // 855nm cruised at 500kt = 102.6min -> rounds to 103. Taxi out 10 + taxi in 8.
        var result = BlockTimeEstimator.Estimate(distanceNm: 1000, cruiseTasKts: 500);

        Assert.Equal(10, result.TaxiOutMinutes);
        Assert.Equal(15, result.ClimbMinutes);
        Assert.Equal(103, result.CruiseMinutes);
        Assert.Equal(15, result.DescentMinutes);
        Assert.Equal(8, result.TaxiInMinutes);
        Assert.Equal(151, result.TotalMinutes);
    }

    [Fact]
    public void Estimate_TotalMinutes_AlwaysEqualsSumOfBreakdown()
    {
        var result = BlockTimeEstimator.Estimate(distanceNm: 3000, cruiseTasKts: 447);

        var sum = result.TaxiOutMinutes + result.ClimbMinutes + result.CruiseMinutes + result.DescentMinutes + result.TaxiInMinutes;
        Assert.Equal(sum, result.TotalMinutes);
    }

    [Fact]
    public void Estimate_ShortHop_ScalesClimbAndDescentDownAndSkipsCruise()
    {
        var result = BlockTimeEstimator.Estimate(distanceNm: 50, cruiseTasKts: 450);

        Assert.Equal(0, result.CruiseMinutes);
        Assert.True(result.ClimbMinutes < 15);
        Assert.True(result.DescentMinutes < 15);
    }

    [Fact]
    public void Estimate_ZeroDistance_ReturnsOnlyTaxiTime()
    {
        var result = BlockTimeEstimator.Estimate(distanceNm: 0, cruiseTasKts: 450);

        Assert.Equal(0, result.ClimbMinutes);
        Assert.Equal(0, result.CruiseMinutes);
        Assert.Equal(0, result.DescentMinutes);
        Assert.Equal(18, result.TotalMinutes);
    }

    [Fact]
    public void Estimate_EgkkToLeblDistance_LandsInBelievableRange()
    {
        // ~620 nm at a typical A320 cruise speed of 447 kt - real-world scheduled block time
        // for this city pair is a little under 2 hours.
        var result = BlockTimeEstimator.Estimate(distanceNm: 620, cruiseTasKts: 447);

        Assert.InRange(result.TotalMinutes, 100, 130);
    }

    [Fact]
    public void Estimate_ZeroCruiseSpeed_DoesNotThrowAndSkipsCruiseMinutes()
    {
        var result = BlockTimeEstimator.Estimate(distanceNm: 1000, cruiseTasKts: 0);

        Assert.Equal(0, result.CruiseMinutes);
    }
}
