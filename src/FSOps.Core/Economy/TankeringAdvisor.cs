namespace FSOps.Core.Economy;

/// <summary>
/// Advisory-only comparison of "uplift extra here to cover the return leg" against "buy minimal
/// here, refuel at the destination" - see docs/PLAN.md "Persistent fuel state and tankering",
/// "the trade-off". Every figure is a raw number, never pre-formatted - money is stored in base
/// units and formatted only for display (the currency is user-selectable), so callers own that.
/// </summary>
public sealed record TankeringAdvisory(
    decimal DeparturePricePerKg,
    decimal DestinationPricePerKg,
    double ReturnLegFuelRequirementKg,
    double CostOfCarryKg,
    decimal CostOfCarryAmount,
    decimal GrossSavingAmount,
    decimal NetSavingAmount,
    bool Recommended,
    bool ExceedsMtow);

/// <summary>
/// Pure "would tankering pay off" calculator. Compares uplifting enough extra fuel here to cover
/// the return leg (paying this airport's price for all of it, but burning a little more of it in
/// the process - <see cref="EconomyConfig.CostOfCarryRatePerHour"/>) against buying nothing extra
/// here and refuelling the return leg at the destination's price. Deliberately does not touch
/// weight-based landing fees as a further counterweight - see the docs/PLAN.md finding recorded
/// alongside this class in Chunk E1's report: FlightCostCalculator.LandingFee is keyed off the
/// aircraft type's fixed MtowTonnes, not the aircraft's actual operating weight, so carrying extra
/// fuel does not currently raise the landing fee it will pay. Cost-of-carry burn is therefore the
/// only counterweight this advisory can honestly account for.
/// </summary>
public static class TankeringAdvisor
{
    /// <summary>
    /// Standard mass value (kg) for one passenger plus carry-on/checked baggage, the same
    /// industry-standard figure (IATA/ICAO "standard weights") real airlines use for load
    /// planning when individual passengers aren't actually weighed. Used only for the MTOW
    /// sanity check below - FSOps has no real weight-and-balance model (aircraft operating empty
    /// weight isn't tracked at all), so this deliberately checks the one thing that's always
    /// true regardless of the aircraft's own empty weight: fuel mass plus a standard estimate of
    /// payload mass can never legitimately exceed the whole aircraft's maximum certified weight.
    /// </summary>
    private const double StandardPaxAndBagWeightKg = 100;

    public static TankeringAdvisory Evaluate(
        EconomyConfig config,
        decimal departurePricePerKg,
        decimal destinationPricePerKg,
        double returnLegFuelRequirementKg,
        double thisLegAirborneHours,
        double fuelOnBoardBeforeExtraUpliftKg,
        int paxBooked,
        double mtowTonnes)
    {
        var priceDeltaPerKg = destinationPricePerKg - departurePricePerKg;

        // No rational reason to tanker at all when the destination isn't pricier - reported as a
        // clean "no advantage" zero rather than a negative saving nobody would ever act on.
        if (returnLegFuelRequirementKg <= 0 || priceDeltaPerKg <= 0)
        {
            return new TankeringAdvisory(departurePricePerKg, destinationPricePerKg, 0, 0, 0m, 0m, 0m, false, false);
        }

        var costOfCarryKg = returnLegFuelRequirementKg * config.Fuel.CostOfCarryRatePerHour * Math.Max(0, thisLegAirborneHours);
        var costOfCarryAmount = (decimal)costOfCarryKg * departurePricePerKg;

        var grossSavingAmount = priceDeltaPerKg * (decimal)returnLegFuelRequirementKg;
        var netSavingAmount = grossSavingAmount - costOfCarryAmount;
        var recommended = netSavingAmount > 0;

        var proposedFuelKg = fuelOnBoardBeforeExtraUpliftKg + returnLegFuelRequirementKg;
        var paxWeightKg = paxBooked * StandardPaxAndBagWeightKg;
        var exceedsMtow = mtowTonnes > 0 && (proposedFuelKg + paxWeightKg) > mtowTonnes * 1000.0;

        return new TankeringAdvisory(
            departurePricePerKg, destinationPricePerKg, returnLegFuelRequirementKg,
            costOfCarryKg, costOfCarryAmount, grossSavingAmount, netSavingAmount, recommended, exceedsMtow);
    }
}
