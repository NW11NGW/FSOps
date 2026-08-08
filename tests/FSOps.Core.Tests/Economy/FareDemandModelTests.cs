using FSOps.Core.Economy;
using FSOps.Core.Entities;

namespace FSOps.Core.Tests.Economy;

public class FareDemandModelTests
{
    private static readonly EconomyConfig Config = EconomyConfig.Default();
    private const decimal ReferenceFare = 100m;
    private const int Seats = 200;

    public static IEnumerable<object[]> AllProfiles() =>
        Enum.GetValues<AirlineStrategyProfile>().Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Calculate_AtReferenceFare_ProducesExactlyTheStrategysBaselineLoadFactor(AirlineStrategyProfile profile)
    {
        var strategy = Config.GetStrategy(profile);

        // Seats=200 makes 200 * BaselineLoadFactor an exact integer for every shipped profile
        // (152, 156, 150, 136, 146), so the realised load factor matches the config value bit
        // for bit rather than merely within a rounding tolerance.
        var expectedPax = (int)Math.Round(Seats * strategy.BaselineLoadFactor, MidpointRounding.AwayFromZero);
        Assert.Equal(Seats * strategy.BaselineLoadFactor, (double)expectedPax, precision: 6);

        var result = FareDemandModel.Calculate(
            Config.MaxLoadFactor, strategy, ReferenceFare, ReferenceFare, Seats,
            Config.CaptiveFareCeilingMultiple, Config.PostCaptiveElasticity);

        Assert.Equal(expectedPax, result.PaxBooked);
        Assert.Equal(strategy.BaselineLoadFactor, result.LoadFactor, precision: 9);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Calculate_AtZeroFare_LoadFactorNeverExceedsMaxLoadFactor(AirlineStrategyProfile profile)
    {
        var strategy = Config.GetStrategy(profile);

        // A generous market pool (well above seats * maxLoadFactor) so the market cap cannot be
        // what is holding the ceiling down - this isolates the formula's own clamp.
        var result = FareDemandModel.Calculate(
            Config.MaxLoadFactor, strategy, fare: 0m, ReferenceFare, Seats, marketDemandPax: 1_000_000,
            Config.CaptiveFareCeilingMultiple, Config.PostCaptiveElasticity);

        Assert.True(result.LoadFactor <= Config.MaxLoadFactor);
        Assert.Equal((int)Math.Round(Seats * Config.MaxLoadFactor, MidpointRounding.AwayFromZero), result.PaxBooked);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Calculate_SameInputsTwice_ProducesIdenticalResult(AirlineStrategyProfile profile)
    {
        var strategy = Config.GetStrategy(profile);

        var first = FareDemandModel.Calculate(
            Config.MaxLoadFactor, strategy, 123.45m, ReferenceFare, Seats, 140,
            Config.CaptiveFareCeilingMultiple, Config.PostCaptiveElasticity);
        var second = FareDemandModel.Calculate(
            Config.MaxLoadFactor, strategy, 123.45m, ReferenceFare, Seats, 140,
            Config.CaptiveFareCeilingMultiple, Config.PostCaptiveElasticity);

        Assert.Equal(first, second);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Calculate_UndercuttingHalfReferenceFare_HalvesRevenueExactly(AirlineStrategyProfile profile)
    {
        var strategy = Config.GetStrategy(profile);

        // Below the reference fare the market cap is already the binding constraint for every
        // shipped profile (the formula-side pax count only falls below the market pool above
        // the reference fare - see the FareDemandModel type doc), so paxBooked is identical at
        // both fares and revenue must scale with fare exactly.
        var atReference = FareDemandModel.Calculate(
            Config.MaxLoadFactor, strategy, ReferenceFare, ReferenceFare, Seats,
            Config.CaptiveFareCeilingMultiple, Config.PostCaptiveElasticity);
        var atHalf = FareDemandModel.Calculate(
            Config.MaxLoadFactor, strategy, ReferenceFare * 0.5m, ReferenceFare, Seats,
            Config.CaptiveFareCeilingMultiple, Config.PostCaptiveElasticity);

        Assert.Equal(atReference.PaxBooked, atHalf.PaxBooked);
        Assert.Equal(atReference.Revenue * 0.5m, atHalf.Revenue);
        Assert.True(atHalf.Revenue < atReference.Revenue, "Dumping the fare must lose revenue, not gain it.");
    }

    [Fact]
    public void Calculate_NegativeFare_Throws()
    {
        var strategy = Config.GetStrategy(AirlineStrategyProfile.Domestic);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FareDemandModel.Calculate(Config.MaxLoadFactor, strategy, -1m, ReferenceFare, Seats, 100,
                Config.CaptiveFareCeilingMultiple, Config.PostCaptiveElasticity));
    }

    [Fact]
    public void Calculate_ZeroOrNegativeSeats_Throws()
    {
        var strategy = Config.GetStrategy(AirlineStrategyProfile.Domestic);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FareDemandModel.Calculate(Config.MaxLoadFactor, strategy, ReferenceFare, ReferenceFare, 0, 100,
                Config.CaptiveFareCeilingMultiple, Config.PostCaptiveElasticity));
    }
}
