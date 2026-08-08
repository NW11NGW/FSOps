namespace FSOps.Core.Entities;

public enum AirportSizeCategory
{
    Small,
    Medium,
    Large,
    Heliport,
    Seaplane,
    Closed,
}

public enum AirlineStrategyProfile
{
    International,
    Domestic,
    LowCost,
    Premium,
    Balanced,
}

/// <summary>
/// Chosen once, at airline creation, and permanent for the airline's life (see docs/PLAN.md
/// "Playstyle - Casual vs True-life"). A playstyle is a named set of overrides in
/// economy-config.json (starter lease, insurance, lease deposit, starting capital) - never a code
/// path, so nothing in the economy engine itself branches on this value. It is resolved once, at
/// the point a config is needed for a specific airline, via EconomyConfigCatalog.
/// </summary>
public enum AirlinePlaystyle
{
    Casual,
    TrueLife,
}

public enum AircraftOwnership
{
    Owned,
    Leased,
}

public enum FleetAircraftStatus
{
    Active,
    InMaintenance,
    InFlight,
}

public enum PilotStatus
{
    Available,
    Flying,
    Inactive,
}

public enum FlightStatus
{
    Planned,
    InProgress,
    Completed,
    Interrupted,
    Abandoned,

    /// <summary>
    /// A virtual pilot's scheduled flight that could not fly (aircraft in maintenance, still away,
    /// or otherwise unavailable) and the airline's Playstyle is Casual - see docs/PLAN.md
    /// "Playstyle is not only numbers - it changes behaviour". Recorded and visible in history so a
    /// silent skip can never hide a scheduling bug, but no ledger lines are posted at all: no lost
    /// revenue (nothing was ever booked) and no penalty. See VirtualFlightResolverService.
    /// </summary>
    Skipped,

    /// <summary>
    /// A virtual pilot's scheduled flight that could not fly and the airline's Playstyle is
    /// True-life - see docs/PLAN.md "Playstyle is not only numbers - it changes behaviour". Unlike
    /// <see cref="Skipped"/>, this posts a single <see cref="LedgerCategory.CancellationFee"/>
    /// ledger line (see EconomyConfig.UnflyableSchedule) so a badly-planned schedule genuinely
    /// bites. Whether an unflyable occurrence resolves to Skipped or Cancelled is entirely
    /// config-driven (EconomyConfig.UnflyableSchedule.CancellationFee is zero for Casual, positive
    /// for True-life) - there is no `if (playstyle)` branch anywhere in the resolution logic.
    /// </summary>
    Cancelled,
}

public enum FlightEventType
{
    PhaseChange,
    Touchdown,
    PositionSnapshot,
    Mismatch,
    Note,
}

public enum LedgerCategory
{
    TicketRevenue,
    Fuel,
    LandingFees,
    Handling,
    Maintenance,
    Salary,
    LeasePayment,
    LoanPayment,
    AircraftPurchase,
    Insurance,
    GsxServices,
    StartingCapital,
    LoanProceeds,

    /// <summary>
    /// Posted for a virtual pilot's flight that couldn't fly under a True-life airline - see
    /// <see cref="FlightStatus.Cancelled"/> and docs/PLAN.md's Playstyle behaviour table. Never
    /// posted for a Casual airline (see <see cref="FlightStatus.Skipped"/>) and never posted
    /// alongside a <see cref="TicketRevenue"/> line for the same flight - a cancelled sector never
    /// flew, so it never earns revenue.
    /// </summary>
    CancellationFee,

    Other,
}

public enum MaintenanceEventType
{
    ACheck,
    CCheck,
    Unscheduled,
}

public enum DistanceUnit
{
    Nm,
    Km,
}

public enum AltitudeUnit
{
    Feet,
    Metres,
}

public enum WeightUnit
{
    Kg,
    Lb,
}

public enum TimeDisplay
{
    Utc,
    Local,
}

/// <summary>Starter aircraft choice offered during airline creation - not persisted directly,
/// each family maps to one specific AircraftType variant (see AirlineEndpoints).</summary>
public enum StarterAircraftFamily
{
    A320,
    B737,
}
