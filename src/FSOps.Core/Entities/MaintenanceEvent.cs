namespace FSOps.Core.Entities;

public class MaintenanceEvent
{
    public Guid Id { get; set; }

    public Guid FleetAircraftId { get; set; }

    public MaintenanceEventType Type { get; set; }

    public DateTimeOffset StartUtc { get; set; }

    public DateTimeOffset? EndUtc { get; set; }

    public decimal Cost { get; set; }

    public string Notes { get; set; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? DeletedUtc { get; set; }
}
