using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Flights;
using FSOps.Data;
using FSOps.Server.Auth;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Endpoints;

/// <summary>
/// Backs the Stats page. Everything here is derived
/// straight from posted <see cref="Flight"/> rows (never a cached total), same discipline
/// <see cref="FinanceEndpoints"/> already applies to money: on-time performance and load factor use
/// exactly the same delay/measurability rules as <c>AirlineEndpoints.GetReputationAsync</c>
/// (Completed, <see cref="Flight.InUtc"/> set, not <see cref="Flight.SimRateElevated"/>) so this
/// page's numbers can never disagree with the reputation card's. The revenue/cost split and
/// per-route P&amp;L this page shows are deliberately NOT recomputed here - the frontend reuses
/// <see cref="FinanceEndpoints"/>'s <c>/finance/costs</c> and <c>/finance/routes</c> directly, so
/// there is exactly one place either figure is ever calculated.
/// </summary>
public static class StatsEndpoints
{
    private const int DefaultPeriodDays = 30;
    private const int MaxPeriodDays = 365;

    public static void MapStatsEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/stats/performance", PerformanceAsync);
        group.MapGet("/stats/fleet", FleetAsync);
        group.MapGet("/stats/pilots", PilotsAsync);
    }

    private static int ResolvePeriodDays(int? days) => days is > 0 and <= MaxPeriodDays ? days.Value : DefaultPeriodDays;

    /// <summary>
    /// On-time performance and load factor, bucketed by the local completion day. A day is only present in
    /// <c>points</c> if at least one sector completed that day, so a quiet stretch never renders as
    /// a fabricated zero. <c>onTimePercent</c> mirrors <c>AirlineEndpoints.GetReputationAsync</c>'s
    /// own delay/measurability rule exactly (Completed, InUtc set, not SimRateElevated - a
    /// SimRateElevated sector is excluded, never scored as a miss) so this chart can never disagree
    /// with the Dashboard's reputation card over the same window. <c>loadFactorPercent</c> is
    /// PaxFlown against the flown aircraft's own seat capacity, averaged over the day's sectors;
    /// null (never 0) for a day where no flown flight can be matched to a known aircraft type.
    /// </summary>
    internal static async Task<IResult> PerformanceAsync(
        int? days, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var periodDays = ResolvePeriodDays(days);
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(new { periodDays, points = Array.Empty<object>() });
        }

        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(periodDays);

        // Materialise before filtering/grouping by date - SQLite can't translate ORDER BY/GROUP BY
        // over DateTimeOffset (see project EF/SQLite traps).
        var flights = (await db.Flights
                .Where(f => f.AirlineId == airline.Id && f.Status == FlightStatus.Completed && f.DeletedUtc == null)
                .ToListAsync(ct))
            .Where(f => (f.InUtc ?? f.CreatedUtc) >= cutoff)
            .ToList();

        if (flights.Count == 0)
        {
            return Results.Ok(new { periodDays, points = Array.Empty<object>() });
        }

        var fleetAircraftIds = flights.Select(f => f.FleetAircraftId).Distinct().ToList();
        var fleet = await db.FleetAircraft.Where(f => fleetAircraftIds.Contains(f.Id)).ToListAsync(ct);
        var typeIdByAircraft = fleet.ToDictionary(f => f.Id, f => f.AircraftTypeId);
        var typeIds = fleet.Select(f => f.AircraftTypeId).Distinct().ToList();
        var capacityByType = await db.AircraftTypes.Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.PaxCapacity, ct);

        var points = flights
            .GroupBy(f => (f.InUtc ?? f.CreatedUtc).UtcDateTime.Date)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var measurableDelays = g
                    .Where(f => f.InUtc is not null && !f.SimRateElevated)
                    .Select(f => (f.InUtc!.Value - f.PlannedDepartureUtc.AddMinutes(f.PlannedBlockMinutes)).TotalMinutes)
                    .ToList();
                double? onTimePercent = measurableDelays.Count == 0
                    ? null
                    : Math.Round(100.0 * measurableDelays.Count(d => d <= economyConfig.Reputation.OnTimeToleranceMinutes) / measurableDelays.Count, 1);

                var loadFactors = g
                    .Select(f =>
                    {
                        var capacity = typeIdByAircraft.TryGetValue(f.FleetAircraftId, out var typeId) && capacityByType.TryGetValue(typeId, out var cap)
                            ? cap
                            : 0;
                        return capacity > 0 ? (double?)(100.0 * f.PaxFlown / capacity) : null;
                    })
                    .Where(v => v is not null)
                    .Select(v => v!.Value)
                    .ToList();
                double? loadFactorPercent = loadFactors.Count == 0 ? null : Math.Round(loadFactors.Average(), 1);

                return new
                {
                    dateUtc = DateOnly.FromDateTime(g.Key).ToString("yyyy-MM-dd"),
                    sectorsFlown = g.Count(),
                    onTimePercent,
                    loadFactorPercent,
                };
            })
            .ToList();

        return Results.Ok(new { periodDays, points });
    }

    /// <summary>
    /// Fleet utilisation: hours flown per aircraft, idle time, and how close each airframe is to
    /// its next check. <c>hoursFlownInPeriod</c> is summed from completed flights'
    /// own OOOI times (<see cref="BlockTimeCalculator"/>), not the lifetime
    /// <see cref="FleetAircraft.AirframeHours"/> counter, so it actually reflects the requested
    /// window. <c>hoursToNextACheck</c>/<c>hoursToNextCCheck</c> mirror
    /// <c>FleetEndpoints.ListAsync</c>'s own computation exactly, so the Fleet page and this page can
    /// never disagree about how close an airframe is to its next check.
    /// </summary>
    internal static async Task<IResult> FleetAsync(
        int? days, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var periodDays = ResolvePeriodDays(days);
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(new { periodDays, aircraft = Array.Empty<object>() });
        }

        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);
        var fleet = (await db.FleetAircraft.Where(f => f.AirlineId == airline.Id && f.DeletedUtc == null).ToListAsync(ct))
            .OrderBy(f => f.Registration, StringComparer.Ordinal)
            .ToList();

        if (fleet.Count == 0)
        {
            return Results.Ok(new { periodDays, aircraft = Array.Empty<object>() });
        }

        var typeIds = fleet.Select(f => f.AircraftTypeId).Distinct().ToList();
        var typesById = await db.AircraftTypes.Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(periodDays);
        var fleetIds = fleet.Select(f => f.Id).ToList();
        var flights = (await db.Flights
                .Where(f => fleetIds.Contains(f.FleetAircraftId) && f.Status == FlightStatus.Completed && f.DeletedUtc == null)
                .ToListAsync(ct))
            .Where(f => (f.InUtc ?? f.CreatedUtc) >= cutoff)
            .ToList();
        var flightsByAircraft = flights.GroupBy(f => f.FleetAircraftId).ToDictionary(g => g.Key, g => g.ToList());

        var periodHours = periodDays * 24.0;

        var result = fleet.Select(f =>
        {
            typesById.TryGetValue(f.AircraftTypeId, out var type);
            var aircraftFlights = flightsByAircraft.GetValueOrDefault(f.Id) ?? new List<Flight>();
            var hoursFlown = aircraftFlights.Sum(fl => BlockTimeCalculator.BlockHours(fl.OutUtc, fl.InUtc));
            var idleHours = Math.Max(0, periodHours - hoursFlown);
            var hoursToNextACheck = Math.Max(0, economyConfig.Maintenance.ACheckIntervalHours - f.HoursSinceACheck);
            var hoursToNextCCheck = Math.Max(0, economyConfig.Maintenance.CCheckIntervalHours - f.HoursSinceCCheck);

            return new
            {
                fleetAircraftId = f.Id,
                registration = f.Registration,
                aircraftTypeName = type?.Name ?? "Unknown type",
                status = f.Status.ToString(),
                sectorsFlown = aircraftFlights.Count,
                hoursFlownInPeriod = Math.Round(hoursFlown, 1),
                idleHoursInPeriod = Math.Round(idleHours, 1),
                utilisationPercent = periodHours > 0 ? Math.Round(100.0 * Math.Min(hoursFlown, periodHours) / periodHours, 1) : 0,
                hoursSinceACheck = Math.Round(f.HoursSinceACheck, 1),
                hoursSinceCCheck = Math.Round(f.HoursSinceCCheck, 1),
                hoursToNextACheck = Math.Round(hoursToNextACheck, 1),
                hoursToNextCCheck = Math.Round(hoursToNextCCheck, 1),
                conditionPercent = Math.Round(f.ConditionPercent, 1),
            };
        }).ToList();

        return Results.Ok(new { periodDays, aircraft = result });
    }

    /// <summary>
    /// Pilot logbook: sectors, hours and performance per pilot, the player included.
    /// <c>hoursFlown</c> is summed from completed flights' own OOOI times, exactly like
    /// <see cref="FleetAsync"/> above, never from the lifetime <see cref="Pilot.HoursFlown"/>
    /// counter - that figure is not windowed and this page's whole premise is "over the requested
    /// period". <c>onTimePercent</c> uses the same rule as <see cref="PerformanceAsync"/>;
    /// <c>averageLandingFpm</c> is null (never 0) when nothing in the window captured a touchdown
    /// rate (most commonly every sector in-window was a manual completion).
    /// </summary>
    internal static async Task<IResult> PilotsAsync(
        int? days, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var periodDays = ResolvePeriodDays(days);
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(new { periodDays, pilots = Array.Empty<object>() });
        }

        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);
        var pilots = await db.Pilots.Where(p => p.AirlineId == airline.Id && p.DeletedUtc == null).ToListAsync(ct);
        if (pilots.Count == 0)
        {
            return Results.Ok(new { periodDays, pilots = Array.Empty<object>() });
        }

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(periodDays);
        var pilotIds = pilots.Select(p => p.Id).ToList();
        var flights = (await db.Flights
                .Where(f => pilotIds.Contains(f.PilotId) && f.Status == FlightStatus.Completed && f.DeletedUtc == null)
                .ToListAsync(ct))
            .Where(f => (f.InUtc ?? f.CreatedUtc) >= cutoff)
            .ToList();
        var flightsByPilot = flights.GroupBy(f => f.PilotId).ToDictionary(g => g.Key, g => g.ToList());

        var result = pilots.Select(p =>
        {
            var pilotFlights = flightsByPilot.GetValueOrDefault(p.Id) ?? new List<Flight>();
            var hoursFlown = pilotFlights.Sum(f => BlockTimeCalculator.BlockHours(f.OutUtc, f.InUtc));

            var measurableDelays = pilotFlights
                .Where(f => f.InUtc is not null && !f.SimRateElevated)
                .Select(f => (f.InUtc!.Value - f.PlannedDepartureUtc.AddMinutes(f.PlannedBlockMinutes)).TotalMinutes)
                .ToList();
            double? onTimePercent = measurableDelays.Count == 0
                ? null
                : Math.Round(100.0 * measurableDelays.Count(d => d <= economyConfig.Reputation.OnTimeToleranceMinutes) / measurableDelays.Count, 1);

            var landingFpms = pilotFlights.Where(f => f.LandingFpmFirst is not null).Select(f => f.LandingFpmFirst!.Value).ToList();
            double? averageLandingFpm = landingFpms.Count == 0 ? null : Math.Round(landingFpms.Average(), 0);

            return new
            {
                pilotId = p.Id,
                name = p.Name,
                isPlayer = p.IsPlayer,
                sectorsFlown = pilotFlights.Count,
                hoursFlown = Math.Round(hoursFlown, 1),
                onTimePercent,
                averageLandingFpm,
            };
        }).OrderByDescending(p => p.sectorsFlown).ToList();

        return Results.Ok(new { periodDays, pilots = result });
    }
}
