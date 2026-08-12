namespace FSOps.Core.Entities;

public class Airline
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string IcaoCode { get; set; } = string.Empty;

    public string HomeAirportIcao { get; set; } = string.Empty;

    public AirlineStrategyProfile StrategyProfile { get; set; }

    /// <summary>
    /// Chosen at creation, permanent for the airline's life. Switching mid-game would either
    /// multiply an existing airline's fixed costs roughly twelvefold overnight or trivialise
    /// everything already earned, leaving its whole history earned under rules that no longer
    /// apply. There is deliberately no way to change this after creation (see
    /// AirlineEndpoints.UpdateAsync, which never touches it); switching means deleting the airline
    /// and starting a new one, since a mid-game change would either bankrupt a healthy airline
    /// (Casual -&gt; True-life) or trivialise everything already earned (the reverse).
    /// </summary>
    public AirlinePlaystyle Playstyle { get; set; }

    public string AccentColour { get; set; } = "#3b82f6";

    public double ReputationScore { get; set; } = 50;

    /// <summary>Owning user. Every query against airline-scoped data must filter by this.</summary>
    public Guid OwnerUserId { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? DeletedUtc { get; set; }
}
