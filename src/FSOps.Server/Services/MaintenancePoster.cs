using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Data;

namespace FSOps.Server.Services;

/// <summary>
/// Applies one flight's worth of hours to a fleet aircraft: bumps AirframeHours, runs
/// <see cref="MaintenanceScheduler"/> to see whether an A- or C-check fires, and if one does,
/// grounds the aircraft, writes the <see cref="MaintenanceEvent"/> row and posts the ledger cost -
/// all in the same unit of work the caller is already building (FlightLifecycleService's telemetry
/// completion path and FlightEndpoints' manual-completion path both call this instead of duplicating
/// the same handful of lines twice, which is how the two paths went out of sync before). Does not
/// call SaveChangesAsync itself - the caller's existing SaveChangesAsync commits everything together,
/// same as every other poster in this project (see FlightEconomicsPoster).
/// </summary>
public static class MaintenancePoster
{
    public static void PostFlightHours(
        FsOpsDbContext db, FleetAircraft aircraft, Airline airline, EconomyConfig economyConfig, double flightHours, DateTimeOffset completionUtc)
    {
        aircraft.AirframeHours += flightHours;

        var outcome = MaintenanceScheduler.Apply(aircraft, flightHours, economyConfig);

        aircraft.HoursSinceACheck = outcome.NewHoursSinceACheck;
        aircraft.HoursSinceCCheck = outcome.NewHoursSinceCCheck;
        aircraft.ConditionPercent = outcome.NewConditionPercent;

        if (!outcome.CheckTriggered)
        {
            return;
        }

        var type = outcome.Type!.Value;

        // Overrides whatever status the caller had just set (e.g. InFlight -> Active on landing) -
        // a check due this instant grounds the aircraft regardless of what it was about to become.
        aircraft.Status = FleetAircraftStatus.InMaintenance;
        aircraft.GroundedUntilUtc = completionUtc.AddHours(outcome.DowntimeHours);

        db.MaintenanceEvents.Add(new MaintenanceEvent
        {
            Id = Guid.NewGuid(),
            FleetAircraftId = aircraft.Id,
            Type = type,
            StartUtc = completionUtc,
            EndUtc = aircraft.GroundedUntilUtc,
            Cost = outcome.Cost,
            Notes = $"{type} triggered at {aircraft.AirframeHours:F1} airframe hours.",
            CreatedUtc = completionUtc,
        });

        db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            AirlineId = airline.Id,
            Utc = completionUtc,
            Category = LedgerCategory.Maintenance,
            Amount = -outcome.Cost,
            Description = $"{type} maintenance: {aircraft.Registration}",
        });
    }
}
