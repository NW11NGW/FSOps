using FSOps.Core.Economy;
using FSOps.Core.Entities;

namespace FSOps.Core.Tests.Economy;

public class DemandCalculatorTests
{
    private static readonly DemandConfig Config = EconomyConfig.Default().Demand;

    [Fact]
    public void AvailablePassengers_SweetSpotLargeToLarge_MatchesExactCalculation()
    {
        // catchment = sqrt(10*10) = 10, distanceFactor = 1.0 (within the 300-2500nm sweet spot),
        // 2026-01-01 is a Thursday in January: season 0.90 x day 1.00, reputation 50 = baseline (1.0).
        // raw = 45 * 10 * 1.0 * 0.90 * 1.00 * 1.0 = 405.0
        var date = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var pax = DemandCalculator.AvailablePassengers(
            Config, AirportSizeCategory.Large, AirportSizeCategory.Large, distanceNm: 1000, flightDateUtc: date, reputationScore: 50);

        Assert.Equal(405, pax);
    }

    [Fact]
    public void AvailablePassengers_LongHaulSmallToMedium_MatchesExactCalculation()
    {
        // catchment = sqrt(0.6*3.0) = 1.3416407865, distanceFactor beyond the 2500nm sweet spot
        // max = 1.0 - 0.00035*(3000-2500) = 0.825, 2026-06-15 is a Monday in June: season 1.15 x
        // day 1.05, reputation 80 -> factor 1.0 + (80-50)/50*0.5 = 1.3.
        // raw = 45 * 1.3416407865 * 0.825 * 1.15 * 1.05 * 1.3 = 78.1867... -> 78
        var date = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

        var pax = DemandCalculator.AvailablePassengers(
            Config, AirportSizeCategory.Small, AirportSizeCategory.Medium, distanceNm: 3000, flightDateUtc: date, reputationScore: 80);

        Assert.Equal(78, pax);
    }

    [Fact]
    public void AvailablePassengers_ShortHopLargeToLarge_MatchesExactCalculation()
    {
        // distanceFactor below the 300nm sweet-spot minimum = 0.08 + (1-0.08)*(100/300) = 0.386667,
        // 2026-03-10 is a Tuesday in March: season 0.95 x day 0.95, reputation 50 = baseline.
        var date = new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

        var pax = DemandCalculator.AvailablePassengers(
            Config, AirportSizeCategory.Large, AirportSizeCategory.Large, distanceNm: 100, flightDateUtc: date, reputationScore: 50);

        var expectedDistanceFactor = 0.08 + (1 - 0.08) * (100.0 / 300.0);
        var expectedRaw = 45.0 * 10.0 * expectedDistanceFactor * 0.95 * 0.95 * 1.0;
        var expected = (int)Math.Round(expectedRaw, MidpointRounding.AwayFromZero);

        Assert.Equal(expected, pax);
    }

    [Fact]
    public void AvailablePassengers_MicroSector_IsNearlyEmptyEvenBetweenTwoLargeAirports()
    {
        // A 20nm hop is well inside the no-air-market band (below NoAirMarketBelowNm=50) - even
        // between two Large airports the realistic passenger pool should be tiny, not just
        // "somewhat suppressed". This is the real fix for the micro-sector exploit (see
        // FlightEconomicsIntegrityTests.MicroSectorLoop_EvenAtTheBestPossibleFare_IsNetNegative) -
        // demand collapsing toward nil, not a cost or fare lever.
        var date = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var pax = DemandCalculator.AvailablePassengers(
            Config, AirportSizeCategory.Large, AirportSizeCategory.Large, distanceNm: 20, flightDateUtc: date, reputationScore: 50);

        Assert.True(pax < 15, $"Expected a heavily suppressed micro-sector demand pool, got {pax}.");
    }

    [Fact]
    public void AvailablePassengers_BelowNoAirMarketThreshold_CollapsesTowardNilRelativeToAnOrdinaryShortHop()
    {
        // Below the no-air-market threshold, demand must be a small fraction of what the same
        // Large-Large pair produces at an ordinary short hop just above it - not merely lower.
        var date = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var microSectorPax = DemandCalculator.AvailablePassengers(
            Config, AirportSizeCategory.Large, AirportSizeCategory.Large, distanceNm: 20, flightDateUtc: date, reputationScore: 50);
        var ordinaryShortHopPax = DemandCalculator.AvailablePassengers(
            Config, AirportSizeCategory.Large, AirportSizeCategory.Large, distanceNm: 100, flightDateUtc: date, reputationScore: 50);

        Assert.True(microSectorPax < ordinaryShortHopPax / 10,
            $"Expected a 20nm sector's demand ({microSectorPax}) to be well under a tenth of a 100nm sector's ({ordinaryShortHopPax}).");
    }

    [Fact]
    public void AvailablePassengers_AtAndAboveNoAirMarketThreshold_MatchesTheOriginalShortHopFormula()
    {
        // Routes at/above NoAirMarketBelowNm (114nm - the plan's own EGGD->EGSS scenario) must be
        // completely unaffected by the no-air-market fix - only sub-threshold hops changed.
        var date = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var pax = DemandCalculator.AvailablePassengers(
            Config, AirportSizeCategory.Medium, AirportSizeCategory.Medium, distanceNm: 114, flightDateUtc: date, reputationScore: 50);

        var expectedDistanceFactor = Config.ShortHopFloorFactor + (1 - Config.ShortHopFloorFactor) * (114.0 / Config.SweetSpotMinNm);
        var catchment = Math.Sqrt(Config.CatchmentMedium * Config.CatchmentMedium);
        var expectedRaw = Config.BaseDemandPerCatchmentPoint * catchment * expectedDistanceFactor *
            Config.MonthlySeasonality[0] * Config.DayOfWeekMultiplier[(int)date.DayOfWeek] * 1.0;
        var expected = (int)Math.Round(expectedRaw, MidpointRounding.AwayFromZero);

        Assert.Equal(expected, pax);
    }

    [Fact]
    public void AvailablePassengers_HigherReputation_NeverProducesFewerPassengers()
    {
        var date = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var lowReputation = DemandCalculator.AvailablePassengers(
            Config, AirportSizeCategory.Medium, AirportSizeCategory.Medium, 1000, date, reputationScore: 20);
        var highReputation = DemandCalculator.AvailablePassengers(
            Config, AirportSizeCategory.Medium, AirportSizeCategory.Medium, 1000, date, reputationScore: 90);

        Assert.True(highReputation > lowReputation);
    }

    [Fact]
    public void AvailablePassengers_NonPositiveDistance_Throws()
    {
        var date = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DemandCalculator.AvailablePassengers(Config, AirportSizeCategory.Large, AirportSizeCategory.Large, 0, date, 50));
    }
}
