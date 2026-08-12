using FSOps.Core.Entities;

namespace FSOps.Core.Economy;

/// <summary>
/// Every cost line for a single flight, itemised. Fuel is charged on what was actually burned,
/// at the departure airport's price - that is where the aircraft would realistically have been
/// fuelled - never on what was merely carried, and never credited for fuel left in the tank at
/// the end. Landing, handling and parking are weight-based like the real thing, so a bigger
/// aircraft costs more to operate into an airport than a smaller one.
/// <para>
/// That weight is the type's <b>fixed certificated MTOW</b>, never the aircraft's actual weight on
/// the day, so how much fuel happens to be on board never changes these fees.
/// </para>
/// </summary>
public static class FlightCostCalculator
{
    /// <summary>Charged only on a positive burn; zero for a non-positive or missing figure (see
    /// <see cref="FSOps.Core.Flights.FuelBurnResolver"/> for how a burn is resolved before it
    /// reaches here - this is purely the price multiplication, with its own defensive floor).</summary>
    public static decimal FuelBurnCost(double burnedKg, decimal pricePerKgAtDepartureAirport)
    {
        if (burnedKg <= 0)
        {
            return 0m;
        }

        return Math.Round((decimal)burnedKg * pricePerKgAtDepartureAirport, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Real-world-style landing fee: rate per tonne of MTOW, scaled by how significant
    /// the airport is. Identical regardless of airline strategy - this is a regulatory/physical
    /// charge, not a service-level one.</summary>
    public static decimal LandingFee(CostConfig config, AirportSizeCategory airportSize, double mtowTonnes)
        => WeightBasedFee(config.LandingFeeRate, airportSize, mtowTonnes, strategyCostMultiplier: 1.0);

    public static decimal HandlingFee(CostConfig config, AirportSizeCategory airportSize, double mtowTonnes, double strategyCostMultiplier = 1.0)
        => WeightBasedFee(config.HandlingFeeRate, airportSize, mtowTonnes, strategyCostMultiplier);

    public static decimal ParkingFee(CostConfig config, AirportSizeCategory airportSize, double mtowTonnes, double strategyCostMultiplier = 1.0)
        => WeightBasedFee(config.ParkingFeeRate, airportSize, mtowTonnes, strategyCostMultiplier);

    /// <summary>Flat per-sector ground-ops/gate charge - deliberately NOT weight-based, unlike
    /// the fees above. See CostConfig.TurnaroundFeeRate: this is what stops a cheater dodging
    /// every other cost by flying a tiny, light aircraft on a trivial sector.</summary>
    public static decimal TurnaroundFee(CostConfig config, AirportSizeCategory airportSize)
        => config.TurnaroundFeeRate.RateFor(airportSize);

    public static decimal PassengerCharge(CostConfig config, AirportSizeCategory airportSize, int paxBooked, double strategyCostMultiplier = 1.0)
    {
        if (paxBooked <= 0)
        {
            return 0m;
        }

        var rate = config.PassengerChargeRate.RateFor(airportSize);
        return Math.Round(rate * paxBooked * (decimal)strategyCostMultiplier, 2, MidpointRounding.AwayFromZero);
    }

    public static decimal MaintenanceAccrual(CostConfig config, double flightHours)
    {
        if (flightHours <= 0)
        {
            return 0m;
        }

        return Math.Round(config.MaintenanceAccrualPerHour * (decimal)flightHours, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Crew are paid for a minimum duty block (CostConfig.MinimumCrewDutyHours)
    /// regardless of how short the sector was - see the field doc for why.</summary>
    public static decimal CrewCost(CostConfig config, double flightHours)
    {
        if (flightHours <= 0)
        {
            return 0m;
        }

        var billedHours = Math.Max(flightHours, config.MinimumCrewDutyHours);
        return Math.Round(config.CrewCostPerHour * (decimal)billedHours, 2, MidpointRounding.AwayFromZero);
    }

    private static decimal WeightBasedFee(AirportSizeRateTable table, AirportSizeCategory airportSize, double mtowTonnes, double strategyCostMultiplier)
    {
        if (mtowTonnes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mtowTonnes), "MTOW must be positive.");
        }

        var rate = table.RateFor(airportSize);
        return Math.Round(rate * (decimal)mtowTonnes * (decimal)strategyCostMultiplier, 2, MidpointRounding.AwayFromZero);
    }
}
