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
    Other,
}

public enum MaintenanceEventType
{
    ACheck,
    CCheck,
    Unscheduled,
}
