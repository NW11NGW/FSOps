using FSOps.Core.Entities;

namespace FSOps.Core.Economy;

/// <summary>
/// Itemised financial outcome of one flight - every revenue and cost line kept separate so the
/// report card UI can show where the money went, and so later ledger-posting code can turn each
/// line into its own LedgerTransaction with the right LedgerCategory rather than one lump sum.
/// TotalCost and NetProfit are computed, never stored, so they can never drift out of sync with
/// the line items they're built from.
///
/// <para><b>Integrity by construction, not by convention:</b> there is deliberately no field here
/// that can grow because a flight was quick, early, or ahead of schedule - <c>TicketRevenue</c>
/// is always <c>paxBooked x fare</c> and nothing else feeds it. A caller with a block-time input
/// may let it affect costs (less fuel burnt, less crew/maintenance time), but there is no
/// mechanism anywhere in this type for it to affect revenue. An incomplete or abandoned flight -
/// modelled here as zero <c>PaxBooked</c>, since nobody boarded a flight that never operated -
/// therefore always has <c>TicketRevenue = 0</c>: NetProfit can never be positive for one unless
/// every cost line is also zero, and fuel already uplifted is never refunded (see
/// FlightCostCalculator.FuelUpliftCost), so a flight that uplifted fuel and then went nowhere
/// always nets strictly negative.</para>
/// </summary>
public sealed record FlightEconomicsResult(
    int PaxBooked,
    double LoadFactor,
    decimal TicketRevenue,
    decimal FuelCost,
    decimal LandingFee,
    decimal HandlingFee,
    decimal ParkingFee,
    decimal PassengerCharge,
    decimal TurnaroundFee,
    decimal MaintenanceAccrual,
    decimal CrewCost)
{
    public decimal TotalCost =>
        FuelCost + LandingFee + HandlingFee + ParkingFee + PassengerCharge + TurnaroundFee + MaintenanceAccrual + CrewCost;

    public decimal NetProfit => TicketRevenue - TotalCost;
}

/// <summary>Ties DemandCalculator, FareDemandModel, FuelPricing and FlightCostCalculator
/// together into one itemised result for a single flight. This is the shape the later wiring
/// task (posting to the ledger, showing the report card) is expected to call.</summary>
public static class FlightEconomicsCalculator
{
    public static FlightEconomicsResult Calculate(
        EconomyConfig config,
        AirlineStrategyProfile strategy,
        decimal fare,
        decimal referenceFare,
        int seats,
        int marketDemandPax,
        // Named for BURN, not uplift, and the distinction is the whole of how fuel is billed now: a
        // sector pays for the fuel it actually consumed, priced where it departed. The old names
        // survived the change to burn billing and were mistaken for dead weight afterwards - they
        // are not. A virtual pilot's sector passes a real figure here (see PilotEndpoints), because
        // nothing tracked its tanks; a flown sector passes zero and is charged separately from
        // measured burn, which is why both a zero and a real value are correct callers.
        double chargedFuelKg,
        decimal pricePerKgAtDepartureAirport,
        AirportSizeCategory arrivalAirportSize,
        double mtowTonnes,
        double flightHours)
    {
        var strategyConfig = config.GetStrategy(strategy);
        var booking = FareDemandModel.Calculate(
            config.MaxLoadFactor, strategyConfig, fare, referenceFare, seats, marketDemandPax,
            config.CaptiveFareCeilingMultiple, config.PostCaptiveElasticity);

        var fuelCost = FlightCostCalculator.FuelBurnCost(chargedFuelKg, pricePerKgAtDepartureAirport);
        var landingFee = FlightCostCalculator.LandingFee(config.Costs, arrivalAirportSize, mtowTonnes);
        var handlingFee = FlightCostCalculator.HandlingFee(config.Costs, arrivalAirportSize, mtowTonnes, strategyConfig.CostMultiplier);
        var parkingFee = FlightCostCalculator.ParkingFee(config.Costs, arrivalAirportSize, mtowTonnes, strategyConfig.CostMultiplier);
        var passengerCharge = FlightCostCalculator.PassengerCharge(config.Costs, arrivalAirportSize, booking.PaxBooked, strategyConfig.CostMultiplier);
        var turnaroundFee = FlightCostCalculator.TurnaroundFee(config.Costs, arrivalAirportSize);
        var maintenance = FlightCostCalculator.MaintenanceAccrual(config.Costs, flightHours);
        var crew = FlightCostCalculator.CrewCost(config.Costs, flightHours);

        return new FlightEconomicsResult(
            booking.PaxBooked,
            booking.LoadFactor,
            booking.Revenue,
            fuelCost,
            landingFee,
            handlingFee,
            parkingFee,
            passengerCharge,
            turnaroundFee,
            maintenance,
            crew);
    }
}
