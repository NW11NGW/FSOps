using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Planning;
using FSOps.Core.Scheduling;
using FSOps.Data;
using FSOps.Server.Auth;
using FSOps.Server.Services;
using Microsoft.EntityFrameworkCore;
using Route = FSOps.Core.Entities.Route;

namespace FSOps.Server.Endpoints;

/// <summary>
/// Hiring virtual pilots and building their standing weekly schedule - see docs/PLAN.md "Virtual
/// pilot scheduling - standing assignments and the schedule builder". The actual wall-clock
/// resolution of what a saved schedule produces happens in
/// <see cref="FSOps.Server.Services.VirtualFlightResolverService"/>, not here - these endpoints
/// only ever read/write the schedule template and validate it with
/// <see cref="PilotScheduleValidator"/>, which is airline-scoped (an aircraft's chain and a
/// pilot's rest both have to account for every OTHER pilot touching the same aircraft, not just
/// the one being edited).
/// <para>
/// <b>Aircraft-per-duty-day (docs/PLAN.md "2a"/"2c").</b> The wire shape is deliberately
/// duty-day-first, not leg-first: <see cref="DutyDayRequest"/> carries ONE
/// <see cref="DutyDayRequest.FleetAircraftId"/> for every leg inside it. This is what makes
/// "continuity holds by construction" actually true at the API boundary, not just in the
/// validator - there is no field anywhere on the wire that could smuggle two different aircraft
/// into one duty day, because a leg's aircraft is never sent per-leg at all.
/// <see cref="PilotScheduleValidator"/> still enforces the same invariant defensively
/// (<c>ValidateDutyDayAircraftConsistency</c>), since it is a pure function that must not trust its
/// caller, but the shape here is the first line of defence.
/// </para>
/// </summary>
public static class PilotEndpoints
{
    public static void MapPilotEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/pilots", ListAsync);
        group.MapPost("/pilots", HireAsync);
        group.MapDelete("/pilots/{id:guid}", ReleaseAsync);
        group.MapGet("/pilots/{id:guid}/schedule", GetScheduleAsync);
        group.MapPut("/pilots/{id:guid}/schedule", SaveScheduleAsync);
        group.MapPost("/pilots/{id:guid}/schedule/aircraft-options", GetAircraftOptionsAsync);
        group.MapPost("/pilots/{id:guid}/schedule/leg-options", GetLegOptionsAsync);
        group.MapGet("/pilots/schedule/overview", GetScheduleOverviewAsync);
    }

    internal static async Task<IResult> ListAsync(FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(Array.Empty<object>());
        }

        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);
        var pilots = (await db.Pilots.Where(p => p.AirlineId == airline.Id).ToListAsync(ct))
            .OrderByDescending(p => p.IsPlayer)
            .ThenBy(p => p.CreatedUtc)
            .ToList();

        var summaries = new List<object>();
        foreach (var pilot in pilots)
        {
            var weekly = pilot.IsPlayer
                ? WeeklySummary.None
                : await ComputeWeeklySummaryAsync(db, airline, economyConfig, pilot.Id, ct);

            summaries.Add(new
            {
                pilot.Id,
                pilot.Name,
                pilot.IsPlayer,
                pilot.MonthlySalary,
                pilot.HoursFlown,
                pilot.SkillRating,
                Status = pilot.Status.ToString(),
                pilot.CreatedUtc,
                sectorsPerWeek = weekly.SectorsPerWeek,
                weeklyEstimatedRevenue = weekly.EstimatedRevenue,
                weeklyEstimatedCost = weekly.EstimatedCost,
            });
        }

        return Results.Ok(summaries);
    }

    /// <summary>
    /// Hires a virtual pilot at the playstyle's standard salary - see docs/PLAN.md "Virtual pilots
    /// must be affordable early enough to matter": no upfront cost or cash-balance gate, only the
    /// recurring monthly salary EconomyClockService already posts for every pilot on record, same
    /// as the founding pilot AirlineEndpoints.CreateAsync hires.
    /// </summary>
    internal static async Task<IResult> HireAsync(
        HirePilotRequest request, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline before hiring a pilot." });
        }

        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            var existingCount = await db.Pilots.CountAsync(p => p.AirlineId == airline.Id && !p.IsPlayer, ct);
            name = $"First Officer {existingCount + 1}";
        }
        else if (name.Length > 40)
        {
            return Results.BadRequest(new { error = "name must be 40 characters or fewer." });
        }

        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);
        var now = DateTimeOffset.UtcNow;

        var pilot = new Pilot
        {
            Id = Guid.NewGuid(),
            AirlineId = airline.Id,
            Name = name,
            IsPlayer = false,
            MonthlySalary = economyConfig.AirlineStartup.StartingPilotMonthlySalary,
            HoursFlown = 0,
            // Fixed at the same baseline the founding player pilot uses - there is no basis in the
            // plan for varying this at hire time, and a fixed, known starting skill keeps "hire a
            // pilot, give them a schedule" tests exact-value rather than probabilistic.
            SkillRating = 50,
            Status = PilotStatus.Available,
            CreatedUtc = now,
        };

        db.Pilots.Add(pilot);
        await db.SaveChangesAsync(ct);

        var cashBalance = await CashBalanceAsync(db, airline.Id, ct);
        return Results.Created($"/api/v1/pilots/{pilot.Id}", new { pilot, cashBalance });
    }

    internal static async Task<IResult> ReleaseAsync(Guid id, FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.NotFound();
        }

        var pilot = await db.Pilots.FirstOrDefaultAsync(p => p.Id == id && p.AirlineId == airline.Id, ct);
        if (pilot is null)
        {
            return Results.NotFound();
        }

        if (pilot.IsPlayer)
        {
            return Results.BadRequest(new { error = "The player pilot cannot be released." });
        }

        var now = DateTimeOffset.UtcNow;
        pilot.DeletedUtc = now;

        // Cascade to the schedule - see AirlineEndpoints.DeleteAsync for the same cascade at
        // whole-airline scope. Without this, VirtualFlightResolverService would keep resolving
        // occurrences for a pilot who no longer exists.
        var schedule = await db.PilotSchedules.FirstOrDefaultAsync(s => s.PilotId == pilot.Id, ct);
        if (schedule is not null)
        {
            schedule.DeletedUtc = now;
            var entries = await db.PilotScheduleEntries.Where(e => e.PilotScheduleId == schedule.Id).ToListAsync(ct);
            foreach (var entry in entries)
            {
                entry.DeletedUtc = now;
            }
        }

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    internal static async Task<IResult> GetScheduleAsync(Guid id, FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var pilot = await LoadOwnedPilotAsync(db, currentUser, id, ct);
        if (pilot is null)
        {
            return Results.NotFound();
        }

        var schedule = await db.PilotSchedules.FirstOrDefaultAsync(s => s.PilotId == pilot.Id, ct);
        if (schedule is null)
        {
            // No schedule saved yet - report the same default the first save will apply (see
            // SaveScheduleAsync), so a UI reading this before any save still shows the true state.
            return Results.Ok(new { pilotId = pilot.Id, dutyDays = Array.Empty<object>(), autoSuspendOnMaintenance = true });
        }

        var entries = await db.PilotScheduleEntries.Where(e => e.PilotScheduleId == schedule.Id).ToListAsync(ct);
        var dto = await BuildDutyDayDtosAsync(db, entries, ct);
        return Results.Ok(new { pilotId = pilot.Id, dutyDays = dto, autoSuspendOnMaintenance = schedule.AutoSuspendOnMaintenance });
    }

    /// <summary>
    /// Replaces this pilot's ENTIRE week in one call - see docs/PLAN.md "set once and leave", the
    /// whole reason the builder is a week grid rather than one-flight-at-a-time administration.
    /// Validated airline-wide: merges every OTHER pilot's existing entries with this pilot's
    /// proposed replacement set before calling <see cref="PilotScheduleValidator"/>, since an
    /// aircraft's chain and double-booking rules span every pilot that touches it, not just this one.
    /// </summary>
    internal static async Task<IResult> SaveScheduleAsync(
        Guid id, SaveScheduleRequest request, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline before building a schedule." });
        }

        var pilot = await db.Pilots.FirstOrDefaultAsync(p => p.Id == id && p.AirlineId == airline.Id, ct);
        if (pilot is null)
        {
            return Results.NotFound();
        }

        if (pilot.IsPlayer)
        {
            return Results.BadRequest(new { error = "The player pilot cannot be given a standing schedule - they fly whatever they choose from the Fly screen." });
        }

        var (parseError, proposed) = ParseDutyDays(pilot.Id, request.DutyDays);
        if (parseError is not null)
        {
            return Results.BadRequest(new { error = parseError });
        }

        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);
        var otherEntries = await LoadOtherPilotsEntriesAsync(db, airline.Id, excludingPilotId: pilot.Id, ct);
        var unionEntries = otherEntries.Concat(proposed).ToList();

        var (routesById, fleetById, blockMinutesByLeg, existingRoutePairs) = await BuildValidationDataAsync(db, airline, economyConfig, unionEntries, ct);

        // requireWeekClosure: true - a SAVED week must genuinely repeat (docs/PLAN.md "the week
        // repeats indefinitely"), unlike the leg-options endpoint below, which is deliberately
        // asking a narrower, per-leg question about a week still under construction. Never weaken
        // this.
        var result = PilotScheduleValidator.Validate(unionEntries, routesById, fleetById, blockMinutesByLeg, economyConfig.Scheduling, existingRoutePairs, requireWeekClosure: true);
        if (!result.IsValid)
        {
            return Results.BadRequest(new { error = "This schedule has conflicts.", conflicts = result.Conflicts });
        }

        var now = DateTimeOffset.UtcNow;
        var schedule = await db.PilotSchedules.FirstOrDefaultAsync(s => s.PilotId == pilot.Id, ct);
        if (schedule is null)
        {
            schedule = new PilotSchedule { Id = Guid.NewGuid(), PilotId = pilot.Id, AirlineId = airline.Id, CreatedUtc = now };
            db.PilotSchedules.Add(schedule);
        }
        else
        {
            var existingEntries = await db.PilotScheduleEntries.Where(e => e.PilotScheduleId == schedule.Id).ToListAsync(ct);
            db.PilotScheduleEntries.RemoveRange(existingEntries);
        }

        // Defaults true when omitted (docs/PLAN.md "suspend during maintenance and resume
        // automatically" is the safe default) - an older client that has never heard of this field
        // must never silently clear it to false, see SaveScheduleRequest's own remarks.
        schedule.AutoSuspendOnMaintenance = request.AutoSuspendOnMaintenance ?? true;
        schedule.UpdatedUtc = now;

        foreach (var e in proposed)
        {
            db.PilotScheduleEntries.Add(new PilotScheduleEntry
            {
                Id = Guid.NewGuid(),
                PilotScheduleId = schedule.Id,
                DayOfWeek = e.DayOfWeek,
                DepartureTimeUtc = e.DepartureTimeUtc,
                RouteId = e.RouteId,
                FleetAircraftId = e.FleetAircraftId,
                CreatedUtc = now,
            });
        }

        await db.SaveChangesAsync(ct);

        var savedEntries = await db.PilotScheduleEntries.Where(e => e.PilotScheduleId == schedule.Id).ToListAsync(ct);
        var dto = await BuildDutyDayDtosAsync(db, savedEntries, ct);
        return Results.Ok(new { pilotId = pilot.Id, dutyDays = dto, autoSuspendOnMaintenance = schedule.AutoSuspendOnMaintenance });
    }

    /// <summary>
    /// Step one of the redesigned two-step picker (docs/PLAN.md "2a"/"2b"): "pick the aircraft
    /// first". Lists every fleet aircraft with a single eligibility verdict - reserved-for-player
    /// aircraft are never assignable (docs/PLAN.md "3a" - shown once, quietly, never repeated per
    /// route) and a currently-grounded aircraft is flagged with why and until when. This is
    /// deliberately NOT a full validator run: with no legs chosen yet there is nothing to check
    /// continuity or turnaround against, so this step only ever screens on the aircraft's own
    /// state. Real conflicts (overlap, continuity, rest) surface once the player asks
    /// <see cref="GetLegOptionsAsync"/> which legs the chosen aircraft can fly.
    /// </summary>
    internal static async Task<IResult> GetAircraftOptionsAsync(
        Guid id, AircraftOptionsRequest request, FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(new { options = Array.Empty<object>() });
        }

        var pilot = await db.Pilots.FirstOrDefaultAsync(p => p.Id == id && p.AirlineId == airline.Id, ct);
        if (pilot is null)
        {
            return Results.NotFound();
        }

        if (request.Day is < 0 or > 6)
        {
            return Results.BadRequest(new { error = "day must be 0-6 (Sunday-Saturday)." });
        }

        // Same lazy-release as the Fly screen's options endpoint - a grounding that has already
        // elapsed must not still show as blocking.
        await MaintenanceReleaser.ReleaseDueAsync(db, airline.Id, DateTimeOffset.UtcNow, ct);

        var fleet = (await db.FleetAircraft.Where(f => f.AirlineId == airline.Id).ToListAsync(ct))
            .OrderBy(f => f.CreatedUtc)
            .ToList();
        var typeIds = fleet.Select(f => f.AircraftTypeId).Distinct().ToList();
        var typesById = await db.AircraftTypes.Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);

        // Informational only - how many legs this aircraft already carries elsewhere in the SAVED
        // week, so the player can judge idle capacity while picking. Never blocking: a schedulable
        // aircraft can legitimately serve more than one pilot's day, as long as the legs themselves
        // don't overlap (checked for real in GetLegOptionsAsync).
        var scheduledElsewhere = await db.PilotScheduleEntries
            .Where(e => fleet.Select(f => f.Id).Contains(e.FleetAircraftId))
            .GroupBy(e => e.FleetAircraftId)
            .Select(g => new { FleetAircraftId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.FleetAircraftId, x => x.Count, ct);

        var options = fleet.Select(aircraft =>
        {
            // Same priority rule as the Fly screen's OptionsAsync: a hard physical blocker (in
            // maintenance right now) is reported before reservation, because releasing a grounded
            // aircraft would not actually make it schedulable either - telling the player "release
            // it" when that alone will not fix anything is the wrong reason to lead with (docs/PLAN.md
            // "2b"). Only say "reserved for the player" when releasing genuinely is sufficient.
            string? reason = null;
            if (aircraft.Status == FleetAircraftStatus.InMaintenance)
            {
                reason = aircraft.GroundedUntilUtc is { } until
                    ? $"{aircraft.Registration} is in maintenance until {until:yyyy-MM-dd HH:mm} UTC."
                    : $"{aircraft.Registration} is in maintenance.";
            }
            else if (aircraft.ReservedForPlayer)
            {
                reason = $"{aircraft.Registration} is reserved for the player - release it on the Fleet page to schedule it here.";
            }

            typesById.TryGetValue(aircraft.AircraftTypeId, out var type);
            scheduledElsewhere.TryGetValue(aircraft.Id, out var legCount);

            return (object)new
            {
                fleetAircraftId = aircraft.Id,
                aircraft.Registration,
                AircraftTypeName = type?.Name,
                LocationIcao = aircraft.LocationIcao,
                Eligible = reason is null,
                Reason = reason,
                ScheduledLegsThisWeek = legCount,
            };
        }).ToList();

        return Results.Ok(new { options });
    }

    /// <summary>
    /// Step two of the redesigned picker: with the aircraft already fixed, which routes can it fly
    /// at this day/time - see docs/PLAN.md "2a"/"2b"/"2c". Answers "does this leg fit with what
    /// you've built so far", not "is the whole week valid" - see the class-level remarks on
    /// aircraft-per-duty-day for why <c>requireWeekClosure: false</c> is correct here and must never
    /// be changed to true (a week under construction is legitimately open).
    /// <para>
    /// A reserved aircraft can never reach this validator at all (docs/PLAN.md "3a" - "not offered
    /// to the scheduler") - every route comes back illegal with the SAME single reason rather than
    /// running the full validator, which both matches "say it once, quietly" and avoids reporting a
    /// wall of near-identical per-route reasons for a setting the player already chose.
    /// </para>
    /// </summary>
    internal static async Task<IResult> GetLegOptionsAsync(
        Guid id, LegOptionsRequest request, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(new { legal = Array.Empty<object>(), illegal = Array.Empty<object>() });
        }

        var pilot = await db.Pilots.FirstOrDefaultAsync(p => p.Id == id && p.AirlineId == airline.Id, ct);
        if (pilot is null)
        {
            return Results.NotFound();
        }

        if (request.Day is < 0 or > 6 || !TimeSpan.TryParse(request.Time, out var departureTime))
        {
            return Results.BadRequest(new { error = "day must be 0-6 and time must be a time of day like '08:30'." });
        }

        if (request.FleetAircraftId is not Guid fleetAircraftId)
        {
            return Results.BadRequest(new { error = "fleetAircraftId is required - pick the aircraft for this duty day first." });
        }

        var fleetAircraft = await db.FleetAircraft.FirstOrDefaultAsync(f => f.Id == fleetAircraftId && f.AirlineId == airline.Id, ct);
        if (fleetAircraft is null)
        {
            return Results.BadRequest(new { error = "Aircraft not found." });
        }

        var dayOfWeek = (DayOfWeek)request.Day;
        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);

        var (parseError, draftOwnEntries) = ParseDutyDays(pilot.Id, request.DraftDutyDays);
        if (parseError is not null)
        {
            return Results.BadRequest(new { error = parseError });
        }

        var routes = await db.Routes.Where(r => r.AirlineId == airline.Id && r.IsActive).ToListAsync(ct);

        // Priority matters (2b: "one short reason ... ending in an action that fixes it", and see
        // PilotEndpoints' aircraft-options doc for the same rule) - a hard physical blocker (in
        // maintenance right now) is reported before reservation, because releasing a grounded
        // aircraft would not actually make it usable either. Grounding is deliberately NOT part of
        // PilotScheduleValidator (which reasons about the week as an abstract, indefinitely-repeating
        // template - a momentary A/C-check must never permanently block saving a schedule that will
        // be perfectly flyable once it lifts); here the player is looking at "can I use this aircraft
        // right now", so today's real grounding state is a genuinely useful signal, checked first and
        // never fed back into what PUT /schedule enforces.
        if (fleetAircraft.Status == FleetAircraftStatus.InMaintenance)
        {
            var groundingReason = fleetAircraft.GroundedUntilUtc is { } until
                ? $"{fleetAircraft.Registration} is in maintenance until {until:yyyy-MM-dd HH:mm} UTC."
                : $"{fleetAircraft.Registration} is in maintenance.";
            var groundedIllegal = routes.Select(r => (object)new { routeId = r.Id, reason = groundingReason }).ToList();
            return Results.Ok(new { legal = Array.Empty<object>(), illegal = groundedIllegal });
        }

        if (fleetAircraft.ReservedForPlayer)
        {
            var reservedIllegal = routes
                .Select(r => (object)new
                {
                    routeId = r.Id,
                    reason = $"{fleetAircraft.Registration} is reserved for the player - release it on the Fleet page to schedule it here.",
                })
                .ToList();
            return Results.Ok(new { legal = Array.Empty<object>(), illegal = reservedIllegal });
        }

        var otherEntries = await LoadOtherPilotsEntriesAsync(db, airline.Id, excludingPilotId: pilot.Id, ct);
        var baseline = otherEntries.Concat(draftOwnEntries).ToList();

        var candidates = routes.Select(route => new PilotScheduleEntryInput(pilot.Id, dayOfWeek, departureTime, route.Id, fleetAircraftId)).ToList();
        var allEntriesForValidation = baseline.Concat(candidates).ToList();
        var (routesById, fleetById, blockMinutesByLeg, existingRoutePairs) = await BuildValidationDataAsync(db, airline, economyConfig, allEntriesForValidation, ct);

        var baselineConflicts = PilotScheduleValidator
            .Validate(baseline, routesById, fleetById, blockMinutesByLeg, economyConfig.Scheduling, existingRoutePairs, requireWeekClosure: false)
            .Conflicts.ToHashSet();

        var legal = new List<object>();
        var illegal = new List<object>();

        foreach (var candidate in candidates)
        {
            var route = routesById[candidate.RouteId];

            var withCandidate = baseline.Append(candidate).ToList();
            var candidateResult = PilotScheduleValidator.Validate(
                withCandidate, routesById, fleetById, blockMinutesByLeg, economyConfig.Scheduling, existingRoutePairs, requireWeekClosure: false);
            var newConflicts = candidateResult.Conflicts.Where(c => !baselineConflicts.Contains(c)).ToList();

            if (newConflicts.Count == 0)
            {
                legal.Add(new { routeId = candidate.RouteId, route.DepartureIcao, route.ArrivalIcao, route.FlightNumber });
            }
            else
            {
                illegal.Add(new { routeId = candidate.RouteId, reason = newConflicts[0] });
            }
        }

        return Results.Ok(new { legal, illegal });
    }

    /// <summary>
    /// Read-only, airline-wide - see docs/PLAN.md "2a": "every aircraft as a row, its legs across
    /// the week, colour-coded by pilot" plus toggleable by-pilot/by-aircraft views of the same week.
    /// Both shapes are returned together from one call since they are the same underlying data
    /// (every persisted <see cref="PilotScheduleEntry"/> for the airline), just grouped two ways -
    /// there is nothing to edit here, only to look at, so a single read covers both toggle states
    /// without asking the client to reconcile two separate responses.
    /// </summary>
    internal static async Task<IResult> GetScheduleOverviewAsync(FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(new { byAircraft = Array.Empty<object>(), byPilot = Array.Empty<object>() });
        }

        var schedules = await db.PilotSchedules.Where(s => s.AirlineId == airline.Id).ToListAsync(ct);
        var scheduleIds = schedules.Select(s => s.Id).ToList();
        var pilotIdByScheduleId = schedules.ToDictionary(s => s.Id, s => s.PilotId);

        var entries = scheduleIds.Count == 0
            ? new List<PilotScheduleEntry>()
            : await db.PilotScheduleEntries.Where(e => scheduleIds.Contains(e.PilotScheduleId)).ToListAsync(ct);

        var pilots = await db.Pilots.Where(p => p.AirlineId == airline.Id && !p.IsPlayer).ToListAsync(ct);
        var pilotsById = pilots.ToDictionary(p => p.Id);

        var routeIds = entries.Select(e => e.RouteId).Distinct().ToList();
        var routesById = await db.Routes.Where(r => routeIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var fleet = await db.FleetAircraft.Where(f => f.AirlineId == airline.Id).ToListAsync(ct);
        var fleetById = fleet.ToDictionary(f => f.Id);

        var enrichedLegs = entries.Select(e =>
        {
            routesById.TryGetValue(e.RouteId, out var route);
            var pilotId = pilotIdByScheduleId.TryGetValue(e.PilotScheduleId, out var pid) ? pid : Guid.Empty;
            pilotsById.TryGetValue(pilotId, out var pilot);

            return new
            {
                e.FleetAircraftId,
                PilotId = pilotId,
                PilotName = pilot?.Name,
                DayOfWeek = (int)e.DayOfWeek,
                DepartureTimeUtc = e.DepartureTimeUtc.ToString(@"hh\:mm\:ss"),
                e.RouteId,
                DepartureIcao = route?.DepartureIcao,
                ArrivalIcao = route?.ArrivalIcao,
                FlightNumber = route?.FlightNumber,
            };
        }).ToList();

        var byAircraft = fleet
            .OrderBy(f => f.CreatedUtc)
            .Select(f => (object)new
            {
                fleetAircraftId = f.Id,
                f.Registration,
                f.LocationIcao,
                Legs = enrichedLegs
                    .Where(l => l.FleetAircraftId == f.Id)
                    .OrderBy(l => l.DayOfWeek).ThenBy(l => l.DepartureTimeUtc)
                    .ToList(),
            })
            .ToList();

        var byPilot = pilots
            .OrderBy(p => p.CreatedUtc)
            .Select(p => (object)new
            {
                pilotId = p.Id,
                p.Name,
                DutyDays = enrichedLegs
                    .Where(l => l.PilotId == p.Id)
                    .GroupBy(l => l.DayOfWeek)
                    .OrderBy(g => g.Key)
                    .Select(g => (object)new
                    {
                        DayOfWeek = g.Key,
                        FleetAircraftId = g.First().FleetAircraftId,
                        Registration = fleetById.TryGetValue(g.First().FleetAircraftId, out var a) ? a.Registration : null,
                        Legs = g.OrderBy(l => l.DepartureTimeUtc).ToList(),
                    })
                    .ToList(),
            })
            .ToList();

        return Results.Ok(new { byAircraft, byPilot });
    }

    private static async Task<Pilot?> LoadOwnedPilotAsync(FsOpsDbContext db, ICurrentUser currentUser, Guid pilotId, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return null;
        }

        return await db.Pilots.FirstOrDefaultAsync(p => p.Id == pilotId && p.AirlineId == airline.Id, ct);
    }

    /// <summary>
    /// Expands the duty-day-shaped request into the flat <see cref="PilotScheduleEntryInput"/> list
    /// the validator needs, checking as it goes that every day with legs actually named an aircraft
    /// (a day with legs and no aircraft is a client bug, not a schedulable state) - see this class's
    /// own doc for why the wire shape carries one aircraft per day rather than one per leg. An empty
    /// day (no legs) is simply skipped, aircraft or not - there is nothing to validate about a day
    /// nobody is flying.
    /// </summary>
    private static (string? Error, List<PilotScheduleEntryInput> Entries) ParseDutyDays(Guid pilotId, IReadOnlyList<DutyDayRequest>? dutyDays)
    {
        var result = new List<PilotScheduleEntryInput>();
        foreach (var day in dutyDays ?? Array.Empty<DutyDayRequest>())
        {
            if (day.DayOfWeek is < 0 or > 6)
            {
                return ($"dayOfWeek must be 0-6 (Sunday-Saturday), was {day.DayOfWeek}.", result);
            }

            var legs = day.Legs ?? Array.Empty<DutyLegRequest>();
            if (legs.Count == 0)
            {
                continue;
            }

            if (day.FleetAircraftId is not Guid fleetAircraftId)
            {
                return ($"{(DayOfWeek)day.DayOfWeek} has legs but no aircraft chosen - pick an aircraft for this duty day first.", result);
            }

            foreach (var leg in legs)
            {
                if (!TimeSpan.TryParse(leg.DepartureTimeUtc, out var departureTime) || departureTime < TimeSpan.Zero || departureTime >= TimeSpan.FromDays(1))
                {
                    return ($"departureTimeUtc '{leg.DepartureTimeUtc}' must be a time of day like '08:30:00'.", result);
                }

                if (leg.RouteId is not Guid routeId)
                {
                    return ("Every leg needs a routeId.", result);
                }

                result.Add(new PilotScheduleEntryInput(pilotId, (DayOfWeek)day.DayOfWeek, departureTime, routeId, fleetAircraftId));
            }
        }

        return (null, result);
    }

    /// <summary>Every OTHER pilot's currently-saved entries for this airline, in validator input
    /// shape - the "everyone else's chains" half of the whole-airline validation merge.</summary>
    private static async Task<List<PilotScheduleEntryInput>> LoadOtherPilotsEntriesAsync(
        FsOpsDbContext db, Guid airlineId, Guid excludingPilotId, CancellationToken ct)
    {
        var otherSchedules = await db.PilotSchedules
            .Where(s => s.AirlineId == airlineId && s.PilotId != excludingPilotId)
            .ToListAsync(ct);
        if (otherSchedules.Count == 0)
        {
            return new List<PilotScheduleEntryInput>();
        }

        var scheduleIds = otherSchedules.Select(s => s.Id).ToList();
        var pilotIdByScheduleId = otherSchedules.ToDictionary(s => s.Id, s => s.PilotId);

        var entries = await db.PilotScheduleEntries.Where(e => scheduleIds.Contains(e.PilotScheduleId)).ToListAsync(ct);
        return entries
            .Select(e => new PilotScheduleEntryInput(pilotIdByScheduleId[e.PilotScheduleId], e.DayOfWeek, e.DepartureTimeUtc, e.RouteId, e.FleetAircraftId))
            .ToList();
    }

    /// <summary>Loads exactly the routes/fleet/block-minutes the validator needs for a given set of
    /// entries - airline-scoped dictionaries plus a per-(route, aircraft) block-minutes cache
    /// computed with the same RoutePreviewCalculator every other flight-time-estimate call site
    /// uses, so a schedule's block-time figures can never quietly disagree with the Fly screen's.
    /// Also returns every active route the AIRLINE has, as departure/arrival ICAO pairs - deliberately
    /// NOT limited to routesById (which only covers routes these entries reference), because the
    /// validator needs to know whether a connecting route exists even when nothing is scheduled on
    /// it yet (docs/PLAN.md "2b").</summary>
    private static async Task<(
        Dictionary<Guid, Route> RoutesById,
        Dictionary<Guid, FleetAircraft> FleetById,
        Dictionary<(Guid RouteId, Guid FleetAircraftId), int> BlockMinutesByLeg,
        HashSet<(string DepartureIcao, string ArrivalIcao)> ExistingRoutePairs)> BuildValidationDataAsync(
        FsOpsDbContext db, Airline airline, EconomyConfig economyConfig, IReadOnlyList<PilotScheduleEntryInput> entries, CancellationToken ct)
    {
        var routeIds = entries.Select(e => e.RouteId).Distinct().ToList();
        var fleetIds = entries.Select(e => e.FleetAircraftId).Distinct().ToList();

        var routesById = await db.Routes.Where(r => routeIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var fleetById = await db.FleetAircraft.Where(f => fleetIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);

        var typeIds = fleetById.Values.Select(f => f.AircraftTypeId).Distinct().ToList();
        var typesById = await db.AircraftTypes.Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);

        var icaos = routesById.Values.SelectMany(r => new[] { r.DepartureIcao, r.ArrivalIcao }).Distinct().ToList();
        var airportsByIcao = await db.Airports.Where(a => icaos.Contains(a.Icao)).ToDictionaryAsync(a => a.Icao, ct);

        var blockMinutesByLeg = new Dictionary<(Guid, Guid), int>();
        foreach (var entry in entries)
        {
            var key = (entry.RouteId, entry.FleetAircraftId);
            if (blockMinutesByLeg.ContainsKey(key) ||
                !routesById.TryGetValue(entry.RouteId, out var route) ||
                !fleetById.TryGetValue(entry.FleetAircraftId, out var aircraft) ||
                !typesById.TryGetValue(aircraft.AircraftTypeId, out var type) ||
                !airportsByIcao.TryGetValue(route.DepartureIcao, out var dep) ||
                !airportsByIcao.TryGetValue(route.ArrivalIcao, out var arr))
            {
                continue;
            }

            var plan = RoutePreviewCalculator.Calculate(economyConfig, dep, arr, type, airline.StrategyProfile);
            blockMinutesByLeg[key] = plan.BlockTimeBreakdown.TotalMinutes;
        }

        var existingRoutePairs = await db.Routes
            .Where(r => r.AirlineId == airline.Id && r.IsActive)
            .Select(r => new { r.DepartureIcao, r.ArrivalIcao })
            .ToListAsync(ct);
        var existingRoutePairSet = existingRoutePairs
            .Select(r => (r.DepartureIcao.ToUpperInvariant(), r.ArrivalIcao.ToUpperInvariant()))
            .ToHashSet();

        return (routesById, fleetById, blockMinutesByLeg, existingRoutePairSet);
    }

    /// <summary>Groups a flat list of persisted entries into the duty-day-shaped response DTO - the
    /// mirror image of <see cref="ParseDutyDays"/>. Every entry in one duty-day group shares its
    /// <c>FleetAircraftId</c> (enforced at save time by <see cref="PilotScheduleValidator"/>), so
    /// the aircraft is read once from the group's first entry rather than repeated per leg.</summary>
    private static async Task<IReadOnlyList<object>> BuildDutyDayDtosAsync(FsOpsDbContext db, IReadOnlyList<PilotScheduleEntry> entries, CancellationToken ct)
    {
        if (entries.Count == 0)
        {
            return Array.Empty<object>();
        }

        var routeIds = entries.Select(e => e.RouteId).Distinct().ToList();
        var routesById = await db.Routes.Where(r => routeIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var fleetIds = entries.Select(e => e.FleetAircraftId).Distinct().ToList();
        var fleetById = await db.FleetAircraft.Where(f => fleetIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);
        var typeIds = fleetById.Values.Select(f => f.AircraftTypeId).Distinct().ToList();
        var typesById = await db.AircraftTypes.Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var icaos = routesById.Values.SelectMany(r => new[] { r.DepartureIcao, r.ArrivalIcao }).Distinct().ToList();
        var airportsByIcao = await db.Airports.Where(a => icaos.Contains(a.Icao)).ToDictionaryAsync(a => a.Icao, ct);

        return entries
            .OrderBy(e => (int)e.DayOfWeek).ThenBy(e => e.DepartureTimeUtc)
            .GroupBy(e => e.DayOfWeek)
            .OrderBy(g => (int)g.Key)
            .Select(dayGroup =>
            {
                var first = dayGroup.First();
                fleetById.TryGetValue(first.FleetAircraftId, out var dayAircraft);

                var legs = dayGroup.Select(e =>
                {
                    routesById.TryGetValue(e.RouteId, out var route);
                    int? blockMinutes = null;
                    if (route is not null && fleetById.TryGetValue(e.FleetAircraftId, out var legAircraft) &&
                        typesById.TryGetValue(legAircraft.AircraftTypeId, out var type) &&
                        airportsByIcao.TryGetValue(route.DepartureIcao, out var dep) &&
                        airportsByIcao.TryGetValue(route.ArrivalIcao, out var arr))
                    {
                        // Casual approximation for display - the same call the resolver itself
                        // makes, so this can never show a different block time than what actually
                        // resolves.
                        blockMinutes = BlockTimeEstimator.Estimate(GreatCircle.DistanceNm(dep.Latitude, dep.Longitude, arr.Latitude, arr.Longitude), type.CruiseTasKts).TotalMinutes;
                    }

                    return (object)new
                    {
                        e.Id,
                        DepartureTimeUtc = e.DepartureTimeUtc.ToString(@"hh\:mm\:ss"),
                        e.RouteId,
                        DepartureIcao = route?.DepartureIcao,
                        ArrivalIcao = route?.ArrivalIcao,
                        FlightNumber = route?.FlightNumber,
                        BlockMinutes = blockMinutes,
                    };
                }).ToList();

                return (object)new
                {
                    DayOfWeek = (int)dayGroup.Key,
                    FleetAircraftId = first.FleetAircraftId,
                    Registration = dayAircraft?.Registration,
                    Legs = legs,
                };
            })
            .ToList();
    }

    private static async Task<WeeklySummary> ComputeWeeklySummaryAsync(
        FsOpsDbContext db, Airline airline, EconomyConfig economyConfig, Guid pilotId, CancellationToken ct)
    {
        var schedule = await db.PilotSchedules.FirstOrDefaultAsync(s => s.PilotId == pilotId, ct);
        if (schedule is null)
        {
            return WeeklySummary.None;
        }

        var entries = await db.PilotScheduleEntries.Where(e => e.PilotScheduleId == schedule.Id).ToListAsync(ct);
        if (entries.Count == 0)
        {
            return WeeklySummary.None;
        }

        var routeIds = entries.Select(e => e.RouteId).Distinct().ToList();
        var routesById = await db.Routes.Where(r => routeIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var fleetIds = entries.Select(e => e.FleetAircraftId).Distinct().ToList();
        var fleetById = await db.FleetAircraft.Where(f => fleetIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);
        var typeIds = fleetById.Values.Select(f => f.AircraftTypeId).Distinct().ToList();
        var typesById = await db.AircraftTypes.Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var icaos = routesById.Values.SelectMany(r => new[] { r.DepartureIcao, r.ArrivalIcao }).Distinct().ToList();
        var airportsByIcao = await db.Airports.Where(a => icaos.Contains(a.Icao)).ToDictionaryAsync(a => a.Icao, ct);
        var worldSeed = await FlightEconomicsPoster.ResolveWorldSeedAsync(db, ct);
        var now = DateTimeOffset.UtcNow;

        decimal revenue = 0m;
        decimal cost = 0m;

        foreach (var entry in entries)
        {
            if (!routesById.TryGetValue(entry.RouteId, out var route) ||
                !fleetById.TryGetValue(entry.FleetAircraftId, out var aircraft) ||
                !typesById.TryGetValue(aircraft.AircraftTypeId, out var type) ||
                !airportsByIcao.TryGetValue(route.DepartureIcao, out var dep) ||
                !airportsByIcao.TryGetValue(route.ArrivalIcao, out var arr))
            {
                continue;
            }

            var plan = RoutePreviewCalculator.Calculate(economyConfig, dep, arr, type, airline.StrategyProfile);
            var referenceFare = ReferenceFareCalculator.Calculate(economyConfig, airline.StrategyProfile, route.DistanceNm);
            var marketDemandPax = DemandCalculator.AvailablePassengers(economyConfig.Demand, dep.SizeCategory, arr.SizeCategory, route.DistanceNm, now, airline.ReputationScore);
            var pricePerKg = FuelPricing.PricePerKg(economyConfig.Fuel, dep.Icao, dep.Country, now, worldSeed);

            var result = FlightEconomicsCalculator.Calculate(
                economyConfig, airline.StrategyProfile, route.BaseFare, referenceFare, type.PaxCapacity, marketDemandPax,
                upliftKg: plan.FuelBreakdown.ChargedFuelKg, pricePerKgAtUpliftAirport: pricePerKg,
                arr.SizeCategory, type.MtowTonnes, plan.BlockTimeBreakdown.TotalMinutes / 60.0);

            revenue += result.TicketRevenue;
            cost += result.TotalCost;
        }

        return new WeeklySummary(entries.Count, Math.Round(revenue, 2), Math.Round(cost, 2));
    }

    private static async Task<decimal> CashBalanceAsync(FsOpsDbContext db, Guid airlineId, CancellationToken ct)
    {
        var amounts = await db.LedgerTransactions.Where(t => t.AirlineId == airlineId).Select(t => t.Amount).ToListAsync(ct);
        return amounts.Sum();
    }

    private sealed record WeeklySummary(int SectorsPerWeek, decimal EstimatedRevenue, decimal EstimatedCost)
    {
        public static WeeklySummary None { get; } = new(0, 0m, 0m);
    }
}

public record HirePilotRequest(string? Name);

/// <summary>One leg inside a <see cref="DutyDayRequest"/> - deliberately carries no aircraft field
/// of its own, see PilotEndpoints' class doc.</summary>
public record DutyLegRequest(string DepartureTimeUtc, Guid? RouteId);

/// <summary>One duty day: the aircraft the player picked for it, then the legs dropped into it in
/// departure order. <see cref="FleetAircraftId"/> may be null only when <see cref="Legs"/> is empty
/// or absent (a day nobody has started building yet) - see PilotEndpoints.ParseDutyDays.</summary>
public record DutyDayRequest(int DayOfWeek, Guid? FleetAircraftId, IReadOnlyList<DutyLegRequest>? Legs);

/// <summary>
/// <see cref="AutoSuspendOnMaintenance"/> mirrors <see cref="FSOps.Core.Entities.PilotSchedule.AutoSuspendOnMaintenance"/>
/// - <c>null</c> means the field was omitted from the wire payload (an older client that predates
/// this setting), which <see cref="PilotEndpoints.SaveScheduleAsync"/> must treat as "true", never
/// "false" - see that method's own remarks for why silently clearing it would be the wrong default.
/// </summary>
public record SaveScheduleRequest(IReadOnlyList<DutyDayRequest>? DutyDays, bool? AutoSuspendOnMaintenance = null);

/// <summary>Body for POST /pilots/{id}/schedule/aircraft-options.</summary>
public record AircraftOptionsRequest(int Day);

/// <summary>
/// Body for POST /pilots/{id}/schedule/leg-options. <see cref="Day"/> is 0-6 (Sunday-Saturday,
/// matching <see cref="System.DayOfWeek"/>), <see cref="Time"/> a time of day like "08:30",
/// <see cref="FleetAircraftId"/> the aircraft already chosen for this duty day (step one of the
/// picker). <see cref="DraftDutyDays"/> is this pilot's in-progress week exactly as the client has
/// built it so far - not yet saved, and entirely replacing what would otherwise be read from the
/// database for this pilot (mirrors PUT's whole-week replacement so a leg being added to a day in
/// progress is judged against the real, up-to-date aircraft position rather than a stale one).
/// </summary>
public record LegOptionsRequest(int Day, string Time, Guid? FleetAircraftId, IReadOnlyList<DutyDayRequest>? DraftDutyDays);
