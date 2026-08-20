namespace FSOps.Core.Scheduling;

/// <summary>One leg reduced to what cycle ordering needs: when in the week it goes, and the two
/// airports it joins. Deliberately not tied to a persisted row or to
/// <see cref="PilotScheduleEntryInput"/>, so both the validator (which has routes in hand) and the
/// endpoints (which have their own) can describe a chain to <see cref="WeekCycle"/> without either
/// one owning the other's shape.</summary>
public readonly record struct CycleLeg(DayOfWeek DayOfWeek, TimeSpan DepartureTimeUtc, string DepartureIcao, string ArrivalIcao);

/// <summary>
/// A saved schedule is a rolling weekly CYCLE - "the schedule should be for the week rolling
/// forever until it's modified" (user's decision, 2026-08-13) - and this is the arithmetic that
/// treats it as one.
/// <para>
/// <b>Why a plain week minute is not enough.</b> Ordering a week by
/// <c>(int)DayOfWeek * 1440 + time</c> puts Sunday first, because .NET numbers Sunday 0. That is a
/// perfectly good total order, but it invents a "first leg of the week" that a cycle does not have -
/// and the scheduler leans on that first leg twice: it is the one anchored to where the aircraft
/// physically is, and the pair that closes back to it is the one skipped while a week is still being
/// built. Both of those are decisions about where the loop is <i>open</i>, and Sunday midnight is
/// simply not where it is open. Real-use defect, 2026-08-20: a pilot flying Monday to Saturday out
/// of Bristol, whose Saturday chain lands the aircraft back at EGGD, was offered nothing but EGPH
/// departures on Sunday morning - because Sunday sorted before Monday, so Sunday's leg was treated
/// as the pattern's first and anchored to the airframe's live position at EGPH instead of to
/// Saturday's arrival. Saturday to Sunday and Sunday to Monday are just two more consecutive pairs.
/// </para>
/// <para>
/// <b>What replaces it.</b> <see cref="OriginMinute"/> works out where the aircraft actually enters
/// the loop, and <see cref="MinutesFrom"/> re-expresses every leg as "how far round the cycle from
/// there" - so the ordering is relative to a real, physical fact about this aircraft rather than to
/// a calendar convention. With an origin of 0 this is exactly the old Sunday-first arithmetic, which
/// is what an aircraft with nothing scheduled on it still gets.
/// </para>
/// </summary>
public static class WeekCycle
{
    public const int WeekMinutes = 7 * 24 * 60;

    public const int DayMinutes = 24 * 60;

    /// <summary>Minute-of-week under .NET's own <see cref="DayOfWeek"/> numbering (Sunday 00:00 = 0).
    /// Still the storage and wire convention everywhere - see this class's own remarks on why that
    /// deliberately did NOT change - and the input every other member here takes.</summary>
    public static int AbsoluteMinute(DayOfWeek dayOfWeek, TimeSpan timeOfDay) => (int)dayOfWeek * DayMinutes + (int)timeOfDay.TotalMinutes;

    /// <summary>How far forward round the cycle <paramref name="absoluteMinute"/> is from
    /// <paramref name="originMinute"/>, always in [0, <see cref="WeekMinutes"/>). Sorting by this is
    /// what makes the week start where the aircraft joins it rather than at Sunday midnight.</summary>
    public static int MinutesFrom(int originMinute, int absoluteMinute) =>
        ((absoluteMinute - originMinute) % WeekMinutes + WeekMinutes) % WeekMinutes;

    /// <summary>
    /// The point in the week at which this aircraft enters this chain - the leg with nothing usable
    /// before it, which is the only thing in a cycle that behaves like a "first" leg. Two facts
    /// decide it, and both matter: where the airframe is standing, and where the loop is actually
    /// OPEN (a leg whose cyclic predecessor lands somewhere else - a "break"). Four rules, in order:
    /// <list type="number">
    /// <item><b>A break that departs from where the aircraft is.</b> Both facts agree: the chain is
    /// open here, and the airframe can take this leg. Nothing else is a better answer.</item>
    /// <item><b>Otherwise, any leg departing from where the aircraft is.</b> The chain is closed at
    /// that point, so the airframe simply joins it there and everything follows - the same reasoning
    /// <see cref="ScheduleStallDetector"/> applies to a saved pattern ("it picks up when the leg
    /// departing here next comes round"), so the two never disagree about whether an aircraft is
    /// stuck. Deliberately ahead of rule 3: an aircraft standing on the pattern is not out of
    /// position, and starting the loop at some distant break would make it look like it was.</item>
    /// <item><b>Otherwise, the first break.</b> The loop is open somewhere the airframe cannot reach -
    /// so the chain starts at the opening, and the anchor check gets to say the aircraft is not
    /// there.</item>
    /// <item><b>Otherwise, the earliest leg in plain week order.</b> A closed loop the aircraft is
    /// nowhere on: it cannot join anywhere, the choice cannot be made better, and this keeps the old
    /// behaviour for the one case where it was never wrong.</item>
    /// </list>
    /// <para>
    /// <b>Why rule 1 is not just "any leg departing from where the aircraft is".</b> That was the
    /// first attempt, and it broke the picker: adding a Monday 06:00 EGGD departure to a week that
    /// already flew EGGD every morning Tuesday to Friday made the NEW leg the earliest EGGD departure
    /// in plain week order, so the candidate itself became the pattern's start - and the chain was
    /// then read as "Monday lands at EGPH but the next leg departs EGGD on Tuesday", refusing a leg
    /// that is perfectly legal. Requiring the origin to also be where the chain is genuinely open
    /// picks Tuesday (whose predecessor is the new Monday leg, landing elsewhere) and leaves the
    /// candidate where it belongs - at the end of the loop, on the pair a week under construction
    /// does not have to close yet.
    /// </para>
    /// <paramref name="aircraftLocationIcao"/> is null when the position is not knowable - an
    /// aircraft mid-sector records its DEPARTURE airport, not where it is - in which case rules 1 and
    /// 2 are skipped rather than matched against a value that is knowably stale.
    /// </summary>
    public static int OriginMinute(string? aircraftLocationIcao, IReadOnlyCollection<CycleLeg> legs)
    {
        if (legs.Count == 0)
        {
            return 0;
        }

        var ordered = legs
            .OrderBy(l => AbsoluteMinute(l.DayOfWeek, l.DepartureTimeUtc))
            .ToList();

        bool IsBreak(int index)
        {
            var previous = ordered[(index - 1 + ordered.Count) % ordered.Count];
            return !string.Equals(previous.ArrivalIcao, ordered[index].DepartureIcao, StringComparison.OrdinalIgnoreCase);
        }

        bool DepartsFromAircraft(int index) =>
            !string.IsNullOrWhiteSpace(aircraftLocationIcao) &&
            string.Equals(ordered[index].DepartureIcao, aircraftLocationIcao, StringComparison.OrdinalIgnoreCase);

        int MinuteOf(int index) => AbsoluteMinute(ordered[index].DayOfWeek, ordered[index].DepartureTimeUtc);

        for (var i = 0; i < ordered.Count; i++)
        {
            if (DepartsFromAircraft(i) && IsBreak(i))
            {
                return MinuteOf(i);
            }
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            if (DepartsFromAircraft(i))
            {
                return MinuteOf(i);
            }
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            if (IsBreak(i))
            {
                return MinuteOf(i);
            }
        }

        return MinuteOf(0);
    }
}
