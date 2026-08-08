namespace FSOps.Core.Scheduling;

/// <summary>
/// Pure date arithmetic: turns a <see cref="System.DayOfWeek"/> + time-of-day (UTC) template - one
/// <c>PilotScheduleEntry</c> - into the concrete UTC departure instants it produces within a window.
/// No I/O, no entity dependencies, so <see cref="FSOps.Server.Services.VirtualFlightResolverService"/>
/// and tests can both drive it directly. "The week repeats indefinitely" (docs/PLAN.md) means there
/// is no end condition here beyond the window the caller asks for.
/// </summary>
public static class WeeklyOccurrenceCalculator
{
    /// <summary>
    /// Every departure instant for one weekly template that falls in <c>(fromExclusive, toInclusive]</c>.
    /// Half-open-on-the-left/closed-on-the-right so a resolver can call this repeatedly with
    /// <c>fromExclusive</c> set to the previous call's watermark without ever double-counting or
    /// skipping the boundary instant.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> OccurrencesBetween(
        DayOfWeek dayOfWeek, TimeSpan departureTimeUtc, DateTimeOffset fromExclusive, DateTimeOffset toInclusive)
    {
        if (toInclusive <= fromExclusive)
        {
            return Array.Empty<DateTimeOffset>();
        }

        var results = new List<DateTimeOffset>();

        // Anchor at the most recent midnight on-or-before fromExclusive, then walk forward day by
        // day until we hit the target day-of-week - simpler and less error-prone than modular
        // arithmetic on DayOfWeek's int values, and this loop runs at most 7 times per call.
        var cursor = new DateTimeOffset(fromExclusive.UtcDateTime.Date, TimeSpan.Zero);
        while (cursor.DayOfWeek != dayOfWeek)
        {
            cursor = cursor.AddDays(1);
        }

        var occurrence = cursor + departureTimeUtc;
        if (occurrence <= fromExclusive)
        {
            occurrence = occurrence.AddDays(7);
        }

        while (occurrence <= toInclusive)
        {
            results.Add(occurrence);
            occurrence = occurrence.AddDays(7);
        }

        return results;
    }

    /// <summary>
    /// The single most recent occurrence at or before <paramref name="atOrBefore"/> - used by the
    /// live operations map to find "this week's" departure for a schedule entry without having to
    /// enumerate a whole window (see docs/PLAN.md "Live operations map" - position is interpolated
    /// from the current occurrence's elapsed fraction, not from any persisted state).
    /// </summary>
    public static DateTimeOffset MostRecentOccurrenceAtOrBefore(DayOfWeek dayOfWeek, TimeSpan departureTimeUtc, DateTimeOffset atOrBefore)
    {
        var cursor = new DateTimeOffset(atOrBefore.UtcDateTime.Date, TimeSpan.Zero);
        while (cursor.DayOfWeek != dayOfWeek)
        {
            cursor = cursor.AddDays(-1);
        }

        var occurrence = cursor + departureTimeUtc;
        if (occurrence > atOrBefore)
        {
            occurrence = occurrence.AddDays(-7);
        }

        return occurrence;
    }
}
