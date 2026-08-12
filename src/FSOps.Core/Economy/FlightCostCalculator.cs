using FSOps.Core.Entities;

namespace FSOps.Core.Economy;

/// <summary>
/// Every cost line for a single flight, itemised. Fuel is charged on what was uplifted, at the
/// price where it was uplifted - never on what was burned, and never when nothing was uplifted
/// (a return leg flown on fuel already in the tanks costs nothing further in fuel). Landing,
/// handling and parking are weight-based like the real thing, so a bigger aircraft costs more to
/// operate into an airport than a smaller one.
/// <para>
/// That weight is the type's <b>fixed certificated MTOW</b>, never the aircraft's actual weight on
/// the day, so <b>tankered fuel does not raise these fees at all</b> - a tankered sector pays
/// exactly what an empty one does. An earlier version of this comment claimed the opposite and
/// called it tankering's "second counterweight"; it is not. The extra fuel burned carrying the
/// extra weight (see BlockFuelEstimator's cost-of-carry) is the <b>only</b> counterweight, which
/// is worth knowing before tuning either number - there is no second lever here to lean on.
/// </para>
/// </summary>
public static class FlightCostCalculator
{
    /// <summary>Charged only on a positive rise in fuel on board; zero for any leg where no
    /// fuel was uplifted (fuel already owned has already been paid for).</summary>
    public static decimal FuelUpliftCost(double upliftKg, decimal pricePerKgAtUpliftAirport)
    {
        if (upliftKg <= 0)
        {
            return 0m;
        }

        return Math.Round((decimal)upliftKg * pricePerKgAtUpliftAirport, 2, MidpointRounding.AwayFromZero);
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
