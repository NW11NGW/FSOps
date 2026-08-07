namespace FSOps.Core.Entities;

public class Flight
{
    public Guid Id { get; set; }

    public Guid AirlineId { get; set; }

    public Guid RouteId { get; set; }

    public Guid? ScheduleId { get; set; }

    public Guid FleetAircraftId { get; set; }

    public Guid PilotId { get; set; }

    public FlightStatus Status { get; set; } = FlightStatus.Planned;

    public DateTimeOffset PlannedDepartureUtc { get; set; }

    public int PlannedBlockMinutes { get; set; }

    // OOOI - Out/Off/On/In times, captured as the flight actually progresses.
    public DateTimeOffset? OutUtc { get; set; }

    public DateTimeOffset? OffUtc { get; set; }

    public DateTimeOffset? OnUtc { get; set; }

    public DateTimeOffset? InUtc { get; set; }

    public int PaxBooked { get; set; }

    public int PaxFlown { get; set; }

    public double FuelPlannedKg { get; set; }

    public double FuelUsedKg { get; set; }

    public double? LandingFpmFirst { get; set; }

    public double? LandingFpmHardest { get; set; }

    public double? LandingGForce { get; set; }

    public double? CentrelineDeviationM { get; set; }

    public string TitleFlown { get; set; } = string.Empty;

    /// <summary>Informational only - a wrong-family aircraft is flagged, never penalised.</summary>
    public bool TypeMismatch { get; set; }

    public decimal Revenue { get; set; }

    public decimal TotalCost { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? DeletedUtc { get; set; }
}
