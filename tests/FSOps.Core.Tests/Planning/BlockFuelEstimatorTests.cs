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
}
