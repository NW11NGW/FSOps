namespace FSOps.Core.Entities;

public class Airline
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string IcaoCode { get; set; } = string.Empty;

    public string HomeAirportIcao { get; set; } = string.Empty;

    public AirlineStrategyProfile StrategyProfile { get; set; }

    public string AccentColour { get; set; } = "#3b82f6";

    public double ReputationScore { get; set; } = 50;

    /// <summary>Owning user. Every query against airline-scoped data must filter by this.</summary>
    public Guid OwnerUserId { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? DeletedUtc { get; set; }
}
