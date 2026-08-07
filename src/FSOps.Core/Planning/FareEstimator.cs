using FSOps.Core.Entities;

namespace FSOps.Core.Planning;

/// <summary>
/// A simple distance-based baseline fare (in the base currency unit), adjusted by strategy
/// profile. The real economy engine - demand, elasticity, seasonality - arrives in Chunk D;
/// this just needs to feel roughly sane for the route builder to show something today.
/// </summary>
public static class FareEstimator
{
    private const decimal BaseFarePerNm = 0.12m;
    private const decimal MinimumFare = 35m;

    public static decimal SuggestFare(double distanceNm, AirlineStrategyProfile strategy)
    {
        var multiplier = strategy switch
        {
            AirlineStrategyProfile.LowCost => 0.75m,
            AirlineStrategyProfile.Premium => 1.6m,
            AirlineStrategyProfile.International => 1.15m,
            AirlineStrategyProfile.Domestic => 1.0m,
            _ => 1.0m,
        };

        var fare = (decimal)distanceNm * BaseFarePerNm * multiplier;
        return Math.Max(MinimumFare, Math.Round(fare, 2, MidpointRounding.AwayFromZero));
    }
}
