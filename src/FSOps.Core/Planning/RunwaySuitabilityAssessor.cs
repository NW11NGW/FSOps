using FSOps.Core.Entities;

namespace FSOps.Core.Planning;

/// <summary>
/// One airframe as far as runway suitability is concerned: what it is, the shortest runway it can
/// use, how heavy it is, and whether it is currently reserved to the player. Deliberately not
/// <see cref="FleetAircraft"/> - same reasoning as <see cref="RangeCandidateAircraft"/>: this keeps
/// <see cref="RunwaySuitabilityAssessor"/> a pure function over plain data the caller has already
/// loaded, and keeps the runway question free of anything to do with type MATCHING (family-level
/// type matching is informational and never penalised - runway suitability is a physical limit of
/// a specific airframe against a specific piece of ground, and must never become a back-door type
/// penalty).
/// </summary>
public sealed record RunwayCandidateAircraft(string Registration, string TypeName, int MinRunwayFt, double MtowTonnes, bool ReservedForPlayer);

/// <summary>
/// What the airline as a whole can do about a given airport pair's runways - the runway mirror of
/// <see cref="RouteRangeVerdict"/>. Same distinction, same reason it matters: "you can't fly this
/// yourself yet" (fixable by reserving) must never block, and "nothing you own can physically use
/// this ground" is the only genuine block.
/// </summary>
public enum RunwaySuitabilityVerdict
{
    /// <summary>Nothing to assess: no fleet, or either airport has no runway data at all.</summary>
    NotAssessed,

    /// <summary>An aircraft already reserved to the player can use both ends. Nothing to say.</summary>
    ReservedCanUse,

    /// <summary>Nothing reserved can use both ends, but something in the fleet can. Guidance, never a block.</summary>
    RequiresReservation,

    /// <summary>No aircraft in the fleet can use both ends. The only genuine block.</summary>
    BeyondFleet,
}

/// <summary>
/// The specific physical reason ONE aircraft cannot use ONE airport - "too short" and "too soft"
/// are different facts a player needs told apart (one is fixed by a different aircraft, the other
/// by a different airport), so every caller reports exactly one of these rather than folding both
/// into a single boolean.
/// </summary>
public enum RunwaySuitabilityProblem
{
    /// <summary>At least one non-closed runway is both long enough and, if the aircraft is heavy, paved.</summary>
    None,

    /// <summary>No non-closed runway is long enough for this aircraft, regardless of surface.</summary>
    TooShort,

    /// <summary>A non-closed runway is long enough, but every one that is, is a soft surface this
    /// heavy an aircraft cannot use - length is irrelevant here, see <see cref="RunwaySuitabilityAssessor.IsHeavy"/>.</summary>
    SoftSurface,
}

/// <summary>
/// The outcome of asking "can this airline use these two runways?", including the exact sentence to
/// show. Built here rather than in the API or the UI for the same reason as
/// <see cref="RouteRangeAssessment"/>: route creation, the route preview and any future caller must
/// never be able to drift into saying different things about the same fleet.
/// </summary>
public sealed record RunwaySuitabilityAssessment(
    RunwaySuitabilityVerdict Verdict,
    bool Blocking,
    string? Message,
    string? AircraftRegistration,
    string? AircraftTypeName)
{
    public static RunwaySuitabilityAssessment NotAssessed { get; } =
        new(RunwaySuitabilityVerdict.NotAssessed, Blocking: false, Message: null, AircraftRegistration: null, AircraftTypeName: null);
}

/// <summary>
/// Answers the runway question about the AIRLINE, not about one aircraft type - the runway
/// counterpart to <see cref="RouteRangeAssessor"/>, built the same way for the same reason: a
/// verdict compared against a single arbitrarily-chosen aircraft type names the wrong thing and
/// blocks routes the player could actually fly with something else they own. Length mirrors the
/// range rule exactly (guidance everywhere nothing is genuinely beyond the fleet, a hard refusal
/// only when it is). Surface does not: a heavy aircraft physically cannot use a soft runway no
/// matter how long it is, so surface is folded into the same per-aircraft-per-airport check as
/// length rather than becoming a second mechanism - see <see cref="AssessAirport"/>.
/// <para>
/// Pure and deterministic - no I/O, no clock, no randomness - so every branch is unit-testable with
/// exact expected strings.
/// </para>
/// </summary>
public static class RunwaySuitabilityAssessor
{
    /// <summary>
    /// ICAO wake-turbulence "Heavy" category threshold (maximum take-off weight of 136 tonnes /
    /// 300,000 lb). Reused rather than invented so the rule reads the same way a pilot already
    /// understands it, and so every widebody in the seeded fleet (A330 upward) is "heavy" while
    /// every narrowbody and regional type is not - see AircraftTypeSeeder for the figures this
    /// lines up against.
    /// </summary>
    public const double HeavyMtowTonnesThreshold = 136.0;

    public static bool IsHeavy(double mtowTonnes) => mtowTonnes >= HeavyMtowTonnesThreshold;

    /// <summary>
    /// Whether ONE aircraft can use ONE airport at all - true only when <see cref="AssessAirport"/>
    /// finds no problem.
    /// </summary>
    public static bool CanUse(Airport airport, int minRunwayFt, double mtowTonnes) =>
        AssessAirport(airport, minRunwayFt, mtowTonnes) == RunwaySuitabilityProblem.None;

    /// <summary>
    /// The physical reason (if any) this aircraft cannot use this airport. A runway must satisfy
    /// BOTH length and (for a heavy aircraft) surface at once - a long grass strip and a short
    /// paved one at the same airport do not combine into one usable runway, so this checks each
    /// non-closed runway on its own rather than checking "is anything long enough" and "is anything
    /// paved" as two independent questions.
    /// <para>
    /// Falls back to the airport's stamped <see cref="Airport.LongestRunwayFt"/> (length only, no
    /// surface opinion) when <see cref="Airport.Runways"/> is empty - either because the airport
    /// genuinely has no runway rows on record, or because a caller loaded the airport without
    /// including them. Either way, the rule must never turn "no per-runway data available" into a
    /// silent full block.
    /// </para>
    /// </summary>
    public static RunwaySuitabilityProblem AssessAirport(Airport airport, int minRunwayFt, double mtowTonnes)
    {
        if (airport.Runways.Count == 0)
        {
            return airport.LongestRunwayFt >= minRunwayFt ? RunwaySuitabilityProblem.None : RunwaySuitabilityProblem.TooShort;
        }

        var heavy = IsHeavy(mtowTonnes);
        var anyLongEnough = false;

        foreach (var runway in airport.Runways)
        {
            if (runway.IsClosed || runway.LengthFt < minRunwayFt)
            {
                continue;
            }

            anyLongEnough = true;

            if (!heavy || !RunwaySurfaceClassifier.IsSoft(runway.Surface))
            {
                return RunwaySuitabilityProblem.None;
            }
        }

        return anyLongEnough ? RunwaySuitabilityProblem.SoftSurface : RunwaySuitabilityProblem.TooShort;
    }

    /// <summary>
    /// The same check across BOTH ends of a route, stopping at the first problem found (departure
    /// checked first) - a route needs both airports to work, and "one plain reason" means reporting
    /// whichever end fails first rather than stacking both into one sentence.
    /// </summary>
    public static RunwaySuitabilityProblem AssessRoute(
        Airport departure, Airport arrival, int minRunwayFt, double mtowTonnes, out Airport blockingAirport)
    {
        var departureProblem = AssessAirport(departure, minRunwayFt, mtowTonnes);
        if (departureProblem != RunwaySuitabilityProblem.None)
        {
            blockingAirport = departure;
            return departureProblem;
        }

        var arrivalProblem = AssessAirport(arrival, minRunwayFt, mtowTonnes);
        blockingAirport = arrival;
        return arrivalProblem;
    }

    /// <summary>
    /// Assesses a route against every aircraft the airline owns - the fleet-wide guidance question
    /// for route planning, mirroring <see cref="RouteRangeAssessor.Assess"/> exactly. Candidate
    /// ordering favours the aircraft most likely to fit a tight or soft strip (light before heavy,
    /// then shortest minimum runway, then registration), so the same fleet always names the same
    /// aircraft no matter what order the caller loaded them in.
    /// </summary>
    public static RunwaySuitabilityAssessment Assess(Airport departure, Airport arrival, IReadOnlyList<RunwayCandidateAircraft> fleet)
    {
        if (fleet.Count == 0)
        {
            return RunwaySuitabilityAssessment.NotAssessed;
        }

        var ordered = fleet
            .OrderBy(a => IsHeavy(a.MtowTonnes) ? 1 : 0)
            .ThenBy(a => a.MinRunwayFt)
            .ThenBy(a => a.Registration, StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool CandidateFits(RunwayCandidateAircraft a) =>
            AssessRoute(departure, arrival, a.MinRunwayFt, a.MtowTonnes, out _) == RunwaySuitabilityProblem.None;

        var reserved = ordered.FirstOrDefault(a => a.ReservedForPlayer && CandidateFits(a));
        if (reserved is not null)
        {
            return new RunwaySuitabilityAssessment(
                RunwaySuitabilityVerdict.ReservedCanUse, Blocking: false, Message: null, reserved.Registration, reserved.TypeName);
        }

        var capable = ordered.FirstOrDefault(CandidateFits);
        if (capable is not null)
        {
            return new RunwaySuitabilityAssessment(
                RunwaySuitabilityVerdict.RequiresReservation, Blocking: false,
                $"{departure.Icao} and {arrival.Icao} need a runway both aircraft-compatible ends can use. Nothing reserved to you " +
                $"can, but {capable.Registration} ({capable.TypeName}) can - reserve it on the Fleet page to fly it yourself, or " +
                "roster it to a virtual pilot as it is.",
                capable.Registration, capable.TypeName);
        }

        var best = ordered[0];
        var problem = AssessRoute(departure, arrival, best.MinRunwayFt, best.MtowTonnes, out var blockingAirport);
        var message = problem == RunwaySuitabilityProblem.SoftSurface
            ? $"{blockingAirport.Icao}'s runways are too soft for anything in your fleet capable of reaching it. Your most " +
              $"runway-tolerant aircraft is {best.Registration} ({best.TypeName}), and even it can't use a grass, gravel, dirt " +
              "or water runway at its weight - a heavy aircraft needs a paved runway regardless of length."
            : $"{blockingAirport.Icao}'s longest runway is {blockingAirport.LongestRunwayFt:N0} ft, too short for anything in " +
              $"your fleet. Your most runway-tolerant aircraft is {best.Registration} ({best.TypeName}), which needs " +
              $"{best.MinRunwayFt:N0} ft - add a shorter-field aircraft from the Fleet page to fly this route.";

        return new RunwaySuitabilityAssessment(RunwaySuitabilityVerdict.BeyondFleet, Blocking: true, message, best.Registration, best.TypeName);
    }
}

/// <summary>
/// Classifies a runway's freeform OurAirports "surface" string as soft (grass, gravel, dirt, or
/// water) or not. The source data is inconsistent free text ("TURF", "GRS", "ASP-GRS", "Grassed
/// brown clay", "UNK", a bare "G", ...), so this tokenises on anything that isn't a letter and
/// matches whole tokens against small sets of known soft- and hard-surface words rather than a raw
/// substring search - "ASPH" must never match because it happens to contain letters found elsewhere.
/// <para>
/// <b>Permissive by default, in both directions this can go wrong.</b> This exists to block a heavy
/// aircraft from ground that is genuinely soft throughout - not to second-guess every oddly-recorded
/// row in a public-domain dataset. Two situations get the same treatment for the same reason: the
/// cost of guessing wrong is identical either way (a block the player has no recourse from and no
/// explanation that makes sense to them), so both resolve to "usable" rather than "soft":
/// </para>
/// <list type="bullet">
/// <item>Unknown or ambiguous codes (blank, "UNK", a bare letter, "PEM", "MATS", ...) - nothing here
/// names a soft surface at all, so there is nothing to block on.</item>
/// <item>A composite code that names a recognisable HARD surface alongside anything else (e.g.
/// "ASP-GRS", "CONC-TURF") - most likely an asphalt/concrete runway with a grass verge, or a
/// part-and-part strip, not ground a heavy aircraft cannot use. There is no reliable way to tell
/// from the code alone that the paved portion isn't what a player would actually use, so a hard
/// token anywhere in the code wins over a soft one, never the other way round.</item>
/// </list>
/// <para>
/// Only a code that names a soft surface and NOTHING recognisably hard ("TURF", "GRASS / SOD",
/// "Turf/Dirt", "GRVL-G" - the ambiguous trailing "G" isn't a hard token) is actually soft. This is
/// the airport-level check's only source of a SoftSurface verdict; <see cref="RunwaySuitabilityAssessor.AssessAirport"/>
/// still finds an airport usable if ANY of its runways clears length and (for a heavy aircraft)
/// isn't soft here - a single soft-classified runway can never veto an airport that also has a good
/// hard one.
/// </para>
/// </summary>
public static class RunwaySurfaceClassifier
{
    private static readonly char[] TokenSeparators = ['-', '/', ' ', ',', '_'];

    private static readonly HashSet<string> SoftTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        // Grass
        "GRASS", "GRASSED", "GRS", "TURF", "SOD",
        // Gravel
        "GRAVEL", "GVL", "GRVL", "GRV", "GRE",
        // Dirt / earth
        "DIRT", "EARTH", "CLAY", "MUD",
        // Water
        "WATER", "WAT",
    };

    /// <summary>Recognisable hard/paved tokens - present anywhere in a composite code, they make the
    /// whole code "usable" rather than soft (see this class's own doc for why).</summary>
    private static readonly HashSet<string> HardTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "ASP", "ASPH", "ASPHALT", "CON", "CONC", "CONCRETE", "BIT", "BITUMEN", "TAR", "TARMAC",
        "MAC", "MACADAM", "PEM", "PAVED",
    };

    public static bool IsSoft(string? surface)
    {
        if (string.IsNullOrWhiteSpace(surface))
        {
            return false;
        }

        var tokens = surface.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries);

        // A hard token anywhere wins outright - checked before the soft loop below, not after, so a
        // composite like "ASP-GRS" never even reaches a soft match.
        foreach (var hardCheck in tokens)
        {
            if (HardTokens.Contains(hardCheck))
            {
                return false;
            }
        }

        foreach (var token in tokens)
        {
            if (SoftTokens.Contains(token))
            {
                return true;
            }
        }

        return false;
    }
}
