using FSOps.Core.Economy;
using FSOps.Core.Entities;

namespace FSOps.Core.Tests.Economy;

public class FlightCostCalculatorTests
{
    private static readonly CostConfig Config = EconomyConfig.Default().Costs;

    [Fact]
    public void FuelBurnCost_ZeroBurn_IsZero()
    {
        Assert.Equal(0m, FlightCostCalculator.FuelBurnCost(0, 0.85m));
    }

    [Fact]
    public void FuelBurnCost_NegativeBurn_IsZero()
    {
        // Defensive floor - FuelBurnResolver already guards against a resolved figure ever going
        // negative, but this stays a non-event rather than a credit regardless of what reaches it.
        Assert.Equal(0m, FlightCostCalculator.FuelBurnCost(-500, 0.85m));
    }

    [Fact]
    public void FuelBurnCost_PositiveBurn_MatchesExactCalculation()
    {
        Assert.Equal(850.00m, FlightCostCalculator.FuelBurnCost(1000, 0.85m));
    }

    [Fact]
    public void LandingFee_ScalesLinearlyWithMtow()
    {
        // This is a property of the MTOW input alone: production always passes the aircraft
        // type's fixed certificated MtowTonnes here, never the aircraft's actual weight on the
        // day, so how much fuel happens to be on board never changes this fee.
        var at50Tonnes = FlightCostCalculator.LandingFee(Config, AirportSizeCategory.Large, 50);
        var at100Tonnes = FlightCostCalculator.LandingFee(Config, AirportSizeCategory.Large, 100);

        Assert.Equal(475.00m, at50Tonnes);
        Assert.Equal(950.00m, at100Tonnes);
        Assert.Equal(at50Tonnes * 2, at100Tonnes);
    }

    [Fact]
    public void LandingFee_NonPositiveMtow_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FlightCostCalculator.LandingFee(Config, AirportSizeCategory.Large, 0));
    }

    [Fact]
    public void HandlingFee_AppliesStrategyCostMultiplier()
    {
        var fee = FlightCostCalculator.HandlingFee(Config, AirportSizeCategory.Large, 50, strategyCostMultiplier: 0.85);
        Assert.Equal(276.25m, fee); // 6.50 * 50 * 0.85
    }

    [Fact]
    public void TurnaroundFee_IsFlatRegardlessOfWeight()
    {
        var forALightAircraft = FlightCostCalculator.TurnaroundFee(Config, AirportSizeCategory.Small);
        var forAHeavyAircraft = FlightCostCalculator.TurnaroundFee(Config, AirportSizeCategory.Small);

        Assert.Equal(90m, forALightAircraft);
        Assert.Equal(forALightAircraft, forAHeavyAircraft);
    }

    [Fact]
    public void PassengerCharge_ZeroPax_IsZero()
    {
        Assert.Equal(0m, FlightCostCalculator.PassengerCharge(Config, AirportSizeCategory.Large, 0));
    }

    [Fact]
    public void PassengerCharge_MatchesExactCalculation()
    {
        Assert.Equal(420.00m, FlightCostCalculator.PassengerCharge(Config, AirportSizeCategory.Large, 35));
    }

    [Fact]
    public void MaintenanceAccrual_MatchesExactCalculation()
    {
        Assert.Equal(525.00m, FlightCostCalculator.MaintenanceAccrual(Config, 2.5));
    }

    [Fact]
    public void CrewCost_LongFlight_MatchesExactCalculation()
    {
        Assert.Equal(850.00m, FlightCostCalculator.CrewCost(Config, 2.5));
    }

    [Fact]
    public void CrewCost_UltraShortFlight_IsFlooredAtMinimumDutyHours()
    {
        // A 6-minute sector still bills at least one hour of crew duty (MinimumCrewDutyHours) -
        // a real crew doesn't get sent home and re-hired for a hop this short.
        var ultraShort = FlightCostCalculator.CrewCost(Config, flightHours: 0.1);
        var exactlyOneHour = FlightCostCalculator.CrewCost(Config, flightHours: 1.0);

        Assert.Equal(exactlyOneHour, ultraShort);
        Assert.Equal(340.00m, ultraShort);
    }

    [Fact]
    public void CrewCost_ZeroFlightHours_IsZero()
    {
        // Distinguishes "flight never happened" from "flight happened but was very short" -
        // only a flight that actually operated bills the minimum duty floor.
        Assert.Equal(0m, FlightCostCalculator.CrewCost(Config, 0));
    }
}
