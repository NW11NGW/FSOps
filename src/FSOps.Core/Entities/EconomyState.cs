namespace FSOps.Core.Entities;

/// <summary>Singleton row holding world-simulation state shared across the whole app.</summary>
public class EconomyState
{
    public Guid Id { get; set; }

    public DateTimeOffset LastProcessedUtc { get; set; }

    public decimal FuelPricePerKg { get; set; }

    public int WorldSeed { get; set; }
}
