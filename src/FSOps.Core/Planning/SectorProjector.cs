using FSOps.Core.Economy;
using FSOps.Core.Entities;

namespace FSOps.Core.Planning;

/// <summary>
/// Everything about one sector that does NOT depend on the fare: how far it is, how long it takes,
/// what it burns and what that fuel costs where it departs, how big the market is that day, how
/// many seats are on offer, and what the app would charge if the player expressed no opinion (the
/// reference fare).
/// <para>
/// Split out from <see cref="SectorProjection"/> deliberately so a caller sweeping a range of fares
/// (the fare workbench) computes all of this exactly once and then varies only the price. That is
/// not merely an optimisation: it is what guarantees every point on a fare curve is priced against
/// the identical market, block time and fuel price, so the shape of the curve is caused by the fare
/// and by nothing else.
/// </para>
/// </summary>
public sealed record SectorPlan(
    double DistanceNm,
    int BlockMinutes,
    double ChargedFuelKg,
    decimal FuelPricePerKg,
    decimal ReferenceFare,
    int MarketDemandPax,
    int Seats,
    AirportSizeCategory ArrivalSizeCategory,
    double MtowTonnes)
{
    public double BlockHours => BlockMinutes / 60.0;
}

/// <summary>
/// One sector priced at one fare: the plan above, the fare asked, and the itemised money the real
/// economy engine says that produces.
/// </summary>
public sealed record SectorProjection(SectorPlan Plan, decimal Fare, FlightEconomicsResult Economics)
{
    public int PaxBooked => Economics.PaxBooked;

    public double LoadFactor => Economics.LoadFactor;

    public decimal Revenue => Economics.TicketRevenue;

    public decimal TotalCost => Economics.TotalCost;

    public decimal NetProfit => Economics.NetProfit;
}

/// <summary>
/// The single place the app answers "what would this sector be worth?" - one aircraft, one city
/// pair, one fare, one day.
///
/// <para><b>Why this type exists at all.</b> The same six-step chain (plan the sector, look up the
/// reference fare, size the market, price the fuel, run <see cref="FlightEconomicsCalculator"/>,
/// read the profit off it) had been hand-written three separate times across the server before this
/// - once for the schedule leg picker, once for a pilot's weekly summary, and once, in a cut-down
/// revenue-only form, in the route preview. Every new decision surface added another copy, and a
/// second copy of a financial calculation is a defect waiting to happen: it does not fail loudly, it
/// drifts, and the player finds out when a number they were shown before committing disagrees with
/// the one the ledger posts afterwards. Every caller now goes through here, so a prediction and the
/// posting cannot disagree for reasons of their own.</para>
///
/// <para><b>What "agrees with the ledger" means precisely.</b> The money lines come from
/// <see cref="FlightEconomicsCalculator"/>, which is exactly what
/// <c>FSOps.Server.Services.FlightEconomicsPoster.PostCompletionAsync</c> calls to write the real
/// <c>LedgerTransaction</c> rows. Fuel is the one line that needs care: the poster charges a FLOWN
/// sector for its measured burn (posted separately, <c>PostFuelBurn</c>) and therefore passes zero
/// fuel into the economics calculator, whereas a sector with no telemetry to measure - a virtual
/// pilot's occurrence, a manual completion - is billed the planned figure. This projection uses the
/// planned figure (<see cref="FuelBreakdown.ChargedFuelKg"/>, priced at the departure airport, the
/// only place a sector's fuel is ever priced), so it is exact for a sector nobody hand-flies and a
/// best estimate for one that is: a human who flies more economically than the plan pays less than
/// predicted, and one who does not pays more. Every other line - tickets, landing, handling,
/// parking, passenger charges, turnaround, maintenance accrual, crew - is the identical arithmetic
/// the ledger will post.</para>
///
/// <para><b>Deterministic.</b> No clock and no randomness of its own: the caller supplies the
/// instant to price at and the world seed the fuel-price walk keys off, so the same inputs always
/// produce the same figures and the exact-value tests can assert them.</para>
/// </summary>
public static class SectorProjector
{
    /// <summary>
    /// Everything fare-independent about this sector. <paramref name="pricedAtUtc"/> feeds both the
    /// demand model (season and day of week) and the fuel-price walk; <paramref name="worldSeed"/>
    /// is the airline's own world seed, resolved by the caller the same way the poster resolves it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The two airports are the same, or otherwise zero distance apart. A sector with no distance
    /// has no market to speak of and <see cref="DemandCalculator"/> rejects it; callers that may be
    /// handed such a pair should check for it and say "no figure" rather than quoting a guess.
    /// </exception>
    public static SectorPlan Plan(
        EconomyConfig config,
        AirlineStrategyProfile strategy,
        double reputationScore,
        Airport departure,
        Airport arrival,
        AircraftType aircraftType,
        DateTimeOffset pricedAtUtc,
        int worldSeed)
    {
        var distanceNm = GreatCircle.DistanceNm(departure.Latitude, departure.Longitude, arrival.Latitude, arrival.Longitude);
        if (distanceNm <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(arrival), "A sector must have a positive distance - there is no market for a zero-length sector to price.");
        }

        return Plan(config, strategy, reputationScore, departure, arrival, aircraftType, distanceNm, pricedAtUtc, worldSeed);
    }

    /// <summary>
    /// Overload for a saved <see cref="Entities.Route"/>, whose <see cref="Entities.Route.DistanceNm"/>
    /// was stamped when it was created. Uses that stored figure rather than recomputing it, so a
    /// prediction is priced against exactly the distance the ledger will price the sector against -
    /// <c>FlightEconomicsPoster</c> reads <c>route.DistanceNm</c>, never the airports' coordinates.
    /// </summary>
    public static SectorPlan Plan(
        EconomyConfig config,
        AirlineStrategyProfile strategy,
        double reputationScore,
        Airport departure,
        Airport arrival,
        AircraftType aircraftType,
        double distanceNm,
        DateTimeOffset pricedAtUtc,
        int worldSeed)
    {
        if (distanceNm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(distanceNm), "Distance must be positive.");
        }

        var blockTime = BlockTimeEstimator.Estimate(distanceNm, aircraftType.CruiseTasKts);
        var fuel = BlockFuelEstimator.Estimate(blockTime, aircraftType.FuelBurnKgPerHour);
        var fuelPricePerKg = FuelPricing.PricePerKg(config.Fuel, departure.Icao, departure.Country, pricedAtUtc, worldSeed);
        var referenceFare = ReferenceFareCalculator.Calculate(config, strategy, distanceNm);
        var marketDemandPax = DemandCalculator.AvailablePassengers(
            config.Demand, departure.SizeCategory, arrival.SizeCategory, distanceNm, pricedAtUtc, reputationScore);

        return new SectorPlan(
            distanceNm,
            blockTime.TotalMinutes,
            fuel.ChargedFuelKg,
            fuelPricePerKg,
            referenceFare,
            marketDemandPax,
            aircraftType.PaxCapacity,
            arrival.SizeCategory,
            aircraftType.MtowTonnes);
    }

    /// <summary>Prices an already-planned sector at one fare. Cheap, and safe to call repeatedly
    /// over a grid of fares - that is what the fare curve is.</summary>
    public static SectorProjection AtFare(EconomyConfig config, AirlineStrategyProfile strategy, SectorPlan plan, decimal fare)
    {
        var economics = FlightEconomicsCalculator.Calculate(
            config,
            strategy,
            fare,
            plan.ReferenceFare,
            plan.Seats,
            plan.MarketDemandPax,
            chargedFuelKg: plan.ChargedFuelKg,
            pricePerKgAtDepartureAirport: plan.FuelPricePerKg,
            plan.ArrivalSizeCategory,
            plan.MtowTonnes,
            plan.BlockHours);

        return new SectorProjection(plan, fare, economics);
    }

    /// <summary>Plan and price in one step. <paramref name="fare"/> null means "whatever the app
    /// would suggest", i.e. the reference fare for this distance and strategy.</summary>
    public static SectorProjection Project(
        EconomyConfig config,
        AirlineStrategyProfile strategy,
        double reputationScore,
        Airport departure,
        Airport arrival,
        AircraftType aircraftType,
        decimal? fare,
        DateTimeOffset pricedAtUtc,
        int worldSeed)
    {
        var plan = Plan(config, strategy, reputationScore, departure, arrival, aircraftType, pricedAtUtc, worldSeed);
        return AtFare(config, strategy, plan, fare ?? plan.ReferenceFare);
    }

    /// <summary>Plan and price in one step against a route's stored distance - see the matching
    /// <see cref="Plan(EconomyConfig,AirlineStrategyProfile,double,Airport,Airport,AircraftType,double,DateTimeOffset,int)"/>
    /// overload for why a saved route must use its own stamped distance.</summary>
    public static SectorProjection Project(
        EconomyConfig config,
        AirlineStrategyProfile strategy,
        double reputationScore,
        Airport departure,
        Airport arrival,
        AircraftType aircraftType,
        double distanceNm,
        decimal? fare,
        DateTimeOffset pricedAtUtc,
        int worldSeed)
    {
        var plan = Plan(config, strategy, reputationScore, departure, arrival, aircraftType, distanceNm, pricedAtUtc, worldSeed);
        return AtFare(config, strategy, plan, fare ?? plan.ReferenceFare);
    }
}
