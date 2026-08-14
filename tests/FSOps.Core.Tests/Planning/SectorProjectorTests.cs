using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Planning;

namespace FSOps.Core.Tests.Planning;

/// <summary>
/// Exact-value tests for the projection every decision surface in the app quotes from.
///
/// <para>The fixture is chosen so every figure can be worked out by hand from
/// <c>economy-config.json</c>'s own numbers rather than read off a previous run: a 1,000 nm sector
/// (comfortably inside the demand sweet spot, so the distance factor is exactly 1.0), between two
/// Medium airports (catchment exactly 3.0), on a Monday in June (seasonality 1.15, day-of-week
/// 1.05), at baseline reputation (factor exactly 1.0), flown by an aircraft cruising at exactly
/// 450 kt with a round 2,400 kg/h burn. Every expected number below is derived in the comment
/// beside it.</para>
///
/// <para>Fuel is the one line whose price comes from a hash-based walk rather than arithmetic
/// anybody would do on paper, so it is asserted against <see cref="FuelPricing"/> itself. That is
/// the right assertion for it in any case: the point is that the projection charges the fuel the
/// ledger will charge, not that the walk produces a particular constant.</para>
/// </summary>
public class SectorProjectorTests
{
    private static readonly EconomyConfig Config = EconomyConfig.Default();

    private const AirlineStrategyProfile Strategy = AirlineStrategyProfile.Domestic;
    private const double BaselineReputation = 50.0;
    private const double DistanceNm = 1000;
    private const int WorldSeed = 1;

    /// <summary>Monday 15 June 2026 - month 6 (seasonality 1.15) and Monday (day-of-week 1.05).</summary>
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

    private static SectorPlan PlanFixture() => SectorProjector.Plan(
        Config, Strategy, BaselineReputation, Departure, Arrival, Narrowbody, DistanceNm, PricedAt, WorldSeed);

    [Fact]
    public void Plan_ProducesTheExactFiguresTheConfigImplies()
    {
        var plan = PlanFixture();

        Assert.Equal(1000, plan.DistanceNm);

        // Block time: 10 taxi out + 15 climb + cruise + 15 descent + 8 taxi in, where cruise covers
        // 1000 - 70 - 75 = 855 nm at 450 kt = exactly 114 minutes. 10+15+114+15+8 = 162.
        Assert.Equal(162, plan.BlockMinutes);
        Assert.Equal(2.7, plan.BlockHours, 10);

        // Charged fuel = trip + taxi + contingency. Airborne = 15+114+15 = 144 min = 2.4 h, so trip
        // is 2.4 x 2400 = 5,760 kg, contingency 5% of that = 288 kg, taxi a flat 200 kg.
        Assert.Equal(6248, plan.ChargedFuelKg, 10);

        // Reference fare: 1000 nm x 0.12 per nm x 1.00 (Domestic) = 120.00, above the 65.00 floor.
        Assert.Equal(120.00m, plan.ReferenceFare);

        // Market: catchment sqrt(3.0 x 3.0) = 3.0, distance factor 1.0 (1,000 nm is inside the
        // 300-2,500 nm sweet spot), June 1.15, Monday 1.05, reputation 50 -> 1.0.
        // 45.0 x 3.0 x 1.0 x 1.15 x 1.05 x 1.0 = 163.0125, rounded away from zero = 163.
        Assert.Equal(163, plan.MarketDemandPax);

        Assert.Equal(180, plan.Seats);
        Assert.Equal(AirportSizeCategory.Medium, plan.ArrivalSizeCategory);
        Assert.Equal(78.0, plan.MtowTonnes);

        // Fuel price is the walk's own figure, not a constant anybody would derive by hand - what
        // matters is that it is the departure airport's price on the day, which is where a sector's
        // fuel is billed.
        Assert.Equal(
            FuelPricing.PricePerKg(Config.Fuel, "EGGD", "United Kingdom", PricedAt, WorldSeed),
            plan.FuelPricePerKg);
    }

    [Fact]
    public void AtReferenceFare_EveryMoneyLineIsTheExactFigureTheLedgerWouldPost()
    {
        var plan = PlanFixture();
        var projection = SectorProjector.AtFare(Config, Strategy, plan, plan.ReferenceFare);

        // At exactly the reference fare the fare-side curve evaluates to MaxLoadFactor (0.92), so
        // the seat formula offers round(180 x 0.92) = 166 seats' worth - but the market only holds
        // 163, and the market is the binding constraint here.
        Assert.Equal(163, projection.PaxBooked);
        Assert.Equal(163.0 / 180.0, projection.LoadFactor, 12);

        // 163 passengers x 120.00.
        Assert.Equal(19_560.00m, projection.Revenue);

        var economics = projection.Economics;
        Assert.Equal(390.00m, economics.LandingFee);      // 5.00/tonne (Medium) x 78 t
        Assert.Equal(273.00m, economics.HandlingFee);     // 3.50/tonne x 78 t x 1.00 (Domestic cost multiplier)
        Assert.Equal(50.70m, economics.ParkingFee);       // 0.65/tonne x 78 t
        Assert.Equal(1_141.00m, economics.PassengerCharge); // 7.00/pax (Medium) x 163 pax
        Assert.Equal(220.00m, economics.TurnaroundFee);   // flat Medium rate
        Assert.Equal(567.00m, economics.MaintenanceAccrual); // 210/h x 2.7 h
        Assert.Equal(918.00m, economics.CrewCost);        // 340/h x 2.7 h

        // 6,248 kg burned, priced where it departed.
        var expectedFuelCost = FlightCostCalculator.FuelBurnCost(
            6248, FuelPricing.PricePerKg(Config.Fuel, "EGGD", "United Kingdom", PricedAt, WorldSeed));
        Assert.Equal(expectedFuelCost, economics.FuelCost);

        var expectedNonFuelCost = 390.00m + 273.00m + 50.70m + 1_141.00m + 220.00m + 567.00m + 918.00m;
        Assert.Equal(expectedNonFuelCost + expectedFuelCost, economics.TotalCost);
        Assert.Equal(19_560.00m - expectedNonFuelCost - expectedFuelCost, projection.NetProfit);
    }

    /// <summary>
    /// The rule the whole feature rests on: a projection is not a second model. Assembling the
    /// inputs by hand and calling <see cref="FlightEconomicsCalculator"/> - the exact call
    /// <c>FlightEconomicsPoster</c> makes when it writes ledger rows - must produce the identical
    /// record.
    /// </summary>
    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(180)]
    [InlineData(400)]
    public void AtFare_IsExactlyFlightEconomicsCalculatorOverTheSameInputs(int fare)
    {
        var plan = PlanFixture();
        var projection = SectorProjector.AtFare(Config, Strategy, plan, fare);

        var expected = FlightEconomicsCalculator.Calculate(
            Config,
            Strategy,
            fare,
            ReferenceFareCalculator.Calculate(Config, Strategy, DistanceNm),
            Narrowbody.PaxCapacity,
            DemandCalculator.AvailablePassengers(
                Config.Demand, Departure.SizeCategory, Arrival.SizeCategory, DistanceNm, PricedAt, BaselineReputation),
            chargedFuelKg: BlockFuelEstimator.Estimate(
                BlockTimeEstimator.Estimate(DistanceNm, Narrowbody.CruiseTasKts), Narrowbody.FuelBurnKgPerHour).ChargedFuelKg,
            pricePerKgAtDepartureAirport: FuelPricing.PricePerKg(
                Config.Fuel, Departure.Icao, Departure.Country, PricedAt, WorldSeed),
            Arrival.SizeCategory,
            Narrowbody.MtowTonnes,
            BlockTimeEstimator.Estimate(DistanceNm, Narrowbody.CruiseTasKts).TotalMinutes / 60.0);

        Assert.Equal(expected, projection.Economics);
    }

    [Fact]
    public void Project_IsDeterministic_SameInputsGiveByteIdenticalFigures()
    {
        var first = SectorProjector.Project(
            Config, Strategy, BaselineReputation, Departure, Arrival, Narrowbody, DistanceNm, fare: 137.25m, PricedAt, WorldSeed);
        var second = SectorProjector.Project(
            Config, Strategy, BaselineReputation, Departure, Arrival, Narrowbody, DistanceNm, fare: 137.25m, PricedAt, WorldSeed);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Project_WithNoFare_PricesAtTheReferenceFare()
    {
        var projection = SectorProjector.Project(
            Config, Strategy, BaselineReputation, Departure, Arrival, Narrowbody, DistanceNm, fare: null, PricedAt, WorldSeed);

        Assert.Equal(120.00m, projection.Fare);
        Assert.Equal(projection.Plan.ReferenceFare, projection.Fare);
    }

    /// <summary>
    /// A route's stored distance is what <c>FlightEconomicsPoster</c> prices against, so the
    /// route-shaped overload must use it verbatim rather than recomputing from coordinates - a
    /// projection quoted against a different distance from the one the ledger will use is exactly
    /// the silent disagreement this whole design exists to prevent.
    /// </summary>
    [Fact]
    public void Plan_RouteOverload_UsesTheSuppliedDistanceNotTheGreatCircleOne()
    {
        var plan = SectorProjector.Plan(
            Config, Strategy, BaselineReputation, Departure, Arrival, Narrowbody, distanceNm: 1000, PricedAt, WorldSeed);

        var greatCircleNm = GreatCircle.DistanceNm(
            Departure.Latitude, Departure.Longitude, Arrival.Latitude, Arrival.Longitude);

        Assert.Equal(1000, plan.DistanceNm);
        Assert.NotEqual(Math.Round(greatCircleNm), plan.DistanceNm);
    }

    [Fact]
    public void Plan_ZeroDistance_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SectorProjector.Plan(
            Config, Strategy, BaselineReputation, Departure, Arrival, Narrowbody, distanceNm: 0, PricedAt, WorldSeed));
    }
}
