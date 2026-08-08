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
    /// safety and, on a normal flight, stay in the tanks rather than being consumed. This is the
    /// figure the interim per-sector billing model charges (see
    /// FSOps.Server.Services.FlightEconomicsPoster.PostFuelUplift and docs/PLAN.md "Persistent
    /// fuel state and tankering" - "you pay for fuel when you BUY it, not when you burn it").
    /// Charging the full <see cref="TotalFuelKg"/> every sector would bill for reserve/alternate
    /// fuel that's never actually consumed and simply vanishes, since <c>FleetAircraft</c> has no
    /// persisted tank state yet to carry it over between flights. Once persisted fuel state and
    /// real uplift detection land, this property is superseded entirely - the charge will be
    /// whatever was genuinely uplifted, and this simplification goes away. <see cref="TotalFuelKg"/>
    /// itself is untouched by this and stays the full, realistic block-fuel figure a pilot would
    /// actually load - that's a separate concept (what to load) from this one (what gets billed).
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
        var tripFuelKg = fuelBurnKgPerHour * airborneMinutes / 60.0;
        var contingencyKg = tripFuelKg * ContingencyRate;
        var finalReserveKg = fuelBurnKgPerHour * FinalReserveMinutes / 60.0;
        var totalKg = tripFuelKg + TaxiFuelKg + contingencyKg + AlternateFuelKg + finalReserveKg;

        return new FuelBreakdown(tripFuelKg, TaxiFuelKg, contingencyKg, AlternateFuelKg, finalReserveKg, totalKg);
    }
}
