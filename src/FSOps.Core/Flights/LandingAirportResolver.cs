using FSOps.Core.Entities;
using FSOps.Core.Planning;

namespace FSOps.Core.Flights;

/// <summary>How a flight's post-landing airport was decided.</summary>
public enum LandingAirportDecision
{
    /// <summary>No tracked position was available at all - the planned arrival was used as-is
    /// (this is the manual-completion path, which has no reliable telemetry).</summary>
    NoPositionData,

    /// <summary>The final tracked position resolved to the planned arrival airport - an ordinary,
    /// undiverted landing.</summary>
    MatchesPlannedArrival,

    /// <summary>The final tracked position resolved to an airport other than the planned arrival -
    /// a genuine diversion. The aircraft is left where it actually parked.</summary>
    Diverted,

    /// <summary>A final position was known but no airport was found near it (or it landed off any
    /// known airport, e.g. a crash) - falls back to the planned arrival rather than recording a
    /// location that's probably wrong.</summary>
    UnresolvedFallbackToPlanned,
}

public sealed record LandingAirportResult(string Icao, LandingAirportDecision Decision, double? DistanceFromFinalPositionNm);

/// <summary>
/// Works out which airport a flight actually ended up at from its last tracked position, so a
/// diversion leaves the fleet aircraft where it really parked instead of wherever the flight was
/// planned to go - see the "Landing somewhere else entirely" rule in docs/PLAN.md. Pure spherical
/// maths, no I/O: callers load whatever candidate airports are worth checking (typically a cheap
/// bounding-box query) and pass them in.
/// </summary>
public static class LandingAirportResolver
{
    /// <summary>How far the final tracked position can be from an airport and still count as
    /// "landed there" - generous enough to cover taxiing to a gate, tight enough that a genuine
    /// diversion to a nearby airport is never mistaken for the planned one.</summary>
    public const double SearchRadiusNm = 5.0;

    public static LandingAirportResult Resolve(
        IReadOnlyList<Airport> candidateAirports,
        (double LatitudeDeg, double LongitudeDeg)? finalPosition,
        string plannedArrivalIcao)
    {
        if (finalPosition is not { } position)
        {
            return new LandingAirportResult(plannedArrivalIcao, LandingAirportDecision.NoPositionData, null);
        }

        Airport? nearest = null;
        var nearestDistanceNm = double.MaxValue;
        foreach (var airport in candidateAirports)
        {
            var distanceNm = GreatCircle.DistanceNm(position.LatitudeDeg, position.LongitudeDeg, airport.Latitude, airport.Longitude);
            if (distanceNm < nearestDistanceNm)
            {
                nearestDistanceNm = distanceNm;
                nearest = airport;
            }
        }

        if (nearest is null || nearestDistanceNm > SearchRadiusNm)
        {
            return new LandingAirportResult(
                plannedArrivalIcao, LandingAirportDecision.UnresolvedFallbackToPlanned, nearest is null ? null : nearestDistanceNm);
        }

        var decision = string.Equals(nearest.Icao, plannedArrivalIcao, StringComparison.OrdinalIgnoreCase)
            ? LandingAirportDecision.MatchesPlannedArrival
            : LandingAirportDecision.Diverted;

        return new LandingAirportResult(nearest.Icao, decision, nearestDistanceNm);
    }
}
