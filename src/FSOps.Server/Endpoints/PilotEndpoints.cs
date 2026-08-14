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
/// Hiring virtual pilots and building their standing weekly schedule. Assignment is something you
/// set once and leave - a repeating week, not one flight at a time, because the point of virtual
/// pilots is that the airline keeps running while you are not flying. The actual wall-clock
/// resolution of what a saved schedule produces happens in
/// <see cref="FSOps.Server.Services.VirtualFlightResolverService"/>, not here - these endpoints
/// only ever read/write the schedule template and validate it with
/// <see cref="PilotScheduleValidator"/>, which is airline-scoped (an aircraft's chain and a
/// pilot's rest both have to account for every OTHER pilot touching the same aircraft, not just
/// the one being edited).
/// <para>
/// <b>Aircraft-per-duty-day.</b> The wire shape is deliberately
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

        var now = DateTimeOffset.UtcNow;
        var summaries = new List<object>();
        foreach (var pilot in pilots)
        {
            var weekly = pilot.IsPlayer
                ? WeeklySummary.None
                : await ComputeWeeklySummaryAsync(db, airline, economyConfig, pilot.Id, ct);

            // SkillRating is only refreshed on disk each time VirtualFlightResolverService's
            // periodic pass runs (see its ApplyIdlePilotSkillDecay) - a pilot left idle between
            // passes could otherwise show a figure that's already stale by the time the player
            // looks at it. PilotSkillCalculator.Compute is pure and safe to call for display at any
            // time (see its own doc), so this always shows the true-right-now number rather than
            // whatever the last resolver tick happened to write. EarnedSkillRating is the same
            // pilot's hours-only figure with no idle decay applied, so the UI can show "earned X,
            // currently Y" rather than a smaller number with no explanation. Decay has to be
            // visible and understandable before it bites, or it just reads as the app taking
            // something away.
            var liveSkillRating = PilotSkillCalculator.Compute(pilot.HoursFlown, pilot.LastFlewUtc, now, economyConfig.PilotSkill);
            var earnedSkillRating = PilotSkillCalculator.ComputeEarnedSkill(pilot.HoursFlown, economyConfig.PilotSkill);

            double? idleDays = null;
            bool isDecaying = false;
            double? decayGraceDaysRemaining = null;
            if (!pilot.IsPlayer && pilot.LastFlewUtc is { } lastFlew)
            {
                var idleHours = Math.Max(0, (now - lastFlew).TotalHours);
                idleDays = Math.Round(idleHours / 24.0, 1);
                isDecaying = idleHours > economyConfig.PilotSkill.IdleGracePeriodHours;
                decayGraceDaysRemaining = isDecaying
                    ? 0
                    : Math.Round((economyConfig.PilotSkill.IdleGracePeriodHours - idleHours) / 24.0, 1);
            }

            summaries.Add(new
            {
                pilot.Id,
                pilot.Name,
                pilot.IsPlayer,
                pilot.MonthlySalary,
                pilot.HoursFlown,
                SkillRating = Math.Round(liveSkillRating, 1),
                EarnedSkillRating = Math.Round(earnedSkillRating, 1),
                pilot.LastFlewUtc,
                IdleDays = idleDays,
                IsDecaying = isDecaying,
                DecayGraceDaysRemaining = decayGraceDaysRemaining,
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
    /// Hires a virtual pilot at the playstyle's standard salary. Hiring has to be affordable early
    /// enough to matter - if the first pilot is out of reach, a casual player is stranded with
    /// costs designed for utilisation they cannot reach alone. So there is no upfront cost and no
    /// cash-balance gate, only the
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

        // Releasing soft-deletes the pilot and cascades to their schedule below, so doing it to
        // someone with a sector in the air would leave that flight owned by a deleted pilot -
        // recoverable, but only by hand. Refuse instead. Note this cannot lean on Pilot.Status:
        // that column only ever reads Available (nothing in the app writes Flying), so the flight
        // itself is the sole reliable answer to "are they up right now".
        var flightInProgress = await db.Flights
            .AnyAsync(f => f.PilotId == pilot.Id && f.Status == FlightStatus.InProgress, ct);
        if (flightInProgress)
        {
            return Results.BadRequest(new
            {
                error = $"{pilot.Name} is in the air right now. Wait for the flight to finish before releasing them.",
            });
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
    /// Replaces this pilot's ENTIRE week in one call - the schedule is set once and left, which is
    /// the whole reason the builder is a week grid rather than one-flight-at-a-time administration.
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

        var (routesById, fleetById, aircraftTypesById, blockMinutesByLeg, existingRoutePairs, airportsByIcao) = await BuildValidationDataAsync(db, airline, economyConfig, unionEntries, ct);

        // requireWeekClosure: true - a SAVED week must genuinely repeat indefinitely, so Saturday's
        // last leg on an aircraft has to connect back to Sunday's first. Unlike the leg-options
        // endpoint below, which is deliberately
        // asking a narrower, per-leg question about a week still under construction. Never weaken
        // this.
        var result = PilotScheduleValidator.Validate(unionEntries, routesById, fleetById, aircraftTypesById, blockMinutesByLeg, airportsByIcao, economyConfig.Scheduling, existingRoutePairs, requireWeekClosure: true);
        if (!result.IsValid)
        {
            // Only what THIS save actually introduces. Validating airline-wide is right and stays
            // (an aircraft's chain and double-booking span every pilot), but reporting airline-wide
            // is not: a conflict that is equally true whether or not this pilot's week is saved was
            // not caused by it, cannot be fixed from the screen the player is looking at, and may
            // name an aircraft that appears nowhere in the week in front of them. Real-use defect,
            // 2026-08-13: saving one virtual pilot's week was refused with "G-NZHG is at LFPG, but
            // this pattern's first leg departs EGGD" while every day on screen showed a different
            // airframe entirely - the conflict belonged to ANOTHER pilot's already-saved schedule.
            // Same "before" vs "after" set difference GetLegOptionsAsync has always used to tell a
            // genuine disqualifier from a pre-existing one; nothing is relaxed, because a save that
            // genuinely worsens another pilot's chain produces a conflict sentence that is NOT in
            // the baseline (different aircraft, airports or times) and is therefore still refused,
            // and the other pilot's own save still faces their own conflict exactly as before.
            var preExisting = PilotScheduleValidator
                .Validate(otherEntries, routesById, fleetById, aircraftTypesById, blockMinutesByLeg, airportsByIcao, economyConfig.Scheduling, existingRoutePairs, requireWeekClosure: true)
                .Conflicts.ToHashSet();
            var introduced = result.Conflicts.Where(c => !preExisting.Contains(c)).ToList();

            if (introduced.Count > 0)
            {
                return Results.BadRequest(new { error = "This schedule has conflicts.", conflicts = introduced });
            }
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

        // Defaults true when omitted: suspending during maintenance and resuming automatically is
        // the safe default, since a cancellation fee is meant to punish a schedule the player built
        // badly, never maintenance the simulation imposed. An older client that has never heard of this field
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

        // Same "only what the player can see and act on" rule the conflict differential above
        // applies, for the same reason: an advisory about an airframe that appears nowhere in the
        // week they just saved is the exact unactionable message this whole fix exists to remove.
        // Advisories are always worded "{registration} is at ...", so the leading token identifies
        // the aircraft without needing a second structured channel for one sentence.
        var ownRegistrations = proposed
            .Select(e => fleetById.TryGetValue(e.FleetAircraftId, out var a) ? a.Registration : null)
            .Where(r => r is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var advisories = result.Advisories
            .Where(a => ownRegistrations.Contains(a.Split(' ', 2)[0]))
            .ToList();

        return Results.Ok(new { pilotId = pilot.Id, dutyDays = dto, autoSuspendOnMaintenance = schedule.AutoSuspendOnMaintenance, advisories });
    }

    /// <summary>
    /// Step one of the redesigned two-step picker: pick the aircraft FIRST, then the leg. The
    /// original builder listed every route x aircraft combination, which produced six near-
    /// identical paragraphs for one slot and could not be scanned. Lists every fleet aircraft with
    /// a single eligibility verdict - reserved-for-player aircraft are never assignable (said once,
    /// quietly, never repeated against every route) and a currently-grounded aircraft is flagged
    /// with why and until when. This is
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
            // it" when that alone will not fix anything is the wrong reason to lead with: every
            // reason shown should end in an action that actually works. Only say "reserved for the
            // player" when releasing genuinely is sufficient.
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
    /// at this day/time. Answers "does this leg fit with what
    /// you've built so far", not "is the whole week valid" - see the class-level remarks on
    /// aircraft-per-duty-day for why <c>requireWeekClosure: false</c> is correct here and must never
    /// be changed to true (a week under construction is legitimately open).
    /// <para>
    /// A reserved aircraft can never reach this validator at all - it is not offered to the
    /// scheduler in the first place, so the conflict cannot be created. Every route comes back
    /// illegal with the SAME single reason rather than
    /// running the full validator, which both matches "say it once, quietly" and avoids reporting a
    /// wall of near-identical per-route reasons for a setting the player already chose.
    /// </para>
    /// <para>
    /// <b>A conflict with what comes BEFORE this slot disqualifies a candidate; a conflict that only
    /// exists against a LATER already-drafted entry does not</b> (real-use defect, 2026-08-12: a
    /// pilot flying an out-and-back round trip five identical days a week could never add a further
    /// leg at all, because inserting either half of a new round trip on its own always looked like it
    /// stranded the aircraft away from the very next already-drafted day - the incremental picker was
    /// enforcing an END-STATE invariant, closure included, at every intermediate step, so neither half
    /// of a paired leg could ever be legal before the other existed). Nothing the player does later
    /// can make "the aircraft isn't there yet" or "this route is out of range" untrue, so those stay
    /// hard refusals. A LATER conflict is different: it is a consequence of picking this leg that the
    /// player is about to take on, not a reason they can't - it is resolvable with the very next leg
    /// they add (a return leg, or an edit to what already follows), so it is surfaced as a
    /// <c>warnings</c> entry on an otherwise-legal, still-selectable option rather than hidden behind
    /// "not available". See <see cref="GetLegOptionsAsync"/>'s body for exactly how "before" and
    /// "after" are told apart - by re-running the same validator against two different slices of the
    /// same entries, never by relaxing what it checks. Nothing here changes what
    /// <see cref="SaveScheduleAsync"/> requires: <c>requireWeekClosure: true</c> there still refuses
    /// an unclosed week outright, so a warning shown here is a promise the player still has to keep
    /// before the week can actually be saved, never a permission slip.
    /// </para>
    /// <para>
    /// <b>Every legal option also carries what it is worth.</b> <c>expectedNetProfit</c> is one
    /// sector's expected profit on that route flown by THIS aircraft, and
    /// <c>scheduledLegsThisWeek</c> is how many legs other pilots already fly on it. Both are
    /// informational, exactly like <c>aircraftPosition</c> - they are what lets a caller rank the
    /// options it has been given, and neither has any say in which options it is given. See where
    /// they are computed below for how they are derived and why they are aircraft-specific.
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
        var (routesById, fleetById, aircraftTypesById, blockMinutesByLeg, existingRoutePairs, airportsByIcao) = await BuildValidationDataAsync(db, airline, economyConfig, allEntriesForValidation, ct);

        var baselineResult = PilotScheduleValidator
            .Validate(baseline, routesById, fleetById, aircraftTypesById, blockMinutesByLeg, airportsByIcao, economyConfig.Scheduling, existingRoutePairs, requireWeekClosure: false);
        var baselineConflicts = baselineResult.Conflicts.ToHashSet();
        var baselineGaps = baselineResult.ContinuityGaps.ToHashSet();

        // The "before" slice: only what's already committed strictly earlier than this slot (any
        // pilot, any aircraft - reservation/range/runway are structural per-entry checks that fire
        // regardless, and continuity/rest are pairwise, so nothing chronologically LATER than the
        // candidate can ever appear as its "next" entry once it's excluded here). Validating against
        // this slice, and only this slice, is what tells a genuine, permanent disqualifier (the
        // aircraft isn't there yet, no rest since the last already-drafted duty day, ...) apart from
        // a conflict that only exists because of something drafted AFTER this slot - see this
        // method's own remarks above for why that distinction is the whole fix.
        var slotMinute = AbsoluteWeekMinuteFor(dayOfWeek, departureTime);
        var beforeBaseline = baseline.Where(e => AbsoluteWeekMinuteFor(e.DayOfWeek, e.DepartureTimeUtc) < slotMinute).ToList();
        var beforeBaselineConflicts = PilotScheduleValidator
            .Validate(beforeBaseline, routesById, fleetById, aircraftTypesById, blockMinutesByLeg, airportsByIcao, economyConfig.Scheduling, existingRoutePairs, requireWeekClosure: false)
            .Conflicts.ToHashSet();

        // Display-only - never used to decide legality (see this method's own remarks: every route
        // is still tested, exactly as before). The arrival ICAO of whichever of THIS aircraft's own
        // entries in beforeBaseline departs latest, or its recorded LocationIcao if nothing precedes
        // the slot yet - the same anchor PilotScheduleValidator's own chain check uses for a week's
        // real first leg. Lets the picker lead with "G-TXFE is at EGGD" instead of making the player
        // infer it from which options happen to be legal.
        var aircraftPosition = beforeBaseline
            .Where(e => e.FleetAircraftId == fleetAircraftId)
            .OrderByDescending(e => AbsoluteWeekMinuteFor(e.DayOfWeek, e.DepartureTimeUtc))
            .Select(e => routesById.TryGetValue(e.RouteId, out var r) ? r.ArrivalIcao : null)
            .FirstOrDefault() ?? fleetAircraft.LocationIcao;

        // What each candidate route would actually EARN, flown by THIS aircraft. Deliberately not a
        // second economic model: the same ReferenceFareCalculator / DemandCalculator /
        // FlightEconomicsCalculator chain the weekly summary below and FlightEconomicsPoster (the
        // real, money-moving path) both run, so a figure quoted here can never disagree with what
        // the sector eventually posts to the ledger for reasons of its own.
        //
        // <b>Informational only.</b> Nothing about this decides whether an option is legal - every
        // route is still tested by PilotScheduleValidator exactly as before, and a route with a
        // magnificent profit and a conflict is still illegal. It exists so callers can RANK options
        // that are already legal: the "Suggest a starter schedule" generator picks the most
        // profitable leg the aircraft can fly rather than merely the first legal one.
        //
        // <b>Aircraft-specific by construction</b>, which is the whole point: seats feed the booking
        // model, MTOW feeds the fee lines, and block time - resolved against this airframe, exactly
        // as blockMinutes below is - feeds crew, maintenance and fuel burn. The same city pair is
        // therefore genuinely worth different money to different airframes, which is what makes
        // "the most profitable route for THIS aircraft" a different question from "the best route".
        var worldSeed = await FlightEconomicsPoster.ResolveWorldSeedAsync(db, ct);
        var pricedAtUtc = DateTimeOffset.UtcNow;
        var netProfitByRouteId = new Dictionary<Guid, decimal>();
        if (aircraftTypesById.TryGetValue(fleetAircraft.AircraftTypeId, out var chosenType))
        {
            foreach (var route in routes)
            {
                // A zero-distance route would make DemandCalculator throw (it has no market to
                // speak of anyway), and a world-data gap leaves an airport unresolvable - either
                // way the honest answer is "no figure", never a guessed one.
                if (route.DistanceNm <= 0 ||
                    !airportsByIcao.TryGetValue(route.DepartureIcao, out var departureAirport) ||
                    !airportsByIcao.TryGetValue(route.ArrivalIcao, out var arrivalAirport))
                {
                    continue;
                }

                var projection = SectorProjector.Project(
                    economyConfig, airline.StrategyProfile, airline.ReputationScore, departureAirport, arrivalAirport, chosenType,
                    route.DistanceNm, route.BaseFare, pricedAtUtc, worldSeed);

                netProfitByRouteId[route.Id] = Math.Round(projection.NetProfit, 2);
            }
        }

        // How hard the rest of the airline already works this leg. OTHER pilots' saved entries only,
        // never this pilot's draft - a count that climbed as the generator built would make one
        // week's own days disagree with each other, and the whole week is meant to be one repeating
        // pattern out of one base. A city pair's market is finite, so this is what lets a caller
        // treat the second pilot put on the same pair as worth less than the first.
        var otherPilotLegsByRouteId = otherEntries
            .GroupBy(e => e.RouteId)
            .ToDictionary(g => g.Key, g => g.Count());

        var legal = new List<object>();
        var illegal = new List<object>();

        foreach (var candidate in candidates)
        {
            var route = routesById[candidate.RouteId];

            var beforeWithCandidate = beforeBaseline.Append(candidate).ToList();
            var beforeResult = PilotScheduleValidator.Validate(
                beforeWithCandidate, routesById, fleetById, aircraftTypesById, blockMinutesByLeg, airportsByIcao, economyConfig.Scheduling, existingRoutePairs, requireWeekClosure: false);
            var disqualifying = beforeResult.Conflicts.Where(c => !beforeBaselineConflicts.Contains(c)).ToList();

            if (disqualifying.Count > 0)
            {
                illegal.Add(new { routeId = candidate.RouteId, reason = disqualifying[0] });
                continue;
            }

            // Nothing before the slot rules this out - now check the FULL week (everything already
            // drafted, including entries later than this slot) to see what picking it takes on. Any
            // NEW conflict here is, by construction, only reachable through a later entry (a "before"
            // conflict would already have shown up above) - a consequence to warn about, never a
            // reason to refuse the option.
            var withCandidate = baseline.Append(candidate).ToList();
            var candidateResult = PilotScheduleValidator.Validate(
                withCandidate, routesById, fleetById, aircraftTypesById, blockMinutesByLeg, airportsByIcao, economyConfig.Scheduling, existingRoutePairs, requireWeekClosure: false);

            // A continuity gap (this leg leaves the aircraft somewhere else; a later already-drafted
            // leg needs it back) is resolvable purely by continuing to build the week - adding the
            // bridging leg is the ordinary next step of an ordinary action, not a sign anything is
            // wrong, so it gets its own forward-looking phrasing ("add a leg after this one") and
            // "info" severity rather than the validator's own "before"-case wording ("...before this
            // one to reposition it"), which is correct for ITS case (a leg departing where the
            // aircraft isn't) but backwards here. See PilotScheduleValidator.ContinuityGap's own
            // remarks for why this reads the validator's structured facts instead of re-deriving them.
            // Every OTHER warning reachable here - double-booking, insufficient turnaround/rest, an
            // over-long duty day - is a genuine incompatibility with something the player already
            // committed to elsewhere, not resolvable simply by carrying on building, so those keep
            // "alert" severity and are surfaced first (LegOptionRow only ever shows warnings[0]).
            var newGaps = candidateResult.ContinuityGaps.Where(g => !baselineGaps.Contains(g)).ToList();
            var newGapConflictTexts = newGaps.Select(g => g.ConflictText).ToHashSet();
            var otherWarnings = candidateResult.Conflicts
                .Where(c => !baselineConflicts.Contains(c) && !newGapConflictTexts.Contains(c))
                .Select(message => new LegWarning(message, "alert"));
            var gapWarnings = newGaps.Select(g => new LegWarning(PhraseContinuityGap(g), "info"));
            var warnings = otherWarnings.Concat(gapWarnings).ToList();

            // K34: this MUST be the block time for the aircraft actually chosen for this duty
            // day (blockMinutesByLeg is keyed by (RouteId, FleetAircraftId) - see
            // BuildValidationDataAsync), never a route-level default computed against whatever
            // aircraft type happens to be first in the fleet. The draft grid used to fall back
            // to exactly that route-only default because this DTO carried no block time at all,
            // which is why an ATR duty day could show an A320's faster block time right up until
            // save recomputed it against the real airframe and the number visibly jumped.
            var blockMinutes = blockMinutesByLeg.TryGetValue((candidate.RouteId, fleetAircraftId), out var minutes) ? (int?)minutes : null;
            var expectedNetProfit = netProfitByRouteId.TryGetValue(candidate.RouteId, out var profit) ? (decimal?)profit : null;
            otherPilotLegsByRouteId.TryGetValue(candidate.RouteId, out var scheduledLegsThisWeek);
            legal.Add(new
            {
                routeId = candidate.RouteId,
                route.DepartureIcao,
                route.ArrivalIcao,
                route.FlightNumber,
                blockMinutes,
                expectedNetProfit,
                scheduledLegsThisWeek,
                warnings,
            });
        }

        return Results.Ok(new { legal, illegal, aircraftPosition });
    }

    /// <summary>
    /// Phrases a <see cref="ContinuityGap"/> for the leg-options picker specifically - a
    /// consequence and a next step ("this is where the aircraft ends up; add a leg after this one
    /// to bring it back"), deliberately not the validator's own prose (which is correct for the
    /// "before" case - a leg departing where the aircraft physically isn't - and would read
    /// backwards here, telling the player to add a leg BEFORE one they haven't picked yet).
    /// Mirrors the validator's own "schedule a leg" vs "you'd need to create a route" distinction
    /// via <see cref="ContinuityGap.RouteExistsForReposition"/>.
    /// </summary>
    private static string PhraseContinuityGap(ContinuityGap gap)
    {
        var when = $"{gap.NextDepartureDayOfWeek} at {gap.NextDepartureTimeUtc:hh\\:mm}";
        var fix = gap.RouteExistsForReposition
            ? $"add a {gap.GapDepartureIcao} -> {gap.GapArrivalIcao} leg after this one"
            : $"create a {gap.GapDepartureIcao} -> {gap.GapArrivalIcao} route on the Routes page, then add a leg on it after this one";
        return $"Leaves {gap.AircraftRegistration} at {gap.GapDepartureIcao}. Its next leg departs {gap.GapArrivalIcao} ({when}) - {fix}.";
    }

    /// <summary>Same absolute-minute-of-week convention as <see cref="PilotScheduleValidator.WeekMinutes"/>
    /// (Sunday 00:00 = 0), exposed as its own small helper here rather than reaching into the
    /// validator's private one - <see cref="GetLegOptionsAsync"/> is answering "is this entry before
    /// or after that slot", a simpler question than the validator's own chain logic, not a second
    /// implementation of it.</summary>
    private static int AbsoluteWeekMinuteFor(DayOfWeek dayOfWeek, TimeSpan timeOfDay) => (int)dayOfWeek * 1440 + (int)timeOfDay.TotalMinutes;

    /// <summary>
    /// Read-only, airline-wide: every aircraft as a row, its legs across the week, colour-coded by
    /// pilot, plus toggleable by-pilot/by-aircraft views of the same week. This is what surfaces
    /// idle aircraft, gaps and over-committed airframes at a glance - "is my fleet actually being
    /// used" has no answer in the per-pilot views alone.
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
    /// it yet. That distinction is what lets the builder say "you'd need to CREATE an EGPH -> EGLL
    /// route" versus "the route exists - schedule an EGPH -> EGLL leg to reposition first",
    /// instead of sending the player to the wrong screen to solve a problem they do not have.</summary>
    private static async Task<(
        Dictionary<Guid, Route> RoutesById,
        Dictionary<Guid, FleetAircraft> FleetById,
        Dictionary<Guid, AircraftType> AircraftTypesById,
        Dictionary<(Guid RouteId, Guid FleetAircraftId), int> BlockMinutesByLeg,
        HashSet<(string DepartureIcao, string ArrivalIcao)> ExistingRoutePairs,
        Dictionary<string, Airport> AirportsByIcao)> BuildValidationDataAsync(
        FsOpsDbContext db, Airline airline, EconomyConfig economyConfig, IReadOnlyList<PilotScheduleEntryInput> entries, CancellationToken ct)
    {
        var routeIds = entries.Select(e => e.RouteId).Distinct().ToList();
        var fleetIds = entries.Select(e => e.FleetAircraftId).Distinct().ToList();

        var routesById = await db.Routes.Where(r => routeIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var fleetById = await db.FleetAircraft.Where(f => fleetIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);

        var typeIds = fleetById.Values.Select(f => f.AircraftTypeId).Distinct().ToList();
        var typesById = await db.AircraftTypes.Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);

        var icaos = routesById.Values.SelectMany(r => new[] { r.DepartureIcao, r.ArrivalIcao }).Distinct().ToList();
        // Include(Runways) - PilotScheduleValidator's runway-suitability check needs the actual
        // runway rows, not just the stamped LongestRunwayFt.
        var airportsByIcao = await db.Airports.Include(a => a.Runways).Where(a => icaos.Contains(a.Icao)).ToDictionaryAsync(a => a.Icao, ct);

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

        return (routesById, fleetById, typesById, blockMinutesByLeg, existingRoutePairSet, airportsByIcao);
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

            // Same one projector every other "what would this be worth" surface runs, and the same
            // one FlightEconomicsPoster posts from - see SectorProjector's class doc.
            var projection = SectorProjector.Project(
                economyConfig, airline.StrategyProfile, airline.ReputationScore, dep, arr, type,
                route.DistanceNm, route.BaseFare, now, worldSeed);

            revenue += projection.Revenue;
            cost += projection.TotalCost;
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

/// <summary>
/// One entry in a legal leg option's `warnings` array (see <see cref="PilotEndpoints.GetLegOptionsAsync"/>).
/// `Severity` is `"info"` for a consequence resolvable purely by continuing to build the week (a
/// continuity gap - the aircraft ends up somewhere new and needs a leg after this one to bring it
/// back, the ordinary halfway point of an ordinary action) and `"alert"` for a genuine
/// incompatibility with something the player already committed to elsewhere (double-booking,
/// insufficient turnaround or rest, an over-long duty day) that carrying on building this leg does
/// not by itself resolve. The frontend uses this to choose neutral vs amber styling; it never
/// changes whether the option is selectable - see <c>LegalLegOption.warnings</c> doc, `schedule.ts`.
/// </summary>
public record LegWarning(string Message, string Severity);
