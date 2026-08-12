using FSOps.Core.Economy;
using FSOps.Core.Finance;

namespace FSOps.Core.Tests.Finance;

/// <summary>
/// Exact-value coverage for the rule that selling an owned aircraft must lose money on the spread:
/// the depreciation curve itself, and proof the buy-then-sell exploit (new and used) is closed. Pure
/// arithmetic, no database - same convention as MaintenanceSchedulerTests.
/// </summary>
public class AircraftDepreciationCalculatorTests
{
    private static readonly AircraftDepreciationConfig DefaultConfig = new();

    [Fact]
    public void BrandNewAircraft_SoldImmediately_LosesTheResaleSpread()
    {
        var quote = AircraftDepreciationCalculator.ComputeSaleValue(
            newPrice: 100_000m,
            conditionPercent: 100,
            hoursSinceACheck: 0,
            hoursSinceCCheck: 0,
            aCheckIntervalHours: 500,
            cCheckIntervalHours: 4000,
            isGroundedForMaintenance: false,
            DefaultConfig);

        Assert.Equal(0.80m, quote.ResaleFactorApplied);
        Assert.Equal(80_000.00m, quote.SaleValue);
        // The exploit test: buying new and selling immediately, with zero wear at all, is still a
        // loss. Buy then immediately sell must be a net loss, or cash stops meaning anything.
        Assert.True(quote.SaleValue < 100_000m);
    }

    [Fact]
    public void UsedAircraftAnchor_MatchesTheDocumentedUsedDiscountCurve_AndUndercutsIt()
    {
        // Exactly EconomyConfig.UsedAircraft's shipped defaults: 70% condition, 70% into both
        // maintenance cycles (500h A-check / 4000h C-check interval). A used aircraft costs 55% of
        // new (UsedAircraftConfig.PriceMultiplier) - the resale curve must land BELOW that so a
        // used-aircraft flip (buy used, sell immediately) is also a loss, not a breakeven round trip.
        var quote = AircraftDepreciationCalculator.ComputeSaleValue(
            newPrice: 100_000m,
            conditionPercent: 70,
            hoursSinceACheck: 350,   // 500 * 0.7
            hoursSinceCCheck: 2_800, // 4000 * 0.7
            aCheckIntervalHours: 500,
            cCheckIntervalHours: 4000,
            isGroundedForMaintenance: false,
            DefaultConfig);

        Assert.Equal(0.50m, quote.ResaleFactorApplied);
        Assert.Equal(50_000.00m, quote.SaleValue);

        var usedPurchasePrice = 100_000m * 0.55m; // EconomyConfig.UsedAircraft.PriceMultiplier
        Assert.True(quote.SaleValue < usedPurchasePrice, "A used-aircraft flip must also lose money.");
        Assert.Equal(5_000.00m, usedPurchasePrice - quote.SaleValue);
    }

    [Fact]
    public void GroundedForMaintenance_SellsForLess_ThanTheSameAircraftNotGrounded()
    {
        var notGrounded = AircraftDepreciationCalculator.ComputeSaleValue(
            100_000m, 100, 0, 0, 500, 4000, isGroundedForMaintenance: false, DefaultConfig);
        var grounded = AircraftDepreciationCalculator.ComputeSaleValue(
            100_000m, 100, 0, 0, 500, 4000, isGroundedForMaintenance: true, DefaultConfig);

        Assert.Equal(0.75m, grounded.ResaleFactorApplied);
        Assert.Equal(75_000.00m, grounded.SaleValue);
        Assert.True(grounded.SaleValue < notGrounded.SaleValue);
    }

    [Fact]
    public void FlownAircraft_DepreciatesFurtherThanAnImmediateFlip()
    {
        // 200 flight hours since new: condition down to 76% (matches MaintenanceScheduler's own
        // 0.12/hour decay - see MaintenanceSchedulerTests), 200/500 into the A-check cycle and
        // 200/4000 into the C-check cycle.
        var quote = AircraftDepreciationCalculator.ComputeSaleValue(
            newPrice: 100_000m,
            conditionPercent: 76,
            hoursSinceACheck: 200,
            hoursSinceCCheck: 200,
            aCheckIntervalHours: 500,
            cCheckIntervalHours: 4000,
            isGroundedForMaintenance: false,
            DefaultConfig);

        Assert.Equal(0.6605m, quote.ResaleFactorApplied);
        Assert.Equal(66_050.00m, quote.SaleValue);
        Assert.True(quote.SaleValue < 80_000.00m, "Flying the aircraft must depreciate it further than an immediate flip.");
    }

    [Fact]
    public void ResaleFactor_NeverDropsBelowTheConfiguredFloor()
    {
        var harshConfig = new AircraftDepreciationConfig
        {
            NewAircraftResaleFactor = 0.5m,
            ConditionDepreciationWeight = 0.4m,
            CycleWearDepreciationWeight = 0.4m,
            GroundedForMaintenancePenalty = 0.3m,
            MinResaleFactor = 0.1m,
        };

        // Raw factor would be 0.5 - 0.4 - 0.4 - 0.3 = -0.6, clamped to the floor.
        var quote = AircraftDepreciationCalculator.ComputeSaleValue(
            newPrice: 100_000m,
            conditionPercent: 0,
            hoursSinceACheck: 500,
            hoursSinceCCheck: 4000,
            aCheckIntervalHours: 500,
            cCheckIntervalHours: 4000,
            isGroundedForMaintenance: true,
            harshConfig);

        Assert.Equal(0.10m, quote.ResaleFactorApplied);
        Assert.Equal(10_000.00m, quote.SaleValue);
    }
}
