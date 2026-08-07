namespace FSOps.Core.Entities;

/// <summary>
/// A purchasable/leasable aircraft family (not a specific tail number - see FleetAircraft
/// for that). MatchPatterns is a JSON array of regex strings used to identify this family
/// from the freeform TITLE/ATC MODEL text the sim reports, since there's no reliable ICAO
/// type code available at runtime.
/// </summary>
public class AircraftType
{
    public Guid Id { get; set; }

    public string IcaoType { get; set; } = string.Empty;

    public string Family { get; set; } = string.Empty;

    public string Manufacturer { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int PaxCapacity { get; set; }

    public int RangeNm { get; set; }

    public int CruiseTasKts { get; set; }

    public double FuelBurnKgPerHour { get; set; }

    public int MinRunwayFt { get; set; }

    public int ServiceCeilingFt { get; set; }

    public decimal PurchasePrice { get; set; }

    public decimal MonthlyLeaseRate { get; set; }

    public string MatchPatterns { get; set; } = "[]";
}
