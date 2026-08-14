using FSOps.Core.Entities;

namespace FSOps.Core.Planning;

/// <summary>
/// Why one aircraft type cannot operate one city pair - the planning-side counterpart to
/// <see cref="RouteRangeAssessment"/>/<see cref="RunwaySuitabilityAssessment"/>, which answer the
/// same question about a whole FLEET and phrase it as advice to the player. This one is about a
/// single type and is used to decide, mechanically, which type to price a sector with.
/// </summary>
public enum SectorCapabilityProblem
{
    /// <summary>This type can operate the sector.</summary>
    None,

    /// <summary>Beyond this type's operational range (see <see cref="RouteRangeAssessor.OperationalRangeFactor"/>).</summary>
    Range,

    /// <summary>Neither end has a runway this type can use, or one of them does not.</summary>
    Runway,
}

/// <summary>
/// One aircraft type as far as route planning is concerned, plus how many of them the airline owns.
/// Deliberately a plain record rather than <see cref="AircraftType"/> so the rules below stay a pure
/// function over data the caller has already loaded, exactly as <see cref="RangeCandidateAircraft"/>
/// and <see cref="RunwayCandidateAircraft"/> do for the fleet-wide verdicts.
/// </summary>
public sealed record PlanningAircraftType(
    Guid AircraftTypeId,
    string IcaoType,
    string Name,
    int Seats,
    int RangeNm,
    int MinRunwayFt,
    double MtowTonnes,
    int OwnedCount);

/// <summary>
/// "Could this type physically operate this sector?" - range and runway, the two physical limits
/// route creation already refuses on, asked about one type at a time.
///
/// <para>Separate from <see cref="RouteRangeAssessor"/>/<see cref="RunwaySuitabilityAssessor"/> on
/// purpose, and it does not duplicate them: those two answer a question about the whole fleet and
/// their whole value is the exact sentence they produce for the player. This answers the mechanical
/// question underneath - which is used to pick the type a suggestion is priced with, and to say what
/// acquiring a type would unlock - and it defers to those same assessors for every actual threshold
/// (<see cref="RouteRangeAssessor.CanReach"/>, <see cref="RunwaySuitabilityAssessor.AssessRoute"/>),
/// so the two can never disagree about whether something is flyable.</para>
/// </summary>
public static class SectorCapability
{
    /// <summary>
    /// An opportunity shorter than this is not worth suggesting. Not an economic threshold and not a
    /// rule the economy enforces - <see cref="Economy.DemandConfig.NoAirMarketBelowNm"/> already
    /// collapses the market for a genuinely tiny hop and would make such a sector lose money on its
    /// own. This exists purely so the suggestion list is not padded with neighbouring-airport pairs
    /// the model has already priced into the ground.
    /// </summary>
    public const double MinimumSuggestedSectorNm = 100;

    public static SectorCapabilityProblem Assess(PlanningAircraftType type, Airport departure, Airport arrival, double distanceNm)
    {
        if (!RouteRangeAssessor.CanReach(type.RangeNm, distanceNm))
        {
            return SectorCapabilityProblem.Range;
        }

        return RunwaySuitabilityAssessor.AssessRoute(departure, arrival, type.MinRunwayFt, type.MtowTonnes, out _) == RunwaySuitabilityProblem.None
            ? SectorCapabilityProblem.None
            : SectorCapabilityProblem.Runway;
    }

    public static bool CanOperate(PlanningAircraftType type, Airport departure, Airport arrival, double distanceNm) =>
        Assess(type, departure, arrival, distanceNm) == SectorCapabilityProblem.None;

    /// <summary>
    /// Why NO type in a list can operate this sector, as one plain sentence - or null if at least one
    /// can. Range is reported ahead of runway when both fail, matching the order route creation
    /// checks them in, so a player is never told to find a longer runway for a sector they could not
    /// reach anyway. <paramref name="types"/> is walked in longest-range-first order so the sentence
    /// always names the same aircraft regardless of how the caller loaded them.
    /// </summary>
    public static string? ExplainWhyNoneCanOperate(
        IReadOnlyList<PlanningAircraftType> types, Airport departure, Airport arrival, double distanceNm)
    {
        if (types.Count == 0)
        {
            return "You have no aircraft yet, so there is nothing to fly this with.";
        }

        var ordered = types
            .OrderByDescending(t => t.RangeNm)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ordered.Any(t => CanOperate(t, departure, arrival, distanceNm)))
        {
            return null;
        }

        var longestLegged = ordered[0];
        if (!RouteRangeAssessor.CanReach(longestLegged.RangeNm, distanceNm))
        {
            var operationalRangeNm = RouteRangeAssessor.OperationalRangeNm(longestLegged.RangeNm);

            // A sector a handful of miles past the fleet's reach is the most actionable thing this
            // list can surface - a slightly longer-legged aircraft opens a whole market - but both
            // figures round to the same number, and "2,805 nm is beyond every aircraft you own,
            // which plans to about 2,805 nm" reads as a bug rather than as a near miss. Say "just
            // beyond" and quote the limit once.
            if (Math.Round(distanceNm) == Math.Round(operationalRangeNm))
            {
                return $"at {distanceNm:N0} nm it is just beyond every aircraft you own - your longest-legged is the " +
                       $"{longestLegged.Name}, which plans to about the same distance and no further.";
            }

            return $"{distanceNm:N0} nm is beyond every aircraft you own - your longest-legged is the {longestLegged.Name}, " +
                   $"which plans to about {operationalRangeNm:N0} nm.";
        }

        // Something has the legs, so the blocker is the ground. Report it against the type most
        // likely to fit (lightest first, then shortest field), mirroring RunwaySuitabilityAssessor's
        // own candidate ordering.
        var mostTolerant = ordered
            .Where(t => RouteRangeAssessor.CanReach(t.RangeNm, distanceNm))
            .OrderBy(t => RunwaySuitabilityAssessor.IsHeavy(t.MtowTonnes) ? 1 : 0)
            .ThenBy(t => t.MinRunwayFt)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .First();

        var problem = RunwaySuitabilityAssessor.AssessRoute(
            departure, arrival, mostTolerant.MinRunwayFt, mostTolerant.MtowTonnes, out var blockingAirport);

        return problem == RunwaySuitabilityProblem.SoftSurface
            ? $"{blockingAirport.Icao} has no paved runway your {mostTolerant.Name} could use at its weight."
            : $"{blockingAirport.Icao}'s longest runway is {blockingAirport.LongestRunwayFt:N0} ft, too short for your " +
              $"{mostTolerant.Name}, which needs {mostTolerant.MinRunwayFt:N0} ft.";
    }
}
