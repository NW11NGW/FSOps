using FSOps.Core.Entities;
using FSOps.Core.Planning;

namespace FSOps.Core.Tests.Planning;

public class FareEstimatorTests
{
    [Fact]
    public void SuggestFare_Domestic_MatchesExactBaselineCalculation()
    {
        // 620nm * 0.12/nm * 1.0 domestic multiplier = 74.40
        Assert.Equal(74.40m, FareEstimator.SuggestFare(620, AirlineStrategyProfile.Domestic));
    }

    [Fact]
    public void SuggestFare_LowCost_IsCheaperThanDomestic()
    {
        var lowCost = FareEstimator.SuggestFare(620, AirlineStrategyProfile.LowCost);
        var domestic = FareEstimator.SuggestFare(620, AirlineStrategyProfile.Domestic);

        Assert.True(lowCost < domestic);
    }

    [Fact]
    public void SuggestFare_Premium_IsMoreExpensiveThanDomestic()
    {
        var premium = FareEstimator.SuggestFare(620, AirlineStrategyProfile.Premium);
        var domestic = FareEstimator.SuggestFare(620, AirlineStrategyProfile.Domestic);

        Assert.True(premium > domestic);
    }

    [Fact]
    public void SuggestFare_VeryShortHop_NeverGoesBelowMinimumFare()
    {
        var fare = FareEstimator.SuggestFare(5, AirlineStrategyProfile.LowCost);

        Assert.Equal(35m, fare);
    }

    [Fact]
    public void SuggestFare_LongHaul_IsSubstantiallyHigherThanShortHop()
    {
        var longHaul = FareEstimator.SuggestFare(3000, AirlineStrategyProfile.International);
        var shortHop = FareEstimator.SuggestFare(300, AirlineStrategyProfile.International);

        Assert.True(longHaul > shortHop * 5);
    }
}
