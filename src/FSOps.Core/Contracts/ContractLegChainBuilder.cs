using FSOps.Core.Entities;
using FSOps.Core.Planning;

namespace FSOps.Core.Contracts;

/// <summary>
/// One airport as far as contract generation is concerned. Deliberately not
/// <see cref="Airport"/>: this keeps the generator a pure function over plain data the caller has
/// already loaded, so the whole of contract generation is unit-testable with exact expected values
/// and no database anywhere near it.
/// </summary>
public sealed record ContractAirport(
    string Icao,
    string Name,
    string Municipality,
    string Country,
    double Latitude,
    double Longitude,
    int LongestRunwayFt,
    AirportSizeCategory SizeCategory);

/// <summary>One planned sector of a contract, before it becomes a <see cref="ContractLeg"/> row.</summary>
public sealed record ContractLegPlan(
    int Sequence,
    ContractAirport Departure,
    ContractAirport Arrival,
    double DistanceNm,
    int PlannedBlockMinutes);

/// <summary>
/// Turns "get this aeroplane from here to there" into the actual chain of sectors that does it,
/// given what the aeroplane can physically manage.
///
/// <para><b>This is where the most distinctive thing in the app comes from, and it is not
/// special-cased.</b> Nothing here knows what a ferry is or that the North Atlantic exists. It walks
/// from the origin towards the destination in hops no longer than the aircraft's operational range,
/// through whatever airports happen to be there. Ask it to take a Cessna 172 from Bristol to New
/// York and it produces Wick, Reykjavik, Narsarsuaq, Goose Bay by itself - because at 640 nm of
/// published range those genuinely are the only places to stop. The expedition emerges from the
/// arithmetic. Ask it for the same city pair in an A320 and it produces one leg, for the same
/// reason.</para>
///
/// <para><b>Every leg it returns is within range, always.</b> That is the invariant the whole
/// feature rests on - a job on the board is always flyable by the aircraft it names - and it is
/// structural here: a hop is only ever added by code that has already checked it, and a chain that
/// cannot be completed within range returns null rather than a chain with one impossible leg in it.
/// </para>
///
/// <para><b>Why a bounded search rather than plain greedy.</b> Always jumping to whichever reachable
/// airport is nearest the destination is the obvious rule and it strands itself: the greedy choice
/// is often the far corner of an island with nothing within range beyond it. So this explores in
/// greedy order but backtracks when a branch dead-ends, under a fixed expansion budget so a bad city
/// pair costs a bounded amount of work instead of exploring the world. Deterministic throughout -
/// candidates are ordered by remaining distance and then by ICAO, never by list order or by any
/// clock - so the same request always yields the same chain.</para>
/// </summary>
public static class ContractLegChainBuilder
{
    /// <summary>
    /// How many airports the search may expand before giving up. Reached only by genuinely awkward
    /// pairs (a short-legged aircraft asked to cross an ocean with no island chain), and giving up is
    /// a normal, safe answer: the generator simply does not offer that contract.
    /// </summary>
    private const int ExpansionBudget = 400;

    /// <summary>
    /// How many candidate onward stops to consider from any single airport. Keeps a dense region
    /// (Europe) from exploding the search while leaving a sparse one (the North Atlantic) unaffected,
    /// because a sparse region never has this many to begin with.
    /// </summary>
    private const int BranchingFactor = 6;

    /// <summary>
    /// A hop must close at least this fraction of the remaining distance to count as progress.
    /// Without it the search will happily hop between two airports a few miles apart, both nominally
    /// "closer", and burn its whole budget going nowhere.
    /// </summary>
    private const double MinimumProgressFraction = 0.02;

    /// <summary>
    /// The most legs a contract may have. Beyond this a job stops being an expedition and becomes a
    /// chore, and it is also a sign the aircraft is wrong for the journey rather than heroically
    /// suited to it - so the honest answer is not to offer that contract at all.
    ///
    /// <para><b>14 is derived rather than picked.</b> The flagship journey this feature exists for -
    /// a Cessna 172 from Bristol to New York - takes <b>eleven</b> legs through the airports the
    /// bundled world data actually has: Wick, Vagar, Reykjavik, Kulusuk, Narsarsuaq, Nuuk, Iqaluit,
    /// Kuujjuaq, Goose Bay, Halifax. Note that it does <i>not</i> go Narsarsuaq to Goose Bay direct,
    /// the way the shorthand version of this route is usually told: that hop is 675 nm and a 172
    /// reaches 544, so the chain runs up Greenland's west coast and across Baffin Island instead -
    /// which is the route light aircraft have genuinely used. A cap of 12 would have left the app's
    /// single most distinctive job one leg from being refused, and anything slightly shorter-legged
    /// than a 172 refused outright. 14 leaves real headroom above the hardest journey the feature is
    /// meant to produce.</para>
    /// </summary>
    public const int MaxLegs = 14;

    /// <summary>
    /// Builds the chain, or returns null if this aircraft cannot make the journey through the
    /// airports supplied. Null is an ordinary outcome and never an error.
    /// </summary>
    /// <param name="origin">Where the aircraft starts. Always the first leg's departure.</param>
    /// <param name="destination">Where it must end up. Always the last leg's arrival.</param>
    /// <param name="operationalRangeNm">
    /// The DERATED range - what the aircraft will actually plan to, not its catalogue figure. Callers
    /// pass <see cref="RouteRangeAssessor.OperationalRangeNm"/> so contracts and routes agree about
    /// what "can reach" means; a contract generated against the raw figure would be offering sectors
    /// the app's own route planner would refuse.
    /// </param>
    /// <param name="minRunwayFt">The shortest runway this aircraft may use. Intermediate stops are filtered on it.</param>
    /// <param name="cruiseTasKts">Used to estimate each leg's block time, via the same estimator routes use.</param>
    /// <param name="candidates">
    /// Airports available as intermediate stops. The origin and destination need not be in it and are
    /// never chosen from it.
    /// </param>
    public static IReadOnlyList<ContractLegPlan>? Build(
        ContractAirport origin,
        ContractAirport destination,
        double operationalRangeNm,
        int minRunwayFt,
        int cruiseTasKts,
        IReadOnlyList<ContractAirport> candidates)
    {
        if (operationalRangeNm <= 0 || string.Equals(origin.Icao, destination.Icao, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var usable = candidates
            .Where(a => a.LongestRunwayFt >= minRunwayFt)
            .Where(a => !string.Equals(a.Icao, origin.Icao, StringComparison.OrdinalIgnoreCase))
            .Where(a => !string.Equals(a.Icao, destination.Icao, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { origin.Icao };
        var path = new List<ContractAirport>();
        var budget = ExpansionBudget;

        if (!Walk(origin, destination, operationalRangeNm, usable, visited, path, ref budget))
        {
            return null;
        }

        var stops = new List<ContractAirport> { origin };
        stops.AddRange(path);
        stops.Add(destination);

        var legs = new List<ContractLegPlan>(stops.Count - 1);
        for (var i = 0; i < stops.Count - 1; i++)
        {
            var from = stops[i];
            var to = stops[i + 1];
            var distanceNm = GreatCircle.DistanceNm(from.Latitude, from.Longitude, to.Latitude, to.Longitude);

            // Belt and braces on the invariant this whole class exists to hold. Every hop was checked
            // before it was added, so this can only fire if somebody changes the walk and forgets -
            // which is precisely when a silent out-of-range leg would reach a player.
            if (distanceNm > operationalRangeNm)
            {
                return null;
            }

            legs.Add(new ContractLegPlan(
                i + 1, from, to, distanceNm, BlockTimeEstimator.Estimate(distanceNm, cruiseTasKts).TotalMinutes));
        }

        return legs;
    }

    /// <summary>
    /// Depth-first from <paramref name="current"/> towards the destination, greedy-ordered and
    /// backtracking. On success <paramref name="path"/> holds the intermediate stops in order,
    /// excluding both endpoints.
    /// </summary>
    private static bool Walk(
        ContractAirport current,
        ContractAirport destination,
        double rangeNm,
        IReadOnlyList<ContractAirport> candidates,
        HashSet<string> visited,
        List<ContractAirport> path,
        ref int budget)
    {
        var remainingNm = GreatCircle.DistanceNm(current.Latitude, current.Longitude, destination.Latitude, destination.Longitude);
        if (remainingNm <= rangeNm)
        {
            return true;
        }

        // path.Count intermediate stops means path.Count + 1 legs flown so far, and at least one more
        // to come. Stop before building something nobody wants to fly.
        if (path.Count + 2 > MaxLegs || budget <= 0)
        {
            return false;
        }

        var onward = candidates
            .Where(a => !visited.Contains(a.Icao))
            .Select(a => new
            {
                Airport = a,
                HopNm = GreatCircle.DistanceNm(current.Latitude, current.Longitude, a.Latitude, a.Longitude),
                RemainingNm = GreatCircle.DistanceNm(a.Latitude, a.Longitude, destination.Latitude, destination.Longitude),
            })
            .Where(x => x.HopNm <= rangeNm)
            .Where(x => x.RemainingNm <= remainingNm * (1.0 - MinimumProgressFraction))
            // Greedy order - closest to the destination first - then ICAO so ties never depend on the
            // order the caller happened to load airports in.
            .OrderBy(x => x.RemainingNm)
            .ThenBy(x => x.Airport.Icao, StringComparer.Ordinal)
            .Take(BranchingFactor)
            .ToList();

        foreach (var next in onward)
        {
            if (budget <= 0)
            {
                return false;
            }

            budget--;
            visited.Add(next.Airport.Icao);
            path.Add(next.Airport);

            if (Walk(next.Airport, destination, rangeNm, candidates, visited, path, ref budget))
            {
                return true;
            }

            path.RemoveAt(path.Count - 1);
            visited.Remove(next.Airport.Icao);
        }

        return false;
    }
}
