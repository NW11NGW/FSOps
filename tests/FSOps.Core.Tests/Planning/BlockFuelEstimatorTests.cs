using FSOps.Core.Planning;

namespace FSOps.Core.Tests.Planning;

public class BlockFuelEstimatorTests
{
    [Fact]
    public void Estimate_MatchesHandCalculatedBreakdown()
    {
        var blockTime = new BlockTimeBreakdown(
            TaxiOutMinutes: 10, ClimbMinutes: 15, CruiseMinutes: 103, DescentMinutes: 15, TaxiInMinutes: 8, TotalMinutes: 151);

        // Airborne = 15 + 103 + 15 = 133 min. Trip fuel = 2500 * 133/60 = 5541.666...
        // Contingency = 5% of trip = 277.0833... Final reserve = 2500 * 0.5 = 1250. Taxi = 200 flat. Alternate = 1200 flat.
        var result = BlockFuelEstimator.Estimate(blockTime, fuelBurnKgPerHour: 2500);

        Assert.Equal(5541.666666666667, result.TripFuelKg, 6);
        Assert.Equal(200, result.TaxiFuelKg);
        Assert.Equal(277.08333333333337, result.ContingencyFuelKg, 6);
        Assert.Equal(1200, result.AlternateFuelKg);
        Assert.Equal(1250, result.FinalReserveFuelKg);
        Assert.Equal(8468.75, result.TotalFuelKg, 2);
    }

    [Fact]
    public void Estimate_TotalFuelKg_EqualsSumOfBreakdown()
    {
        var blockTime = BlockTimeEstimator.Estimate(3000, 447);
        var result = BlockFuelEstimator.Estimate(blockTime, fuelBurnKgPerHour: 2500);

        var sum = result.TripFuelKg + result.TaxiFuelKg + result.ContingencyFuelKg + result.AlternateFuelKg + result.FinalReserveFuelKg;
        Assert.Equal(sum, result.TotalFuelKg, 6);
    }

    [Fact]
    public void Estimate_ZeroAirborneTime_StillIncludesTaxiAlternateAndReserve()
    {
        var blockTime = new BlockTimeBreakdown(0, 0, 0, 0, 0, 0);

        var result = BlockFuelEstimator.Estimate(blockTime, fuelBurnKgPerHour: 2500);

        Assert.Equal(0, result.TripFuelKg);
        Assert.True(result.TotalFuelKg > 0);
    }

    /// <summary>
    /// ChargedFuelKg (docs/PLAN.md "Persistent fuel state and tankering" - the interim per-sector
    /// billing figure) is trip + taxi + contingency only. Alternate and final reserve are loaded
    /// for safety and normally stay in the tanks unburned, so they must never be billed - see
    /// FuelBreakdown.ChargedFuelKg's own doc for why.
    /// </summary>
    [Fact]
    public void ChargedFuelKg_ExcludesAlternateAndFinalReserve()
    {
        var blockTime = new BlockTimeBreakdown(
            TaxiOutMinutes: 10, ClimbMinutes: 15, CruiseMinutes: 103, DescentMinutes: 15, TaxiInMinutes: 8, TotalMinutes: 151);

        var result = BlockFuelEstimator.Estimate(blockTime, fuelBurnKgPerHour: 2500);

        // Trip 5541.666... + taxi 200 + contingency 277.0833... (see Estimate_MatchesHandCalculatedBreakdown).
        Assert.Equal(6018.75, result.ChargedFuelKg, 2);
        Assert.Equal(result.TripFuelKg + result.TaxiFuelKg + result.ContingencyFuelKg, result.ChargedFuelKg, 6);

        // The whole point: charged fuel must be strictly less than the full block figure, by
        // exactly the alternate + final reserve allowances that never get billed.
        Assert.True(result.ChargedFuelKg < result.TotalFuelKg);
        Assert.Equal(result.AlternateFuelKg + result.FinalReserveFuelKg, result.TotalFuelKg - result.ChargedFuelKg, 6);
    }

    [Fact]
    public void ChargedFuelKg_ZeroAirborneTime_StillChargesTaxiOnly()
    {
        var blockTime = new BlockTimeBreakdown(0, 0, 0, 0, 0, 0);

        var result = BlockFuelEstimator.Estimate(blockTime, fuelBurnKgPerHour: 2500);

        // No trip, no contingency (5% of zero) - just the flat taxi allowance.
        Assert.Equal(200, result.ChargedFuelKg);
    }
}
