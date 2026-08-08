using FSOps.Core.Economy;

namespace FSOps.Core.Tests.Economy;

public class TankeringAdvisorTests
{
    private static readonly EconomyConfig Config = EconomyConfig.Default();

    [Fact]
    public void DestinationCheaperThanDeparture_NotRecommended_NoSaving()
    {
        var advisory = TankeringAdvisor.Evaluate(
            Config, departurePricePerKg: 1.00m, destinationPricePerKg: 0.80m,
            returnLegFuelRequirementKg: 2000, thisLegAirborneHours: 2, fuelOnBoardBeforeExtraUpliftKg: 3000,
            paxBooked: 150, mtowTonnes: 78);

        Assert.False(advisory.Recommended);
        Assert.Equal(0m, advisory.NetSavingAmount);
        Assert.Equal(0m, advisory.GrossSavingAmount);
    }

    [Fact]
    public void DestinationMaterialllyPricier_SmallCostOfCarry_IsRecommended()
    {
        // Destination is 50% pricier; a short 1-hour leg means little cost-of-carry burn.
        var advisory = TankeringAdvisor.Evaluate(
            Config, departurePricePerKg: 0.80m, destinationPricePerKg: 1.20m,
            returnLegFuelRequirementKg: 2000, thisLegAirborneHours: 1, fuelOnBoardBeforeExtraUpliftKg: 3000,
            paxBooked: 150, mtowTonnes: 78);

        Assert.True(advisory.Recommended);
        Assert.True(advisory.NetSavingAmount > 0);
        Assert.Equal(advisory.GrossSavingAmount - advisory.CostOfCarryAmount, advisory.NetSavingAmount);

        // Gross saving is exactly the price delta times the return leg's requirement.
        Assert.Equal((1.20m - 0.80m) * 2000m, advisory.GrossSavingAmount);
    }

    [Fact]
    public void CostOfCarry_ScalesWithExtraFuelAndAirborneHours()
    {
        var oneHour = TankeringAdvisor.Evaluate(
            Config, departurePricePerKg: 0.80m, destinationPricePerKg: 1.20m,
            returnLegFuelRequirementKg: 2000, thisLegAirborneHours: 1, fuelOnBoardBeforeExtraUpliftKg: 3000,
            paxBooked: 150, mtowTonnes: 78);
        var fourHours = TankeringAdvisor.Evaluate(
            Config, departurePricePerKg: 0.80m, destinationPricePerKg: 1.20m,
            returnLegFuelRequirementKg: 2000, thisLegAirborneHours: 4, fuelOnBoardBeforeExtraUpliftKg: 3000,
            paxBooked: 150, mtowTonnes: 78);

        // Same gross saving (same fuel quantity/prices), but carrying it for longer costs more,
        // so the net saving over a long sector must be strictly smaller than over a short one -
        // this is the counterweight the plan requires ("carrying fuel must cost fuel").
        Assert.Equal(oneHour.GrossSavingAmount, fourHours.GrossSavingAmount);
        Assert.True(fourHours.CostOfCarryAmount > oneHour.CostOfCarryAmount);
        Assert.True(fourHours.NetSavingAmount < oneHour.NetSavingAmount);
        Assert.Equal(oneHour.CostOfCarryKg * 4, fourHours.CostOfCarryKg, 6);
    }

    /// <summary>
    /// The advantage is bounded, not unlimited: as the sector gets long enough, cost-of-carry
    /// eventually consumes the whole gross saving and tankering stops paying off, however large
    /// the up-front price gap looked. This is the property the coordinator asked to be verified
    /// rather than assumed.
    /// </summary>
    [Fact]
    public void SufficientlyLongSector_CostOfCarryEventuallyEliminatesTheAdvantage()
    {
        // A modest 11% price gap (0.90 -> 1.00) makes the crossover land within realistic sector
        // lengths: gross saving is fixed at 300 regardless of duration, while cost-of-carry grows
        // with airborne hours, so it must eventually exceed the saving.
        var shortSector = TankeringAdvisor.Evaluate(
            Config, departurePricePerKg: 0.90m, destinationPricePerKg: 1.00m,
            returnLegFuelRequirementKg: 3000, thisLegAirborneHours: 1, fuelOnBoardBeforeExtraUpliftKg: 4000,
            paxBooked: 150, mtowTonnes: 78);
        var longSector = TankeringAdvisor.Evaluate(
            Config, departurePricePerKg: 0.90m, destinationPricePerKg: 1.00m,
            returnLegFuelRequirementKg: 3000, thisLegAirborneHours: 10, fuelOnBoardBeforeExtraUpliftKg: 4000,
            paxBooked: 150, mtowTonnes: 78);

        Assert.True(shortSector.Recommended);
        Assert.False(longSector.Recommended);
        Assert.Equal(shortSector.GrossSavingAmount, longSector.GrossSavingAmount);
        Assert.True(longSector.NetSavingAmount < shortSector.NetSavingAmount);
    }

    [Fact]
    public void ZeroReturnLegRequirement_ProducesTheZeroAdvisory()
    {
        var advisory = TankeringAdvisor.Evaluate(
            Config, departurePricePerKg: 0.80m, destinationPricePerKg: 1.20m,
            returnLegFuelRequirementKg: 0, thisLegAirborneHours: 2, fuelOnBoardBeforeExtraUpliftKg: 3000,
            paxBooked: 150, mtowTonnes: 78);

        Assert.False(advisory.Recommended);
        Assert.Equal(0m, advisory.NetSavingAmount);
        Assert.False(advisory.ExceedsMtow);
    }

    [Fact]
    public void ProposedLoad_WellWithinMtow_DoesNotWarn()
    {
        // A320: MTOW 78t = 78,000 kg. 3,000 kg fuel already planned + 2,000 kg extra + 150 pax at
        // the 100 kg standard mass = 20,000 kg payload - nowhere near 78,000 kg.
        var advisory = TankeringAdvisor.Evaluate(
            Config, departurePricePerKg: 0.80m, destinationPricePerKg: 1.20m,
            returnLegFuelRequirementKg: 2000, thisLegAirborneHours: 2, fuelOnBoardBeforeExtraUpliftKg: 3000,
            paxBooked: 150, mtowTonnes: 78);

        Assert.False(advisory.ExceedsMtow);
    }

    [Fact]
    public void ProposedLoad_ExceedingMtow_Warns()
    {
        // A small type (MTOW 20t = 20,000 kg) asked to carry a genuinely absurd tankered load.
        var advisory = TankeringAdvisor.Evaluate(
            Config, departurePricePerKg: 0.80m, destinationPricePerKg: 1.20m,
            returnLegFuelRequirementKg: 15000, thisLegAirborneHours: 2, fuelOnBoardBeforeExtraUpliftKg: 4000,
            paxBooked: 50, mtowTonnes: 20);

        Assert.True(advisory.ExceedsMtow);
    }
}
