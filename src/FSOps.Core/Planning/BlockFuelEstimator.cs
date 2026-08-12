namespace FSOps.Core.Planning;

public record FuelBreakdown(
    double TripFuelKg,
    double TaxiFuelKg,
    double ContingencyFuelKg,
    double AlternateFuelKg,
    double FinalReserveFuelKg,
    double TotalFuelKg)
{
    /// <summary>
    /// Fuel a normal sector actually burns - trip fuel, taxi, and contingency - as opposed to
    /// <see cref="AlternateFuelKg"/> and <see cref="FinalReserveFuelKg"/>, which are loaded for
    /// safety and, on a normal flight, stay in the tanks rather than being consumed. This is what a
    /// sector is billed for: the departure airport's fuel price times whatever was actually burned
    /// (see FSOps.Server.Services.FlightEconomicsPoster.PostFuelBurn), falling back to this figure
    /// when there's no usable telemetry to measure a real burn from (no connection, a manual
    /// completion, a virtual pilot's occurrence, or an implausible reading - see
    /// FSOps.Core.Flights.FuelBurnResolver).
    /// </summary>
    public double ChargedFuelKg => TripFuelKg + TaxiFuelKg + ContingencyFuelKg;
}

/// <summary>
/// Estimates block fuel from the airborne portion of block time (climb+cruise+descent - taxi
/// gets its own flat allowance since it isn't part of BlockTimeBreakdown's airborne phases) and
/// the type's average burn rate. Adds a 5% contingency, a flat alternate-diversion allowance,
/// and a 30-minute final reserve, following standard IFR planning practice simplified for a
/// game economy.
/// </summary>
public static class BlockFuelEstimator
{
    private const double TaxiFuelKg = 200;
    private const double AlternateFuelKg = 1200;
    private const double ContingencyRate = 0.05;
    private const double FinalReserveMinutes = 30;

    public static FuelBreakdown Estimate(BlockTimeBreakdown blockTime, double fuelBurnKgPerHour)
    {
        var airborneMinutes = blockTime.ClimbMinutes + blockTime.CruiseMinutes + blockTime.DescentMinutes;
        var airborneHours = airborneMinutes / 60.0;
        var tripFuelKg = fuelBurnKgPerHour * airborneHours;
        var contingencyKg = tripFuelKg * ContingencyRate;
        var finalReserveKg = fuelBurnKgPerHour * FinalReserveMinutes / 60.0;
        var totalKg = tripFuelKg + TaxiFuelKg + contingencyKg + AlternateFuelKg + finalReserveKg;

        return new FuelBreakdown(tripFuelKg, TaxiFuelKg, contingencyKg, AlternateFuelKg, finalReserveKg, totalKg);
    }
}
