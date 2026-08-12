using FSOps.Core.Economy;
using FSOps.Core.Entities;

namespace FSOps.Core.Planning;

public record RoutePreviewValidation(
    // "The aircraft type these figures were planned with can reach that far" - a statement about ONE
    // type, which is all this calculator knows about on its own. It is NOT a verdict on whether the
    // airline can fly the route: that is <see cref="Range"/>, and it is the only one a caller should
    // ever block on.
    bool WithinRange,
    bool SameAirport,
    IReadOnlyList<string> Warnings,
    RouteRangeAssessment Range,
    // The runway counterpart to Range - see RunwaySuitabilityAssessor. Replaced the old single-type
    // DepartureRunwayAdequate/ArrivalRunwayAdequate booleans for the same reason Range replaced a
    // single-type range check: a verdict about the whole fleet, not about whichever type these
    // figures happened to be planned with.
    RunwaySuitabilityAssessment Runway);

public record RoutePreviewResult(
    double DistanceNm,
    double InitialBearingDeg,
    BlockTimeBreakdown BlockTimeBreakdown,
    int CruiseAltitudeFt,
    FuelBreakdown FuelBreakdown,
    decimal SuggestedFare,
    IReadOnlyList<(double Lon, double Lat)> GreatCirclePath,
    RoutePreviewValidation Validation);

/// <summary>
/// Composes the pure planning pieces (distance, bearing, altitude, block time, fuel, fare) into
/// one route preview, plus the validation checks the route builder needs. Pure function over
/// already-resolved entities - the caller (RouteEndpoints) is responsible for looking those up
/// and for handling "airport not found" before getting here, since a preview must never throw.
/// </summary>
public static class RoutePreviewCalculator
{
    /// <summary>
    /// Real aircraft rarely fly right up to their catalogue range once reserves and payload are
    /// accounted for, so "within range" uses this fraction of the published range rather than
    /// the raw figure. Defined once, in <see cref="RouteRangeAssessor"/>; kept here as an alias so
    /// existing callers keep working and the two can never disagree.
    /// </summary>
    public const double OperationalRangeFactor = RouteRangeAssessor.OperationalRangeFactor;

    private const int SamplePointCount = 64;
    private const double ShortHopThresholdNm = 200;

    /// <param name="fleet">
    /// Every aircraft the airline owns, for the range verdict. Pass it wherever the answer will be
    /// shown to or enforced against the player: range is a property of the FLEET, and a verdict
    /// derived from the single <paramref name="aircraftType"/> these figures happen to be planned
    /// with would name an aircraft that may be irrelevant to whether the route can be flown (see
    /// <see cref="RouteRangeAssessor"/>). Null means "don't assess" and is right for the callers
    /// that only want block time or fuel out of this - they never surface the warnings.
    /// </param>
    /// <param name="runwayFleet">
    /// The runway counterpart to <paramref name="fleet"/> - see <see cref="RunwaySuitabilityAssessor"/>.
    /// Null means "don't assess" for exactly the same callers and the same reason.
    /// </param>
    public static RoutePreviewResult Calculate(
        EconomyConfig economyConfig, Airport departure, Airport arrival, AircraftType aircraftType, AirlineStrategyProfile? strategy,
        IReadOnlyList<RangeCandidateAircraft>? fleet = null, IReadOnlyList<RunwayCandidateAircraft>? runwayFleet = null)
    {
        var warnings = new List<string>();
        var sameAirport = string.Equals(departure.Icao, arrival.Icao, StringComparison.OrdinalIgnoreCase);

        var distanceNm = sameAirport
            ? 0
            : GreatCircle.DistanceNm(departure.Latitude, departure.Longitude, arrival.Latitude, arrival.Longitude);
        var bearingDeg = sameAirport
            ? 0
            : GreatCircle.InitialBearingDeg(departure.Latitude, departure.Longitude, arrival.Latitude, arrival.Longitude);
        var path = GreatCircle.SamplePath(
            departure.Latitude, departure.Longitude, arrival.Latitude, arrival.Longitude, SamplePointCount);

        var blockTime = BlockTimeEstimator.Estimate(distanceNm, aircraftType.CruiseTasKts);
        var cruiseAltitudeFt = CruiseAltitudeSelector.SelectCruiseAltitudeFt(distanceNm, bearingDeg, aircraftType.ServiceCeilingFt);
        var fuel = BlockFuelEstimator.Estimate(blockTime, aircraftType.FuelBurnKgPerHour);
        // The one true source for "the suggested fare" is ReferenceFareCalculator/EconomyConfig -
        // the same figure FareDemandModel anchors demand and elasticity to (see
        // FlightEconomicsCalculator, RouteEndpoints.PreviewAsync). A separate hardcoded formula
        // used to live here (FareEstimator, a Chunk-A/B placeholder predating the real economy
        // engine) and could silently drift from it - a player would be offered one fare while the
        // demand model scored it against a different one. distanceNm is 0 for a same-airport
        // preview, which ReferenceFareCalculator rejects (a real route never has zero distance),
        // so that case still falls back to the configured minimum fare rather than throwing - this
        // method must never throw (see RouteEndpoints.PreviewAsync's own doc comment).
        var fare = distanceNm > 0
            ? ReferenceFareCalculator.Calculate(economyConfig, strategy ?? AirlineStrategyProfile.Domestic, distanceNm)
            : economyConfig.ReferenceFare.MinimumFare;

        var operationalRangeNm = aircraftType.RangeNm * OperationalRangeFactor;
        var withinRange = distanceNm <= operationalRangeNm;

        if (sameAirport)
        {
            warnings.Add("Departure and arrival are the same airport.");
        }

        // The range verdict is a whole-fleet question - see RouteRangeAssessor's class doc for the
        // defect that made that non-negotiable. This calculator no longer says anything at all about
        // range in terms of the single type it planned with; it either reports the fleet's verdict or
        // says nothing.
        var rangeAssessment = fleet is null ? RouteRangeAssessment.NotAssessed : RouteRangeAssessor.Assess(distanceNm, fleet);

        if (!sameAirport && rangeAssessment.Message is { } rangeMessage)
        {
            warnings.Add(rangeMessage);
        }
        else if (!sameAirport && !withinRange && fleet is { Count: 0 })
        {
            // An airline with no aircraft yet (onboarding, or one that has sold everything) has no
            // fleet to assess, but silently saying nothing about a 4,000 nm sector would be worse
            // than useless. Say what the planning figures assumed, and say plainly that it is an
            // assumption rather than a limit of theirs - and never block on it, because there is no
            // fleet for it to be a fact about.
            warnings.Add(
                $"This route is {distanceNm:F0} nm and you have no aircraft yet. These figures assume a " +
                $"{aircraftType.Name}, which plans to about {operationalRangeNm:F0} nm once reserves are accounted for.");
        }

        // Same shape as the range verdict just above, for the same reason: a whole-fleet question,
        // never a statement about the single type these figures happened to be planned with. Length
        // and surface are both folded into this one assessment - see RunwaySuitabilityAssessor.
        var runwayAssessment = runwayFleet is null ? RunwaySuitabilityAssessment.NotAssessed : RunwaySuitabilityAssessor.Assess(departure, arrival, runwayFleet);

        if (!sameAirport && runwayAssessment.Message is { } runwayMessage)
        {
            warnings.Add(runwayMessage);
        }
        else if (!sameAirport && runwayFleet is { Count: 0 } &&
                 RunwaySuitabilityAssessor.AssessRoute(departure, arrival, aircraftType.MinRunwayFt, aircraftType.MtowTonnes, out _) != RunwaySuitabilityProblem.None)
        {
            // Same "no fleet to assess yet" fallback as range above, and the same guard range uses
            // (!withinRange) - only worth saying anything when the figures' own type would actually
            // have a problem here. Say what the planning figures assume, plainly as an assumption
            // rather than a limit of the airline's, and never block.
            warnings.Add(
                $"You have no aircraft yet. These figures assume a {aircraftType.Name}, which needs at least " +
                $"{aircraftType.MinRunwayFt:N0} ft of runway" +
                (RunwaySuitabilityAssessor.IsHeavy(aircraftType.MtowTonnes) ? " and a paved surface (it is too heavy for grass, gravel, dirt or water)." : "."));
        }

        if (strategy is { } strategyProfile && !sameAirport)
        {
            var internationalSector = !string.Equals(departure.Country, arrival.Country, StringComparison.OrdinalIgnoreCase);
            var rules = AdvisoryRulesFor(strategyProfile);
            if (rules.WarnsOnInternationalSector && internationalSector)
            {
                warnings.Add("This is an international route, which doesn't match your Domestic strategy.");
            }
            else if (rules.WarnsOnShortDomesticHop && !internationalSector && distanceNm < ShortHopThresholdNm)
            {
                warnings.Add("This is a short domestic hop, which doesn't match your International strategy.");
            }
        }

        var validation = new RoutePreviewValidation(
            withinRange, sameAirport, warnings, rangeAssessment, runwayAssessment);

        return new RoutePreviewResult(distanceNm, bearingDeg, blockTime, cruiseAltitudeFt, fuel, fare, path, validation);
    }

    /// <summary>
    /// Which of the two route-suitability advisories above a strategy profile raises. These are
    /// strategy *preferences*, never physical limits - range and runway warnings above apply to
    /// every profile unconditionally, Balanced included. Domestic and International are the only
    /// profiles with a directional preference; every other profile (LowCost, Premium, Balanced,
    /// and any future addition that doesn't opt in here) raises neither. Balanced in particular
    /// is exempt by design - "no route-suitability warnings at all" is the entire point of the
    /// all-rounder profile. Public so the Settings/onboarding profile picker can describe exactly
    /// this behaviour without a second, hand-maintained copy that could drift from it.
    /// </summary>
    public static (bool WarnsOnInternationalSector, bool WarnsOnShortDomesticHop) AdvisoryRulesFor(AirlineStrategyProfile profile) => profile switch
    {
        AirlineStrategyProfile.Domestic => (WarnsOnInternationalSector: true, WarnsOnShortDomesticHop: false),
        AirlineStrategyProfile.International => (WarnsOnInternationalSector: false, WarnsOnShortDomesticHop: true),
        _ => (WarnsOnInternationalSector: false, WarnsOnShortDomesticHop: false),
    };
}
