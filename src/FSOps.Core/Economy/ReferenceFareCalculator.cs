using FSOps.Core.Entities;

namespace FSOps.Core.Economy;

/// <summary>
/// Config-driven reference fare - the anchor point FareDemandModel prices against. Distance and
/// strategy profile decide it, same shape as the existing Planning.FareEstimator, but every
/// number comes from EconomyConfig instead of being hardcoded, so it can be tuned alongside the
/// rest of the economy without touching code.
/// </summary>
public static class ReferenceFareCalculator
{
    public static decimal Calculate(ReferenceFareConfig config, StrategyProfileConfig strategy, double distanceNm)
    {
        if (distanceNm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(distanceNm), "Distance must be positive.");
        }

        var fare = (decimal)distanceNm * config.FarePerNm * strategy.ReferenceFareMultiplier;
        return Math.Max(config.MinimumFare, Math.Round(fare, 2, MidpointRounding.AwayFromZero));
    }

    /// <summary>Convenience overload that looks the strategy up from the full config.</summary>
    public static decimal Calculate(EconomyConfig config, AirlineStrategyProfile strategy, double distanceNm)
        => Calculate(config.ReferenceFare, config.GetStrategy(strategy), distanceNm);
}
