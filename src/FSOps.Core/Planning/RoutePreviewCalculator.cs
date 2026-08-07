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
        Airport departure, Airport arrival, AircraftType aircraftType, AirlineStrategyProfile? strategy)
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
        var fare = FareEstimator.SuggestFare(distanceNm, strategy ?? AirlineStrategyProfile.Domestic);

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
            if (strategyProfile == AirlineStrategyProfile.Domestic && internationalSector)
            {
                warnings.Add("This is an international route, which doesn't match your Domestic strategy.");
            }
            else if (strategyProfile == AirlineStrategyProfile.International && !internationalSector && distanceNm < ShortHopThresholdNm)
            {
                warnings.Add("This is a short domestic hop, which doesn't match your International strategy.");
            }
        }

        var validation = new RoutePreviewValidation(withinRange, departureRunwayAdequate, arrivalRunwayAdequate, sameAirport, warnings);

        return new RoutePreviewResult(distanceNm, bearingDeg, blockTime, cruiseAltitudeFt, fuel, fare, path, validation);
    }
}
