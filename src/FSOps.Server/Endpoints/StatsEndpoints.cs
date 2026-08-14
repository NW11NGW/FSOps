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
        group.MapGet("/stats/trends", TrendsAsync);
        group.MapGet("/stats/fleet", FleetAsync);
        group.MapGet("/stats/pilots", PilotsAsync);
    }

    private static int ResolvePeriodDays(int? days) => days is > 0 and <= MaxPeriodDays ? days.Value : DefaultPeriodDays;

    private static string DayKey(DateTime utcDate) => utcDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Direction of travel for the whole airline: one point per calendar day across the requested
    /// window, so the player can see whether things are getting better or worse rather than only
    /// where they stand today.
    ///
    /// <para>
    /// <b>Every series here is derived from rows that already exist. Nothing is invented, and a day
    /// with nothing to say is null rather than zero.</b>
    /// </para>
    ///
    /// <list type="bullet">
    /// <item><b>cashBalance</b> - exact. The app already defines cash as the sum of every
    /// <see cref="LedgerTransaction.Amount"/>, and the ledger is append-only, so the running total
    /// up to the end of a day <i>is</i> the balance on that day. Transactions before the window are
    /// summed into an opening balance rather than ignored, otherwise every chart would start from
    /// zero. Present for every day in the window, including days nothing happened - cash does not
    /// stop existing on a quiet day, it simply does not move.</item>
    ///
    /// <item><b>onTimePercent / loadFactorPercent</b> - the same per-day figures
    /// <see cref="PerformanceAsync"/> produces, computed by the same shared helper
    /// (<see cref="BuildDailyFlightMetrics"/>) rather than re-derived, so the two endpoints cannot
    /// drift apart. Null on a day with no measurable sector, never a fabricated 0%.</item>
    ///
    /// <item><b>reputation</b> - the airline's genuinely recorded score for that day, read from
    /// <see cref="ReputationSnapshot"/>. Null for any day the app was not open, because no score was
    /// observed; it is never carried forward from the previous day. This series necessarily begins
    /// on the day snapshotting shipped - see that entity's own doc for why reputation before then
    /// cannot be honestly reconstructed.</item>
    ///
    /// <item><b>reputationPressure</b> - the average target that day's sectors were pulling
    /// reputation toward (<see cref="ReputationCalculator.TargetForCompletedFlight"/>). This is
    /// <b>not</b> reputation and is labelled as such by the UI. It is not a proxy invented here
    /// either: it is the exact quantity <c>AirlineEndpoints.GetReputationAsync</c> already averages
    /// to decide whether the dashboard's reputation card reads "improving", "steady" or "declining",
    /// so the chart and that card cannot disagree. Its value is that it works retroactively over
    /// history flown long before snapshots existed. Null on a day whose sectors carried no
    /// measurable on-time or landing signal at all.</item>
    /// </list>
    ///
    /// <para>
    /// <c>currentReputation</c> is the live <see cref="Airline.ReputationScore"/>, so the UI can draw
    /// today's actual standing as a reference against the pressure series without inferring it.
    /// </para>
    /// </summary>
    internal static async Task<IResult> TrendsAsync(
        int? days, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var periodDays = ResolvePeriodDays(days);
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(new { periodDays, points = Array.Empty<object>(), currentReputation = (double?)null, reputationRecordedDays = 0 });
        }

        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);
        var now = DateTimeOffset.UtcNow;
        // Whole UTC days, inclusive of today: a window of N days is today plus the N-1 before it.
        var lastDay = now.UtcDateTime.Date;
        var firstDay = lastDay.AddDays(-(periodDays - 1));
        var windowStart = new DateTimeOffset(firstDay, TimeSpan.Zero);

        // ---- Cash: opening balance + per-day movement -------------------------------------------
        // The whole ledger is materialised (SQLite cannot translate a DateTimeOffset comparison in
        // SQL - the same trap OrderBy hits) and split in memory. This is the same one-query-for-the-
        // whole-ledger shape FinanceEndpoints.CashBalanceAsync already uses.
        var ledger = await db.LedgerTransactions
            .Where(t => t.AirlineId == airline.Id)
            .Select(t => new { t.Utc, t.Amount })
            .ToListAsync(ct);

        var openingBalance = ledger.Where(t => t.Utc < windowStart).Sum(t => t.Amount);
        var movementByDay = ledger
            .Where(t => t.Utc >= windowStart)
            .GroupBy(t => DayKey(t.Utc.UtcDateTime.Date))
            .ToDictionary(g => g.Key, g => g.Sum(t => t.Amount));

        // ---- On-time / load factor: shared with PerformanceAsync --------------------------------
        var flights = (await db.Flights
                .Where(f => f.AirlineId == airline.Id && f.Status == FlightStatus.Completed && f.DeletedUtc == null)
                .ToListAsync(ct))
            .Where(f => (f.InUtc ?? f.CreatedUtc) >= windowStart)
            .ToList();

        var metricsByDay = await BuildDailyFlightMetrics(db, flights, economyConfig, ct);

        // ---- Reputation: recorded snapshots, and the pressure series ----------------------------
        var firstDayKey = DayKey(firstDay);
        var snapshotByDay = (await db.ReputationSnapshots
                .Where(s => s.AirlineId == airline.Id && string.Compare(s.DateUtc, firstDayKey) >= 0)
                .Select(s => new { s.DateUtc, s.Score })
                .ToListAsync(ct))
            // A unique index guarantees one row per airline per day, but grouping defensively means
            // a duplicate could never throw here.
            .GroupBy(s => s.DateUtc)
            .ToDictionary(g => g.Key, g => g.First().Score);

        var pressureByDay = flights
            .GroupBy(f => DayKey((f.InUtc ?? f.CreatedUtc).UtcDateTime.Date))
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var targets = g
                        .Select(f => ReputationCalculator.TargetForCompletedFlight(
                            economyConfig.Reputation,
                            f.SimRateElevated || f.InUtc is null
                                ? null
                                : (f.InUtc.Value - f.PlannedDepartureUtc.AddMinutes(f.PlannedBlockMinutes)).TotalMinutes,
                            f.LandingFpmFirst))
                        .Where(t => t is not null)
                        .Select(t => t!.Value)
                        .ToList();
                    return targets.Count == 0 ? (double?)null : Math.Round(targets.Average(), 1);
                });

        var points = new List<object>(periodDays);
        var runningBalance = openingBalance;
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            var key = DayKey(day);
            runningBalance += movementByDay.GetValueOrDefault(key);
            metricsByDay.TryGetValue(key, out var metrics);

            points.Add(new
            {
                dateUtc = key,
                cashBalance = runningBalance,
                sectorsFlown = metrics?.SectorsFlown ?? 0,
                onTimePercent = metrics?.OnTimePercent,
                loadFactorPercent = metrics?.LoadFactorPercent,
                reputation = snapshotByDay.TryGetValue(key, out var score) ? Math.Round(score, 1) : (double?)null,
                reputationPressure = pressureByDay.GetValueOrDefault(key),
            });
        }

        return Results.Ok(new
        {
            periodDays,
            points,
            currentReputation = Math.Round(airline.ReputationScore, 1),
            // How many days in this window have a genuinely recorded score. Lets the UI say plainly
            // whether the reputation line is a real series yet or is still filling up, instead of
            // showing a line with one point on it and leaving the reader to guess why.
            reputationRecordedDays = snapshotByDay.Count,
        });
    }

    /// <summary>One day's flight-derived metrics - see <see cref="BuildDailyFlightMetrics"/>.</summary>
    private sealed record DailyFlightMetrics(int SectorsFlown, double? OnTimePercent, double? LoadFactorPercent);

    /// <summary>
    /// Buckets completed flights by their local completion day and computes on-time performance and
    /// load factor for each. The single implementation behind both <see cref="PerformanceAsync"/>
    /// and <see cref="TrendsAsync"/>, so the two pages cannot possibly report different numbers for
    /// the same day.
    /// <para>
    /// <c>onTimePercent</c> mirrors <c>AirlineEndpoints.GetReputationAsync</c>'s delay/measurability
    /// rule exactly (Completed, <see cref="Flight.InUtc"/> set, not <see cref="Flight.SimRateElevated"/> -
    /// an elevated-sim-rate sector is excluded, never scored as a miss). <c>loadFactorPercent</c> is
    /// PaxFlown against the flown aircraft's own seat capacity, averaged over the day's sectors.
    /// Both are null (never 0) when the day has nothing that can honestly be measured.
    /// </para>
    /// </summary>
    private static async Task<Dictionary<string, DailyFlightMetrics>> BuildDailyFlightMetrics(
        FsOpsDbContext db, IReadOnlyList<Flight> flights, EconomyConfig economyConfig, CancellationToken ct)
    {
        if (flights.Count == 0)
        {
            return new Dictionary<string, DailyFlightMetrics>();
        }

        var fleetAircraftIds = flights.Select(f => f.FleetAircraftId).Distinct().ToList();
        var fleet = await db.FleetAircraft.Where(f => fleetAircraftIds.Contains(f.Id)).ToListAsync(ct);
        var typeIdByAircraft = fleet.ToDictionary(f => f.Id, f => f.AircraftTypeId);
        var typeIds = fleet.Select(f => f.AircraftTypeId).Distinct().ToList();
        var capacityByType = await db.AircraftTypes.Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.PaxCapacity, ct);

        return flights
            .GroupBy(f => DayKey((f.InUtc ?? f.CreatedUtc).UtcDateTime.Date))
            .ToDictionary(g => g.Key, g =>
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

                return new DailyFlightMetrics(g.Count(), onTimePercent, loadFactorPercent);
            });
    }

    /// <summary>
    /// On-time performance and load factor, bucketed by the local completion day. A day is only present in
    /// <c>points</c> if at least one sector completed that day, so a quiet stretch never renders as
    /// a fabricated zero. <c>onTimePercent</c> mirrors <c>AirlineEndpoints.GetReputationAsync</c>'s
    /// own delay/measurability rule exactly (Completed, InUtc set, not SimRateElevated - a
    /// SimRateElevated sector is excluded, never scored as a miss) so this chart can never disagree
    /// with the Dashboard's reputation card over the same window. <c>loadFactorPercent</c> is
    /// PaxFlown against the flown aircraft's own seat capacity, averaged over the day's sectors;
    /// null (never 0) for a day where no flown flight can be matched to a known aircraft type.
    ///
    /// <para>
    /// <c>onlineSectorsFlown</c>/<c>onlineEligibleSectorsFlown</c> answer "how many sectors were
    /// flown online", over the whole window rather than per-day. <see cref="Flight.VatsimOnline"/>
    /// is three-valued - null means FSOps never checked (no CID configured, the feature was off, or
    /// the feed was unreachable for that whole flight), which is not the same as false ("checked and
    /// never matched"). Counting null as "not online" would tell the player their entire back
    /// catalogue was checked and found offline, including every sector flown before this feature
    /// existed - so <c>onlineEligibleSectorsFlown</c> (the denominator a percentage should use) counts
    /// only flights where VatsimOnline is non-null, and <c>onlineSectorsFlown</c> counts strictly
    /// VatsimOnline == true within that same set.
    /// </para>
    /// </summary>
    internal static async Task<IResult> PerformanceAsync(
        int? days, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var periodDays = ResolvePeriodDays(days);
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(new { periodDays, points = Array.Empty<object>(), onlineSectorsFlown = 0, onlineEligibleSectorsFlown = 0 });
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
            return Results.Ok(new { periodDays, points = Array.Empty<object>(), onlineSectorsFlown = 0, onlineEligibleSectorsFlown = 0 });
        }

        var onlineEligibleSectorsFlown = flights.Count(f => f.VatsimOnline.HasValue);
        var onlineSectorsFlown = flights.Count(f => f.VatsimOnline == true);

        // Shared with TrendsAsync - one implementation of the per-day rules, so the performance
        // chart and the trends chart can never report different numbers for the same day.
        var metricsByDay = await BuildDailyFlightMetrics(db, flights, economyConfig, ct);

        var points = metricsByDay
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new
            {
                dateUtc = pair.Key,
                sectorsFlown = pair.Value.SectorsFlown,
                onTimePercent = pair.Value.OnTimePercent,
                loadFactorPercent = pair.Value.LoadFactorPercent,
            })
            .ToList();

        return Results.Ok(new { periodDays, points, onlineSectorsFlown, onlineEligibleSectorsFlown });
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
