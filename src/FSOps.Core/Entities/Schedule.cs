namespace FSOps.Core.Entities;

public class Schedule
{
    public Guid Id { get; set; }

    public Guid RouteId { get; set; }

    public Guid? FleetAircraftId { get; set; }

    public Guid? PilotId { get; set; }

    /// <summary>Time of day (UTC) the flight departs.</summary>
    public TimeSpan DepartureTimeUtc { get; set; }

    /// <summary>Bitmask, bit 0 = Sunday .. bit 6 = Saturday.</summary>
    public int DaysOfWeekMask { get; set; }

    public int BlockMinutes { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? DeletedUtc { get; set; }
}
