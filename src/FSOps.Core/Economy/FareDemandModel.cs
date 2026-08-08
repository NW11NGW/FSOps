namespace FSOps.Core.Economy;

/// <summary>Passengers booked, resulting load factor and revenue for one fare on one flight.</summary>
public readonly record struct FareBookingResult(double LoadFactor, int PaxBooked, decimal Revenue);

/// <summary>
/// The anti-exploit heart of the economy. Answers "if this route is priced at this fare, how
/// many people book, and how much revenue does that produce" - in a way where an absurd fare is
/// self-defeating through the simulation, not through an arbitrary price cap.
///
/// <para>The model has two independent ceilings on how many seats sell:</para>
/// <list type="bullet">
/// <item>
/// <b>What the fare can sell:</b> <c>maxLoadFactor x (fare / referenceFare) ^ (-elasticity)</c>,
/// clamped to <c>[0, maxLoadFactor]</c>. This is the aircraft's theoretical fill rate if the
/// market were bottomless - it falls as the fare rises, and rises (up to the hard ceiling) as
/// the fare falls.
/// </item>
/// <item>
/// <b>What the market actually holds:</b> a passenger pool for the day (from
/// <see cref="DemandCalculator"/>, or - when the caller has no route-specific figure yet - a
/// stand-in sized from the strategy's <c>BaselineLoadFactor</c>). Below the captive-fare ceiling
/// this does not move with fare; beyond it, it does (see below).
/// </item>
/// </list>
///
/// <para><b>Why the peak lands above the reference fare, not at or below it.</b> At
/// <c>fare = referenceFare</c> the fare-side formula evaluates to exactly <c>maxLoadFactor</c>
/// (by construction: the ratio is 1). Anchoring the curve there and nowhere else means the
/// market pool is what actually pins the reference fare's realised load factor down to the
/// strategy's baseline - the market, not the price, is the bottleneck at that point. Raising the
/// fare from there doesn't cost a single passenger until the fare-side formula's falling curve
/// drops below the market pool's size; until that crossover, revenue rises with fare because
/// volume is untouched. Only past the crossover does the elastic formula start shedding
/// passengers faster than the fare rises, and revenue turns down.
/// </para>
///
/// <para><b>Why that crossover alone is not enough (the captive-market ceiling).</b> For a route
/// with a very thin passenger pool relative to seats, the crossover fare implied purely by the
/// formula above can be enormous - mathematically it grows without limit as the pool shrinks
/// toward zero, because a handful of "captive" passengers would in this model keep booking at
/// any price right up until the seat-formula finally caught up with them. That is not
/// realistic - even a captive audience balks eventually - and left unchecked it lets a cheater
/// earn large absolute revenue from a route nobody would realistically pay that much to fly.
/// <see cref="EconomyConfig.CaptiveFareCeilingMultiple"/> bounds this directly: beyond
/// <c>referenceFare x CaptiveFareCeilingMultiple</c>, the market pool itself starts eroding at
/// <see cref="EconomyConfig.PostCaptiveElasticity"/>, so the crossover - and therefore the
/// revenue-maximising fare - can never exceed that ceiling, regardless of how thin the market is.
/// For a normally-sized market the ceiling never binds: every strategy profile's ordinary
/// crossover already lands within it.</para>
/// </summary>
public static class FareDemandModel
{
    /// <summary>Full form - <paramref name="marketDemandPax"/> comes from
    /// <see cref="DemandCalculator"/> for the real route, date and reputation in play.</summary>
    public static FareBookingResult Calculate(
        double maxLoadFactor,
        StrategyProfileConfig strategy,
        decimal fare,
        decimal referenceFare,
        int seats,
        int marketDemandPax,
        double captiveFareCeilingMultiple,
        double postCaptiveElasticity)
    {
        if (fare < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fare), "Fare cannot be negative.");
        }

        if (referenceFare <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(referenceFare), "Reference fare must be positive.");
        }

        if (seats <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seats), "Seats must be positive.");
        }

        var clampedLoadFactor = ClampedLoadFactorFromFare(maxLoadFactor, strategy.Elasticity, fare, referenceFare);
        var paxFromFormula = (int)Math.Round(seats * clampedLoadFactor, MidpointRounding.AwayFromZero);

        var effectiveMarketCap = EffectiveMarketCap(
            Math.Max(0, marketDemandPax), fare, referenceFare, captiveFareCeilingMultiple, postCaptiveElasticity);

        var paxBooked = Math.Max(0, Math.Min(paxFromFormula, effectiveMarketCap));

        var revenue = paxBooked * fare;
        var loadFactor = (double)paxBooked / seats;

        return new FareBookingResult(loadFactor, paxBooked, revenue);
    }

    /// <summary>Convenience form for when no route-specific demand figure is available yet: the
    /// market pool is stood in for by the strategy's own <c>BaselineLoadFactor</c>, so charging
    /// exactly the reference fare reproduces that baseline exactly (see the type doc). Used by
    /// the exploit tests, and a reasonable fallback anywhere a caller has a fare and a strategy
    /// but hasn't computed real city-pair demand.</summary>
    public static FareBookingResult Calculate(
        double maxLoadFactor,
        StrategyProfileConfig strategy,
        decimal fare,
        decimal referenceFare,
        int seats,
        double captiveFareCeilingMultiple,
        double postCaptiveElasticity)
    {
        var marketDemandPax = (int)Math.Round(seats * strategy.BaselineLoadFactor, MidpointRounding.AwayFromZero);
        return Calculate(maxLoadFactor, strategy, fare, referenceFare, seats, marketDemandPax, captiveFareCeilingMultiple, postCaptiveElasticity);
    }

    /// <summary>
    /// The single fare that maximises revenue for this strategy, route and market. Below the
    /// captive-fare ceiling, revenue rises with fare while the market cap alone binds (see the
    /// type doc); past it, the market itself erodes and revenue falls. The maximum therefore sits
    /// at whichever comes first: the point the seat-side formula's own falling curve drops to the
    /// market's size, or the captive-fare ceiling - so this can never return a fare more than
    /// <c>referenceFare x CaptiveFareCeilingMultiple</c>.
    /// </summary>
    public static decimal RevenueMaximizingFare(
        double maxLoadFactor,
        StrategyProfileConfig strategy,
        decimal referenceFare,
        int seats,
        int marketDemandPax,
        double captiveFareCeilingMultiple)
    {
        if (marketDemandPax <= 0)
        {
            return 0m;
        }

        var marketLoadFactor = (double)marketDemandPax / seats;

        if (marketLoadFactor >= maxLoadFactor)
        {
            // The market never binds - the seat-side ceiling clamp is the active constraint, and
            // that crossover sits at or below the reference fare (see the type doc), so the
            // reference fare itself is a safe, simple stand-in.
            return referenceFare;
        }

        var uncappedMultiple = Math.Pow(maxLoadFactor / marketLoadFactor, 1.0 / strategy.Elasticity);
        var multiple = Math.Min(uncappedMultiple, captiveFareCeilingMultiple);
        return referenceFare * (decimal)multiple;
    }

    /// <summary>Convenience overload matching the strategy's own BaselineLoadFactor-derived
    /// market pool - see the two-argument <see cref="Calculate(double,StrategyProfileConfig,decimal,decimal,int,double,double)"/>.</summary>
    public static decimal RevenueMaximizingFare(
        double maxLoadFactor,
        StrategyProfileConfig strategy,
        decimal referenceFare,
        double captiveFareCeilingMultiple)
    {
        var uncappedMultiple = Math.Pow(maxLoadFactor / strategy.BaselineLoadFactor, 1.0 / strategy.Elasticity);
        var multiple = Math.Min(uncappedMultiple, captiveFareCeilingMultiple);
        return referenceFare * (decimal)multiple;
    }

    private static double ClampedLoadFactorFromFare(double maxLoadFactor, double elasticity, decimal fare, decimal referenceFare)
    {
        if (fare == 0)
        {
            return maxLoadFactor;
        }

        var ratio = (double)(fare / referenceFare);
        var raw = maxLoadFactor * Math.Pow(ratio, -elasticity);
        return Math.Clamp(raw, 0.0, maxLoadFactor);
    }

    /// <summary>The market pool available at this fare: unchanged up to the captive-fare ceiling,
    /// then eroding under <paramref name="postCaptiveElasticity"/> beyond it - see the type doc's
    /// "captive-market ceiling" section for why this exists.</summary>
    private static int EffectiveMarketCap(
        int marketDemandPax, decimal fare, decimal referenceFare, double captiveFareCeilingMultiple, double postCaptiveElasticity)
    {
        var ceilingFare = referenceFare * (decimal)captiveFareCeilingMultiple;

        if (fare <= ceilingFare || marketDemandPax == 0)
        {
            return marketDemandPax;
        }

        var ratio = (double)(fare / ceilingFare);
        var decayed = marketDemandPax * Math.Pow(ratio, -postCaptiveElasticity);
        return Math.Max(0, (int)Math.Round(decayed, MidpointRounding.AwayFromZero));
    }
}
