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
    /// Deliberately phrased as a situation and a next step, never as a failure: the schedule is
    /// working exactly as designed and is still repeating, and the only thing wrong is where the
    /// airframe is standing. Both ways out are offered because they cost differently - flying it
    /// back earns a sector, repositioning costs a fee - and that is the player's call, not ours.
    /// </summary>
    public string Message =>
        $"{Registration} is at {LocationIcao}, and no leg in this weekly pattern departs from there - " +
        $"so it has stopped producing flights, and every occurrence is being recorded as missed. " +
        $"The schedule itself is fine and still repeating: it picks up again as soon as {Registration} " +
        $"is back at {PatternStartIcao}. Fly it there yourself, or reposition it from the Fleet page for the standard fee.";
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

            var reachable = aircraftLegs
                .Select(l => l.DepartureIcao)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (reachable.Contains(aircraft.LocationIcao))
            {
                continue;
            }

            var first = aircraftLegs
                .OrderBy(l => (int)l.DayOfWeek)
                .ThenBy(l => l.DepartureTimeUtc)
                .First();

            stalled.Add(new StalledAircraft(aircraft.Id, aircraft.Registration, aircraft.LocationIcao, first.DepartureIcao));
        }

        return stalled;
    }
}
