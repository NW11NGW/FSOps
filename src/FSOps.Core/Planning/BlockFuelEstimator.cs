namespace FSOps.Core.Planning;

public record FuelBreakdown(
    double TripFuelKg,
    double TaxiFuelKg,
    double ContingencyFuelKg,
    double AlternateFuelKg,
    double FinalReserveFuelKg,
    double TotalFuelKg);

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
        var tripFuelKg = fuelBurnKgPerHour * airborneMinutes / 60.0;
        var contingencyKg = tripFuelKg * ContingencyRate;
        var finalReserveKg = fuelBurnKgPerHour * FinalReserveMinutes / 60.0;
        var totalKg = tripFuelKg + TaxiFuelKg + contingencyKg + AlternateFuelKg + finalReserveKg;

        return new FuelBreakdown(tripFuelKg, TaxiFuelKg, contingencyKg, AlternateFuelKg, finalReserveKg, totalKg);
    }
}
