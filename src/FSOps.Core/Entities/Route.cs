namespace FSOps.Core.Entities;

public class Route
{
    public Guid Id { get; set; }

    public Guid AirlineId { get; set; }

    public string DepartureIcao { get; set; } = string.Empty;

    public string ArrivalIcao { get; set; } = string.Empty;

    /// <summary>
    /// Digits with an optional single-letter suffix (e.g. "101" or "204A"), matching the
    /// convention SimBrief/the aircraft FMS expect. Nullable because older/imported routes may
    /// not have one yet; new routes always get one, either supplied or auto-suggested - see
    /// <see cref="FSOps.Core.Routes.FlightNumberGenerator"/>.
    /// </summary>
    public string? FlightNumber { get; set; }

    public double DistanceNm { get; set; }

    public decimal BaseFare { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? DeletedUtc { get; set; }
}
