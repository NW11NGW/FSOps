using FSOps.Core.Economy;
using FSOps.Core.Entities;

namespace FSOps.Core.Planning;

public record RoutePreviewValidation(
    bool WithinRange,
    bool DepartureRunwayAdequate,
    bool ArrivalRunwayAdequate,
    bool SameAirport,
    IReadOnlyList<string> Warnings);

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
    /// the raw figure.
    /// </summary>
    public const double OperationalRangeFactor = 0.85;

    private const int SamplePointCount = 64;
    private const double ShortHopThresholdNm = 200;

    public static RoutePreviewResult Calculate(
        EconomyConfig economyConfig, Airport departure, Airport arrival, AircraftType aircraftType, AirlineStrategyProfile? strategy)
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
        var departureRunwayAdequate = departure.LongestRunwayFt >= aircraftType.MinRunwayFt;
        var arrivalRunwayAdequate = arrival.LongestRunwayFt >= aircraftType.MinRunwayFt;

        if (sameAirport)
        {
            warnings.Add("Departure and arrival are the same airport.");
        }

        if (!sameAirport && !withinRange)
        {
            warnings.Add(
                $"This route ({distanceNm:F0} nm) is beyond the {aircraftType.Name}'s practical operating range " +
                $"(~{operationalRangeNm:F0} nm once reserves are accounted for).");
        }

        if (!departureRunwayAdequate)
        {
            warnings.Add(
                $"{departure.Icao}'s longest runway ({departure.LongestRunwayFt} ft) may be too short for the " +
                $"{aircraftType.Name} (needs {aircraftType.MinRunwayFt} ft).");
        }

        if (!arrivalRunwayAdequate)
        {
            warnings.Add(
                $"{arrival.Icao}'s longest runway ({arrival.LongestRunwayFt} ft) may be too short for the " +
                $"{aircraftType.Name} (needs {aircraftType.MinRunwayFt} ft).");
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

        var validation = new RoutePreviewValidation(withinRange, departureRunwayAdequate, arrivalRunwayAdequate, sameAirport, warnings);

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
