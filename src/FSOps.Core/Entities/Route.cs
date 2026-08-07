namespace FSOps.Core.Entities;

public class Route
{
    public Guid Id { get; set; }

    public Guid AirlineId { get; set; }

    public string DepartureIcao { get; set; } = string.Empty;

    public string ArrivalIcao { get; set; } = string.Empty;

    public double DistanceNm { get; set; }

    public decimal BaseFare { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? DeletedUtc { get; set; }
}
