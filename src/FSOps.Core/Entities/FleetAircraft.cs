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
    /// Fuel physically on board right now, in kg - a stored asset, not a per-flight expense (see
    /// docs/PLAN.md "Persistent fuel state and tankering"). Written when a flight finishes or is
    /// abandoned, and read at the next flight start to reconcile against what the sim reports: a
    /// rise on the ground is an uplift and is charged at that airport's price, while fuel already
    /// on board has been paid for and costs nothing further to burn. This is what lets a return
    /// leg fly free on the outbound leg's fuel. Defaults to 0 for both new and existing rows.
    /// </summary>
    public double FuelOnBoardKg { get; set; }

    public string LocationIcao { get; set; } = string.Empty;

    public FleetAircraftStatus Status { get; set; } = FleetAircraftStatus.Active;

    /// <summary>
    /// When a <see cref="FleetAircraftStatus.InMaintenance"/> grounding ends - null whenever
    /// Status isn't InMaintenance. Set by <see cref="MaintenanceScheduler"/>-triggered checks (see
    /// MaintenancePoster in FSOps.Server) so the Fly screen can say not just "in maintenance" but
    /// "until when" (docs/PLAN.md's E1 brief: "the Fly screen must say why and until when, not
    /// merely omit it"). Cleared by MaintenanceReleaser once the grounding period has elapsed.
    /// </summary>
    public DateTimeOffset? GroundedUntilUtc { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public DateTimeOffset? DeletedUtc { get; set; }
}
