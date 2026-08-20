using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.SimAircraft;

namespace FSOps.Core.Contracts;

/// <summary>What abandoning an accepted contract costs, and why.</summary>
/// <param name="Charge">
/// The amount to raise as a single negative ledger line, always zero or positive here (the sign is
/// applied when it is posted). Zero is a real answer, not a missing one.
/// </param>
/// <param name="UnflownBlockMinutes">The block time left on the table, for the explanation shown to the player.</param>
/// <param name="UnflownLegCount">How many legs were left.</param>
/// <param name="Reason">The sentence to show. Always populated, including when the charge is zero.</param>
public sealed record ContractAbandonCharge(
    decimal Charge,
    int UnflownBlockMinutes,
    int UnflownLegCount,
    string Reason);

/// <summary>
/// The money in contract flying, in one place: what the whole job pays, how that splits across the
/// legs, and what walking away costs.
///
/// <para>Pure and deterministic - no clock, no randomness, no I/O - so every figure here is testable
/// with exact expected values, the same discipline the rest of the economy engine keeps.</para>
/// </summary>
public static class ContractPayCalculator
{
    /// <summary>
    /// What the whole job pays for flying every leg. <b>Fee scales with the work</b>: distance, leg
    /// count, how big the aircraft is, and what it is carrying. That is the property that makes a
    /// board with a genuine spread of sizes worth browsing - a forty-minute domestic hop and a
    /// multi-leg ocean crossing must not pay remotely the same.
    ///
    /// <para>Leg count is paid for separately from distance on purpose. Five 300 nm sectors are more
    /// work than one 1,500 nm sector - five departures, five approaches, five days - and a fee driven
    /// by distance alone would say they are identical. That is also what stops a light single's
    /// crossing being the worst-paid job on the board despite being the hardest.</para>
    /// </summary>
    public static decimal CalculateFee(
        ContractConfig config,
        ContractKind kind,
        ContractAircraft aircraft,
        double totalDistanceNm,
        int legCount,
        int payloadKg,
        int paxCount)
    {
        var distanceComponent = config.FeePerNm * (decimal)Math.Max(0, totalDistanceNm);
        var legComponent = config.FeePerLeg * Math.Max(0, legCount);
        var loadComponent = config.FeePerPayloadKg * Math.Max(0, payloadKg)
            + config.FeePerPassenger * Math.Max(0, paxCount);

        var subtotal = config.BaseFee + distanceComponent + legComponent + loadComponent;
        var scaled = subtotal * config.CategoryMultiplier(aircraft.Category) * config.KindMultiplier(kind);

        return Math.Round(Math.Max(config.MinimumFee, scaled), 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The lump paid for finishing a whole chain, on top of the per-leg shares.
    ///
    /// <para><b>Why it exists.</b> Per-leg pay alone made the longest jobs the worst-paid per hour.
    /// Measured across 500 boards, a light single earned a median 1,704 per block hour on a one-leg
    /// job and 444 on a nine-plus-leg chain - so the board was quietly telling the player not to fly
    /// the multi-leg crossing that is the whole reason contract flying exists. That is structural:
    /// <see cref="ContractConfig.BaseFee"/> is paid once, so it is worth 1,500 an hour on a
    /// forty-minute hop and 30 an hour spread across thirty hours.</para>
    ///
    /// <para><b>The shape: a floor on pay per block hour, reached only by jobs that fall under it.</b>
    /// The bonus tops a chain up to <see cref="ContractConfig.CompletionBonusFloorPerBlockHour"/> and
    /// is <b>zero for any job already earning more than that</b> - so it repairs the jobs that were
    /// broken and leaves alone the ones that never were.</para>
    ///
    /// <para><b>That precision matters, and an earlier version of this got it wrong.</b> The first
    /// attempt paid every job a lump scaled by block hours and by aircraft category. It fixed the
    /// light singles and simultaneously tilted the whole board about 1.8x toward chains, because it
    /// cured categories that were never ill and gave the largest doses to the biggest aeroplanes.
    /// Measurement is what showed the fault was narrow: two-to-three-leg turboprop and jet work was
    /// already earning 3,919 per hour against 3,428 for single-leg jobs - healthy - while light
    /// singles ran 1,704 down to 457. A light single is the only thing that flies a nine-leg chain,
    /// and therefore the only thing that spreads a fixed base fee across thirty hours.</para>
    ///
    /// <para>The floor is deliberately <b>not</b> scaled by aircraft category. Scaling it would set a
    /// widebody's floor above what widebody jobs already pay and start topping those up too, which is
    /// the same mistake in a smaller coat.</para>
    ///
    /// <para><c>1 - 1/legCount</c> keeps it <b>exactly zero for a single-leg job</b> and lets it grow
    /// with the chain. A bonus every job received would fix the arithmetic and change no incentive at
    /// all.</para>
    ///
    /// <para>Deliberately NOT distributed across the legs: it is paid only on the last one, and
    /// abandoning forfeits it. See <see cref="Entities.Contract.CompletionBonus"/>.</para>
    /// </summary>
    /// <param name="fee">The job's own fee - what the legs sum to. The bonus is the shortfall against
    /// the floor, so it cannot be computed without it.</param>
    public static decimal CalculateCompletionBonus(
        ContractConfig config, decimal fee, int totalPlannedBlockMinutes, int legCount)
    {
        if (legCount <= 1 || totalPlannedBlockMinutes <= 0)
        {
            return 0m;
        }

        var blockHours = (decimal)totalPlannedBlockMinutes / 60m;
        var shortfall = (config.CompletionBonusFloorPerBlockHour * blockHours) - fee;
        if (shortfall <= 0m)
        {
            // Already paying at or above the floor. Nothing was wrong with this job, so nothing is
            // added to it.
            return 0m;
        }

        var chainFactor = 1m - (1m / legCount);
        return Math.Round(Math.Max(0m, shortfall * chainFactor), 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Splits the fee across the legs, <b>weighted by planned block time rather than by leg count</b>.
    ///
    /// <para><b>Why block time.</b> A five-leg Atlantic crossing where leg four is the ocean and the
    /// rest are hops is not five equal fifths. Under equal shares, abandoning immediately before the
    /// hard leg would cost exactly what abandoning after it does, and the player would be paid the
    /// same for the easy legs as for the one that took nerve. Block time is the honest weight; leg
    /// count is merely the simple one.</para>
    ///
    /// <para><b>The weighting lives here, and only here</b>, so preferring plain leg count later is a
    /// one-line change to <see cref="Weight"/> rather than a hunt through the feature.</para>
    ///
    /// <para>Allocated by largest remainder, so the shares sum to <b>exactly</b> the fee - no rounding
    /// dust, in either direction. Paying out a penny more than the contract said, across enough
    /// contracts, is how a ledger stops reconciling.</para>
    /// </summary>
    public static IReadOnlyList<decimal> AllocateFeeShares(decimal fee, IReadOnlyList<int> plannedBlockMinutesPerLeg)
    {
        var count = plannedBlockMinutesPerLeg.Count;
        if (count == 0)
        {
            return Array.Empty<decimal>();
        }

        var weights = plannedBlockMinutesPerLeg.Select(Weight).ToList();
        var totalWeight = weights.Sum();

        // Every leg weightless (a chain of zero-minute legs should be impossible, but a division by
        // zero here would be a crash rather than a wrong number) - fall back to equal shares.
        if (totalWeight <= 0)
        {
            weights = Enumerable.Repeat(1m, count).ToList();
            totalWeight = count;
        }

        var shares = new decimal[count];
        var exact = new decimal[count];
        var allocated = 0m;

        for (var i = 0; i < count; i++)
        {
            exact[i] = fee * weights[i] / totalWeight;
            shares[i] = Math.Floor(exact[i] * 100m) / 100m;
            allocated += shares[i];
        }

        // Hand the remaining pennies out one at a time, largest fractional part first, ties broken by
        // leg order so the allocation is deterministic rather than dependent on sort stability.
        var remainderPennies = (int)Math.Round((fee - allocated) * 100m, MidpointRounding.AwayFromZero);
        var order = Enumerable.Range(0, count)
            .OrderByDescending(i => exact[i] - shares[i])
            .ThenBy(i => i)
            .ToList();

        // Flooring loses strictly less than a penny per leg, so the remainder is always smaller than
        // the leg count and every leg gets at most one. The modulo is belt and braces, not a case.
        for (var i = 0; i < remainderPennies; i++)
        {
            shares[order[i % order.Count]] += 0.01m;
        }

        return shares;
    }

    /// <summary>
    /// The weight one leg carries in the fee split and in the abandon charge. <b>Change this one
    /// method to switch from block time to plain leg count</b> - return 1 and everything downstream
    /// follows, including the tests, which assert against this rather than restating the formula.
    /// </summary>
    private static decimal Weight(int plannedBlockMinutes) => Math.Max(0, plannedBlockMinutes);

    /// <summary>
    /// What it costs to walk away from an accepted contract, and the sentence explaining it.
    ///
    /// <para><b>The justification is real rather than punitive</b>, and it decides the shape of the
    /// rule: an abandoned ferry leaves somebody else's aircraft stranded half-way, and moving it is a
    /// cost the player caused. Everything below follows from that one sentence.</para>
    ///
    /// <para><b>So nothing is charged if no leg was ever flown.</b> If the player accepted a job and
    /// then flew none of it, the aeroplane is exactly where its owner left it and there is nothing to
    /// recover - the stated justification simply does not apply. Charging anyway would make accepting
    /// a contract a trap you cannot back out of, which is not what "a generous deadline, and the world
    /// stays predictable" describes. Handing a job back untouched should be free, and it is.</para>
    ///
    /// <para><b>Otherwise the charge is the value of the legs left</b>, which is exactly what the
    /// user asked for: fly three of five and you are charged for the remaining two. Scaled by
    /// <see cref="ContractConfig.AbandonChargeFraction"/>, which ships at 1.0 - see that property for
    /// why it is not a smaller number, and why the outcome is nearer break-even than it sounds.</para>
    /// </summary>
    public static ContractAbandonCharge CalculateAbandonCharge(
        ContractConfig config,
        IReadOnlyList<int> flownLegBlockMinutes,
        IReadOnlyList<decimal> unflownLegFeeShares,
        IReadOnlyList<int> unflownLegBlockMinutes)
    {
        var unflownLegCount = unflownLegFeeShares.Count;
        var unflownBlockMinutes = unflownLegBlockMinutes.Sum();

        if (unflownLegCount == 0)
        {
            return new ContractAbandonCharge(0m, 0, 0, "Every leg was flown, so there is nothing outstanding to charge for.");
        }

        if (flownLegBlockMinutes.Count == 0)
        {
            return new ContractAbandonCharge(
                0m,
                unflownBlockMinutes,
                unflownLegCount,
                "No leg was flown, so the aircraft is still where its operator left it - handing the job back costs nothing.");
        }

        var outstanding = unflownLegFeeShares.Sum();
        var charge = Math.Round(
            Math.Max(0m, outstanding) * config.AbandonChargeFraction, 2, MidpointRounding.AwayFromZero);

        var legWord = unflownLegCount == 1 ? "leg" : "legs";
        return new ContractAbandonCharge(
            charge,
            unflownBlockMinutes,
            unflownLegCount,
            $"{unflownLegCount} {legWord} left unflown - the operator has to recover the aircraft from where you stopped.");
    }
}
