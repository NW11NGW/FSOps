using FSOps.Core.Entities;

namespace FSOps.Core.Economy;

/// <summary>
/// Pure maintenance arithmetic: given a fleet aircraft's current hours/condition and the flight
/// hours it has just flown, decides whether an A- or C-check fires, what it costs and how long it
/// grounds the aircraft, and the new hours/condition figures either way. No I/O, no randomness -
/// entirely deterministic from its inputs, same convention as the rest of the economy engine (see
/// docs/PLAN.md "Economy design"). The caller (MaintenancePoster, in FSOps.Server) is responsible
/// for actually writing the result back to the database, posting the ledger line and the
/// <see cref="MaintenanceEvent"/> row, and grounding the aircraft.
/// </summary>
public static class MaintenanceScheduler
{
    /// <summary>
    /// Runs one flight's worth of wear against a fleet aircraft's current maintenance state.
    /// A C-check takes priority over an A-check when both thresholds are crossed in the same
    /// evaluation (the shipped config makes CCheckIntervalHours a whole multiple of
    /// ACheckIntervalHours, so that moment is also always an A-check-due moment) - a C-check is a
    /// full overhaul that already covers everything an A-check would, so both cycles reset
    /// together rather than scheduling a redundant A-check moments later.
    /// </summary>
    public static MaintenanceOutcome Apply(FleetAircraft aircraft, double flightHours, EconomyConfig config)
    {
        if (flightHours < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(flightHours), "flightHours cannot be negative.");
        }

        var maintenance = config.Maintenance;

        var hoursSinceACheck = aircraft.HoursSinceACheck + flightHours;
        var hoursSinceCCheck = aircraft.HoursSinceCCheck + flightHours;
        var condition = Math.Clamp(aircraft.ConditionPercent - flightHours * maintenance.ConditionDecayPerHour, 0, 100);

        if (hoursSinceCCheck >= maintenance.CCheckIntervalHours)
        {
            return new MaintenanceOutcome(
                CheckTriggered: true,
                Type: MaintenanceEventType.CCheck,
                Cost: maintenance.CCheckCost,
                DowntimeHours: maintenance.CCheckDowntimeHours,
                NewHoursSinceACheck: 0,
                NewHoursSinceCCheck: 0,
                NewConditionPercent: 100);
        }

        if (hoursSinceACheck >= maintenance.ACheckIntervalHours)
        {
            return new MaintenanceOutcome(
                CheckTriggered: true,
                Type: MaintenanceEventType.ACheck,
                Cost: maintenance.ACheckCost,
                DowntimeHours: maintenance.ACheckDowntimeHours,
                NewHoursSinceACheck: 0,
                NewHoursSinceCCheck: hoursSinceCCheck,
                NewConditionPercent: Math.Min(100, condition + maintenance.ACheckConditionRestorePercent));
        }

        return new MaintenanceOutcome(
            CheckTriggered: false,
            Type: null,
            Cost: 0m,
            DowntimeHours: 0,
            NewHoursSinceACheck: hoursSinceACheck,
            NewHoursSinceCCheck: hoursSinceCCheck,
            NewConditionPercent: condition);
    }

    /// <summary>
    /// Brings an A- or C-check forward at the player's own choosing, ahead of its natural due
    /// point - see docs/PLAN.md "A 'perform maintenance now' button on the Fleet page". Charges the
    /// FULL cost and downtime for the check type, exactly as if it had fired naturally at its usual
    /// interval, and always resets that cycle's hours to zero - so any hours already accrued since
    /// the last check of this type are forfeited. That forfeiture is the deliberate cost of
    /// choosing WHEN the downtime lands rather than being surprised by it mid-week: nothing here is
    /// pro-rated or discounted, or bringing a check forward would be a way to dodge cost rather than
    /// a real trade-off. Pure like <see cref="Apply"/> - the caller (MaintenancePoster) does the
    /// actual grounding/ledger/event writes.
    /// </summary>
    public static MaintenanceOutcome ApplyForced(FleetAircraft aircraft, MaintenanceEventType type, EconomyConfig config)
    {
        var maintenance = config.Maintenance;

        return type switch
        {
            MaintenanceEventType.CCheck => new MaintenanceOutcome(
                CheckTriggered: true,
                Type: MaintenanceEventType.CCheck,
                Cost: maintenance.CCheckCost,
                DowntimeHours: maintenance.CCheckDowntimeHours,
                NewHoursSinceACheck: 0,
                NewHoursSinceCCheck: 0,
                NewConditionPercent: 100),

            MaintenanceEventType.ACheck => new MaintenanceOutcome(
                CheckTriggered: true,
                Type: MaintenanceEventType.ACheck,
                Cost: maintenance.ACheckCost,
                DowntimeHours: maintenance.ACheckDowntimeHours,
                NewHoursSinceACheck: 0,
                // A forced A-check doesn't touch the C-check clock, same as a naturally-triggered
                // one - it's a full overhaul only when the C-check threshold is what's crossed.
                NewHoursSinceCCheck: aircraft.HoursSinceCCheck,
                NewConditionPercent: Math.Min(100, aircraft.ConditionPercent + maintenance.ACheckConditionRestorePercent)),

            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Only ACheck or CCheck can be performed - Unscheduled is never player-triggered."),
        };
    }

    /// <summary>
    /// The hours/condition/purchase-price state a used airframe of the given <paramref name="newPurchasePrice"/>
    /// starts at - see docs/PLAN.md "Used aircraft - cheap to buy, expensive to run". Pure: does not
    /// touch the database or assign a registration; the caller builds the actual FleetAircraft row.
    /// </summary>
    public static UsedAircraftState ResolveUsedAircraftState(decimal newPurchasePrice, EconomyConfig config)
    {
        var used = config.UsedAircraft;
        return new UsedAircraftState(
            PurchasePrice: Math.Round(newPurchasePrice * used.PriceMultiplier, 2, MidpointRounding.AwayFromZero),
            AirframeHours: used.StartingAirframeHours,
            HoursSinceACheck: config.Maintenance.ACheckIntervalHours * used.StartingHoursSinceACheckFraction,
            HoursSinceCCheck: config.Maintenance.CCheckIntervalHours * used.StartingHoursSinceCCheckFraction,
            ConditionPercent: used.StartingConditionPercent);
    }
}

/// <summary>Result of one <see cref="MaintenanceScheduler.Apply"/> call. When <see cref="CheckTriggered"/>
/// is false, <see cref="Type"/> is null and <see cref="Cost"/>/<see cref="DowntimeHours"/> are zero -
/// the caller applies only the New* hours/condition fields and does nothing else.</summary>
public sealed record MaintenanceOutcome(
    bool CheckTriggered,
    MaintenanceEventType? Type,
    decimal Cost,
    double DowntimeHours,
    double NewHoursSinceACheck,
    double NewHoursSinceCCheck,
    double NewConditionPercent);

/// <summary>The purchase-time state a used aircraft of a given type starts at - see
/// <see cref="MaintenanceScheduler.ResolveUsedAircraftState"/>.</summary>
public sealed record UsedAircraftState(
    decimal PurchasePrice,
    double AirframeHours,
    double HoursSinceACheck,
    double HoursSinceCCheck,
    double ConditionPercent);
