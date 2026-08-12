using FSOps.Core.Economy;

namespace FSOps.Core.Finance;

/// <summary>
/// Pure sale-value arithmetic for disposing of an owned <see cref="Entities.FleetAircraft"/> - see
/// selling must lose money on the spread, or buying and selling is a free round trip and cash
/// stops meaning anything. No I/O, no randomness,
/// same convention as <see cref="LoanCalculator"/> and the rest of the economy engine.
/// <para>
/// <b>Formula.</b> Starting from <see cref="AircraftDepreciationConfig.NewAircraftResaleFactor"/>,
/// two wear terms are subtracted - condition (how banged-up the airframe is) and cycle progress
/// (how close it is to needing an A- or C-check) - plus a flat cut while grounded for maintenance,
/// then the result is clamped to <see cref="AircraftDepreciationConfig.MinResaleFactor"/> and
/// applied to the playstyle's own new price for the type
/// (<see cref="Economy.EconomyConfig.PurchasePriceFor"/> - never the shared realistic
/// <c>AircraftType.PurchasePrice</c> directly, same rule as buying new or used).
/// </para>
/// </summary>
public static class AircraftDepreciationCalculator
{
    public static AircraftSaleQuote ComputeSaleValue(
        decimal newPrice,
        double conditionPercent,
        double hoursSinceACheck,
        double hoursSinceCCheck,
        double aCheckIntervalHours,
        double cCheckIntervalHours,
        bool isGroundedForMaintenance,
        AircraftDepreciationConfig config)
    {
        if (newPrice < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newPrice), "newPrice cannot be negative.");
        }

        var conditionFraction = Math.Clamp((100.0 - conditionPercent) / 100.0, 0.0, 1.0);

        var aCheckProgress = aCheckIntervalHours > 0 ? Math.Clamp(hoursSinceACheck / aCheckIntervalHours, 0.0, 1.0) : 0.0;
        var cCheckProgress = cCheckIntervalHours > 0 ? Math.Clamp(hoursSinceCCheck / cCheckIntervalHours, 0.0, 1.0) : 0.0;
        var cycleProgress = (aCheckProgress + cCheckProgress) / 2.0;

        var resaleFactor = config.NewAircraftResaleFactor
            - config.ConditionDepreciationWeight * (decimal)conditionFraction
            - config.CycleWearDepreciationWeight * (decimal)cycleProgress;

        if (isGroundedForMaintenance)
        {
            resaleFactor -= config.GroundedForMaintenancePenalty;
        }

        resaleFactor = Math.Clamp(resaleFactor, config.MinResaleFactor, config.NewAircraftResaleFactor);

        var saleValue = Math.Round(newPrice * resaleFactor, 2, MidpointRounding.AwayFromZero);

        return new AircraftSaleQuote(newPrice, resaleFactor, saleValue);
    }
}

/// <summary>Result of <see cref="AircraftDepreciationCalculator.ComputeSaleValue"/> - the
/// intermediate <see cref="ResaleFactorApplied"/> is exposed so the sale-quote endpoint can show it
/// (a bare final figure with no explanation reads as arbitrary).</summary>
public sealed record AircraftSaleQuote(decimal NewPrice, decimal ResaleFactorApplied, decimal SaleValue);
