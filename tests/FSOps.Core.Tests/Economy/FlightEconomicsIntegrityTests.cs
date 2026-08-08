using FSOps.Core.Economy;
using FSOps.Core.Entities;

namespace FSOps.Core.Tests.Economy;

/// <summary>
/// Covers docs/PLAN.md's "Integrity - you must not be able to cheat your way to money" section,
/// specifically the two rules that are architectural to the pure economy engine (the third,
/// punctuality windows, belongs to the telemetry layer once it exists - see the class doc on
/// FlightEconomicsResult for why revenue can never respond to timing here).
/// </summary>
public class FlightEconomicsIntegrityTests
{
    private static readonly EconomyConfig Config = EconomyConfig.Default();

    public static IEnumerable<object[]> AllProfiles() =>
        Enum.GetValues<AirlineStrategyProfile>().Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void MicroSectorLoop_EvenAtTheBestPossibleFare_IsNetNegative(AirlineStrategyProfile profile)
    {
        // The adversarial case a cheater would actually pick: the biggest realistic single-aisle
        // aircraft (180 seats, 78t MTOW - an A320-sized jet), the two most generous airports
        // (Large-Large maximises the demand pool), and the model's own revenue-maximising fare -
        // not an arbitrary bad one. If a 20nm hop can still turn a profit under those conditions,
        // fixed per-sector costs are not doing their job.
        const int seats = 180;
        const double mtowTonnes = 78;
        const double distanceNm = 20;
        const double flightHours = 0.2; // a realistic block time for a 20nm sector, taxi included
        const double upliftKg = 500; // modest trip fuel for a hop this short
        var date = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var strategy = Config.GetStrategy(profile);
        var referenceFare = ReferenceFareCalculator.Calculate(Config.ReferenceFare, strategy, distanceNm);
        var marketDemandPax = DemandCalculator.AvailablePassengers(
            Config.Demand, AirportSizeCategory.Large, AirportSizeCategory.Large, distanceNm, date, reputationScore: 50);
        var bestFare = FareDemandModel.RevenueMaximizingFare(
            Config.MaxLoadFactor, strategy, referenceFare, seats, marketDemandPax, Config.CaptiveFareCeilingMultiple);
        var fuelPrice = FuelPricing.PricePerKg(Config.Fuel, "EGLL", "United Kingdom", date, worldSeed: 1);

        var result = FlightEconomicsCalculator.Calculate(
            Config, profile, bestFare, referenceFare, seats, marketDemandPax,
            upliftKg, fuelPrice, AirportSizeCategory.Large, mtowTonnes, flightHours);

        Assert.True(result.NetProfit < 0,
            $"{profile}: a 20nm micro-sector at its best fare should lose money, but netted {result.NetProfit:F2} " +
            $"(revenue {result.TicketRevenue:F2}, cost {result.TotalCost:F2}, {result.PaxBooked} pax at {bestFare:F2}).");

        // Framed explicitly as a loop, per the integrity requirement's wording: repeating a
        // lossy sector any number of times cannot turn it into a winning strategy - losses
        // accumulate linearly, there is no economy of scale that flips the sign.
        Assert.True(result.NetProfit * 20 < 0);
    }

    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void AbandonedFlight_EarnsNoRevenueAndDoesNotRefundUpliftedFuel(AirlineStrategyProfile profile)
    {
        // An abandoned flight is modelled here as zero passengers ever boarding - the only
        // faithful way to represent "the flight never operated" with this pure calculator, which
        // has no concept of flight lifecycle state. Fuel already uplifted before the abandonment
        // is a sunk cost: FlightCostCalculator.FuelUpliftCost has no refund path at all, so it
        // still applies in full.
        var strategy = Config.GetStrategy(profile);
        var referenceFare = 100m;

        var result = FlightEconomicsCalculator.Calculate(
            Config, profile, fare: referenceFare, referenceFare, seats: 180, marketDemandPax: 0,
            upliftKg: 2500, pricePerKgAtUpliftAirport: 0.85m,
            arrivalAirportSize: AirportSizeCategory.Large, mtowTonnes: 78, flightHours: 0);

        Assert.Equal(0, result.PaxBooked);
        Assert.Equal(0m, result.TicketRevenue);
        Assert.True(result.FuelCost > 0, "Fuel already uplifted must still be charged - it is never refunded.");
        Assert.True(result.NetProfit < 0,
            $"{profile}: an abandoned flight must never net positive, but netted {result.NetProfit:F2}.");
    }

    [Fact]
    public void FlightEconomicsResult_HasNoFieldThatCanRespondToTiming()
    {
        // Structural guard, not just a behavioural one: enumerate every public property on the
        // result type and confirm none of them are named or shaped to suggest a timing-based
        // bonus. If a future edit adds an "OnTimeBonus" or "EarlyArrivalBonus" property, this
        // test starts failing immediately, before it ever reaches a ledger.
        var suspiciousNames = new[] { "OnTime", "Early", "Punctual", "Bonus", "Late", "Delay" };

        var propertyNames = typeof(FlightEconomicsResult).GetProperties().Select(p => p.Name);

        foreach (var name in propertyNames)
        {
            foreach (var suspicious in suspiciousNames)
            {
                Assert.False(name.Contains(suspicious, StringComparison.OrdinalIgnoreCase),
                    $"FlightEconomicsResult.{name} looks like a timing-based revenue/cost line - revenue must come only from passengers x fare.");
            }
        }
    }
}
