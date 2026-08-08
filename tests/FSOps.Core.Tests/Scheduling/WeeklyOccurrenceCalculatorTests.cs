using FSOps.Core.Scheduling;

namespace FSOps.Core.Tests.Scheduling;

public class WeeklyOccurrenceCalculatorTests
{
    [Fact]
    public void OccurrencesBetween_OneWeekWindow_ReturnsExactlyOneOccurrence()
    {
        // Monday 2026-08-10 is a Monday.
        var from = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddDays(7);

        var occurrences = WeeklyOccurrenceCalculator.OccurrencesBetween(DayOfWeek.Wednesday, TimeSpan.FromHours(8.5), from, to);

        var occurrence = Assert.Single(occurrences);
        Assert.Equal(new DateTimeOffset(2026, 8, 12, 8, 30, 0, TimeSpan.Zero), occurrence);
    }

    [Fact]
    public void OccurrencesBetween_ThreeWeekWindow_ReturnsThreeWeeklyRepeats()
    {
        var from = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddDays(21);

        var occurrences = WeeklyOccurrenceCalculator.OccurrencesBetween(DayOfWeek.Friday, TimeSpan.FromHours(14), from, to);

        Assert.Equal(3, occurrences.Count);
        for (var i = 1; i < occurrences.Count; i++)
        {
            Assert.Equal(TimeSpan.FromDays(7), occurrences[i] - occurrences[i - 1]);
        }
    }

    [Fact]
    public void OccurrencesBetween_IsHalfOpen_BoundaryInstantCountsOnceNotTwice()
    {
        // An occurrence exactly at the boundary must be returned by the call whose window includes
        // it as the (inclusive) upper bound, and never again by a subsequent call using it as the
        // (exclusive) lower bound - this is what lets a resolver walk forward call by call without
        // double-processing or skipping the exact watermark instant.
        var boundary = new DateTimeOffset(2026, 8, 12, 8, 30, 0, TimeSpan.Zero);

        var firstWindow = WeeklyOccurrenceCalculator.OccurrencesBetween(DayOfWeek.Wednesday, TimeSpan.FromHours(8.5), boundary.AddDays(-7), boundary);
        var secondWindow = WeeklyOccurrenceCalculator.OccurrencesBetween(DayOfWeek.Wednesday, TimeSpan.FromHours(8.5), boundary, boundary.AddDays(7));

        Assert.Contains(boundary, firstWindow);
        Assert.DoesNotContain(boundary, secondWindow);
    }

    [Fact]
    public void OccurrencesBetween_EmptyOrInvertedWindow_ReturnsNothing()
    {
        var now = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

        Assert.Empty(WeeklyOccurrenceCalculator.OccurrencesBetween(DayOfWeek.Monday, TimeSpan.Zero, now, now));
        Assert.Empty(WeeklyOccurrenceCalculator.OccurrencesBetween(DayOfWeek.Monday, TimeSpan.Zero, now, now.AddDays(-1)));
    }

    [Fact]
    public void MostRecentOccurrenceAtOrBefore_ReturnsTheLastPastOccurrence_NeverAFutureOne()
    {
        // Wednesday 2026-08-12.
        var atOrBefore = new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.Zero); // Thursday

        var occurrence = WeeklyOccurrenceCalculator.MostRecentOccurrenceAtOrBefore(DayOfWeek.Wednesday, TimeSpan.FromHours(8.5), atOrBefore);

        Assert.Equal(new DateTimeOffset(2026, 8, 12, 8, 30, 0, TimeSpan.Zero), occurrence);
        Assert.True(occurrence <= atOrBefore);
    }

    [Fact]
    public void MostRecentOccurrenceAtOrBefore_ExactlyAtTheOccurrence_ReturnsItself()
    {
        var occurrence = new DateTimeOffset(2026, 8, 12, 8, 30, 0, TimeSpan.Zero);

        var resolved = WeeklyOccurrenceCalculator.MostRecentOccurrenceAtOrBefore(DayOfWeek.Wednesday, TimeSpan.FromHours(8.5), occurrence);

        Assert.Equal(occurrence, resolved);
    }
}
