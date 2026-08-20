using FSOps.Core.Contracts;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.SimAircraft;

namespace FSOps.Core.Tests.Contracts;

/// <summary>
/// The money in contract flying, asserted to the penny.
///
/// <para>Exact values throughout rather than ranges, in the style of the rest of the economy tests:
/// the calculator is pure and deterministic, so there is no excuse for an approximate expectation,
/// and a figure that drifts by a penny is exactly the kind of change that should have to be made
/// deliberately.</para>
/// </summary>
public class ContractPayCalculatorTests
{
    private static readonly ContractConfig Config = new();

    private static ContractAircraft Aircraft(
        ContractAircraftCategory category = ContractAircraftCategory.UtilityTurboprop,
        int seats = 9,
        int payloadKg = 1_400,
        int rangeNm = 1_000,
        int cruiseTasKts = 250) =>
        new("TEST", "Test Aircraft", "Testing", category, seats, payloadKg, rangeNm, cruiseTasKts,
            "[]", SimAircraftAvailability.Standard, Array.Empty<string>());

    // ---------- The fee ----------

    /// <summary>
    /// The whole fee, computed by hand from the shipped defaults so the arithmetic is visible rather
    /// than restated from the implementation:
    ///   base 900 + (3.10 x 400 nm = 1,240) + (850 x 1 leg) + (0.55 x 500 kg = 275) = 3,265
    ///   x 1.00 (UtilityTurboprop) x 1.00 (Cargo) = 3,265.00
    /// </summary>
    [Fact]
    public void CalculateFee_ForASingleLegCargoJob_IsTheHandComputedFigure()
    {
        var fee = ContractPayCalculator.CalculateFee(
            Config, ContractKind.Cargo, Aircraft(), totalDistanceNm: 400, legCount: 1, payloadKg: 500, paxCount: 0);

        Assert.Equal(3_265.00m, fee);
    }

    /// <summary>
    /// The same distance split across five legs pays materially more than one leg, and that is the
    /// point of feePerLeg rather than an accident of it. Five 300 nm sectors are five departures and
    /// five approaches; one 1,500 nm sector is one of each.
    ///   base 900 + (3.10 x 1500 = 4,650) + (850 x 5 = 4,250) = 9,800 x 0.55 (LightSingle) x 1.10 (Ferry)
    /// </summary>
    [Fact]
    public void CalculateFee_PaysPerLegAsWellAsPerMile_SoAManyLeggedCrossingIsNotUnderpaid()
    {
        var aircraft = Aircraft(ContractAircraftCategory.LightSingle, seats: 3, payloadKg: 340, rangeNm: 640, cruiseTasKts: 122);

        var fiveLegs = ContractPayCalculator.CalculateFee(
            Config, ContractKind.Ferry, aircraft, totalDistanceNm: 1_500, legCount: 5, payloadKg: 0, paxCount: 0);
        var oneLeg = ContractPayCalculator.CalculateFee(
            Config, ContractKind.Ferry, aircraft, totalDistanceNm: 1_500, legCount: 1, payloadKg: 0, paxCount: 0);

        // five legs: (900 + 4,650 + 4,250) = 9,800 x 0.55 x 1.10 = 5,929.00
        // one leg:   (900 + 4,650 +   850) = 6,400 x 0.55 x 1.10 = 3,872.00
        Assert.Equal(5_929.00m, fiveLegs);
        Assert.Equal(3_872.00m, oneLeg);
        Assert.True(fiveLegs > oneLeg);
    }

    /// <summary>Scale has to reach the fee, or a board with a genuine spread of sizes would pay the same for all of it.</summary>
    [Fact]
    public void CalculateFee_ScalesWithDistanceAndWithAircraftSize()
    {
        var small = ContractPayCalculator.CalculateFee(
            Config, ContractKind.Ferry, Aircraft(ContractAircraftCategory.LightSingle), 120, 1, 0, 0);
        var large = ContractPayCalculator.CalculateFee(
            Config, ContractKind.Ferry, Aircraft(ContractAircraftCategory.Widebody), 3_600, 1, 0, 0);

        Assert.True(large > small * 20, $"A widebody ocean crossing paid {large} against {small} for a light single hop - the board would feel flat.");
    }

    [Fact]
    public void CalculateFee_NeverFallsBelowTheMinimum()
    {
        var fee = ContractPayCalculator.CalculateFee(
            Config, ContractKind.Cargo, Aircraft(ContractAircraftCategory.LightSingle), totalDistanceNm: 0, legCount: 0, payloadKg: 0, paxCount: 0);

        Assert.Equal(Config.MinimumFee, fee);
    }

    // ---------- Splitting it across the legs ----------

    /// <summary>
    /// The headline property: shares are weighted by block time, not by leg count. A 240-minute
    /// ocean leg is worth four times a 60-minute hop, and the whole thing still sums to the fee.
    /// </summary>
    [Fact]
    public void AllocateFeeShares_WeightsByBlockTime_NotByLegCount()
    {
        var shares = ContractPayCalculator.AllocateFeeShares(10_000m, new[] { 60, 60, 240, 60 });

        // 60/420, 60/420, 240/420, 60/420 of 10,000. Each exact share is floored to the penny
        // (1,428.57 three times, 5,714.28 once, summing to 9,999.99) and the single remaining penny
        // goes to the largest fractional part, which is the 240-minute leg - so the ocean leg reads
        // 5,714.29 and the total is exactly 10,000.
        Assert.Equal(new[] { 1_428.57m, 1_428.57m, 5_714.29m, 1_428.57m }, shares);
        Assert.Equal(10_000m, shares.Sum());

        // And the point of the whole thing: the long leg really is worth four short ones.
        Assert.Equal(shares[0] * 4, shares[2] - 0.01m);
    }

    /// <summary>
    /// Equal legs really do get equal shares - the block-time weighting must not introduce a bias of
    /// its own when there is nothing to weight.
    /// </summary>
    [Fact]
    public void AllocateFeeShares_WithEqualLegs_SplitsEvenly()
    {
        var shares = ContractPayCalculator.AllocateFeeShares(1_000m, new[] { 90, 90, 90, 90 });

        Assert.Equal(new[] { 250m, 250m, 250m, 250m }, shares);
    }

    /// <summary>
    /// <b>The shares sum to exactly the fee, always.</b> Not "to within a penny" - exactly. A ledger
    /// that pays out a penny more than the contract said, across enough contracts, stops reconciling,
    /// and the cash balance in this app is SUM(ledger) with no mutable column to hide the drift in.
    /// </summary>
    [Theory]
    [InlineData("1000.00", new[] { 33, 33, 34 })]
    [InlineData("9999.99", new[] { 7, 11, 13, 17, 19, 23 })]
    [InlineData("12345.67", new[] { 1, 1, 1, 1, 1, 1, 1 })]
    [InlineData("750.00", new[] { 120 })]
    [InlineData("5000.01", new[] { 55, 240, 61, 300, 45, 90, 12, 400 })]
    public void AllocateFeeShares_AlwaysSumsToExactlyTheFee(string feeText, int[] blockMinutes)
    {
        var fee = decimal.Parse(feeText, System.Globalization.CultureInfo.InvariantCulture);

        var shares = ContractPayCalculator.AllocateFeeShares(fee, blockMinutes);

        Assert.Equal(fee, shares.Sum());
        Assert.All(shares, s => Assert.True(s >= 0m, "A leg share went negative."));
    }

    [Fact]
    public void AllocateFeeShares_WithNoLegs_IsEmptyRatherThanThrowing()
    {
        Assert.Empty(ContractPayCalculator.AllocateFeeShares(1_000m, Array.Empty<int>()));
    }

    // ---------- Walking away ----------

    /// <summary>
    /// <b>The user's own worked example, in their own words:</b> <i>"if someone does 3 legs when there
    /// are 2 legs remaining they would get charged for the remaining 2 legs."</i> So the charge is
    /// exactly the value of the legs left - not a fraction of it, which would be a second fraction
    /// they never asked for.
    /// </summary>
    [Fact]
    public void CalculateAbandonCharge_ChargesExactlyTheValueOfTheLegsLeftUnflown()
    {
        var shares = ContractPayCalculator.AllocateFeeShares(50_000m, new[] { 90, 75, 120, 240, 60 });
        var earned = shares.Take(3).Sum();

        var charge = ContractPayCalculator.CalculateAbandonCharge(
            new ContractConfig(),
            flownLegBlockMinutes: new[] { 90, 75, 120 },
            unflownLegFeeShares: shares.Skip(3).ToList(),
            unflownLegBlockMinutes: new[] { 240, 60 });

        Assert.Equal(2, charge.UnflownLegCount);
        Assert.Equal(300, charge.UnflownBlockMinutes);

        // Shares of 50,000 over 585 total minutes come out as
        // [7,692.31, 6,410.26, 10,256.41, 20,512.82, 5,128.20] - summing to exactly 50,000.
        // The two legs left are worth 25,641.02, and that is the charge, to the penny.
        Assert.Equal(shares.Skip(3).Sum(), charge.Charge);
        Assert.Equal(25_641.02m, charge.Charge);
        Assert.Equal(24_358.98m, earned);
    }

    /// <summary>
    /// Three of five <b>equal</b> legs: keep three fifths, pay two fifths, walk away one fifth ahead.
    /// This is the shape the user was describing, and it is the reassuring half of charging the full
    /// outstanding value - most of the way through a job, stopping still leaves them in front.
    /// </summary>
    [Fact]
    public void CalculateAbandonCharge_AfterThreeOfFiveEqualLegs_LeavesThePlayerAhead()
    {
        var blocks = new[] { 90, 90, 90, 90, 90 };
        var shares = ContractPayCalculator.AllocateFeeShares(50_000m, blocks);

        var charge = ContractPayCalculator.CalculateAbandonCharge(
            new ContractConfig(), blocks.Take(3).ToList(), shares.Skip(3).ToList(), blocks.Skip(3).ToList());

        Assert.Equal(30_000m, shares.Take(3).Sum());
        Assert.Equal(20_000m, charge.Charge);
        Assert.Equal(10_000m, shares.Take(3).Sum() - charge.Charge);
    }

    /// <summary>
    /// And the sobering half, which is the block-time weighting doing exactly its job. Stopping on the
    /// beach right <b>before</b> the ocean leg - the single biggest slice of the work - lands the
    /// player a shade under break-even rather than ahead.
    ///
    /// <para>That is the correct outcome and not a punishment for the sectors that were flown: they
    /// keep every penny those earned, and the shortfall is small (about 2.5% of the fee) precisely
    /// because the charge is weighted by the work outstanding rather than by a leg count. "You gained
    /// nothing for the evening" is a fair consequence for leaving somebody's aeroplane on the wrong
    /// side of an ocean.</para>
    /// </summary>
    [Fact]
    public void CalculateAbandonCharge_StoppingJustBeforeTheOceanLeg_IsAboutBreakEven()
    {
        var blocks = new[] { 90, 75, 120, 240, 60 };
        var shares = ContractPayCalculator.AllocateFeeShares(50_000m, blocks);

        var charge = ContractPayCalculator.CalculateAbandonCharge(
            new ContractConfig(), blocks.Take(3).ToList(), shares.Skip(3).ToList(), blocks.Skip(3).ToList());

        var net = shares.Take(3).Sum() - charge.Charge;

        Assert.Equal(-1_282.04m, net);
        Assert.True(Math.Abs(net) < 50_000m * 0.05m, $"Net was {net} on a 50,000 job - that is not near break-even.");
    }

    /// <summary>
    /// <b>Handing an untouched job back is free.</b> The charge exists because somebody else has to
    /// recover an aircraft left half-way; if no leg was flown, the aeroplane never moved and there is
    /// nothing to recover. Charging anyway would make accepting a contract a trap you cannot back out
    /// of, which is not what a predictable world with a generous deadline describes.
    /// </summary>
    [Fact]
    public void CalculateAbandonCharge_WithNoLegFlown_CostsNothingAndSaysWhy()
    {
        var shares = ContractPayCalculator.AllocateFeeShares(20_000m, new[] { 60, 90, 120 });

        var charge = ContractPayCalculator.CalculateAbandonCharge(
            new ContractConfig(),
            flownLegBlockMinutes: Array.Empty<int>(),
            unflownLegFeeShares: shares,
            unflownLegBlockMinutes: new[] { 60, 90, 120 });

        Assert.Equal(0m, charge.Charge);
        Assert.Contains("still where its operator left it", charge.Reason);
    }

    /// <summary>
    /// Finishing the job costs nothing, obviously - but "nothing outstanding" has to be a distinct,
    /// deliberate answer rather than a zero that falls out of the arithmetic by luck.
    /// </summary>
    [Fact]
    public void CalculateAbandonCharge_WithEveryLegFlown_IsZero()
    {
        var charge = ContractPayCalculator.CalculateAbandonCharge(
            new ContractConfig(),
            flownLegBlockMinutes: new[] { 60, 90 },
            unflownLegFeeShares: Array.Empty<decimal>(),
            unflownLegBlockMinutes: Array.Empty<int>());

        Assert.Equal(0m, charge.Charge);
        Assert.Equal(0, charge.UnflownLegCount);
    }

    /// <summary>
    /// The property the block-time weighting exists for, stated directly: <b>stopping just before the
    /// hard leg must not cost the same as stopping just after it.</b> Under equal per-leg shares these
    /// two would be identical, and the player would be paid the same for the easy legs as for the one
    /// that took nerve.
    /// </summary>
    [Fact]
    public void CalculateAbandonCharge_BeforeTheOceanLeg_CostsMoreThanAfterIt()
    {
        var blocks = new[] { 90, 75, 120, 240, 60 };
        var shares = ContractPayCalculator.AllocateFeeShares(50_000m, blocks);

        var beforeTheOcean = ContractPayCalculator.CalculateAbandonCharge(
            new ContractConfig(), blocks.Take(3).ToList(), shares.Skip(3).ToList(), blocks.Skip(3).ToList());

        var afterTheOcean = ContractPayCalculator.CalculateAbandonCharge(
            new ContractConfig(), blocks.Take(4).ToList(), shares.Skip(4).ToList(), blocks.Skip(4).ToList());

        Assert.True(
            beforeTheOcean.Charge > afterTheOcean.Charge * 3,
            $"Stopping before the ocean leg cost {beforeTheOcean.Charge} and after it {afterTheOcean.Charge} - " +
            "the weighting is not distinguishing the hard leg from the hops.");
    }

    /// <summary>
    /// The fraction is a single configured figure and genuinely drives the charge, so softening the
    /// rule later is one edit to economy-config.json rather than a code change. The shipped value is
    /// 1.0 - the user's own figure - and that is asserted here too, so lowering it becomes a
    /// deliberate act rather than something that can drift.
    /// </summary>
    [Fact]
    public void CalculateAbandonCharge_ScalesWithTheConfiguredFraction_AndShipsAtTheFullOutstandingValue()
    {
        var shares = ContractPayCalculator.AllocateFeeShares(10_000m, new[] { 60, 60 });

        var half = ContractPayCalculator.CalculateAbandonCharge(
            new ContractConfig { AbandonChargeFraction = 0.5m }, new[] { 60 }, shares.Skip(1).ToList(), new[] { 60 });
        var shipped = ContractPayCalculator.CalculateAbandonCharge(
            new ContractConfig(), new[] { 60 }, shares.Skip(1).ToList(), new[] { 60 });

        Assert.Equal(1.0m, new ContractConfig().AbandonChargeFraction);

        Assert.Equal(2_500m, half.Charge);
        Assert.Equal(5_000m, shipped.Charge);
    }

    // ---------- The completion bonus ----------

    /// <summary>
    /// <b>A single-leg job gets exactly nothing.</b> This is the property the whole bonus rests on: a
    /// lump every job received would fix the arithmetic and change no incentive at all, because it
    /// would move every job on the board by the same proportion.
    /// </summary>
    [Fact]
    public void CompletionBonus_IsZeroForASingleLegJob()
    {
        // A one-leg job earning far below the floor still gets nothing: the bonus is for finishing a
        // CHAIN, and a single sector is not one.
        Assert.Equal(0m, ContractPayCalculator.CalculateCompletionBonus(Config, fee: 100m, 240, 1));
        // And defensively, for the impossible cases rather than dividing by something odd.
        Assert.Equal(0m, ContractPayCalculator.CalculateCompletionBonus(Config, fee: 100m, 240, 0));
        Assert.Equal(0m, ContractPayCalculator.CalculateCompletionBonus(Config, fee: 100m, 0, 6));
    }

    /// <summary>
    /// <b>A job already paying above the floor gets nothing.</b> This is the property that keeps the
    /// bonus aimed at the fault it was built for. An earlier version paid every chain and tilted the
    /// whole board toward multi-leg work, curing categories that were never ill.
    /// </summary>
    [Fact]
    public void CompletionBonus_IsZeroForAJobAlreadyPayingAboveTheFloor()
    {
        // 10 block hours at a floor of 1,850 means anything at or above 18,500 is fine as it is.
        Assert.Equal(0m, ContractPayCalculator.CalculateCompletionBonus(Config, fee: 18_500m, 600, 5));
        Assert.Equal(0m, ContractPayCalculator.CalculateCompletionBonus(Config, fee: 40_000m, 600, 5));
        Assert.True(ContractPayCalculator.CalculateCompletionBonus(Config, fee: 18_499m, 600, 5) > 0m);
    }

    /// <summary>
    /// It tops a job up TOWARD the floor, and the shortfall is what it is made of - so the worse a
    /// chain was paying, the more it receives.
    /// </summary>
    [Fact]
    public void CompletionBonus_ClosesTheShortfallAgainstTheFloor()
    {
        // 20 block hours, floor 1,850 -> a job at the floor would pay 37,000.
        // A 10,000 fee is 27,000 short; a 30,000 fee is 7,000 short.
        var badlyPaid = ContractPayCalculator.CalculateCompletionBonus(Config, fee: 10_000m, 1_200, 5);
        var nearlyFine = ContractPayCalculator.CalculateCompletionBonus(Config, fee: 30_000m, 1_200, 5);

        Assert.True(badlyPaid > nearlyFine);
        // Exact, in the house style: shortfall * (1 - 1/5).
        Assert.Equal(21_600m, badlyPaid);
        Assert.Equal(5_600m, nearlyFine);
    }

    /// <summary>
    /// It grows with the chain: the same shortfall spread over more legs is worth more, because it is
    /// more commitment across more evenings.
    /// </summary>
    [Fact]
    public void CompletionBonus_GrowsWithTheLengthOfTheChain()
    {
        var two = ContractPayCalculator.CalculateCompletionBonus(Config, fee: 10_000m, 1_200, 2);
        var five = ContractPayCalculator.CalculateCompletionBonus(Config, fee: 10_000m, 1_200, 5);
        var eleven = ContractPayCalculator.CalculateCompletionBonus(Config, fee: 10_000m, 1_200, 11);

        Assert.True(two < five, $"2 legs paid {two}, 5 legs paid {five}.");
        Assert.True(five < eleven, $"5 legs paid {five}, 11 legs paid {eleven}.");

        // 20 block hours, floor 1,850, fee 10,000 -> shortfall 27,000.
        Assert.Equal(13_500m, two);        // x (1 - 1/2)
        Assert.Equal(21_600m, five);       // x (1 - 1/5)
        Assert.Equal(24_545.45m, eleven);  // x (1 - 1/11)
    }

    /// <summary>
    /// Turning the floor off restores the behaviour the bonus was added to fix, so the knob in
    /// economy-config.json is demonstrably the one that controls this.
    /// </summary>
    [Fact]
    public void CompletionBonusFloor_ControlsIt()
    {
        var off = ContractPayCalculator.CalculateCompletionBonus(
            new ContractConfig { CompletionBonusFloorPerBlockHour = 0m }, fee: 10_000m, 1_200, 6);
        var shipped = ContractPayCalculator.CalculateCompletionBonus(Config, fee: 10_000m, 1_200, 6);

        Assert.Equal(0m, off);
        Assert.True(shipped > 0m);
    }

    /// <summary>
    /// <b>The bonus is not part of the fee, and so cannot reach the per-leg shares.</b> That is what
    /// keeps it out of the abandon charge, which is computed from unflown shares - if it leaked in,
    /// walking away would cost more than the legs left were worth.
    /// </summary>
    [Fact]
    public void CompletionBonus_IsSeparateFromTheFeeAndFromEveryLegShare()
    {
        var fee = ContractPayCalculator.CalculateFee(
            Config, ContractKind.Ferry, Aircraft(ContractAircraftCategory.LightSingle),
            totalDistanceNm: 2_400, legCount: 10, payloadKg: 0, paxCount: 0);
        var bonus = ContractPayCalculator.CalculateCompletionBonus(Config, fee, 1_800, 10);
        var shares = ContractPayCalculator.AllocateFeeShares(fee, Enumerable.Repeat(180, 10).ToList());

        Assert.True(bonus > 0m, "A long light-single chain is exactly the case the bonus exists for.");
        // The shares sum to the fee exactly - there is no room in them for the bonus.
        Assert.Equal(fee, shares.Sum());
    }
}
