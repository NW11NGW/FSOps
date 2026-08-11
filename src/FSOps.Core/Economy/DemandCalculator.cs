using FSOps.Core.Entities;

namespace FSOps.Core.Economy;

/// <summary>
/// Computes the passenger pool available for a city pair on a given day - how many people
/// would ever want to fly this route today, independent of the fare charged. FareDemandModel
/// then decides what share of the pool (and what share of the aircraft's seats) actually books,
/// given the price. Entirely deterministic: no randomness, just catchment x distance-decay x
/// season x day-of-week x reputation.
/// </summary>
public static class DemandCalculator
{
    public static int AvailablePassengers(
        DemandConfig config,
        AirportSizeCategory departureSize,
        AirportSizeCategory arrivalSize,
        double distanceNm,
        DateTimeOffset flightDateUtc,
        double reputationScore)
    {
        if (distanceNm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(distanceNm), "Distance must be positive.");
        }

        // Geometric mean of both endpoints' catchment scores - a hub-to-hub pair scores highly,
        // a hub-to-spoke pair sits in the middle, and a spoke-to-spoke pair scores low, matching
        // how real point-to-point demand concentrates around significant airports on both ends.
        var catchment = Math.Sqrt(Catchment(config, departureSize) * Catchment(config, arrivalSize));

        var distanceFactor = DistanceFactor(config, distanceNm);
        var seasonFactor = config.MonthlySeasonality[flightDateUtc.UtcDateTime.Month - 1];
        var dayFactor = config.DayOfWeekMultiplier[(int)flightDateUtc.UtcDateTime.DayOfWeek];
        var reputationFactor = ReputationFactor(config, reputationScore);

        var raw = config.BaseDemandPerCatchmentPoint * catchment * distanceFactor * seasonFactor * dayFactor * reputationFactor;
        return Math.Max(0, (int)Math.Round(raw, MidpointRounding.AwayFromZero));
    }

    private static double Catchment(DemandConfig config, AirportSizeCategory size) => size switch
    {
        AirportSizeCategory.Large => config.CatchmentLarge,
        AirportSizeCategory.Medium => config.CatchmentMedium,
        AirportSizeCategory.Small => config.CatchmentSmall,
        _ => config.CatchmentOther,
    };

    /// <summary>
    /// Collapses toward nil below <see cref="DemandConfig.NoAirMarketBelowNm"/> (surface transport
    /// wins and there is essentially no scheduled passenger market for a hop that short), ramps up
    /// from a short-hop floor toward 1.0 as distance approaches the sweet spot, holds at 1.0
    /// through the sweet spot (people fly point-to-point most readily in this band - long enough
    /// to beat driving, short enough that a connection isn't more convenient), then decays
    /// linearly beyond it down to a floor (long-haul demand never fully evaporates - it's smaller
    /// and pickier, not zero).
    /// </summary>
    private static double DistanceFactor(DemandConfig config, double distanceNm)
    {
        if (distanceNm < config.NoAirMarketBelowNm)
        {
            // Cubic, not linear: demand must stay near NoAirMarketFloorFactor across most of this
            // band and only pick up sharply as the threshold approaches, rather than granting a
            // meaningfully sized market at, say, half the threshold distance - that's what let a
            // 20 nm sector sell 57 seats before this fix (see
            // FlightEconomicsIntegrityTests.MicroSectorLoop_EvenAtTheBestPossibleFare_IsNetNegative).
            var thresholdFactor = ShortHopRampFactor(config, config.NoAirMarketBelowNm);
            var t = distanceNm / config.NoAirMarketBelowNm;
            return config.NoAirMarketFloorFactor + (thresholdFactor - config.NoAirMarketFloorFactor) * t * t * t;
        }

        if (distanceNm < config.SweetSpotMinNm)
        {
            return ShortHopRampFactor(config, distanceNm);
        }

        if (distanceNm <= config.SweetSpotMaxNm)
        {
            return 1.0;
        }

        var beyond = distanceNm - config.SweetSpotMaxNm;
        var decayed = 1.0 - config.LongHaulDecayPerNm * beyond;
        return Math.Max(config.MinDistanceFactor, decayed);
    }

    /// <summary>The ordinary short-hop ramp (unchanged from before the no-air-market fix): a
    /// straight line from <see cref="DemandConfig.ShortHopFloorFactor"/> at zero distance to 1.0
    /// at <see cref="DemandConfig.SweetSpotMinNm"/>, evaluated at any distance including below
    /// <see cref="DemandConfig.NoAirMarketBelowNm"/> purely to find that threshold's hand-off
    /// value - ordinary short-haul routes (well above the threshold) see exactly the same number
    /// this always produced.</summary>
    private static double ShortHopRampFactor(DemandConfig config, double distanceNm)
    {
        var t = distanceNm / config.SweetSpotMinNm;
        return config.ShortHopFloorFactor + (1.0 - config.ShortHopFloorFactor) * t;
    }

    private static double ReputationFactor(DemandConfig config, double reputationScore)
    {
        var factor = 1.0 + (reputationScore - config.ReputationBaselineScore) / config.ReputationBaselineScore * config.ReputationSensitivity;
        return Math.Max(config.ReputationFloor, factor);
    }

    /// <summary>
    /// Public entry point onto <see cref="ReputationFactor"/> for a read-only reporting view (the
    /// dashboard's "this is affecting your bookings" reputation summary) that needs to quote the
    /// exact same demand multiplier <see cref="AvailablePassengers"/> actually applies, rather than
    /// re-deriving the baseline/sensitivity/floor formula a second time somewhere else.
    /// </summary>
    public static double ReputationDemandMultiplier(DemandConfig config, double reputationScore) =>
        ReputationFactor(config, reputationScore);
}
