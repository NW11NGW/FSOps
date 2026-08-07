namespace FSOps.Core.Entities;

/// <summary>Seeded from the OurAirports public-domain dataset. Icao is the primary key.</summary>
public class Airport
{
    public string Icao { get; set; } = string.Empty;

    public string? Iata { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Municipality { get; set; } = string.Empty;

    public string Country { get; set; } = string.Empty;

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    public int ElevationFt { get; set; }

    public AirportSizeCategory SizeCategory { get; set; }

    public bool HasScheduledService { get; set; }

    public int LongestRunwayFt { get; set; }

    public List<Runway> Runways { get; set; } = new();
}
