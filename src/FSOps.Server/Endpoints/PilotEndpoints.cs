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
        group.MapPost("/pilots/{id:guid}/schedule/options", GetScheduleOptionsAsync);
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
            return Results.Ok(new { pilotId = pilot.Id, entries = Array.Empty<object>() });
        }

        var entries = await db.PilotScheduleEntries.Where(e => e.PilotScheduleId == schedule.Id).ToListAsync(ct);
        var dto = await EnrichEntriesAsync(db, entries, ct);
        return Results.Ok(new { pilotId = pilot.Id, entries = dto });
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

        var proposedEntries = request.Entries ?? Array.Empty<ScheduleEntryRequest>();
        var proposed = new List<PilotScheduleEntryInput>();
        foreach (var e in proposedEntries)
        {
            if (e.DayOfWeek is < 0 or > 6)
            {
                return Results.BadRequest(new { error = $"dayOfWeek must be 0-6 (Sunday-Saturday), was {e.DayOfWeek}." });
            }

            if (!TimeSpan.TryParse(e.DepartureTimeUtc, out var departureTime) || departureTime < TimeSpan.Zero || departureTime >= TimeSpan.FromDays(1))
            {
                return Results.BadRequest(new { error = $"departureTimeUtc '{e.DepartureTimeUtc}' must be a time of day like '08:30:00'." });
            }

            if (e.RouteId is not Guid routeId || e.FleetAircraftId is not Guid fleetAircraftId)
            {
                return Results.BadRequest(new { error = "Every entry needs routeId and fleetAircraftId." });
            }

            proposed.Add(new PilotScheduleEntryInput(pilot.Id, (DayOfWeek)e.DayOfWeek, departureTime, routeId, fleetAircraftId));
        }

        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);
        var otherEntries = await LoadOtherPilotsEntriesAsync(db, airline.Id, excludingPilotId: pilot.Id, ct);
        var unionEntries = otherEntries.Concat(proposed).ToList();

        var (routesById, fleetById, blockMinutesByLeg, existingRoutePairs) = await BuildValidationDataAsync(db, airline, economyConfig, unionEntries, ct);

        // requireWeekClosure: true - a SAVED week must genuinely repeat (docs/PLAN.md "the week
        // repeats indefinitely"), unlike the options endpoint below, which is deliberately asking a
        // narrower, per-leg question about a week still under construction. Never weaken this.
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
        var dto = await EnrichEntriesAsync(db, savedEntries, ct);
        return Results.Ok(new { pilotId = pilot.Id, entries = dto });
    }

    /// <summary>
    /// Which (route, aircraft) combinations are legal for this pilot at a given day/time - see
    /// docs/PLAN.md "which legs and aircraft are legal at that slot, with human-readable reasons for
    /// those that are not". Tries every route x fleet-aircraft combination the airline has (small
    /// counts for a hobby airline, so brute force is fine) against the SAME validator the save
    /// endpoint uses.
    /// <para>
    /// <b>Answers "does this leg fit with what you've built so far", not "is the whole week valid"</b>
    /// - deliberately different from PUT /schedule. Two consequences, both load-bearing (found by
    /// the schedule-builder agent: the original GET-based version made every single candidate
    /// illegal, because it validated full-week closure - including the wraparound from the week's
    /// last leg back to its first - against a lone candidate, which can never close a week by
    /// itself). First: <see cref="PilotScheduleValidator.Validate"/> is called with
    /// <c>requireWeekClosure: false</c>, so a week under construction is never faulted for not yet
    /// looping back to where it started - that is not an error, it is an unfinished week, and
    /// closure is checked where it belongs, at save time. Second: this is a POST with a
    /// <paramref name="request"/> body carrying <see cref="ScheduleOptionsRequest.DraftEntries"/> -
    /// the pilot's in-progress week as the player has built it client-side but not yet saved -
    /// because otherwise this could only ever see what's already in the database, and the second
    /// leg of a day being drafted would be judged against an aircraft position that ignores the
    /// first. Draft entries entirely REPLACE what's read for this pilot (mirroring how PUT replaces
    /// the whole week) and are merged with every OTHER pilot's persisted entries, exactly as a save
    /// eventually will be.
    /// </para>
    /// </summary>
    internal static async Task<IResult> GetScheduleOptionsAsync(
        Guid id, ScheduleOptionsRequest request, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
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

        var dayOfWeek = (DayOfWeek)request.Day;
        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);

        var draftOwnEntries = new List<PilotScheduleEntryInput>();
        foreach (var e in request.DraftEntries ?? Array.Empty<ScheduleEntryRequest>())
        {
            if (e.DayOfWeek is < 0 or > 6 ||
                !TimeSpan.TryParse(e.DepartureTimeUtc, out var draftTime) ||
                e.RouteId is not Guid draftRouteId ||
                e.FleetAircraftId is not Guid draftAircraftId)
            {
                // A malformed draft entry can't be reasoned about - skip it rather than fail the
                // whole request, since the player is mid-edit and the client is the source of any
                // partial/transient state here, not something this endpoint should crash on.
                continue;
            }

            draftOwnEntries.Add(new PilotScheduleEntryInput(pilot.Id, (DayOfWeek)e.DayOfWeek, draftTime, draftRouteId, draftAircraftId));
        }

        var otherEntries = await LoadOtherPilotsEntriesAsync(db, airline.Id, excludingPilotId: pilot.Id, ct);
        var baseline = otherEntries.Concat(draftOwnEntries).ToList();

        var routes = await db.Routes.Where(r => r.AirlineId == airline.Id && r.IsActive).ToListAsync(ct);
        var fleet = await db.FleetAircraft.Where(f => f.AirlineId == airline.Id).ToListAsync(ct);

        var candidates = new List<PilotScheduleEntryInput>();
        foreach (var route in routes)
        {
            foreach (var aircraft in fleet)
            {
                candidates.Add(new PilotScheduleEntryInput(pilot.Id, dayOfWeek, departureTime, route.Id, aircraft.Id));
            }
        }

        var allEntriesForValidation = baseline.Concat(candidates).ToList();
        var (routesById, fleetById, blockMinutesByLeg, existingRoutePairs) = await BuildValidationDataAsync(db, airline, economyConfig, allEntriesForValidation, ct);

        var baselineConflicts = PilotScheduleValidator
            .Validate(baseline, routesById, fleetById, blockMinutesByLeg, economyConfig.Scheduling, existingRoutePairs, requireWeekClosure: false)
            .Conflicts.ToHashSet();

        var legal = new List<object>();
        var illegal = new List<object>();

        foreach (var candidate in candidates)
        {
            var withCandidate = baseline.Append(candidate).ToList();
            var candidateResult = PilotScheduleValidator.Validate(
                withCandidate, routesById, fleetById, blockMinutesByLeg, economyConfig.Scheduling, existingRoutePairs, requireWeekClosure: false);
            var newConflicts = candidateResult.Conflicts.Where(c => !baselineConflicts.Contains(c)).ToList();

            var route = routesById[candidate.RouteId];
            var aircraft = fleetById[candidate.FleetAircraftId];

            // Grounding is deliberately NOT part of PilotScheduleValidator (which reasons about the
            // week as an abstract, indefinitely-repeating template - a momentary A/C-check must
            // never permanently block saving a schedule that will be perfectly flyable once it
            // lifts). Here, though, the player is looking at "can I use this aircraft right now",
            // so today's real grounding state is a genuinely useful signal - checked separately and
            // never fed back into what PUT /schedule enforces.
            var groundingReason = aircraft.Status == FleetAircraftStatus.InMaintenance
                ? (aircraft.GroundedUntilUtc is { } until
                    ? $"{aircraft.Registration} is in maintenance until {until:yyyy-MM-dd HH:mm} UTC."
                    : $"{aircraft.Registration} is in maintenance.")
                : null;

            if (newConflicts.Count == 0 && groundingReason is null)
            {
                legal.Add(new { routeId = candidate.RouteId, route.DepartureIcao, route.ArrivalIcao, fleetAircraftId = candidate.FleetAircraftId, aircraft.Registration });
            }
            else
            {
                illegal.Add(new { routeId = candidate.RouteId, fleetAircraftId = candidate.FleetAircraftId, reason = groundingReason ?? newConflicts[0] });
            }
        }

        return Results.Ok(new { legal, illegal });
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

    private static async Task<IReadOnlyList<object>> EnrichEntriesAsync(FsOpsDbContext db, IReadOnlyList<PilotScheduleEntry> entries, CancellationToken ct)
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

        return entries.OrderBy(e => (int)e.DayOfWeek).ThenBy(e => e.DepartureTimeUtc).Select(e =>
        {
            routesById.TryGetValue(e.RouteId, out var route);
            fleetById.TryGetValue(e.FleetAircraftId, out var aircraft);
            int? blockMinutes = null;
            if (route is not null && aircraft is not null &&
                typesById.TryGetValue(aircraft.AircraftTypeId, out var type) &&
                airportsByIcao.TryGetValue(route.DepartureIcao, out var dep) &&
                airportsByIcao.TryGetValue(route.ArrivalIcao, out var arr))
            {
                // Casual approximation for display - the same call the resolver itself makes, so
                // this can never show a different block time than what actually resolves.
                blockMinutes = BlockTimeEstimator.Estimate(GreatCircle.DistanceNm(dep.Latitude, dep.Longitude, arr.Latitude, arr.Longitude), type.CruiseTasKts).TotalMinutes;
            }

            return (object)new
            {
                e.Id,
                DayOfWeek = (int)e.DayOfWeek,
                DepartureTimeUtc = e.DepartureTimeUtc.ToString(@"hh\:mm\:ss"),
                e.RouteId,
                DepartureIcao = route?.DepartureIcao,
                ArrivalIcao = route?.ArrivalIcao,
                FlightNumber = route?.FlightNumber,
                FleetAircraftId = e.FleetAircraftId,
                Registration = aircraft?.Registration,
                BlockMinutes = blockMinutes,
            };
        }).ToList();
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

public record ScheduleEntryRequest(int DayOfWeek, string DepartureTimeUtc, Guid? RouteId, Guid? FleetAircraftId);

public record SaveScheduleRequest(IReadOnlyList<ScheduleEntryRequest>? Entries);

/// <summary>
/// Body for POST /pilots/{id}/schedule/options. <see cref="Day"/> is 0-6 (Sunday-Saturday, matching
/// <see cref="System.DayOfWeek"/>), <see cref="Time"/> a time of day like "08:30". <see cref="DraftEntries"/>
/// is this pilot's in-progress week exactly as the client has built it so far - not yet saved, and
/// entirely replacing what would otherwise be read from the database for this pilot (see
/// PilotEndpoints.GetScheduleOptionsAsync's own doc for why: without it, a second leg being added to
/// a day in progress would be judged against a stale, pre-edit aircraft position).
/// </summary>
public record ScheduleOptionsRequest(int Day, string Time, IReadOnlyList<ScheduleEntryRequest>? DraftEntries);
