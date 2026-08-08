namespace FSOps.Core.Flights;

/// <summary>
/// Turns a flight's actual Out/In times into the hours to add to a fleet aircraft's airframe
/// total - the maintenance system (A/C checks) reads that running total, so it must reflect real
/// block time, not just be left untouched. Pure, no I/O.
/// </summary>
public static class BlockTimeCalculator
{
    /// <summary>Hours between Out and In, or zero if either is missing or they're out of order.</summary>
    public static double BlockHours(DateTimeOffset? outUtc, DateTimeOffset? inUtc)
    {
        if (outUtc is not { } outTime || inUtc is not { } inTime || inTime <= outTime)
        {
            return 0;
        }

        return (inTime - outTime).TotalHours;
    }
}
