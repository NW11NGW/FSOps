using FSOps.Core.Economy;
using FSOps.Core.Entities;

namespace FSOps.Core.Tests.Economy;

public class ReferenceFareCalculatorTests
{
    private static readonly EconomyConfig Config = EconomyConfig.Default();

    [Fact]
    public void Calculate_Domestic_MatchesExactBaselineCalculation()
    {
        // 620nm * 0.12/nm * 1.00 domestic multiplier = 74.40
        var strategy = Config.GetStrategy(AirlineStrategyProfile.Domestic);
        Assert.Equal(74.40m, ReferenceFareCalculator.Calculate(Config.ReferenceFare, strategy, 620));
    }

    [Fact]
    public void Calculate_LowCost_IsCheaperThanDomestic()
    {
        var lowCost = ReferenceFareCalculator.Calculate(Config, AirlineStrategyProfile.LowCost, 620);
        var domestic = ReferenceFareCalculator.Calculate(Config, AirlineStrategyProfile.Domestic, 620);

        Assert.True(lowCost < domestic);
    }

    [Fact]
    public void Calculate_Premium_IsMoreExpensiveThanDomestic()
    {
        var premium = ReferenceFareCalculator.Calculate(Config, AirlineStrategyProfile.Premium, 620);
        var domestic = ReferenceFareCalculator.Calculate(Config, AirlineStrategyProfile.Domestic, 620);

        Assert.True(premium > domestic);
    }

    [Fact]
    public void Calculate_Balanced_MatchesNeutralMultiplier()
    {
        // Balanced has a 1.00 multiplier just like Domestic - a genuinely neutral choice.
        var balanced = ReferenceFareCalculator.Calculate(Config, AirlineStrategyProfile.Balanced, 620);
        var domestic = ReferenceFareCalculator.Calculate(Config, AirlineStrategyProfile.Domestic, 620);

        Assert.Equal(domestic, balanced);
    }

    [Fact]
    public void Calculate_VeryShortHop_NeverGoesBelowMinimumFare()
    {
        var strategy = Config.GetStrategy(AirlineStrategyProfile.LowCost);
        var fare = ReferenceFareCalculator.Calculate(Config.ReferenceFare, strategy, 5);

        // Raised from 35 to 65 as part of the fuel-honesty fix (docs/PLAN.md "Status after the
        // fuel-honesty fix") - the old floor masked how cheap farePerNm actually was for short
        // domestic hops, leaving them structurally unprofitable against fixed per-sector costs.
        Assert.Equal(65m, fare);
    }

    [Fact]
    public void Calculate_NonPositiveDistance_Throws()
    {
        var strategy = Config.GetStrategy(AirlineStrategyProfile.Domestic);
        Assert.Throws<ArgumentOutOfRangeException>(() => ReferenceFareCalculator.Calculate(Config.ReferenceFare, strategy, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReferenceFareCalculator.Calculate(Config.ReferenceFare, strategy, -10));
    }

    // Coverage moved here from the deleted FareEstimatorTests (FareEstimator was a Chunk-A/B
    // placeholder with its own hardcoded formula, predating this real one - see
    // RoutePreviewCalculator's doc comment on why it was removed rather than kept as a duplicate).

    [Fact]
    public void Calculate_LongHaul_IsSubstantiallyHigherThanShortHop()
    {
        var longHaul = ReferenceFareCalculator.Calculate(Config, AirlineStrategyProfile.International, 3000);
        var shortHop = ReferenceFareCalculator.Calculate(Config, AirlineStrategyProfile.International, 300);

        Assert.True(longHaul > shortHop * 5);
    }

    [Fact]
    public void Calculate_Balanced_IsNeitherCheapestNorMostExpensive()
    {
        var balanced = ReferenceFareCalculator.Calculate(Config, AirlineStrategyProfile.Balanced, 620);
        var lowCost = ReferenceFareCalculator.Calculate(Config, AirlineStrategyProfile.LowCost, 620);
        var premium = ReferenceFareCalculator.Calculate(Config, AirlineStrategyProfile.Premium, 620);

        Assert.True(balanced > lowCost);
        Assert.True(balanced < premium);
    }
}
