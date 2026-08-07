namespace FSOps.Core.Entities;

/// <summary>Append-only log of everything that happens during a flight - never updated or deleted.</summary>
public class FlightEvent
{
    public Guid Id { get; set; }

    public Guid FlightId { get; set; }

    public DateTimeOffset Utc { get; set; }

    public FlightEventType Type { get; set; }

    public string PayloadJson { get; set; } = "{}";
}
