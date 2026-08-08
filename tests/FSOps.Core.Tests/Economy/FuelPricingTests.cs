using FSOps.Core.Economy;

namespace FSOps.Core.Tests.Economy;

public class FuelPricingTests
{
    private static readonly FuelConfig Config = EconomyConfig.Default().Fuel;
    private static readonly DateTimeOffset Date = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PricePerKg_SameInputsTwice_ProducesIdenticalPrice()
    {
        var first = FuelPricing.PricePerKg(Config, "EGLL", "United Kingdom", Date, worldSeed: 42);
        var second = FuelPricing.PricePerKg(Config, "EGLL", "United Kingdom", Date, worldSeed: 42);

        Assert.Equal(first, second);
    }

    [Fact]
    public void PricePerKg_DifferentSeed_CanProduceADifferentPrice()
    {
        // Not a mathematical certainty for every possible pair of seeds, but with a wide search
        // over many seeds at least one must differ, or the seed is being ignored entirely.
        var withSeed1 = FuelPricing.PricePerKg(Config, "EGLL", "United Kingdom", Date, worldSeed: 1);
        var differsSomewhere = Enumerable.Range(2, 50)
            .Select(seed => FuelPricing.PricePerKg(Config, "EGLL", "United Kingdom", Date, seed))
            .Any(price => price != withSeed1);

        Assert.True(differsSomewhere);
    }

    [Fact]
    public void PricePerKg_StaysWithinTheConfiguredVolatilityBand()
    {
        var regionalMultiplier = Config.RegionalMultipliers["United Kingdom"];
        var min = Config.BasePricePerKg * regionalMultiplier * (decimal)(1 - Config.VolatilityAmplitude);
        var max = Config.BasePricePerKg * regionalMultiplier * (decimal)(1 + Config.VolatilityAmplitude);

        for (var day = 0; day < 60; day++)
        {
            var price = FuelPricing.PricePerKg(Config, "EGLL", "United Kingdom", Date.AddDays(day), worldSeed: 7);
            Assert.InRange(price, min, max);
        }
    }

    [Fact]
    public void PricePerKg_RegionalOrderingIsGuaranteedRegardlessOfTheDailyWalk()
    {
        // The volatility band (+/-6%) is narrower than the gap between any two regional
        // multipliers here (30%+), so cheap-region < mid-region < expensive-region must hold on
        // every single day, not just on average - this asserts it holds for a long run of days.
        for (var day = 0; day < 30; day++)
        {
            var date = Date.AddDays(day);
            var cheap = FuelPricing.PricePerKg(Config, "EGLL", "United Arab Emirates", date, worldSeed: 99);
            var mid = FuelPricing.PricePerKg(Config, "EGLL", "United Kingdom", date, worldSeed: 99);
            var expensive = FuelPricing.PricePerKg(Config, "EGLL", "Japan", date, worldSeed: 99);

            Assert.True(cheap < mid, $"Day {day}: cheap ({cheap}) should be below mid ({mid}).");
            Assert.True(mid < expensive, $"Day {day}: mid ({mid}) should be below expensive ({expensive}).");
        }
    }

    [Fact]
    public void PricePerKg_UnknownRegion_FallsBackToDefaultMultiplier()
    {
        var price = FuelPricing.PricePerKg(Config, "ZZZZ", "Nowhereland", Date, worldSeed: 3);
        var min = Config.BasePricePerKg * Config.DefaultRegionalMultiplier * (decimal)(1 - Config.VolatilityAmplitude);
        var max = Config.BasePricePerKg * Config.DefaultRegionalMultiplier * (decimal)(1 + Config.VolatilityAmplitude);

        Assert.InRange(price, min, max);
    }

    [Fact]
    public void PricePerKg_PriceDriftsOverTime_NotConstant()
    {
        var prices = Enumerable.Range(0, 40)
            .Select(day => FuelPricing.PricePerKg(Config, "EGLL", "United Kingdom", Date.AddDays(day), worldSeed: 11))
            .Distinct()
            .Count();

        Assert.True(prices > 1, "Fuel price should vary day to day, not sit perfectly still.");
    }

    [Fact]
    public void PricePerKg_BlankAirportIcao_Throws()
    {
        Assert.Throws<ArgumentException>(() => FuelPricing.PricePerKg(Config, "", "United Kingdom", Date, 1));
    }
}
