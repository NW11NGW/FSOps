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

    /// <summary>
    /// Fuel physically on board right now, in kg - <b>informational only</b>, never a billable
    /// asset. A sector is billed for what it actually burns, at the departure airport's price (see
    /// FSOps.Server.Services.FlightEconomicsPoster.PostFuelBurn), regardless of how much happens to
    /// be sitting in the tank - so this figure no longer drives any charge or credit. Kept purely so
    /// the Fleet page and report card can show a real "fuel on board" reading: synced from live
    /// telemetry when it's available (flight start, and whenever a flight finishes or is
    /// abandoned), otherwise left as whatever was last known. Defaults to 0 for both new and
    /// existing rows.
    /// </summary>
    public double FuelOnBoardKg { get; set; }

    public string LocationIcao { get; set; } = string.Empty;

    public FleetAircraftStatus Status { get; set; } = FleetAircraftStatus.Active;

    /// <summary>
    /// When a <see cref="FleetAircraftStatus.InMaintenance"/> grounding ends - null whenever
    /// Status isn't InMaintenance. Set by <see cref="MaintenanceScheduler"/>-triggered checks (see
    /// MaintenancePoster in FSOps.Server) so the Fly screen can say not just "in maintenance" but
    /// "until when" - an aircraft silently missing from the list teaches the player nothing, and
    /// "in maintenance" alone gives them nothing to plan around.
    /// Cleared by MaintenanceReleaser once the grounding period has elapsed.
    /// </summary>
    public DateTimeOffset? GroundedUntilUtc { get; set; }

    /// <summary>
    /// True if this aircraft is held back for the player rather than offered to the schedule
    /// builder. One airframe is always kept free for the human: opening the app to find your whole
    /// fleet booked out is the fastest way to feel locked out of your own airline. Once the fleet
    /// exceeds one aircraft, exactly one is auto-flagged true the moment the second aircraft is
    /// added (see FleetEndpoints.LeaseAsync/BuyAsync); a single-aircraft fleet also defaults to true
    /// (the plan's "the player chooses explicitly" - defaulting to protected is the safe choice,
    /// see AirlineEndpoints.CreateAsync). From then on this is entirely player-controlled via
    /// PUT /fleet/{id}/reservation - nothing else in the app re-forces this flag, so the player can
    /// freely release it (or reserve a different aircraft) and that choice sticks.
    /// </summary>
    public bool ReservedForPlayer { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? DeletedUtc { get; set; }
}
