using FSOps.Core.Entities;

namespace FSOps.Core.Scheduling;

/// <summary>One scheduled leg reduced to just what stall detection needs: which aircraft flies it,
/// when in the week, and where it departs from.</summary>
public readonly record struct ScheduledLegPosition(
    Guid FleetAircraftId,
    DayOfWeek DayOfWeek,
    TimeSpan DepartureTimeUtc,
    string DepartureIcao);

/// <summary>
/// An aircraft whose schedule can no longer produce a single flight, and the facts needed to say so
/// in words the player can act on. <see cref="PatternStartIcao"/> is where the earliest leg of the
/// week departs from - the natural "where it is supposed to be" to quote alongside
/// <see cref="LocationIcao"/>.
/// </summary>
public sealed record StalledAircraft(
    Guid FleetAircraftId,
    string Registration,
    string LocationIcao,
    string PatternStartIcao)
{
    /// <summary>
    /// The two ways out, worded once and shared by every screen that reports this situation. They
    /// are both offered because they cost differently - flying it back earns a sector, repositioning
    /// costs a fee - and that is the player's call, not ours. Every message below names
    /// <see cref="PatternStartIcao"/> immediately before this, so "there" always has an antecedent.
    /// </summary>
    private string WaysOut => "Fly it there yourself, or reposition it from the Fleet page for the standard fee.";

    /// <summary>
    /// What the Pilots page says about a schedule that is already running. Deliberately phrased as a
    /// situation and a next step, never as a failure: the schedule is working exactly as designed and
    /// is still repeating, and the only thing wrong is where the airframe is standing.
    /// </summary>
    public string Message =>
        $"{Registration} is at {LocationIcao}, and no leg in this weekly pattern departs from there - " +
        $"so it has stopped producing flights, and every occurrence is being recorded as missed. " +
        $"The schedule itself is fine and still repeating: it picks up again as soon as {Registration} " +
        $"is back at {PatternStartIcao}. {WaysOut}";

    /// <summary>
    /// The same situation said at the moment the schedule is SAVED, where nothing has been missed
    /// yet and so claiming otherwise would be wrong. It states the identical fact
    /// (<see cref="LocationIcao"/> is somewhere no leg departs from), names the identical airport to
    /// get back to, and offers the identical <see cref="WaysOut"/> - a player can see this advisory
    /// and <see cref="Message"/> within a minute of each other, so they must never disagree about
    /// what is wrong or what to do about it.
    /// </summary>
    public string SaveTimeMessage =>
        $"{Registration} is at {LocationIcao}, and no leg in this weekly pattern departs from there. " +
        $"The schedule is saved and keeps repeating, but nothing in it can move the aircraft - so every " +
        $"occurrence is reported on the pilot's flight history as missed until {Registration} is back at " +
        $"{PatternStartIcao}. {WaysOut}";
}

/// <summary>
/// Finds standing weekly schedules that have quietly stopped producing flights because their
/// aircraft is somewhere the pattern never departs from.
/// <para>
/// <b>Why this is not just "the aircraft isn't where the week starts".</b>
/// <c>VirtualFlightResolverService</c> judges each occurrence on its own, against the aircraft's
/// position at that moment - so a pattern whose airframe is sitting at some OTHER airport the week
/// visits is not stuck at all: the occurrence that departs from there flies, moves the aircraft on,
/// and because a saved week is a closed loop (PUT /schedule enforces <c>requireWeekClosure: true</c>)
/// every leg after it lines up again on its own. Warning about that case would be crying wolf about
/// a schedule that is busy fixing itself. The pattern is only genuinely dead when the aircraft is
/// standing somewhere NO leg departs from, because then nothing in the schedule can ever move it
/// and every occurrence from here to forever is unflyable. That, and only that, is what this
/// reports.
/// </para>
/// <para>
/// An aircraft in flight is skipped: <see cref="FleetAircraft.LocationIcao"/> is its departure
/// airport mid-sector, not a position, so it is knowably stale rather than wrong (the same rule
/// <see cref="PilotScheduleValidator"/> applies to its own anchor). One in maintenance is skipped
/// too, for a different reason: it is grounded temporarily and will come back, which is a pause
/// rather than a stall - and with <see cref="PilotSchedule.AutoSuspendOnMaintenance"/> set the app
/// is already handling it and already says so.
/// </para>
/// </summary>
public static class ScheduleStallDetector
{
    public static IReadOnlyList<StalledAircraft> Detect(
        IReadOnlyCollection<FleetAircraft> fleet,
        IReadOnlyCollection<ScheduledLegPosition> legs)
    {
        if (legs.Count == 0)
        {
            return Array.Empty<StalledAircraft>();
        }

        var legsByAircraft = legs
            .GroupBy(l => l.FleetAircraftId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var stalled = new List<StalledAircraft>();

        foreach (var aircraft in fleet.OrderBy(f => f.Registration, StringComparer.OrdinalIgnoreCase))
        {
            if (!legsByAircraft.TryGetValue(aircraft.Id, out var aircraftLegs) || aircraftLegs.Count == 0)
            {
                // Nothing scheduled on it - an idle aircraft is a fleet-utilisation question, not a
                // stalled schedule, and there is no pattern here to have stopped.
                continue;
            }

            if (aircraft.Status is FleetAircraftStatus.InFlight or FleetAircraftStatus.InMaintenance)
            {
                continue;
            }

            if (Evaluate(aircraft, aircraftLegs) is { } stall)
            {
                stalled.Add(stall);
            }
        }

        return stalled;
    }

    /// <summary>
    /// The whole judgement for ONE aircraft against ONE aircraft's legs, with no status filtering of
    /// its own - deliberately, because which statuses are worth asking about differs by caller and is
    /// each caller's decision, whereas the geography is not. Returns null when the pattern can still
    /// move this airframe (it is standing at an airport some leg departs from, including the start),
    /// and a <see cref="StalledAircraft"/> when it cannot.
    /// <para>
    /// This is the reasoning <see cref="PilotScheduleValidator"/>'s save-time advisory reuses via
    /// <see cref="DescribeSavedPattern"/> rather than re-deriving. It used to have its own, cruder
    /// rule - "is the aircraft where the week starts" - which told a player whose airframe was
    /// standing at some other airport the week visits that the pattern would wait for it to return to
    /// the start, when in fact it picks up at that airport's own leg without ever going back. Two
    /// copies of the same reasoning is how the two messages drifted apart; there is now one.
    /// </para>
    /// </summary>
    public static StalledAircraft? Evaluate(FleetAircraft aircraft, IReadOnlyCollection<ScheduledLegPosition> legsForAircraft)
    {
        if (legsForAircraft.Count == 0 || ResumeLeg(aircraft.LocationIcao, legsForAircraft) is not null)
        {
            return null;
        }

        return new StalledAircraft(aircraft.Id, aircraft.Registration, aircraft.LocationIcao, PatternStart(legsForAircraft).DepartureIcao);
    }

    /// <summary>
    /// What to say, if anything, when a complete rolling week is SAVED with its aircraft not standing
    /// where the week begins - null when there is nothing worth saying. Three outcomes, and the
    /// middle one is the whole point of routing this through <see cref="Evaluate"/>:
    /// <list type="bullet">
    /// <item>at the pattern's own start - silent;</item>
    /// <item>at some OTHER airport the pattern departs from - it is not stuck, so say which leg picks
    /// it up rather than sending the player to fetch an aircraft that does not need fetching;</item>
    /// <item>somewhere no leg departs from - <see cref="StalledAircraft.SaveTimeMessage"/>, the same
    /// facts and the same two ways out the Pilots page will show for the same aircraft.</item>
    /// </list>
    /// The caller decides which aircraft to ask about at all (<see cref="PilotScheduleValidator"/>
    /// skips one in flight, whose recorded location is knowably stale rather than wrong).
    /// </summary>
    public static string? DescribeSavedPattern(FleetAircraft aircraft, IReadOnlyCollection<ScheduledLegPosition> legsForAircraft)
    {
        if (legsForAircraft.Count == 0)
        {
            return null;
        }

        var patternStart = PatternStart(legsForAircraft);
        if (string.Equals(patternStart.DepartureIcao, aircraft.LocationIcao, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (Evaluate(aircraft, legsForAircraft) is { } stall)
        {
            return stall.SaveTimeMessage;
        }

        return
            $"{aircraft.Registration} is at {aircraft.LocationIcao}, and this pattern starts from " +
            $"{patternStart.DepartureIcao} - but {aircraft.LocationIcao} is on the pattern too, so nothing is stuck. " +
            $"The schedule is saved and keeps repeating, and {aircraft.Registration} picks it up when the leg " +
            $"departing {aircraft.LocationIcao} next comes round - it does not have to go back to " +
            $"{patternStart.DepartureIcao} first. Any leg due before then is reported on the pilot's flight " +
            "history as missed.";
    }

    /// <summary>The earliest leg of the week, in week order - the natural "where this pattern is
    /// supposed to start". Sunday is day 0, so this is never just "whichever row came back first".</summary>
    private static ScheduledLegPosition PatternStart(IEnumerable<ScheduledLegPosition> legsForAircraft) =>
        legsForAircraft
            .OrderBy(l => (int)l.DayOfWeek)
            .ThenBy(l => l.DepartureTimeUtc)
            .First();

    /// <summary>
    /// The earliest leg of the week that departs from <paramref name="locationIcao"/>, or null if no
    /// leg does. Non-null is exactly the self-healing case this class's own doc describes: that leg
    /// can fly, it moves the aircraft on, and a closed weekly loop lines up again by itself.
    /// </summary>
    private static ScheduledLegPosition? ResumeLeg(string locationIcao, IEnumerable<ScheduledLegPosition> legsForAircraft)
    {
        var matches = legsForAircraft
            .Where(l => string.Equals(l.DepartureIcao, locationIcao, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count == 0 ? null : PatternStart(matches);
    }
}
