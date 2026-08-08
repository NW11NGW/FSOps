using FSOps.Core.Planning;

namespace FSOps.Core.Tests.Planning;

/// <summary>
/// Acceptance gate for the tankering weight penalty (docs/PLAN.md "Persistent fuel state and
/// tankering" - "carrying fuel costs fuel"), agreed with the user 2026-08-08: a flight carrying no
/// extra fuel must produce BYTE-IDENTICAL block fuel and cost to the pre-change behaviour, because
/// every documented balance figure (the 275 nm sector, the 600 nm reference, StartupTrajectoryTests,
/// the micro-sector exploit numbers) was decided against the old figures and must not move because
/// of this change. If this file's first test ever fails, stop and do not touch balance constants -
/// see the user's own instruction recorded alongside this test.
/// </summary>
public class BlockFuelEstimatorWeightPenaltyTests
{
    private static readonly BlockTimeBreakdown BlockTime = new(
        TaxiOutMinutes: 10, ClimbMinutes: 15, CruiseMinutes: 103, DescentMinutes: 15, TaxiInMinutes: 8, TotalMinutes: 151);

    [Fact]
    public void NoExtraCarriedFuel_ProducesByteIdenticalFigures_ToThePreWeightPenaltyBehaviour()
    {
        var before = BlockFuelEstimator.Estimate(BlockTime, fuelBurnKgPerHour: 2500);
        var afterDefaultParams = BlockFuelEstimator.Estimate(BlockTime, fuelBurnKgPerHour: 2500);
        var afterExplicitZero = BlockFuelEstimator.Estimate(BlockTime, fuelBurnKgPerHour: 2500, extraCarriedFuelKg: 0, costOfCarryRatePerHour: 0.03);

        Assert.Equal(before, afterDefaultParams);

        // Even with a non-zero rate configured, zero EXTRA carried fuel must still produce zero
        // cost-of-carry and therefore identical figures - the rate alone can never move a normal
        // flight's numbers.
        Assert.Equal(before.TripFuelKg, afterExplicitZero.TripFuelKg, 10);
        Assert.Equal(before.TotalFuelKg, afterExplicitZero.TotalFuelKg, 10);
        Assert.Equal(before.ChargedFuelKg, afterExplicitZero.ChargedFuelKg, 10);
        Assert.Equal(0, afterExplicitZero.CostOfCarryKg);
        Assert.Equal(0, before.CostOfCarryKg);
    }

    [Fact]
    public void ExtraCarriedFuel_AddsCostOfCarry_ProportionalToExtraMassAndAirborneTime()
    {
        // Airborne = 15 + 103 + 15 = 133 min = 2.21666... h.
        var result = BlockFuelEstimator.Estimate(
            BlockTime, fuelBurnKgPerHour: 2500, extraCarriedFuelKg: 2000, costOfCarryRatePerHour: 0.03);

        var airborneHours = (15 + 103 + 15) / 60.0;
        var expectedCostOfCarryKg = 2000 * 0.03 * airborneHours;

        Assert.Equal(expectedCostOfCarryKg, result.CostOfCarryKg, 6);
        Assert.True(result.CostOfCarryKg > 0);

        // Folded into trip fuel (and therefore total/charged) - see FuelBreakdown's own doc.
        var baseline = BlockFuelEstimator.Estimate(BlockTime, fuelBurnKgPerHour: 2500);
        Assert.Equal(baseline.TripFuelKg + expectedCostOfCarryKg, result.TripFuelKg, 6);
        Assert.True(result.TotalFuelKg > baseline.TotalFuelKg);
    }

    [Fact]
    public void ExtraCarriedFuel_WithZeroRate_AddsNoCost()
    {
        var result = BlockFuelEstimator.Estimate(
            BlockTime, fuelBurnKgPerHour: 2500, extraCarriedFuelKg: 2000, costOfCarryRatePerHour: 0);

        Assert.Equal(0, result.CostOfCarryKg);
    }

    [Fact]
    public void ExtraCarriedFuel_ZeroAirborneTime_AddsNoCost()
    {
        var zeroAirborne = new BlockTimeBreakdown(0, 0, 0, 0, 0, 0);
        var result = BlockFuelEstimator.Estimate(
            zeroAirborne, fuelBurnKgPerHour: 2500, extraCarriedFuelKg: 2000, costOfCarryRatePerHour: 0.03);

        Assert.Equal(0, result.CostOfCarryKg);
    }
}
