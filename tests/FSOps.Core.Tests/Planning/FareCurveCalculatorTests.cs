using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Planning;

namespace FSOps.Core.Tests.Planning;

/// <summary>
/// The fare curve is what turns "set a fare" from a guess into a decision, so what it must prove is
/// less about a single number than about shape: raising a fare has to trade passengers for yield,
/// the peak has to be somewhere the player can actually see, and the curve has to agree with the
/// projection the rest of the app quotes. Same hand-derivable fixture as
/// <see cref="SectorProjectorTests"/> - see that file for where each figure comes from.
/// </summary>
public class FareCurveCalculatorTests
{
    private static readonly EconomyConfig Config = EconomyConfig.Default();
    private const double BaselineReputation = 50.0;
    private const double DistanceNm = 1000;
    private const int WorldSeed = 1;
    private static readonly DateTimeOffset PricedAt = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly Airport Departure = new()
    {
        Icao = "EGGD", Name = "Bristol", Country = "United Kingdom",
        Latitude = 51.3827, Longitude = -2.7191, SizeCategory = AirportSizeCategory.Medium, LongestRunwayFt = 8000,
    };

    private static readonly Airport Arrival = new()
    {
        Icao = "EGPH", Name = "Edinburgh", Country = "United Kingdom",
        Latitude = 55.9500, Longitude = -3.3725, SizeCategory = AirportSizeCategory.Medium, LongestRunwayFt = 8400,
    };

    private static readonly AircraftType Narrowbody = new()
    {
        Id = Guid.NewGuid(), IcaoType = "A320", Family = "A320", Name = "Airbus A320",
        PaxCapacity = 180, RangeNm = 3300, CruiseTasKts = 450, FuelBurnKgPerHour = 2400,
        MtowTonnes = 78.0, MinRunwayFt = 5500, ServiceCeilingFt = 39000,
    };

    private static SectorPlan PlanFor(AirlineStrategyProfile strategy) => SectorProjector.Plan(
        Config, strategy, BaselineReputation, Departure, Arrival, Narrowbody, DistanceNm, PricedAt, WorldSeed);

    public static IEnumerable<object[]> AllProfiles() =>
        Enum.GetValues<AirlineStrategyProfile>().Select(p => new object[] { p });

    [Fact]
    public void Calculate_SamplesTheDeclaredGrid_Exactly()
    {
        var plan = PlanFor(AirlineStrategyProfile.Domestic);
        var curve = FareCurveCalculator.Calculate(Config, AirlineStrategyProfile.Domestic, plan);

        // 0.50 to 2.00 inclusive in steps of 0.05 is 31 points - decimal arithmetic, so the last
        // step lands on exactly 2.00 rather than near it.
        Assert.Equal(31, curve.Points.Count);
        Assert.Equal(0.50m, curve.Points[0].MultipleOfReferenceFare);
        Assert.Equal(2.00m, curve.Points[^1].MultipleOfReferenceFare);

        // Reference fare 120.00, so the ends are 60.00 and 240.00 and the middle point is 120.00.
        Assert.Equal(120.00m, curve.ReferenceFare);
        Assert.Equal(60.00m, curve.Points[0].Fare);
        Assert.Equal(240.00m, curve.Points[^1].Fare);
        Assert.Equal(120.00m, curve.Points.Single(p => p.MultipleOfReferenceFare == 1.00m).Fare);

        Assert.Equal(curve.Points.OrderBy(p => p.Fare).Select(p => p.Fare), curve.Points.Select(p => p.Fare));
    }

    /// <summary>Every point is the same projection the ledger-facing calculator produces - the curve
    /// adds no arithmetic of its own, it only chooses which fares to ask about.</summary>
    [Fact]
    public void EveryPoint_MatchesTheProjectionAtThatFare()
    {
        var plan = PlanFor(AirlineStrategyProfile.Domestic);
        var curve = FareCurveCalculator.Calculate(Config, AirlineStrategyProfile.Domestic, plan);

        foreach (var point in curve.Points)
        {
            var projection = SectorProjector.AtFare(Config, AirlineStrategyProfile.Domestic, plan, point.Fare);
            Assert.Equal(projection.PaxBooked, point.PaxBooked);
            Assert.Equal(projection.LoadFactor, point.LoadFactor);
            Assert.Equal(projection.Revenue, point.Revenue);
            Assert.Equal(projection.TotalCost, point.TotalCost);
            Assert.Equal(projection.NetProfit, point.NetProfit);
        }
    }

    /// <summary>The trade the player is being shown: more money per seat, fewer seats sold. Never
    /// the other way round, on any profile.</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void RaisingTheFare_NeverIncreasesPassengers(AirlineStrategyProfile profile)
    {
        var curve = FareCurveCalculator.Calculate(Config, profile, PlanFor(profile));

        for (var i = 1; i < curve.Points.Count; i++)
        {
            Assert.True(curve.Points[i].PaxBooked <= curve.Points[i - 1].PaxBooked,
                $"{profile}: passengers rose from {curve.Points[i - 1].PaxBooked} to {curve.Points[i].PaxBooked} " +
                $"when the fare rose from {curve.Points[i - 1].Fare:F2} to {curve.Points[i].Fare:F2}.");
        }
    }

    /// <summary>
    /// The sweet spot has to be visible inside the sampled band, or the curve teaches the player
    /// nothing - a curve still climbing at its right-hand edge reads as "charge more, always".
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TheProfitPeak_SitsInsideTheSampledBand_NotAtItsEdge(AirlineStrategyProfile profile)
    {
        var curve = FareCurveCalculator.Calculate(Config, profile, PlanFor(profile));
        var best = curve.BestProfitPoint;

        Assert.True(best.MultipleOfReferenceFare > FareCurveCalculator.MinMultiple,
            $"{profile}: the best sampled fare sits on the bottom edge of the band ({best.MultipleOfReferenceFare}).");
        Assert.True(best.MultipleOfReferenceFare < FareCurveCalculator.MaxMultiple,
            $"{profile}: the best sampled fare sits on the top edge of the band ({best.MultipleOfReferenceFare}).");
        Assert.Equal(curve.Points.Max(p => p.NetProfit), best.NetProfit);
    }

    /// <summary>Ties go to the lower fare - the cheapest fare that earns the money, which is both
    /// friendlier to the player and the only tie-break that cannot depend on list ordering.</summary>
    [Fact]
    public void BestProfitPoint_OnATie_TakesTheLowerFare()
    {
        var plan = PlanFor(AirlineStrategyProfile.Domestic);
        var curve = FareCurveCalculator.Calculate(Config, AirlineStrategyProfile.Domestic, plan);

        var equalBest = curve.Points.Where(p => p.NetProfit == curve.BestProfitPoint.NetProfit).ToList();
        Assert.Equal(equalBest.Min(p => p.Fare), curve.BestProfitPoint.Fare);
    }

    /// <summary>
    /// The revenue peak is the closed-form figure from the demand model itself, never something read
    /// off the sampled points - and the captive-market ceiling bounds it, so it can never sit outside
    /// the band the curve draws.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void RevenueMaximizingFare_IsTheModelsOwnFigure_AndInsideTheCaptiveCeiling(AirlineStrategyProfile profile)
    {
        var plan = PlanFor(profile);
        var curve = FareCurveCalculator.Calculate(Config, profile, plan);

        var expected = Math.Round(
            FareDemandModel.RevenueMaximizingFare(
                Config.MaxLoadFactor, Config.GetStrategy(profile), plan.ReferenceFare, plan.Seats, plan.MarketDemandPax,
                Config.CaptiveFareCeilingMultiple),
            2, MidpointRounding.AwayFromZero);

        Assert.Equal(expected, curve.RevenueMaximizingFare);
        Assert.True(curve.RevenueMaximizingFare <= plan.ReferenceFare * (decimal)Config.CaptiveFareCeilingMultiple);
    }

    /// <summary>
    /// Profit peaks at or above the revenue peak, never below it: past the revenue peak each
    /// passenger lost also takes a passenger charge with them, so the last stretch of falling revenue
    /// costs less than it looks. Sampled to the nearest grid step, hence the one-step tolerance.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void TheProfitPeak_IsNeverBelowTheRevenuePeak(AirlineStrategyProfile profile)
    {
        var plan = PlanFor(profile);
        var curve = FareCurveCalculator.Calculate(Config, profile, plan);
        var stepInMoney = plan.ReferenceFare * FareCurveCalculator.MultipleStep;

        Assert.True(curve.BestProfitPoint.Fare >= curve.RevenueMaximizingFare - stepInMoney,
            $"{profile}: profit peaked at {curve.BestProfitPoint.Fare:F2}, more than one grid step below the " +
            $"revenue peak at {curve.RevenueMaximizingFare:F2}.");
    }
}
