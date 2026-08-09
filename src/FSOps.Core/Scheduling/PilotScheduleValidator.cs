using FSOps.Core.Economy;
using FSOps.Core.Entities;

namespace FSOps.Core.Scheduling;

/// <summary>
/// One proposed or existing leg in a weekly schedule, in the shape the validator needs - not tied
/// to a persisted <see cref="PilotScheduleEntry"/> row, so the caller can validate a brand-new,
/// not-yet-saved week (the whole point of PUT /pilots/{id}/schedule) the same way it validates
/// what's already in the database.
/// </summary>
public sealed record PilotScheduleEntryInput(Guid PilotId, DayOfWeek DayOfWeek, TimeSpan DepartureTimeUtc, Guid RouteId, Guid FleetAircraftId);

public sealed record ScheduleValidationResult(bool IsValid, IReadOnlyList<string> Conflicts)
{
    public static ScheduleValidationResult Ok { get; } = new(true, Array.Empty<string>());
}

/// <summary>
/// Pure validation for a weekly schedule - see docs/PLAN.md "Virtual pilot scheduling" for every
/// rule this enforces: one aircraft per pilot per duty day (see
/// <see cref="ValidateDutyDayAircraftConsistency"/> - the 2a/2c redesign's central invariant),
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
/// <b>The week wraps - but only when the caller asks for that.</b> "The week repeats indefinitely"
/// (docs/PLAN.md) means Saturday's last leg on an aircraft must connect back to Sunday's first leg
/// on that same aircraft, and a pilot's last duty day must get its rest before its first duty day
/// comes back around - both checks treat the week as a 10,080-minute cycle rather than a flat
/// Sunday-to-Saturday line, using <see cref="WeekMinutes"/>. <see cref="requireWeekClosure"/>
/// controls whether the closing (last -&gt; first) pair is actually checked: PUT /schedule passes
/// true, because a saved week must genuinely repeat; the options endpoint passes false, because a
/// week under construction is legitimately open - it has not been closed yet, and closing it is not
/// this leg's job. With closure off, every INTERIOR pair (each entry against the one immediately
/// after it, in departure order) is still fully checked - only the single pair that would close the
/// loop back to the first entry is skipped. See docs/PLAN.md "a week under construction is
/// legitimately open - that is not an error, it is an unfinished week" (2026-08-08 clarification).
/// </para>
/// </summary>
public static class PilotScheduleValidator
{
    public const int WeekMinutes = 7 * 24 * 60;

    public static ScheduleValidationResult Validate(
        IReadOnlyList<PilotScheduleEntryInput> entries,
        IReadOnlyDictionary<Guid, Route> routesById,
        IReadOnlyDictionary<Guid, FleetAircraft> fleetById,
        IReadOnlyDictionary<(Guid RouteId, Guid FleetAircraftId), int> blockMinutesByLeg,
        SchedulingConfig config,
        // Every route the AIRLINE actually has, departure/arrival ICAO pairs, regardless of whether
        // any entry references it - NOT the same thing as routesById above, which only covers routes
        // this particular entry set touches. This is what lets a broken chain distinguish "the
        // connecting route doesn't exist" from "it exists but nothing is scheduled on it" (see
        // docs/PLAN.md "2b. The unavailable list must not be a wall of text", user feedback
        // 2026-08-08: the validator used to say "you'd need a EGPH -> EGLL route" even when the
        // player already had one in both directions - what was actually missing was a scheduled
        // repositioning leg, not the route).
        IReadOnlyCollection<(string DepartureIcao, string ArrivalIcao)> existingRoutePairs,
        bool requireWeekClosure = true)
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
                conflicts.Add($"{aircraft.Registration} is reserved for the player and cannot be assigned to a virtual pilot's schedule - release it first from the Fleet page if you want it flown by a pilot instead.");
            }
        }

        if (conflicts.Count > 0)
        {
            return new ScheduleValidationResult(false, conflicts);
        }

        // The aircraft-per-duty-day invariant (docs/PLAN.md "2a"/"2c") - see this method's own doc
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
            return new ScheduleValidationResult(false, conflicts);
        }

        ValidateAircraftChains(entries, routesById, fleetById, blockMinutesByLeg, config, existingRoutePairs, conflicts, requireWeekClosure);
        ValidatePilotDutyAndRest(entries, blockMinutesByLeg, config, conflicts, requireWeekClosure);

        return conflicts.Count == 0 ? ScheduleValidationResult.Ok : new ScheduleValidationResult(false, conflicts);
    }

    /// <summary>
    /// Structural invariant from the aircraft-per-duty-day redesign (docs/PLAN.md "2a": "assign an
    /// aircraft per pilot per DUTY DAY, not per leg"). Every entry belonging to the same
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
        bool requireWeekClosure)
    {
        foreach (var group in entries.GroupBy(e => e.FleetAircraftId))
        {
            var aircraft = fleetById[group.Key];
            var ordered = group
                .Select(e => new
                {
                    Entry = e,
                    Departure = AbsoluteWeekMinute(e.DayOfWeek, e.DepartureTimeUtc),
                    Block = blockMinutesByLeg.TryGetValue((e.RouteId, e.FleetAircraftId), out var minutes) ? minutes : 0,
                })
                .OrderBy(x => x.Departure)
                .ToList();

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
                    var nextLeg = isWrap ? "its first leg next week" : "its next leg";
                    var gapDeparture = currentRoute.ArrivalIcao;
                    var gapArrival = nextRoute.DepartureIcao;

                    // Two genuinely different problems, and telling them apart is the whole point of
                    // this check (see this method's own doc): if the route itself doesn't exist yet,
                    // send the player to create it; if it already exists, the gap is a scheduling
                    // gap, not a routing gap, and sending them to create a route they already have
                    // solves nothing.
                    var fix = RouteExistsBetween(existingRoutePairs, gapDeparture, gapArrival)
                        ? $"schedule a {gapDeparture} -> {gapArrival} leg before this one to reposition it"
                        : $"you'd need to create a {gapDeparture} -> {gapArrival} route on the Routes page for this chain to work";

                    conflicts.Add(
                        $"{aircraft.Registration} lands at {currentRoute.ArrivalIcao} ({FormatSlot(current.Entry.DayOfWeek, current.Entry.DepartureTimeUtc)}) " +
                        $"but {nextLeg} departs {nextRoute.DepartureIcao} ({FormatSlot(next.Entry.DayOfWeek, next.Entry.DepartureTimeUtc)}) - {fix}.");
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

            var flownDays = byDay.Keys.OrderBy(d => (int)d).ToList();
            for (var i = 0; i < flownDays.Count; i++)
            {
                var day = flownDays[i];
                var isWrap = i == flownDays.Count - 1;

                // Same closure exemption as ValidateAircraftChains - the last flown day's rest
                // before the FIRST flown day comes back around is a whole-week property. The gap
                // between every other consecutive pair of flown days is still fully checked.
                if (isWrap && !requireWeekClosure)
                {
                    continue;
                }

                var nextDay = flownDays[(i + 1) % flownDays.Count];

                var dutyEnd = dutyEndByDay[day];
                var nextFirst = byDay[nextDay][0];
                var nextStart = AbsoluteWeekMinute(nextDay, nextFirst.DepartureTimeUtc) + (isWrap ? WeekMinutes : 0);

                var restMinutes = nextStart - dutyEnd;
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
