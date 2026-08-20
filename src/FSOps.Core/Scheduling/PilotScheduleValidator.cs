using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Planning;

namespace FSOps.Core.Scheduling;

/// <summary>
/// One proposed or existing leg in a weekly schedule, in the shape the validator needs - not tied
/// to a persisted <see cref="PilotScheduleEntry"/> row, so the caller can validate a brand-new,
/// not-yet-saved week (the whole point of PUT /pilots/{id}/schedule) the same way it validates
/// what's already in the database.
/// </summary>
public sealed record PilotScheduleEntryInput(Guid PilotId, DayOfWeek DayOfWeek, TimeSpan DepartureTimeUtc, Guid RouteId, Guid FleetAircraftId);

public sealed record ScheduleValidationResult(bool IsValid, IReadOnlyList<string> Conflicts, IReadOnlyList<ContinuityGap> ContinuityGaps)
{
    /// <summary>
    /// Things worth telling the player that are deliberately NOT reasons to refuse - a valid result
    /// can carry these, and a caller is free to ignore them entirely. Introduced for exactly one
    /// case (see <see cref="PilotScheduleValidator.ValidateAircraftChains"/>): a saved rolling
    /// pattern whose aircraft is not currently standing where the pattern starts. That used to be a
    /// hard conflict, which made a schedule permanently unsavable whenever its own aircraft was away
    /// or in the air - but saying nothing at all would be worse, because under True-life every
    /// occurrence the aircraft cannot reach is a cancellation fee. So it is said once, at save, and
    /// blocks nothing.
    /// </summary>
    public IReadOnlyList<string> Advisories { get; init; } = Array.Empty<string>();

    public static ScheduleValidationResult Ok { get; } = new(true, Array.Empty<string>(), Array.Empty<ContinuityGap>());
}

/// <summary>
/// The structured facts behind one "lands here, but the next already-drafted leg departs
/// somewhere else" conflict from <see cref="PilotScheduleValidator.ValidateAircraftChains"/> -
/// captured alongside the existing prose sentence in <see cref="ScheduleValidationResult.Conflicts"/>,
/// never instead of it, so a caller that wants to phrase this for its own context (see
/// PilotEndpoints.GetLegOptionsAsync's forward-looking warning on an otherwise-legal option) can
/// read the two airports and the next departure directly, rather than re-deriving them by
/// re-running this same chain logic a second time - two implementations of "what does this
/// schedule imply" would only drift apart. <see cref="ConflictText"/> is the exact sentence this
/// gap produced in <c>Conflicts</c>, included purely so a caller doing the "before" vs "after" set
/// difference (as GetLegOptionsAsync already does for every other conflict) can recognise and
/// exclude this specific sentence when it wants to substitute its own phrasing instead - existing
/// callers that only ever read <c>Conflicts</c> (PUT /schedule among them) are completely
/// unaffected by this record's existence.
/// </summary>
public sealed record ContinuityGap(
    string AircraftRegistration,
    // Where the aircraft is left once the leg that produced this gap lands.
    string GapDepartureIcao,
    // Where the next already-drafted leg on this aircraft needs it to be instead.
    string GapArrivalIcao,
    DayOfWeek NextDepartureDayOfWeek,
    TimeSpan NextDepartureTimeUtc,
    // Mirrors the same "schedule a leg" vs "you'd need to create a route" distinction the prose
    // conflict draws - see RouteExistsBetween.
    bool RouteExistsForReposition,
    string ConflictText);

/// <summary>
/// Pure validation for a weekly schedule. Every rule it enforces: one aircraft per pilot per duty
/// day (see <see cref="ValidateDutyDayAircraftConsistency"/> - the scheduler redesign's central
/// invariant),
/// geographic continuity, no double-booking/overlap of an aircraft (across pilots, not just within
/// one), minimum turnaround, pilot rest between duty days, a maximum duty day, and the
/// reserved-for-player aircraft never being assignable. No I/O, no EF - the caller (PilotEndpoints)
/// loads everything this needs and hands it in as plain data, so this is directly unit-testable
/// without a database.
/// <para>
/// <b>Whole-airline scope, not just one pilot.</b> "No double-booking an aircraft across pilots"
/// means an aircraft's chain must be validated across every pilot that touches it, not in
/// isolation - so the caller passes the union of every OTHER pilot's existing entries plus the one
/// pilot's proposed replacement set, and this groups by <see cref="PilotScheduleEntryInput.FleetAircraftId"/>
/// for the continuity/overlap checks (aircraft-scoped) and by
/// <see cref="PilotScheduleEntryInput.PilotId"/> for the rest/duty checks (pilot-scoped).
/// </para>
/// <para>
/// <b>The week is a cycle, and no day is its first.</b> The week repeats indefinitely - that is
/// what makes a standing schedule continuous - so every consecutive pair of legs on an aircraft has
/// to connect, all the way round, and a pilot's every duty day has to get its rest before the next
/// one. Both checks treat the week as a 10,080-minute cycle rather than a flat Sunday-to-Saturday
/// line, using <see cref="WeekMinutes"/>. Where the cycle is ordered FROM is
/// <see cref="WeekCycle.OriginMinute"/>'s answer, not the calendar's: ordering by .NET's
/// <see cref="DayOfWeek"/> (Sunday 0) would make a Sunday leg the pattern's first and anchor it to
/// the airframe's live position instead of to Saturday's arrival, which is exactly the defect of
/// 2026-08-20 (see that method's own remarks). Saturday to Sunday and Sunday to Monday are two
/// ordinary consecutive pairs and nothing more. <see cref="requireWeekClosure"/>
/// controls whether the closing (last -&gt; first) pair is actually checked: PUT /schedule passes
/// true, because a saved week must genuinely repeat; the options endpoint passes false, because a
/// week under construction is legitimately open - it has not been closed yet, and closing it is not
/// this leg's job. With closure off, every INTERIOR pair (each entry against the one immediately
/// after it, in departure order) is still fully checked - only the single pair that would close the
/// loop back to the first entry is skipped. A week under construction is legitimately open: that
/// is not an error, it is an unfinished week, and reporting it as a conflict would make the
/// builder unusable while a player is still filling it in (clarified 2026-08-08).
/// </para>
/// <para>
/// <b>While a week is being BUILT, the leg the aircraft ENTERS the cycle on has to depart from where
/// the aircraft actually is.</b>
/// Geographic continuity between consecutive legs (above) says nothing about where an aircraft's
/// chain is entered, so <see cref="ValidateAircraftChains"/> anchors that one entry per aircraft to
/// <see cref="FleetAircraft.LocationIcao"/>. Which entry that is comes from
/// <see cref="WeekCycle.OriginMinute"/> - the leg departing from where the airframe is standing, or,
/// failing that, the leg after the chain's own break. Every OTHER entry's starting point is wherever
/// the previous leg left the aircraft, which the ordinary pairwise continuity check already
/// guarantees - so this needs no day-by-day special-casing, only the one entry with nothing usable
/// before it.
/// </para>
/// <para>
/// <b>It is a build-time check, not a permanent property of a saved pattern</b> (user's decision,
/// 2026-08-13: "the schedule should be for the week rolling forever until it's modified"). It fires
/// as a hard conflict only when <see cref="requireWeekClosure"/> is <c>false</c> - the leg-options
/// endpoint's case, where the player is choosing a leg right now and "the aircraft isn't there" is
/// real, actionable guidance. When closure IS required - a complete rolling week being saved - the
/// wrap check has already proved the pattern is a closed cycle, and this anchor would add only "and
/// the airframe happens to be standing at the cycle's start <i>today</i>": a fact about this minute,
/// not about the pattern. Enforcing it there made a schedule permanently unsavable the moment its own
/// aircraft flew somewhere and stayed - which happens every time a virtual pilot flies - and, because
/// chains are validated across every pilot, it also blocked OTHER pilots' saves over an aircraft they
/// never touch (real-use defect, 2026-08-13). So at save it becomes an entry in
/// <see cref="ScheduleValidationResult.Advisories"/> instead: said once, blocking nothing. Nothing
/// about resolution time changes - <c>VirtualFlightResolverService</c> still refuses to fly a leg
/// whose aircraft is elsewhere, with its own stated reason, exactly as strictly as before.
/// </para>
/// <para>
/// <b>An aircraft in the air is not "at" anywhere.</b> While <see cref="FleetAircraft.Status"/> is
/// <see cref="FleetAircraftStatus.InFlight"/>, <c>LocationIcao</c> is the departure airport of a
/// sector in progress, not a position - so the anchor is skipped entirely rather than compared
/// against a value that is knowably stale.
/// </para>
/// </summary>
public static class PilotScheduleValidator
{
    public const int WeekMinutes = 7 * 24 * 60;

    public static ScheduleValidationResult Validate(
        IReadOnlyList<PilotScheduleEntryInput> entries,
        IReadOnlyDictionary<Guid, Route> routesById,
        IReadOnlyDictionary<Guid, FleetAircraft> fleetById,
        // Keyed by AircraftTypeId, covering every type the entries' aircraft belong to - what makes
        // "an A320 must never be scheduled beyond its range" checkable here rather than only being
        // discovered when the leg fails to resolve.
        IReadOnlyDictionary<Guid, AircraftType> aircraftTypesById,
        IReadOnlyDictionary<(Guid RouteId, Guid FleetAircraftId), int> blockMinutesByLeg,
        // Keyed by ICAO, covering every airport any entry's route touches - runway suitability
        // (length AND surface, see RunwaySuitabilityAssessor) is a hard refusal here for the same
        // reason range is: an aircraft rostered onto a leg it cannot physically use is not a
        // reservation problem, it is an impossible leg. An airport missing from this dictionary
        // (e.g. world data drifted under an existing route) means "say nothing" rather than a crash -
        // the same graceful-skip every other lookup in this method already uses.
        IReadOnlyDictionary<string, Airport> airportsByIcao,
        SchedulingConfig config,
        // Every route the AIRLINE actually has, departure/arrival ICAO pairs, regardless of whether
        // any entry references it - NOT the same thing as routesById above, which only covers routes
        // this particular entry set touches. This is what lets a broken chain distinguish "the
        // connecting route doesn't exist" from "it exists but nothing is scheduled on it" (see
        // user feedback from real use,
        // 2026-08-08: the validator used to say "you'd need a EGPH -> EGLL route" even when the
        // player already had one in both directions - what was actually missing was a scheduled
        // repositioning leg, not the route).
        IReadOnlyCollection<(string DepartureIcao, string ArrivalIcao)> existingRoutePairs,
        bool requireWeekClosure = true,
        // Where each aircraft's cycle is OPEN, when the caller already knows and needs every call to
        // agree - keyed by FleetAircraftId, absolute minute of week. Left null (the normal case) this
        // is derived per call by WeekCycle.OriginMinute from the entries in hand.
        //
        // GetLegOptionsAsync is why it exists. It runs this validator three times over three
        // overlapping slices - what's committed before the slot, that plus the candidate, and the
        // whole draft plus the candidate - and DIFFERENCES the results to tell a refusal from a
        // consequence. Deriving the origin separately each time lets the candidate itself move the
        // loop's opening, and then the three passes are no longer talking about the same week: a
        // Monday leg added to a week that already flew Tuesday to Friday out of the same base became
        // the earliest departure from the aircraft's own airport, so the chain was read as starting
        // there and the leg was refused for not connecting to Tuesday - a leg that is perfectly
        // legal. The opening belongs to what is already committed; the candidate is inserted into it,
        // never allowed to redefine it.
        IReadOnlyDictionary<Guid, int>? cycleOriginMinutesByAircraft = null)
    {
        var conflicts = new List<string>();

        // Structural checks first - a leg referencing something that doesn't exist, or a reserved
        // aircraft, makes every downstream geometry check meaningless, so fail fast here rather
        // than also reporting confusing follow-on conflicts for the same bad entry.
        foreach (var entry in entries)
        {
            if (!routesById.TryGetValue(entry.RouteId, out var route))
            {
                conflicts.Add("One of the scheduled legs references a route that no longer exists.");
                continue;
            }

            if (!fleetById.TryGetValue(entry.FleetAircraftId, out var aircraft))
            {
                conflicts.Add($"{route.DepartureIcao} -> {route.ArrivalIcao}: the assigned aircraft no longer exists.");
                continue;
            }

            if (aircraft.ReservedForPlayer)
            {
                // One conflict per aircraft, not one per leg - for the same reason the range check
                // below de-duplicates: a week that repeats the same reserved airframe five days
                // running is ONE problem, and printing the identical sentence five times is exactly
                // the wall of text the one-plain-reason rule exists to prevent.
                var reservedConflict =
                    $"{aircraft.Registration} is reserved for the player and cannot be assigned to a virtual pilot's schedule - release it first from the Fleet page if you want it flown by a pilot instead.";
                if (!conflicts.Contains(reservedConflict))
                {
                    conflicts.Add(reservedConflict);
                }
            }

            // Range is a hard physical refusal here, unlike on the Routes page where it is guidance:
            // a route the airline cannot fly today is still worth having, but an A320 rostered onto a
            // sector it cannot reach is simply an impossible leg. It joins the existing set of one
            // plain reason per illegal leg (wrong airport, reserved aircraft, no rest room) rather
            // than getting a mechanism of its own. Route.DistanceNm is the same great-circle figure
            // the route was created with, so this needs no airports and stays pure.
            if (aircraftTypesById.TryGetValue(aircraft.AircraftTypeId, out var aircraftType) &&
                !RouteRangeAssessor.CanReach(aircraftType.RangeNm, route.DistanceNm))
            {
                // One conflict per (route, aircraft) pair, not one per leg - a week of fourteen
                // identical legs is one problem, and repeating the same sentence fourteen times is
                // the "wall of text" the unavailable-list rule exists to prevent.
                var rangeConflict =
                    $"{route.DepartureIcao} -> {route.ArrivalIcao} is {route.DistanceNm:F0} nm, beyond {aircraft.Registration}'s " +
                    $"({aircraftType.Name}) practical range of about {RouteRangeAssessor.OperationalRangeNm(aircraftType.RangeNm):F0} nm " +
                    "once reserves are accounted for - fly this leg with a longer-legged aircraft.";
                if (!conflicts.Contains(rangeConflict))
                {
                    conflicts.Add(rangeConflict);
                }
            }

            // Runway suitability mirrors range exactly, for the same reason: length AND surface are
            // both physical facts about this specific airframe and this specific piece of ground,
            // never a type-matching penalty (see RunwaySuitabilityAssessor's class doc).
            if (aircraftTypesById.TryGetValue(aircraft.AircraftTypeId, out var aircraftTypeForRunway) &&
                airportsByIcao.TryGetValue(route.DepartureIcao, out var depAirport) &&
                airportsByIcao.TryGetValue(route.ArrivalIcao, out var arrAirport))
            {
                var runwayProblem = RunwaySuitabilityAssessor.AssessRoute(
                    depAirport, arrAirport, aircraftTypeForRunway.MinRunwayFt, aircraftTypeForRunway.MtowTonnes, out var blockingAirport);

                if (runwayProblem != RunwaySuitabilityProblem.None)
                {
                    // Same one-conflict-per-(route, aircraft) de-duplication as range above.
                    var runwayConflict = runwayProblem == RunwaySuitabilityProblem.TooShort
                        ? $"{route.DepartureIcao} -> {route.ArrivalIcao}: {blockingAirport.Icao}'s longest runway " +
                          $"({blockingAirport.LongestRunwayFt:N0} ft) is too short for {aircraft.Registration} ({aircraftTypeForRunway.Name}), " +
                          $"which needs {aircraftTypeForRunway.MinRunwayFt:N0} ft - fly this leg with a shorter-field aircraft."
                        : $"{route.DepartureIcao} -> {route.ArrivalIcao}: {blockingAirport.Icao}'s runways are too soft for " +
                          $"{aircraft.Registration} ({aircraftTypeForRunway.Name}) at this weight - heavy aircraft need a paved runway " +
                          "there regardless of length.";
                    if (!conflicts.Contains(runwayConflict))
                    {
                        conflicts.Add(runwayConflict);
                    }
                }
            }
        }

        if (conflicts.Count > 0)
        {
            return new ScheduleValidationResult(false, conflicts, Array.Empty<ContinuityGap>());
        }

        // The aircraft-per-duty-day invariant - see this method's own doc
        // for why it has to run, and fail fast, BEFORE ValidateAircraftChains: that method groups by
        // FleetAircraftId, so two legs on two DIFFERENT aircraft in one pilot's day are never
        // compared to each other at all - each aircraft's own chain can look perfectly valid in
        // isolation while the pilot's day is geographically impossible. This is exactly the
        // 2026-08-09 defect shape (two same-origin legs, one duty day, two different airframes, a
        // meaningless "turnaround" rendered between them). Enforcing "one aircraft per duty day"
        // here is what makes continuity hold by construction rather than by hoping every caller
        // behaves - see ValidateDutyDayAircraftConsistency's own doc.
        ValidateDutyDayAircraftConsistency(entries, fleetById, conflicts);
        if (conflicts.Count > 0)
        {
            return new ScheduleValidationResult(false, conflicts, Array.Empty<ContinuityGap>());
        }

        var continuityGaps = new List<ContinuityGap>();
        var advisories = new List<string>();
        ValidateAircraftChains(entries, routesById, fleetById, blockMinutesByLeg, config, existingRoutePairs, conflicts, continuityGaps, advisories, requireWeekClosure, cycleOriginMinutesByAircraft);
        ValidatePilotDutyAndRest(entries, blockMinutesByLeg, config, conflicts, requireWeekClosure);

        // Advisories ride along with a valid result too - they are not refusals, so they must never
        // change IsValid (see ScheduleValidationResult.Advisories' own doc).
        return conflicts.Count == 0
            ? ScheduleValidationResult.Ok with { Advisories = advisories }
            : new ScheduleValidationResult(false, conflicts, continuityGaps) { Advisories = advisories };
    }

    /// <summary>
    /// Structural invariant from the aircraft-per-duty-day redesign: an aircraft is assigned per
    /// pilot per DUTY DAY, not per leg. With one airframe fixed for the day, "does the next leg
    /// depart where the last one arrived" becomes a single check against one aircraft's position,
    /// and a turnaround gap always means what it says. Per-leg aircraft selection is what made
    /// continuity checkable-but-easily-skipped, and it let a real defect through. Every entry belonging to the same
    /// (<see cref="PilotScheduleEntryInput.PilotId"/>, <see cref="PilotScheduleEntryInput.DayOfWeek"/>)
    /// pair must reference the same <see cref="PilotScheduleEntryInput.FleetAircraftId"/> - the
    /// player picks the aircraft for a duty day once, then drops legs into it, so the API layer
    /// should never be able to construct anything else. This check exists as the pure validator's own
    /// defence regardless: it is what actually closes the 2026-08-09 defect (two EGPH -&gt; EGLL legs
    /// in one duty day, one on G-PKS0 and one on G-LHRE, each individually unremarkable to
    /// <see cref="ValidateAircraftChains"/> because it groups by aircraft and never saw both legs in
    /// the same group) - without this, that shape passes cleanly no matter how the redesigned API
    /// happens to be wired.
    /// </summary>
    private static void ValidateDutyDayAircraftConsistency(
        IReadOnlyList<PilotScheduleEntryInput> entries,
        IReadOnlyDictionary<Guid, FleetAircraft> fleetById,
        List<string> conflicts)
    {
        foreach (var dutyDay in entries.GroupBy(e => (e.PilotId, e.DayOfWeek)))
        {
            var aircraftIds = dutyDay.Select(e => e.FleetAircraftId).Distinct().ToList();
            if (aircraftIds.Count <= 1)
            {
                continue;
            }

            var registrations = aircraftIds
                .Select(id => fleetById.TryGetValue(id, out var aircraft) ? aircraft.Registration : "an unknown aircraft")
                .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
                .ToList();

            conflicts.Add(
                $"{dutyDay.Key.DayOfWeek} has legs assigned to {string.Join(" and ", registrations)} - a duty day flies a single " +
                "aircraft throughout. Pick one aircraft for this day, or move the extra legs to another day.");
        }
    }

    /// <summary>Geographic continuity, overlap/double-booking, and minimum turnaround - all scoped
    /// per aircraft, across every pilot that touches it.</summary>
    private static void ValidateAircraftChains(
        IReadOnlyList<PilotScheduleEntryInput> entries,
        IReadOnlyDictionary<Guid, Route> routesById,
        IReadOnlyDictionary<Guid, FleetAircraft> fleetById,
        IReadOnlyDictionary<(Guid RouteId, Guid FleetAircraftId), int> blockMinutesByLeg,
        SchedulingConfig config,
        IReadOnlyCollection<(string DepartureIcao, string ArrivalIcao)> existingRoutePairs,
        List<string> conflicts,
        List<ContinuityGap> continuityGaps,
        List<string> advisories,
        bool requireWeekClosure,
        IReadOnlyDictionary<Guid, int>? cycleOriginMinutesByAircraft)
    {
        foreach (var group in entries.GroupBy(e => e.FleetAircraftId))
        {
            var aircraft = fleetById[group.Key];

            // Where this airframe joins its own loop - see WeekCycle.OriginMinute. Ordering from
            // there, rather than from Sunday midnight, is what makes Saturday -> Sunday an ordinary
            // interior pair instead of the one pair a week under construction skips. An aircraft
            // mid-sector has no knowable position, so it gets no location rule (see this class's own
            // remarks). A caller that needs several passes to agree supplies the opening instead of
            // letting each pass find its own - see Validate's own parameter doc.
            var origin = cycleOriginMinutesByAircraft is not null && cycleOriginMinutesByAircraft.TryGetValue(group.Key, out var suppliedOrigin)
                ? suppliedOrigin
                : WeekCycle.OriginMinute(
                    aircraft.Status == FleetAircraftStatus.InFlight ? null : aircraft.LocationIcao,
                    group
                        .Select(e => new CycleLeg(e.DayOfWeek, e.DepartureTimeUtc, routesById[e.RouteId].DepartureIcao, routesById[e.RouteId].ArrivalIcao))
                        .ToList());

            var ordered = group
                .Select(e => new
                {
                    Entry = e,
                    // Minutes round the cycle from the origin, NOT a minute-of-week - so the gap
                    // arithmetic below stays monotonic no matter which day the loop is entered on.
                    Departure = WeekCycle.MinutesFrom(origin, AbsoluteWeekMinute(e.DayOfWeek, e.DepartureTimeUtc)),
                    Block = blockMinutesByLeg.TryGetValue((e.RouteId, e.FleetAircraftId), out var minutes) ? minutes : 0,
                })
                .OrderBy(x => x.Departure)
                .ToList();

            // The entry this aircraft ENTERS the cycle on - ordered[0], by the rotation above - is
            // anchored to where the aircraft actually is. Not a second mechanism, just the same
            // continuity rule this loop already enforces between every other consecutive pair,
            // anchored once at the one entry with nothing usable before it. Every other entry's
            // starting point is wherever the previous leg left the aircraft, which the pairwise
            // checks below already guarantee.
            //
            // Whether that anchor REFUSES or merely informs depends on what is being asked, and
            // this class's own doc explains why at length: building a week (requireWeekClosure
            // false) it is a hard refusal, because the player is picking a leg right now and can
            // act on it; saving a complete rolling week (closure true) it is an advisory, because
            // a pattern that repeats forever must not be governed by where the airframe happens to
            // be standing this minute. An aircraft mid-sector is skipped altogether - its recorded
            // location is knowably stale rather than wrong.
            if (aircraft.Status != FleetAircraftStatus.InFlight)
            {
                if (requireWeekClosure)
                {
                    // What this SAYS - and whether there is anything to say at all - is
                    // ScheduleStallDetector's decision, not this method's, and deliberately so.
                    // Until 2026-08-14 the advisory here promised the pattern would start "as soon
                    // as {aircraft} is back at {where the week starts}" - which is simply untrue
                    // whenever the airframe is standing at some other airport the week already
                    // visits: that airport's own leg flies, and the closed loop lines up from there
                    // without the aircraft ever returning to the start. The detector has always
                    // reasoned about this correctly for the Pilots page, and a player can see both
                    // messages within a minute of each other, so this one comes from the same place
                    // rather than being a second copy free to drift. It is asked unconditionally
                    // (it returns null when there is nothing worth saying) rather than only when the
                    // rotated anchor happens to mismatch: the rotation deliberately picks a leg
                    // departing from where the airframe is whenever one exists, so gating on that
                    // would silence exactly the "it is not stuck, this is where it picks up" case
                    // the detector was written to explain.
                    var advisory = ScheduleStallDetector.DescribeSavedPattern(
                        aircraft,
                        ordered
                            .Select(x => new ScheduledLegPosition(
                                x.Entry.FleetAircraftId,
                                x.Entry.DayOfWeek,
                                x.Entry.DepartureTimeUtc,
                                routesById[x.Entry.RouteId].DepartureIcao))
                            .ToList());

                    if (advisory is not null)
                    {
                        advisories.Add(advisory);
                    }
                }
                else
                {
                    var firstRoute = routesById[ordered[0].Entry.RouteId];
                    if (!string.Equals(firstRoute.DepartureIcao, aircraft.LocationIcao, StringComparison.OrdinalIgnoreCase))
                    {
                        var slot = FormatSlot(ordered[0].Entry.DayOfWeek, ordered[0].Entry.DepartureTimeUtc);
                        conflicts.Add(
                            $"{aircraft.Registration} is at {aircraft.LocationIcao}, but this pattern's first leg departs " +
                            $"{firstRoute.DepartureIcao} ({slot}) - " +
                            "the first leg of the week has to depart from where the aircraft actually is.");
                    }
                }
            }

            for (var i = 0; i < ordered.Count; i++)
            {
                var current = ordered[i];
                var isWrap = i == ordered.Count - 1;

                // The closing pair (last entry back to the first) is a whole-week property - skip
                // it when the caller is only asking "does this fit with what's built so far" (see
                // this class's own doc for why). Every interior pair - each entry against the very
                // next one in departure order - is still fully checked either way.
                if (isWrap && !requireWeekClosure)
                {
                    continue;
                }

                var next = ordered[(i + 1) % ordered.Count];

                var currentRoute = routesById[current.Entry.RouteId];
                var nextRoute = routesById[next.Entry.RouteId];

                if (!string.Equals(currentRoute.ArrivalIcao, nextRoute.DepartureIcao, StringComparison.OrdinalIgnoreCase))
                {
                    // The wrap case is the week repeating, so word it as such - slotting "the
                    // following week" into "its {when} leg" produced "but its the following week
                    // leg departs", which is the sort of thing a player reads twice and trusts less.
                    // Deliberately not "its first leg next week" any more: which pair closes the loop
                    // depends on where the aircraft enters it (see WeekCycle.OriginMinute), so the
                    // closing pair is often not a Monday, and calling a Thursday leg "next week's
                    // first" is a sentence the player cannot reconcile with their own grid.
                    var nextLeg = isWrap ? "its next leg when this pattern comes round again" : "its next leg";
                    var gapDeparture = currentRoute.ArrivalIcao;
                    var gapArrival = nextRoute.DepartureIcao;

                    // Two genuinely different problems, and telling them apart is the whole point of
                    // this check (see this method's own doc): if the route itself doesn't exist yet,
                    // send the player to create it; if it already exists, the gap is a scheduling
                    // gap, not a routing gap, and sending them to create a route they already have
                    // solves nothing.
                    var routeExists = RouteExistsBetween(existingRoutePairs, gapDeparture, gapArrival);
                    var fix = routeExists
                        ? $"schedule a {gapDeparture} -> {gapArrival} leg before this one to reposition it"
                        : $"you'd need to create a {gapDeparture} -> {gapArrival} route on the Routes page for this chain to work";

                    // The time in "lands at X (...)" has to be the leg's ARRIVAL, not its departure -
                    // quoting the departure time made the sentence say an aircraft leaving Monday
                    // 08:00 also lands Monday 08:00, which reads as a broken clock next to the
                    // double-booking message below that already prints the real arrival.
                    var conflictText =
                        $"{aircraft.Registration} lands at {currentRoute.ArrivalIcao} ({FormatArrival(current.Entry.DayOfWeek, current.Entry.DepartureTimeUtc, current.Block)}) " +
                        $"but {nextLeg} departs {nextRoute.DepartureIcao} ({FormatSlot(next.Entry.DayOfWeek, next.Entry.DepartureTimeUtc)}) - {fix}.";
                    conflicts.Add(conflictText);
                    continuityGaps.Add(new ContinuityGap(
                        aircraft.Registration,
                        gapDeparture,
                        gapArrival,
                        next.Entry.DayOfWeek,
                        next.Entry.DepartureTimeUtc,
                        routeExists,
                        conflictText));
                }

                var currentArrival = current.Departure + current.Block;
                var nextDeparture = isWrap ? next.Departure + WeekMinutes : next.Departure;
                var gapMinutes = nextDeparture - currentArrival;

                if (gapMinutes < 0)
                {
                    conflicts.Add(
                        $"{aircraft.Registration} is double-booked: the leg landing at {FormatArrival(current.Entry.DayOfWeek, current.Entry.DepartureTimeUtc, current.Block)} " +
                        $"overlaps the one departing {FormatSlot(next.Entry.DayOfWeek, next.Entry.DepartureTimeUtc)}.");
                }
                else if (gapMinutes < config.MinTurnaroundMinutes)
                {
                    conflicts.Add(
                        $"{aircraft.Registration} only has {gapMinutes} minutes on the ground before its next departure " +
                        $"({FormatSlot(next.Entry.DayOfWeek, next.Entry.DepartureTimeUtc)}) - needs at least {config.MinTurnaroundMinutes:0} for turnaround.");
                }
            }
        }
    }

    /// <summary>Maximum duty length per day and minimum rest between duty days - scoped per pilot,
    /// cyclically across the week.</summary>
    private static void ValidatePilotDutyAndRest(
        IReadOnlyList<PilotScheduleEntryInput> entries,
        IReadOnlyDictionary<(Guid RouteId, Guid FleetAircraftId), int> blockMinutesByLeg,
        SchedulingConfig config,
        List<string> conflicts,
        bool requireWeekClosure)
    {
        foreach (var pilotGroup in entries.GroupBy(e => e.PilotId))
        {
            var byDay = pilotGroup
                .GroupBy(e => e.DayOfWeek)
                .ToDictionary(g => g.Key, g => g.OrderBy(e => e.DepartureTimeUtc).ToList());

            if (byDay.Count == 0)
            {
                continue;
            }

            var dutyEndByDay = new Dictionary<DayOfWeek, int>();
            foreach (var (day, dayEntries) in byDay)
            {
                var first = dayEntries[0];
                var last = dayEntries[^1];
                var lastBlock = blockMinutesByLeg.TryGetValue((last.RouteId, last.FleetAircraftId), out var minutes) ? minutes : 0;

                var dutyStart = AbsoluteWeekMinute(first.DayOfWeek, first.DepartureTimeUtc);
                var dutyEnd = AbsoluteWeekMinute(last.DayOfWeek, last.DepartureTimeUtc) + lastBlock;
                dutyEndByDay[day] = dutyEnd;

                var dutyMinutes = dutyEnd - dutyStart;
                if (dutyMinutes > config.MaxDutyHoursPerDay * 60)
                {
                    conflicts.Add($"Duty on {day} runs {dutyMinutes / 60.0:0.#} hours, above the {config.MaxDutyHoursPerDay:0.#}-hour maximum duty day.");
                }
            }

            // Which flown day follows which is a property of the cycle, so it does not depend on
            // where the week is ordered from - Saturday is followed by Sunday whichever day sorts
            // first. Every consecutive pair is measured here; which single one is EXEMPT while a
            // week is under construction is decided below.
            var flownDays = byDay.Keys.OrderBy(d => (int)d).ToList();
            var rests = new List<(DayOfWeek Day, DayOfWeek NextDay, int RestMinutes)>();
            for (var i = 0; i < flownDays.Count; i++)
            {
                var day = flownDays[i];
                var isWrap = i == flownDays.Count - 1;
                var nextDay = flownDays[(i + 1) % flownDays.Count];

                var dutyEnd = dutyEndByDay[day];
                var nextFirst = byDay[nextDay][0];
                var nextStart = AbsoluteWeekMinute(nextDay, nextFirst.DepartureTimeUtc) + (isWrap ? WeekMinutes : 0);

                rests.Add((day, nextDay, nextStart - dutyEnd));
            }

            // Same closure exemption as ValidateAircraftChains: a week under construction is open
            // somewhere, and the join across that opening is not this leg's to close. What changed
            // on 2026-08-20 is WHICH join gets the exemption. It used to be whichever pair happened
            // to close the Sunday-first ordering - so a player with legs on both Saturday and Sunday
            // never had that pair's rest checked while building, and could be offered a leg the save
            // would then refuse. In a cycle there is no such thing as the last pair, so the exemption
            // goes to the LONGEST rest instead: that is the join most plausibly still unbuilt, and it
            // is the only choice that cannot hide a genuine problem - if the longest gap is under the
            // minimum then every other gap is too, and they are all still reported.
            var exemptIndex = -1;
            if (!requireWeekClosure && rests.Count > 0)
            {
                var longest = rests.Max(r => r.RestMinutes);
                exemptIndex = rests.FindIndex(r => r.RestMinutes == longest);
            }

            for (var i = 0; i < rests.Count; i++)
            {
                if (i == exemptIndex)
                {
                    continue;
                }

                var (day, nextDay, restMinutes) = rests[i];
                if (restMinutes < config.MinRestHoursBetweenDutyDays * 60)
                {
                    conflicts.Add(
                        $"Only {restMinutes / 60.0:0.#} hours' rest between duty on {day} and {nextDay} - " +
                        $"needs at least {config.MinRestHoursBetweenDutyDays:0.#} hours.");
                }
            }
        }
    }

    /// <summary>Whether the airline already has an (active) route flying exactly this departure ->
    /// arrival pair, case-insensitively - the distinction the whole "create a route" vs "schedule a
    /// leg" wording turns on.</summary>
    private static bool RouteExistsBetween(
        IReadOnlyCollection<(string DepartureIcao, string ArrivalIcao)> existingRoutePairs,
        string departureIcao,
        string arrivalIcao)
    {
        foreach (var pair in existingRoutePairs)
        {
            if (string.Equals(pair.DepartureIcao, departureIcao, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(pair.ArrivalIcao, arrivalIcao, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int AbsoluteWeekMinute(DayOfWeek dayOfWeek, TimeSpan timeOfDay) => (int)dayOfWeek * 1440 + (int)timeOfDay.TotalMinutes;

    private static string FormatSlot(DayOfWeek day, TimeSpan time) => $"{day} {time:hh\\:mm}";

    private static string FormatArrival(DayOfWeek departureDay, TimeSpan departureTime, int blockMinutes)
    {
        var arrivalAbsolute = AbsoluteWeekMinute(departureDay, departureTime) + blockMinutes;
        var arrivalDay = (DayOfWeek)((arrivalAbsolute / 1440) % 7);
        var arrivalTime = TimeSpan.FromMinutes(arrivalAbsolute % 1440);
        return FormatSlot(arrivalDay, arrivalTime);
    }
}
