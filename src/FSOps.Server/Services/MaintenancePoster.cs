using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Scheduling;
using FSOps.Data;

namespace FSOps.Server.Services;

/// <summary>
/// Applies one flight's worth of hours to a fleet aircraft: bumps AirframeHours, runs
/// <see cref="MaintenanceScheduler"/> to see whether an A- or C-check fires, and if one does,
/// grounds the aircraft, writes the <see cref="MaintenanceEvent"/> row and posts the ledger cost -
/// all in the same unit of work the caller is already building (FlightLifecycleService's telemetry
/// completion path, FlightEndpoints' manual-completion path and VirtualFlightResolverService's
/// virtual-pilot path all call this instead of duplicating the same handful of lines three times,
/// which is how the paths went out of sync before - see the <paramref name="pilot"/> parameter's
/// own note). Does not call SaveChangesAsync itself - the caller's existing SaveChangesAsync commits
/// everything together, same as every other poster in this project (see FlightEconomicsPoster).
/// </summary>
public static class MaintenancePoster
{
    /// <param name="pilot">
    /// The pilot who flew this sector, or null if it could not be resolved. <see cref="Pilot.HoursFlown"/>
    /// accrues here, alongside the airframe's own hours, rather than at each call site - a flight's
    /// hours belong to both the airframe that flew them and the pilot who flew them, and putting the
    /// accrual here (where the airframe side already lives) is what makes it impossible for a future
    /// caller to add a fourth completion path and forget the pilot side the way two of the three
    /// existing paths already had, which left the player's own HoursFlown sitting at zero however
    /// much they flew. Null is accepted rather than required so a caller that genuinely cannot resolve the
    /// pilot (a dangling/deleted PilotId) still posts the airframe and maintenance effects instead of
    /// throwing - the airframe wore the same either way.
    /// </param>
    public static void PostFlightHours(
        FsOpsDbContext db, FleetAircraft aircraft, Pilot? pilot, Airline airline, EconomyConfig economyConfig, double flightHours, DateTimeOffset completionUtc)
    {
        if (pilot is not null)
        {
            pilot.HoursFlown += flightHours;

            // Skill growth belongs here too, for the same "one shared place, not three call sites
            // that can drift apart" reason HoursFlown accrual does - see this method's own class
            // doc. LastFlewUtc is stamped to completionUtc (i.e. "now" for PilotSkillCalculator),
            // so this always resolves to pure growth with zero idle decay - a pilot who just flew
            // cannot be decaying at the same instant. Applies to every pilot, player included: see
            // PilotSkillCalculator's own doc for why growing this number is harmless for a player
            // (their real flights are always judged by real telemetry, never by SkillRating) and
            // why it can never DECREASE the player's number, which is the one non-negotiable rule
            // here: the player's own pilot record never decays.
            pilot.SkillRating = PilotSkillCalculator.Compute(pilot.HoursFlown, completionUtc, completionUtc, economyConfig.PilotSkill);
            pilot.LastFlewUtc = completionUtc;
        }

        aircraft.AirframeHours += flightHours;

        var outcome = MaintenanceScheduler.Apply(aircraft, flightHours, economyConfig);

        aircraft.HoursSinceACheck = outcome.NewHoursSinceACheck;
        aircraft.HoursSinceCCheck = outcome.NewHoursSinceCCheck;
        aircraft.ConditionPercent = outcome.NewConditionPercent;

        if (!outcome.CheckTriggered)
        {
            return;
        }

        ApplyTriggeredOutcome(db, aircraft, airline, outcome, completionUtc, forced: false);
    }

    /// <summary>
    /// Brings an A- or C-check forward at the player's own deliberate choice - see
    /// <see cref="MaintenanceScheduler.ApplyForced"/>, which this just applies to the database the
    /// same way <see cref="PostFlightHours"/> applies a naturally-triggered one. Blocked (by the
    /// caller, MaintenanceEndpoints) while the aircraft is airborne or already grounded - this
    /// method itself trusts the caller to have checked, same division of responsibility as the rest
    /// of this project's posters (validation lives at the endpoint, posting logic lives here).
    /// </summary>
    public static void PostForcedCheck(
        FsOpsDbContext db, FleetAircraft aircraft, Airline airline, MaintenanceEventType type, EconomyConfig economyConfig, DateTimeOffset performedUtc)
    {
        var outcome = MaintenanceScheduler.ApplyForced(aircraft, type, economyConfig);

        aircraft.HoursSinceACheck = outcome.NewHoursSinceACheck;
        aircraft.HoursSinceCCheck = outcome.NewHoursSinceCCheck;
        aircraft.ConditionPercent = outcome.NewConditionPercent;

        ApplyTriggeredOutcome(db, aircraft, airline, outcome, performedUtc, forced: true);
    }

    /// <summary>Shared tail end of both posting paths above: grounds the aircraft, writes the
    /// <see cref="MaintenanceEvent"/> row and posts the ledger cost. <paramref name="forced"/> only
    /// changes the wording of the event/ledger notes, so a forced check is never mistaken for a
    /// naturally-triggered one when reading history.</summary>
    private static void ApplyTriggeredOutcome(
        FsOpsDbContext db, FleetAircraft aircraft, Airline airline, MaintenanceOutcome outcome, DateTimeOffset effectiveUtc, bool forced)
    {
        var type = outcome.Type!.Value;

        // Overrides whatever status the caller had just set (e.g. InFlight -> Active on landing) -
        // a check due this instant grounds the aircraft regardless of what it was about to become.
        aircraft.Status = FleetAircraftStatus.InMaintenance;
        aircraft.GroundedUntilUtc = effectiveUtc.AddHours(outcome.DowntimeHours);

        db.MaintenanceEvents.Add(new MaintenanceEvent
        {
            Id = Guid.NewGuid(),
            FleetAircraftId = aircraft.Id,
            Type = type,
            StartUtc = effectiveUtc,
            EndUtc = aircraft.GroundedUntilUtc,
            Cost = outcome.Cost,
            Notes = forced
                ? $"{type} performed early by the player at {aircraft.AirframeHours:F1} airframe hours."
                : $"{type} triggered at {aircraft.AirframeHours:F1} airframe hours.",
            CreatedUtc = effectiveUtc,
        });

        db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            AirlineId = airline.Id,
            Utc = effectiveUtc,
            Category = LedgerCategory.Maintenance,
            Amount = -outcome.Cost,
            Description = forced
                ? $"{type} maintenance (performed early): {aircraft.Registration}"
                : $"{type} maintenance: {aircraft.Registration}",
        });
    }
}
