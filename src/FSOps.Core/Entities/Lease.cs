namespace FSOps.Core.Entities;

public class Lease
{
    public Guid Id { get; set; }

    public Guid AirlineId { get; set; }

    public Guid FleetAircraftId { get; set; }

    public decimal MonthlyRate { get; set; }

    public DateTimeOffset StartUtc { get; set; }

    public DateTimeOffset? EndUtc { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? DeletedUtc { get; set; }
}
