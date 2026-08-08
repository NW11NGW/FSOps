using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Finance;
using FSOps.Core.Planning;

namespace FSOps.Core.Tests.Balance;

/// <summary>
/// Validates docs/PLAN.md's "The progression loop" section end to end: one aircraft flown
/// casually (roughly one leg a day) must be genuinely profitable, not merely break-even, and a
/// second aircraft's lease deposit must be affordable within about 7-10 flights at that pace.
/// Every number here is computed from the real economy engine (ReferenceFareCalculator,
/// DemandCalculator, FareDemandModel, FuelPricing, FlightEconomicsCalculator) and the real
/// economy-config.json defaults - nothing is a hand-picked hardcoded result. Only the lease rate
/// and purchase price are local constants, because <c>AircraftType</c> rows are seeded by
/// FSOps.Data (not referenced from this project); both are commented with the exact
/// AircraftTypeSeeder A320 entry they must stay in sync with.
///
/// <para><b>History, briefly (see docs/PLAN.md for the full account):</b> Chunk D's original
/// break-even target was "4-5 sectors/day/aircraft". The fuel-honesty fix (charging
/// <see cref="FuelBreakdown.ChargedFuelKg"/> - trip + taxi + contingency only - instead of the
/// full block-fuel figure, which had been silently over-billing every sector for reserve/alternate
/// fuel that's never burned) moved that to ~5.60 sectors/day, which is unrealistic for a casual
/// player: it assumed the PLAYER's own flying, not the airline's total utilisation including
/// virtual pilots, would reach that pace. The user then redecided the whole shape of the
/// balance (2026-08-08, "The progression loop"): one aircraft flown casually must be genuinely
/// profitable on its own, and the starter aircraft's lease/insurance are now deliberate
/// game-balance figures (not realistic ones) tuned to make that true - see
/// AircraftTypeSeeder.cs and EconomyConfig.cs's <see cref="FleetFinanceConfig"/>/
/// <see cref="AirlineStartupConfig"/> doc comments for the numbers and the explicit "do not
/// correct this toward realism" warning. The old sectors-per-day-band tests this file used to
/// contain are gone - that framing no longer describes the design.</para>
///
/// <para><b>The scenario:</b> a 275.2 nm short-haul sector - the real distance between EGGD and
/// EGPH, one of the routes this rebalance was checked against (see docs/PLAN.md and
/// FlightFuelChargeBalanceTests) - Medium-to-Medium airports, flown on the Domestic strategy
/// profile at exactly the suggested (reference) fare, the fare a new player actually sees. 180
/// seats, 78t MTOW, 447 kt cruise and a 2,500 kg/h burn are the seeded A320's own figures. Fuel
/// and block time go through the same BlockTimeEstimator/BlockFuelEstimator the player's flight
/// brief and fuel charge actually use, so this can never quietly disagree with what the player
/// gets charged.</para>
/// </summary>
public class StartupTrajectoryTests
{
    private static readonly EconomyConfig Config = EconomyConfig.Default();

    private const AirlineStrategyProfile Strategy = AirlineStrategyProfile.Domestic;
    private const double DistanceNm = 275.2; // EGGD -> EGPH, the plan's own reference casual-player route.
    private const int Seats = 180;
    private const double MtowTonnes = 78;
    private const int CruiseTasKts = 447;
    private const double FuelBurnKgPerHour = 2500;
    private const AirportSizeCategory AirportSize = AirportSizeCategory.Medium;
    private const double ReputationScore = 50; // Config.Demand.ReputationBaselineScore - a brand new airline, no reputation bonus or penalty yet.
    private const string ArrivalIcao = "EGCC";
    private const string RegionKey = "United Kingdom";
    private const int WorldSeed = 1;

    /// <summary>A Wednesday in April - an unremarkable mid-week, mid-season day, not a cherry-picked peak.</summary>
    private static readonly DateTimeOffset FlightDate = new(2026, 4, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Matches AircraftTypeSeeder's A320 entry (src/FSOps.Data/Import/AircraftTypeSeeder.cs) -
    /// MonthlyLeaseRate = 30,000, a deliberate game-balance figure (roughly 8% of a real A320
    /// lease) since the 2026-08-08 progression-loop rebalance - see that file's class doc for why.</summary>
    private const decimal A320MonthlyLeaseRate = 30_000m;

    /// <summary>Matches AircraftTypeSeeder's A320 entry - untouched by the progression-loop
    /// rebalance on purpose. Buying outright stays a genuine later milestone funded by retained
    /// profit or a loan, not something the cheapened lease rate should also cheapen.</summary>
    private const decimal A320PurchasePrice = 47_500_000m;

    /// <summary>One-way sector net profit at the suggested (reference) fare - the figure every
    /// other test in this file builds on. Fuel is <see cref="FuelBreakdown.ChargedFuelKg"/> (what
    /// <c>FlightEconomicsPoster.PostFuelUplift</c> actually posts to the ledger), and flight hours
    /// come from the same <c>BlockTimeEstimator</c> call the flight brief uses - both the same
    /// figures the player's flight actually experiences, not a hand-picked stand-in.</summary>
    private static decimal TypicalSectorNetProfit()
    {
        var blockTime = BlockTimeEstimator.Estimate(DistanceNm, CruiseTasKts);
        var fuel = BlockFuelEstimator.Estimate(blockTime, FuelBurnKgPerHour);
        var flightHours = blockTime.TotalMinutes / 60.0;

        var strategy = Config.GetStrategy(Strategy);
        var referenceFare = ReferenceFareCalculator.Calculate(Config.ReferenceFare, strategy, DistanceNm);
        var marketDemandPax = DemandCalculator.AvailablePassengers(
            Config.Demand, AirportSize, AirportSize, DistanceNm, FlightDate, ReputationScore);
        var fuelPrice = FuelPricing.PricePerKg(Config.Fuel, ArrivalIcao, RegionKey, FlightDate, WorldSeed);

        var result = FlightEconomicsCalculator.Calculate(
            Config, Strategy, fare: referenceFare, referenceFare: referenceFare, Seats, marketDemandPax,
            fuel.ChargedFuelKg, fuelPrice, AirportSize, MtowTonnes, flightHours);

        return result.NetProfit;
    }

    /// <summary>Lease + starting pilot's salary + monthly insurance - the fixed costs one aircraft
    /// carries every month regardless of how much it flies.</summary>
    private static decimal MonthlyFixedCostPerAircraft() =>
        A320MonthlyLeaseRate + Config.AirlineStartup.StartingPilotMonthlySalary + Config.FleetFinance.MonthlyInsurancePerAircraft;

    private static decimal MonthlyNetAt(double sectorsPerDay, decimal netProfitPerSector, decimal monthlyFixedCost) =>
        netProfitPerSector * (decimal)(sectorsPerDay * 30) - monthlyFixedCost;

    [Fact]
    public void TypicalShortHaulSector_275Nm_AtTheSuggestedFare_NetsAFewThousand()
    {
        var netProfit = TypicalSectorNetProfit();

        // Real figure is ~£3,016 - comfortably the same "few thousand" order of magnitude the
        // plan has always described for a single sector; this test only pins the ballpark.
        Assert.InRange(netProfit, 1_500m, 6_000m);
    }

    /// <summary>
    /// The single most important property in the rebalance: a player flying roughly one leg a day
    /// must run a genuinely profitable airline, not merely survive. Real figure is ~£45,485/month
    /// (30 sectors x ~£3,016 net, against £45,000/month fixed cost: £30,000 lease + £9,000 salary
    /// + £6,000 insurance) - comfortably positive, not a hair above zero.
    /// </summary>
    [Fact]
    public void OneAircraft_OneLegADay_IsGenuinelyProfitable()
    {
        var netProfit = TypicalSectorNetProfit();
        var monthlyFixedCost = MonthlyFixedCostPerAircraft();

        var monthlyNet = MonthlyNetAt(1.0, netProfit, monthlyFixedCost);

        Assert.True(monthlyNet > 30_000m,
            $"One leg a day should be comfortably profitable, not just break-even, but netted only {monthlyNet:C0}/month.");
    }

    /// <summary>
    /// Confirms the shape of the fix, not just the headline number: break-even utilisation is now
    /// well under one sector a day (real figure ~0.50) - a direct, deliberate consequence of
    /// cutting the starter aircraft's fixed costs to a game-balance figure. Contrast with the
    /// pre-rebalance history: ~4.50 sectors/day originally, ~5.60 after the fuel-honesty fix alone
    /// (both far too demanding for a casual player - see the class doc).
    /// </summary>
    [Fact]
    public void BreakEvenUtilisation_IsNowWellUnderOneSectorPerDay()
    {
        var netProfit = TypicalSectorNetProfit();
        var monthlyFixedCost = MonthlyFixedCostPerAircraft();

        var breakEvenSectorsPerDay = (double)monthlyFixedCost / ((double)netProfit * 30.0);

        Assert.True(breakEvenSectorsPerDay < 1.0,
            $"Break-even utilisation should now be well under one sector/day, but is {breakEvenSectorsPerDay:F2}.");
    }

    /// <summary>
    /// The second pillar of the progression loop: the second aircraft's lease deposit
    /// (leaseRate x LeaseDepositMonths, now 1 month) must be affordable within roughly 7-10
    /// flights at the same casual one-leg-a-day pace - the user's own stated target. Real figure:
    /// £30,000 deposit / ~£3,016 per-sector net =~ 9.95 flights.
    /// </summary>
    [Fact]
    public void SecondAircraft_LeaseDeposit_AffordableWithinRoughlySevenToTenFlights()
    {
        var netProfit = TypicalSectorNetProfit();
        var deposit = A320MonthlyLeaseRate * (decimal)Config.AirlineStartup.LeaseDepositMonths;

        var flightsToAffordDeposit = (double)(deposit / netProfit);

        Assert.InRange(flightsToAffordDeposit, 7.0, 11.0);
    }

    [Fact]
    public void StartingCapital_CoversTheLeaseDepositAndAGenerouslyLongRunwayEvenFlyingNothing()
    {
        var leaseDeposit = A320MonthlyLeaseRate * (decimal)Config.AirlineStartup.LeaseDepositMonths;
        var cashAfterDeposit = Config.AirlineStartup.StartingCapital - leaseDeposit;

        Assert.True(cashAfterDeposit > 0, "The lease deposit alone must not exhaust starting capital.");

        var monthlyFixedCost = MonthlyFixedCostPerAircraft();
        var monthsOfRunwayFlyingNothing = cashAfterDeposit / monthlyFixedCost;

        // The design intent flipped with the progression-loop rebalance: runway must now be
        // GENEROUS, not lean - "the balance must not punish low activity" (docs/PLAN.md "The
        // progression loop"). Real figure is ~44 months, because fixed costs collapsed roughly
        // tenfold while starting capital stayed the same. There is deliberately no tight upper
        // bound here any more (the old "must not exceed ~6 months" assertion directly contradicted
        // the new design goal) - only a loose sanity ceiling against something being totally broken.
        Assert.True(monthsOfRunwayFlyingNothing >= 12.0m,
            $"Starting capital only covers {monthsOfRunwayFlyingNothing:F1} months of fixed costs with zero flying - " +
            "too tight for the 'must not punish low activity' design goal.");
        Assert.True(monthsOfRunwayFlyingNothing < 200.0m,
            $"{monthsOfRunwayFlyingNothing:F1} months of zero-flying runway is implausible - sanity check only.");
    }

    /// <summary>
    /// The old version of this test simulated a ramp that started as a deliberate loss (the point
    /// of the pre-rebalance design was that low utilisation should feel tight). That is now
    /// exactly backwards: "one to two sectors a day should sit around break-even rather than
    /// bleeding" is the explicit design goal, so this instead proves there is no forced early
    /// loss - every month at a genuinely casual, growing pace (1, then 2, then 3 sectors/day) is
    /// already profitable, and cash grows steadily from month one.
    /// </summary>
    [Fact]
    public void CasualRampUp_NeverForcesAnEarlyLoss_AndCashGrowsFromMonthOne()
    {
        var netProfit = TypicalSectorNetProfit();
        var monthlyFixedCost = MonthlyFixedCostPerAircraft();

        var leaseDeposit = A320MonthlyLeaseRate * (decimal)Config.AirlineStartup.LeaseDepositMonths;
        var cash = Config.AirlineStartup.StartingCapital - leaseDeposit;
        var startingCash = cash;

        // A genuinely casual growth curve: one leg a day for the first two months, then a little
        // more as the player gets comfortable - never approaching anything like a full-time pace.
        var monthlySectorsPerDay = new[] { 1.0, 1.0, 2.0, 2.0, 3.0, 3.0 };
        var monthlyNet = new decimal[monthlySectorsPerDay.Length];

        for (var month = 0; month < monthlySectorsPerDay.Length; month++)
        {
            monthlyNet[month] = MonthlyNetAt(monthlySectorsPerDay[month], netProfit, monthlyFixedCost);
            cash += monthlyNet[month];

            Assert.True(monthlyNet[month] > 0,
                $"Month {month + 1} at {monthlySectorsPerDay[month]} sectors/day should already be profitable - " +
                $"there must be no forced early loss - but netted {monthlyNet[month]:C0}.");
        }

        Assert.True(cash > startingCash,
            $"After 6 months cash ({cash:C0}) should exceed the post-deposit starting position ({startingCash:C0}).");
    }

    [Fact]
    public void BuyingOutright_IsUnaffordableAtCreationButReachableAfterAPlausiblePeriodOfProfitableOperation()
    {
        var netProfit = TypicalSectorNetProfit();
        var monthlyFixedCost = MonthlyFixedCostPerAircraft();

        var leaseDeposit = A320MonthlyLeaseRate * (decimal)Config.AirlineStartup.LeaseDepositMonths;
        var cashAfterDeposit = Config.AirlineStartup.StartingCapital - leaseDeposit;

        // 1. Unaffordable at creation - the entire point of leasing instead of buying outright.
        // Purchase price is untouched by the progression-loop rebalance (see the class doc), so
        // this is exactly as true as it always was.
        Assert.True(cashAfterDeposit < A320PurchasePrice,
            "Buying the starter aircraft outright must not be affordable straight out of creation.");

        // 2. Reachable after a plausible period of profitable operation, at an established,
        // multi-pilot-fed operating tempo well above the casual one-leg-a-day baseline above -
        // buying outright is explicitly still a later, ambitious milestone (docs/PLAN.md "The
        // progression loop": "buying outright stays the long-term milestone it already is"), not
        // something casual play alone should fund. Ramp to, then hold, 8 sectors/day for 24
        // months, then take a loan for whatever shortfall remains and confirm it is serviceable
        // out of ongoing profit at that same tempo.
        var cash = cashAfterDeposit;
        var rampMonths = new[] { 4.0, 5.0, 6.0, 7.0 };
        foreach (var sectorsPerDay in rampMonths)
        {
            cash += MonthlyNetAt(sectorsPerDay, netProfit, monthlyFixedCost);
        }

        const int sustainedMonths = 20; // months 5..24 at a steady, established 8 sectors/day
        cash += MonthlyNetAt(8.0, netProfit, monthlyFixedCost) * sustainedMonths;

        var shortfall = A320PurchasePrice - cash;
        Assert.True(shortfall > 0, "Sanity check: 24 months of a single aircraft's profit should not fully fund the purchase on its own.");

        var loanPayment = LoanCalculator.MonthlyPayment(shortfall, annualRatePct: 6.0, termMonths: 120);

        // Once owned outright there is no more lease payment - the loan payment replaces it.
        var ownedMonthlyFixedCost = loanPayment + Config.AirlineStartup.StartingPilotMonthlySalary + Config.FleetFinance.MonthlyInsurancePerAircraft;

        var ownedMonthlyNet = netProfit * (decimal)(8.0 * 30) - ownedMonthlyFixedCost;

        Assert.True(ownedMonthlyNet > 0,
            $"After 24 months of profitable operation, financing the remaining {shortfall:C0} ({loanPayment:C0}/month) should still leave the airline profitable, but netted {ownedMonthlyNet:C0}/month.");
    }
}
