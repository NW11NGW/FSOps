namespace FSOps.Core.Entities;

public class Runway
{
    public Guid Id { get; set; }

    public string AirportIcao { get; set; } = string.Empty;

    public string Designator { get; set; } = string.Empty;

    public int LengthFt { get; set; }

    public int WidthFt { get; set; }

    public string Surface { get; set; } = string.Empty;

    public double HeadingTrue { get; set; }

    // Threshold coordinates - nullable because not every OurAirports row has them.
    // Needed later for centreline deviation scoring during landing.
    public double? LatitudeStart { get; set; }

    public double? LongitudeStart { get; set; }

    public double? LatitudeEnd { get; set; }

    public double? LongitudeEnd { get; set; }

    public bool IsLighted { get; set; }

    public bool IsClosed { get; set; }
}
