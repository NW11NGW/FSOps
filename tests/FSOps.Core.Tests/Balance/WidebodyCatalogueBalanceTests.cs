using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Planning;

namespace FSOps.Core.Tests.Balance;

/// <summary>
/// docs/PLAN.md "The aircraft catalogue must cover what people actually fly" - "Balance impact must
/// be checked, not assumed. Widebodies change the economics - far more seats, far higher costs, and
/// routes long enough to reach the demand model's 300-2,500 nm sweet spot. Adding them must not
/// break the guardrails". This is that check, using the Boeing 777-300ER (B77W) as the
/// representative widebody: 396 seats, 351.5t MTOW, 490kt cruise, 8,200kg/h burn - the largest,
/// heaviest, thirstiest type in the catalogue, and so the hardest case for every guardrail.
/// </summary>
public class WidebodyCatalogueBalanceTests
{
    private static readonly EconomyConfigCatalog Catalog = EconomyConfigCatalog.Default();

    private const AirlineStrategyProfile Strategy = AirlineStrategyProfile.International;
    private const int Seats = 396;
    private const double MtowTonnes = 351.5;
    private const int CruiseTasKts = 490;
    private const double FuelBurnKgPerHour = 8200;
    private const double ReputationScore = 50;
    private static readonly DateTimeOffset FlightDate = new(2026, 4, 15, 12, 0, 0, TimeSpan.Zero);

    public static IEnumerable<object[]> AllPlaystyles() =>
        Enum.GetValues<AirlinePlaystyle>().Select(p => new object[] { p });

    /// <summary>
    /// A long-haul widebody sector squarely inside the demand model's sweet spot (300-2,500 nm) at
    /// the suggested fare must be profitable, in both playstyles - the far higher fixed/fuel costs
    /// a widebody carries must not accidentally flip a sensible route unprofitable, and the far
    /// larger seat count must not accidentally make every route trivially profitable either (the
    /// point of this test is that it ACTUALLY checks, rather than assuming size scales cleanly).
    /// </summary>
    [Theory]
    [MemberData(nameof(AllPlaystyles))]
    public void Widebody_SweetSpotRoute_IsProfitableAtTheSuggestedFare(AirlinePlaystyle playstyle)
    {
        var config = Catalog.Get(playstyle);
        const double distanceNm = 2200; // within Demand.SweetSpotMinNm(300)..SweetSpotMaxNm(2500)
        var blockTime = BlockTimeEstimator.Estimate(distanceNm, CruiseTasKts);
        var fuel = BlockFuelEstimator.Estimate(blockTime, FuelBurnKgPerHour);
        var flightHours = blockTime.TotalMinutes / 60.0;

        var strategy = config.GetStrategy(Strategy);
        var referenceFare = ReferenceFareCalculator.Calculate(config.ReferenceFare, strategy, distanceNm);
        var marketDemandPax = DemandCalculator.AvailablePassengers(
            config.Demand, AirportSizeCategory.Large, AirportSizeCategory.Large, distanceNm, FlightDate, ReputationScore);
        var fuelPrice = FuelPricing.PricePerKg(config.Fuel, "EGLL", "United Kingdom", FlightDate, worldSeed: 1);

        var result = FlightEconomicsCalculator.Calculate(
            config, Strategy, fare: referenceFare, referenceFare: referenceFare, Seats, marketDemandPax,
            fuel.ChargedFuelKg, fuelPrice, AirportSizeCategory.Large, MtowTonnes, flightHours);

        Assert.True(result.NetProfit > 0,
            $"{playstyle} widebody at {distanceNm}nm: the suggested fare should be profitable, but netted {result.NetProfit:F2} " +
            $"(revenue {result.TicketRevenue:F2}, cost {result.TotalCost:F2}).");
    }

    /// <summary>
    /// The 20nm micro-sector exploit must stay loss-making for a widebody too - its far greater
    /// seat count must not let it "buy" enough demand to escape the geography-based demand collapse
    /// (<see cref="FSOps.Core.Economy.DemandConfig.NoAirMarketBelowNm"/> caps PASSENGERS regardless
    /// of how many seats the aircraft has), while its far higher weight-based fees and fuel burn
    /// make the loss worse, not better, than the narrowbody case
    /// (PlaystyleInvariantsTests.MicroSectorExploit_20Nm_StaysLossMaking_AtTheRevenueMaximisingFare).
    /// </summary>
    [Theory]
    [MemberData(nameof(AllPlaystyles))]
    public void Widebody_MicroSectorExploit_20Nm_StaysLossMaking_AtTheRevenueMaximisingFare(AirlinePlaystyle playstyle)
    {
        var config = Catalog.Get(playstyle);
        const double distanceNm = 20;
        const double flightHours = 0.2;
        const double upliftKg = 2000;

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
            $"{playstyle}: a 20nm micro-sector flown by a widebody at its best fare should lose money, but netted {result.NetProfit:F2}.");
    }

    /// <summary>Every catalogue lease/purchase figure must resolve without throwing for both
    /// playstyles, and Casual must never exceed True-life for any type - a config-authoring
    /// mistake in either "casual" or "trueLife" block would otherwise ship silently.</summary>
    [Theory]
    [InlineData("A319")] [InlineData("A320")] [InlineData("A321")] [InlineData("A20N")] [InlineData("A21N")]
    [InlineData("B737")] [InlineData("B738")] [InlineData("B739")] [InlineData("B38M")] [InlineData("B752")]
    [InlineData("A332")] [InlineData("A333")] [InlineData("A359")] [InlineData("A388")]
    [InlineData("B763")] [InlineData("B77W")] [InlineData("B789")] [InlineData("B748")]
    [InlineData("E170")] [InlineData("E175")] [InlineData("E190")] [InlineData("E195")]
    [InlineData("CRJ7")] [InlineData("CRJ9")] [InlineData("AT42")] [InlineData("AT72")] [InlineData("DH8D")]
    public void EveryCatalogueType_HasALeaseRateInBothPlaystyles_AndCasualNeverExceedsTrueLife(string icaoType)
    {
        var casual = Catalog.Get(AirlinePlaystyle.Casual);
        var trueLife = Catalog.Get(AirlinePlaystyle.TrueLife);

        var casualRate = casual.LeaseRateFor(icaoType); // throws if missing - that IS the assertion
        var trueLifeRate = trueLife.LeaseRateFor(icaoType);

        Assert.True(casualRate <= trueLifeRate,
            $"{icaoType}: Casual ({casualRate}) must never exceed True-life ({trueLifeRate}).");
    }
}
