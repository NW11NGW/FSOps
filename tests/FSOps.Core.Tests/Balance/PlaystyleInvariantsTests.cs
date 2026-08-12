using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Planning;

namespace FSOps.Core.Tests.Balance;

/// <summary>
/// Both playstyles must independently satisfy every balance invariant. That is a genuine doubling
/// of balance testing, and it is deliberate: the properties have to be asserted for each preset
/// rather than assumed to carry over from one set of constants to a very different one. This is
/// that doubling.
///
/// <para>
/// Every test here is parameterised over <see cref="AirlinePlaystyle"/> via <c>Enum.GetValues</c>
/// (so a third preset can never be added without coverage) and resolves its config through
/// <see cref="EconomyConfigCatalog"/> rather than <see cref="EconomyConfig.Default"/>, exercising
/// exactly the same resolution path the server uses per-airline.
/// </para>
///
/// <para>
/// <b>Why these numbers turn out identical between playstyles</b> - and that is the point, not an
/// oversight: a playstyle is a named set of overrides to <c>AirlineStartup</c>/<c>FleetFinance</c>
/// only (starting capital, lease deposit term, starter lease rate, insurance - see
/// <see cref="EconomyConfigCatalog"/>'s class doc). Every per-sector figure these exploit/route
/// tests exercise - <see cref="EconomyConfig.ReferenceFare"/>, <see cref="EconomyConfig.Demand"/>,
/// <see cref="EconomyConfig.Fuel"/>, <see cref="EconomyConfig.Costs"/>,
/// <see cref="EconomyConfig.StrategyProfiles"/> - lives in the shared base and is untouched by
/// either override block, exactly as "no if(playstyle) branching in the engine" requires. So these
/// tests prove two things at once: the invariants hold under both playstyles' actual resolved
/// config (not merely assumed), and the shared-base figures are provably identical between them
/// (<see cref="SharedPerSectorConfig_IsIdenticalAcrossBothPlaystyles"/>), which is exactly why the
/// per-sector assertions below come out the same either way.
/// </para>
/// </summary>
public class PlaystyleInvariantsTests
{
    private static readonly EconomyConfigCatalog Catalog = EconomyConfigCatalog.Default();

    private const AirlineStrategyProfile Strategy = AirlineStrategyProfile.Domestic;
    private const int Seats = 180;
    private const double MtowTonnes = 78;
    private const int CruiseTasKts = 447;
    private const double FuelBurnKgPerHour = 2500;
    private const AirportSizeCategory AirportSize = AirportSizeCategory.Medium;
    private const double ReputationScore = 50;
    private static readonly DateTimeOffset FlightDate = new(2026, 4, 15, 12, 0, 0, TimeSpan.Zero);

    public static IEnumerable<object[]> AllPlaystyles() =>
        Enum.GetValues<AirlinePlaystyle>().Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(AllPlaystyles))]
    public void BothPlaystyles_ResolveToAValidConfig(AirlinePlaystyle playstyle)
    {
        // Validate() runs already inside EconomyConfigCatalog.FromJson/Default - not throwing here
        // is the assertion that both playstyle blocks in the real shipped shape are well-formed.
        var config = Catalog.Get(playstyle);
        config.Validate();
    }

    [Fact]
    public void CasualAndTrueLife_HaveDifferentFixedCosts_ButIdenticalStartingPilotSalary()
    {
        var casual = Catalog.Get(AirlinePlaystyle.Casual);
        var trueLife = Catalog.Get(AirlinePlaystyle.TrueLife);

        // The whole point of the split: fixed costs genuinely differ.
        Assert.NotEqual(casual.AirlineStartup.StartingCapital, trueLife.AirlineStartup.StartingCapital);
        Assert.NotEqual(casual.AirlineStartup.LeaseDepositMonths, trueLife.AirlineStartup.LeaseDepositMonths);
        // EVERY leasable type diverges now (2026-08-08 catalogue-wide rebalance) - not just the two
        // starter types. A320/B738 are hard-anchored at exactly 30,000 in Casual; every other type
        // (including every one added by the aircraft-catalogue expansion) is Casual's ~8% multiple
        // of True-life's real rate. The multiplier covers the whole catalogue precisely so that
        // adding aircraft types can never leave them at real-world rates in Casual - see
        // FleetEndpointsTests.Lease_ChargesThePlaystylesOwnRate_NotTheCatalogueColumn
        // for the regression test that pins the two starter types' divergence at the call site.
        foreach (var icaoType in new[] { "A319", "A320", "A321", "B737", "B738", "B739" })
        {
            Assert.True(trueLife.LeaseRateFor(icaoType) > casual.LeaseRateFor(icaoType),
                $"{icaoType}: True-life ({trueLife.LeaseRateFor(icaoType)}) should charge more than Casual ({casual.LeaseRateFor(icaoType)}).");
        }

        Assert.NotEqual(casual.FleetFinance.MonthlyInsurancePerAircraft, trueLife.FleetFinance.MonthlyInsurancePerAircraft);
        Assert.True(trueLife.AirlineStartup.StartingCapital > casual.AirlineStartup.StartingCapital);
        Assert.True(trueLife.FleetFinance.MonthlyInsurancePerAircraft > casual.FleetFinance.MonthlyInsurancePerAircraft);

        // Purchase price now diverges too. It never used to: every type sat at its full realistic
        // value in Casual as well, so buying an A320 at ~3,016 a sector took 15,750 sectors.
        // Casual now applies its own small,
        // catalogue-wide multiplier so a used example is affordable after roughly ten sectors'
        // profit, while True-life keeps the real transaction value throughout.
        Assert.Equal(1.0m, trueLife.PurchasePriceMultiplier);
        Assert.True(casual.PurchasePriceMultiplier < trueLife.PurchasePriceMultiplier);

        // The founding pilot's salary is already realistic and needs no game-balance treatment
        // unlike the starter lease rate beside it - shared, not overridden.
        Assert.Equal(casual.AirlineStartup.StartingPilotMonthlySalary, trueLife.AirlineStartup.StartingPilotMonthlySalary);
    }

    [Fact]
    public void SharedPerSectorConfig_IsIdenticalAcrossBothPlaystyles()
    {
        // Everything the exploit/route-viability tests below actually read - proving why those
        // properties hold under both playstyles without duplicating every formula per playstyle.
        var casual = Catalog.Get(AirlinePlaystyle.Casual);
        var trueLife = Catalog.Get(AirlinePlaystyle.TrueLife);

        Assert.Equal(casual.MaxLoadFactor, trueLife.MaxLoadFactor);
        Assert.Equal(casual.CaptiveFareCeilingMultiple, trueLife.CaptiveFareCeilingMultiple);
        Assert.Equal(casual.PostCaptiveElasticity, trueLife.PostCaptiveElasticity);
        Assert.Equal(casual.ReferenceFare.FarePerNm, trueLife.ReferenceFare.FarePerNm);
        Assert.Equal(casual.ReferenceFare.MinimumFare, trueLife.ReferenceFare.MinimumFare);
        Assert.Equal(casual.Demand.BaseDemandPerCatchmentPoint, trueLife.Demand.BaseDemandPerCatchmentPoint);
        Assert.Equal(casual.Costs.MaintenanceAccrualPerHour, trueLife.Costs.MaintenanceAccrualPerHour);
        Assert.Equal(casual.Costs.CrewCostPerHour, trueLife.Costs.CrewCostPerHour);

        foreach (var profile in Enum.GetValues<AirlineStrategyProfile>())
        {
            var casualStrategy = casual.GetStrategy(profile);
            var trueLifeStrategy = trueLife.GetStrategy(profile);
            Assert.Equal(casualStrategy.ReferenceFareMultiplier, trueLifeStrategy.ReferenceFareMultiplier);
            Assert.Equal(casualStrategy.Elasticity, trueLifeStrategy.Elasticity);
            Assert.Equal(casualStrategy.BaselineLoadFactor, trueLifeStrategy.BaselineLoadFactor);
            Assert.Equal(casualStrategy.CostMultiplier, trueLifeStrategy.CostMultiplier);
        }
    }

    /// <summary>
    /// The micro-sector exploit must stay loss-making at the revenue-maximising fare, in BOTH
    /// playstyles - fixed per-sector costs are what make a trivially short hop structurally
    /// unprofitable, and those costs differ between the two presets.
    /// See FlightEconomicsIntegrityTests.MicroSectorLoop_EvenAtTheBestPossibleFare_IsNetNegative for
    /// the single-playstyle version this doubles.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllPlaystyles))]
    public void MicroSectorExploit_20Nm_StaysLossMaking_AtTheRevenueMaximisingFare(AirlinePlaystyle playstyle)
    {
        var config = Catalog.Get(playstyle);
        const double distanceNm = 20;
        const double flightHours = 0.2;
        const double upliftKg = 500;

        var strategy = config.GetStrategy(Strategy);
        var referenceFare = ReferenceFareCalculator.Calculate(config.ReferenceFare, strategy, distanceNm);
        var marketDemandPax = DemandCalculator.AvailablePassengers(
            config.Demand, AirportSizeCategory.Large, AirportSizeCategory.Large, distanceNm, FlightDate, ReputationScore);
        var bestFare = FareDemandModel.RevenueMaximizingFare(
            config.MaxLoadFactor, strategy, referenceFare, Seats, marketDemandPax, config.CaptiveFareCeilingMultiple);
        var fuelPrice = FuelPricing.PricePerKg(config.Fuel, "EGLL", "United Kingdom", FlightDate, worldSeed: 1);

        var result = FlightEconomicsCalculator.Calculate(
            config, Strategy, bestFare, referenceFare, Seats, marketDemandPax,
            upliftKg, fuelPrice, AirportSizeCategory.Large, MtowTonnes, flightHours);

        Assert.True(result.NetProfit < 0,
            $"{playstyle}: a 20nm micro-sector at its best fare should lose money, but netted {result.NetProfit:F2}.");
    }

    /// <summary>Absurd fares must stay self-defeating in both playstyles - an empty aeroplane
    /// teaches the lesson, where an arbitrary cap would teach nothing. See
    /// FareDemandModelExploitTests.Revenue_At3xAnd10xReference_IsStrictlyLowerThanAtTheOptimum for
    /// the single-playstyle version this doubles.</summary>
    [Theory]
    [MemberData(nameof(AllPlaystyles))]
    public void AbsurdFares_StaySelfDefeating(AirlinePlaystyle playstyle)
    {
        var config = Catalog.Get(playstyle);
        var strategy = config.GetStrategy(Strategy);
        const decimal referenceFare = 100m;
        const int seats = 200;

        FareBookingResult BookAt(decimal fare) => FareDemandModel.Calculate(
            config.MaxLoadFactor, strategy, fare, referenceFare, seats,
            config.CaptiveFareCeilingMultiple, config.PostCaptiveElasticity);

        var optimalFare = FareDemandModel.RevenueMaximizingFare(
            config.MaxLoadFactor, strategy, referenceFare, config.CaptiveFareCeilingMultiple);

        var revenueAtOptimal = BookAt(optimalFare).Revenue;
        var revenueAt3x = BookAt(referenceFare * 3).Revenue;
        var revenueAt10x = BookAt(referenceFare * 10).Revenue;

        Assert.True(revenueAtOptimal > revenueAt3x,
            $"{playstyle}: revenue at optimum ({revenueAtOptimal:F2}) must beat 3x reference ({revenueAt3x:F2}).");
        Assert.True(revenueAt3x > revenueAt10x,
            $"{playstyle}: revenue at 3x reference ({revenueAt3x:F2}) must beat 10x reference ({revenueAt10x:F2}).");
    }

    /// <summary>
    /// The three real routes (275.2 / 276.4 / 114.0 nm) must stay viable in both playstyles - the actual
    /// distances of a real airline's routes out of EGGD (see StartupTrajectoryTests' EGGD-&gt;EGPH
    /// scenario for 275.2nm specifically). Medium-Medium airports, Domestic strategy, at the suggested
    /// (reference) fare - the fare a real player actually sees by default.
    /// </summary>
    [Theory]
    [InlineData(275.2, AirlinePlaystyle.Casual)]
    [InlineData(275.2, AirlinePlaystyle.TrueLife)]
    [InlineData(276.4, AirlinePlaystyle.Casual)]
    [InlineData(276.4, AirlinePlaystyle.TrueLife)]
    [InlineData(114.0, AirlinePlaystyle.Casual)]
    [InlineData(114.0, AirlinePlaystyle.TrueLife)]
    public void RealRoutes_275_276And114Nm_AreProfitableAtTheSuggestedFare(double distanceNm, AirlinePlaystyle playstyle)
    {
        var config = Catalog.Get(playstyle);
        var blockTime = BlockTimeEstimator.Estimate(distanceNm, CruiseTasKts);
        var fuel = BlockFuelEstimator.Estimate(blockTime, FuelBurnKgPerHour);
        var flightHours = blockTime.TotalMinutes / 60.0;

        var strategy = config.GetStrategy(Strategy);
        var referenceFare = ReferenceFareCalculator.Calculate(config.ReferenceFare, strategy, distanceNm);
        var marketDemandPax = DemandCalculator.AvailablePassengers(
            config.Demand, AirportSize, AirportSize, distanceNm, FlightDate, ReputationScore);
        var fuelPrice = FuelPricing.PricePerKg(config.Fuel, "EGCC", "United Kingdom", FlightDate, worldSeed: 1);

        var result = FlightEconomicsCalculator.Calculate(
            config, Strategy, fare: referenceFare, referenceFare: referenceFare, Seats, marketDemandPax,
            fuel.ChargedFuelKg, fuelPrice, AirportSize, MtowTonnes, flightHours);

        Assert.True(result.NetProfit > 0,
            $"{playstyle} at {distanceNm}nm: the suggested fare should be profitable, but netted {result.NetProfit:F2} " +
            $"(revenue {result.TicketRevenue:F2}, cost {result.TotalCost:F2}).");
    }
}
