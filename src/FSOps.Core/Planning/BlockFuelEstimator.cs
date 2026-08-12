namespace FSOps.Core.Planning;

/// <param name="CostOfCarryKg">
/// Extra fuel burned purely because fuel carried beyond this sector's own normal requirement was
/// on board (see <see cref="BlockFuelEstimator.Estimate"/>'s <c>extraCarriedFuelKg</c> parameter
/// and <see cref="FSOps.Core.Economy.EconomyConfig"/>'s <c>Fuel.CostOfCarryRatePerHour</c>) - fuel
/// tankering's counterweight, alongside weight-based landing fees. Zero for every call that
/// doesn't pass extra carried fuel, which is every call site except the tankering advisory - see
/// BlockFuelEstimatorWeightPenaltyTests for the guarantee that a normal (non-tankering) flight's
/// figures are completely unaffected by this field's existence. Already folded into
/// <see cref="TripFuelKg"/> and therefore into <see cref="TotalFuelKg"/>/<see cref="FuelBreakdown.ChargedFuelKg"/>
/// - broken out here purely so the UI can show the player what carrying the extra cost them.
/// </param>
public record FuelBreakdown(
    double TripFuelKg,
    double TaxiFuelKg,
    double ContingencyFuelKg,
    double AlternateFuelKg,
    double FinalReserveFuelKg,
    double TotalFuelKg,
    double CostOfCarryKg = 0)
{
    /// <summary>
    /// Fuel a normal sector actually burns - trip fuel (including any cost-of-carry penalty),
    /// taxi, and contingency - as opposed to <see cref="AlternateFuelKg"/> and
    /// <see cref="FinalReserveFuelKg"/>, which are loaded for safety and, on a normal flight,
    /// stay in the tanks rather than being consumed. Historically this was the interim per-sector
    /// billing figure, until it was found that charging every sector for reserve and alternate fuel
    /// that is never burned made short-haul structurally unprofitable; now that fuel is a
    /// persisted, uplift-charged asset (see FSOps.Server.Services.FlightEconomicsPoster and
    /// FlightEndpoints.StartAsync's reconciliation), this is retained as the "no-telemetry
    /// fallback" charge - the conservative assumption used only when there's no telemetry to
    /// observe a real uplift from.
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

    /// <summary>
    /// <paramref name="extraCarriedFuelKg"/> and <paramref name="costOfCarryRatePerHour"/> both
    /// default to zero, so every existing call site (route preview, the flight brief's planned
    /// figure) is completely unaffected - see BlockFuelEstimatorWeightPenaltyTests. Only the
    /// tankering advisory passes non-zero values, to estimate what carrying extra fuel for this
    /// sector's whole airborne time would cost.
    /// </summary>
    public static FuelBreakdown Estimate(
        BlockTimeBreakdown blockTime, double fuelBurnKgPerHour, double extraCarriedFuelKg = 0, double costOfCarryRatePerHour = 0)
    {
        var airborneMinutes = blockTime.ClimbMinutes + blockTime.CruiseMinutes + blockTime.DescentMinutes;
        var airborneHours = airborneMinutes / 60.0;
        var baseTripFuelKg = fuelBurnKgPerHour * airborneHours;
        var costOfCarryKg = extraCarriedFuelKg > 0 && costOfCarryRatePerHour > 0
            ? extraCarriedFuelKg * costOfCarryRatePerHour * airborneHours
            : 0;
        var tripFuelKg = baseTripFuelKg + costOfCarryKg;
        var contingencyKg = tripFuelKg * ContingencyRate;
        var finalReserveKg = fuelBurnKgPerHour * FinalReserveMinutes / 60.0;
        var totalKg = tripFuelKg + TaxiFuelKg + contingencyKg + AlternateFuelKg + finalReserveKg;

        return new FuelBreakdown(tripFuelKg, TaxiFuelKg, contingencyKg, AlternateFuelKg, finalReserveKg, totalKg, costOfCarryKg);
    }
}
