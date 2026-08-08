using FSOps.Core.Economy;
using FSOps.Core.Entities;

namespace FSOps.Core.Tests.Economy;

public class FlightEconomicsCalculatorTests
{
    private static readonly EconomyConfig Config = EconomyConfig.Default();

    [Fact]
    public void Calculate_NetProfit_AlwaysEqualsRevenueMinusTheSumOfEveryCostLine()
    {
        var result = FlightEconomicsCalculator.Calculate(
            Config, AirlineStrategyProfile.Domestic, fare: 90m, referenceFare: 74.40m,
            seats: 180, marketDemandPax: 150, upliftKg: 2200, pricePerKgAtUpliftAirport: 0.85m,
            arrivalAirportSize: AirportSizeCategory.Medium, mtowTonnes: 78, flightHours: 1.4);

        var expectedTotalCost = result.FuelCost + result.LandingFee + result.HandlingFee + result.ParkingFee +
            result.PassengerCharge + result.TurnaroundFee + result.MaintenanceAccrual + result.CrewCost;

        Assert.Equal(expectedTotalCost, result.TotalCost);
        Assert.Equal(result.TicketRevenue - expectedTotalCost, result.NetProfit);
    }

    [Fact]
    public void Calculate_ReturnLegOnFuelAlreadyInTanks_HasZeroFuelCostButStillEarnsRevenue()
    {
        var result = FlightEconomicsCalculator.Calculate(
            Config, AirlineStrategyProfile.Domestic, fare: 74.40m, referenceFare: 74.40m,
            seats: 180, marketDemandPax: 150, upliftKg: 0, pricePerKgAtUpliftAirport: 0.85m,
            arrivalAirportSize: AirportSizeCategory.Medium, mtowTonnes: 78, flightHours: 1.4);

        Assert.Equal(0m, result.FuelCost);
        Assert.True(result.PaxBooked > 0);
        Assert.True(result.TicketRevenue > 0);
    }

    [Fact]
    public void Calculate_EveryCostLineIsNonNegative()
    {
        var result = FlightEconomicsCalculator.Calculate(
            Config, AirlineStrategyProfile.Premium, fare: 500m, referenceFare: 300m,
            seats: 250, marketDemandPax: 180, upliftKg: 8000, pricePerKgAtUpliftAirport: 1.10m,
            arrivalAirportSize: AirportSizeCategory.Large, mtowTonnes: 180, flightHours: 8.5);

        Assert.True(result.FuelCost >= 0);
        Assert.True(result.LandingFee >= 0);
        Assert.True(result.HandlingFee >= 0);
        Assert.True(result.ParkingFee >= 0);
        Assert.True(result.PassengerCharge >= 0);
        Assert.True(result.TurnaroundFee >= 0);
        Assert.True(result.MaintenanceAccrual >= 0);
        Assert.True(result.CrewCost >= 0);
    }

    [Fact]
    public void Calculate_UnknownStrategyProfile_Throws()
    {
        var incomplete = new EconomyConfig
        {
            StrategyProfiles = new List<StrategyProfileConfig>
            {
                new(AirlineStrategyProfile.Domestic, 1.0m, 0.78, 1.3, 1.0),
            },
        };

        Assert.Throws<InvalidOperationException>(() => FlightEconomicsCalculator.Calculate(
            incomplete, AirlineStrategyProfile.Premium, 100m, 100m, 180, 150, 1000, 0.85m,
            AirportSizeCategory.Medium, 78, 1.4));
    }
}
