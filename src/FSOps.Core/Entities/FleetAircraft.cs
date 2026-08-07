namespace FSOps.Core.Entities;

public class FleetAircraft
{
    public Guid Id { get; set; }

    public Guid AirlineId { get; set; }

    public Guid AircraftTypeId { get; set; }

    public string Registration { get; set; } = string.Empty;

    public AircraftOwnership Ownership { get; set; }

    public double AirframeHours { get; set; }

    public double HoursSinceACheck { get; set; }

    public double HoursSinceCCheck { get; set; }

    public double ConditionPercent { get; set; } = 100;

    public string LocationIcao { get; set; } = string.Empty;

    public FleetAircraftStatus Status { get; set; } = FleetAircraftStatus.Active;

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? DeletedUtc { get; set; }
}
