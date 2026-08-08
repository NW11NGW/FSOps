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
