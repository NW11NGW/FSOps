using FSOps.Core.Entities;
using FSOps.Core.Planning;
using FSOps.Core.Scheduling;
using FSOps.Data;
using FSOps.Server.Auth;
using FSOps.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Endpoints;

/// <summary>
/// Backs the dashboard's live operations map - see docs/PLAN.md "Live operations map (Dashboard,
/// Chunk E - needs virtual pilots to exist)". Everything about a virtual pilot's "airborne" aircraft
/// here is computed on the fly from the schedule template plus the wall clock - nothing extra is
/// persisted for it, which is what keeps it automatically consistent with
/// <see cref="VirtualFlightResolverService"/>'s own eventual resolution: this endpoint never decides
/// whether a flight is economically real, it only visualises where a currently-in-progress
/// occurrence WOULD be if it resolves the way its template says. The player's own tracked flight, by
/// contrast, is driven by real telemetry via <see cref="FlightLifecycleService.GetActiveSnapshot"/>.
/// </summary>
public static class OperationsEndpoints
{
    public static void MapOperationsEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/operations/live", GetLiveAsync);
    }

    internal static async Task<IResult> GetLiveAsync(
        FsOpsDbContext db, ICurrentUser currentUser, FlightLifecycleService lifecycle, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(new { aircraft = Array.Empty<object>(), network = Array.Empty<object>() });
        }

        var now = DateTimeOffset.UtcNow;
        var aircraftList = new List<object>();

        // The player's own live flight - real telemetry, never interpolated.
        var activeFlight = await db.Flights.FirstOrDefaultAsync(
            f => f.AirlineId == airline.Id && (f.Status == FlightStatus.InProgress || f.Status == FlightStatus.Interrupted), ct);
        if (activeFlight is not null)
        {
            var snapshot = lifecycle.GetActiveSnapshot(activeFlight.Id);
            if (snapshot is not null)
            {
                var route = await db.Routes.FirstOrDefaultAsync(r => r.Id == activeFlight.RouteId, ct);
                var fleetAircraft = await db.FleetAircraft.FirstOrDefaultAsync(f => f.Id == activeFlight.FleetAircraftId, ct);
                var aircraftType = fleetAircraft is not null
                    ? await db.AircraftTypes.FirstOrDefaultAsync(t => t.Id == fleetAircraft.AircraftTypeId, ct)
                    : null;
                var pilot = await db.Pilots.FirstOrDefaultAsync(p => p.Id == activeFlight.PilotId, ct);

                aircraftList.Add(new
                {
                    kind = "player",
                    flightId = activeFlight.Id,
                    pilotName = pilot?.Name ?? "You",
                    registration = fleetAircraft?.Registration,
                    aircraftType = aircraftType?.Name,
                    routeId = activeFlight.RouteId,
                    flightNumber = route?.FlightNumber,
                    departureIcao = route?.DepartureIcao,
                    arrivalIcao = route?.ArrivalIcao,
                    latitudeDeg = snapshot.LatitudeDeg,
                    longitudeDeg = snapshot.LongitudeDeg,
                    // Real telemetry, not interpolated - see LiveFlightSnapshot.TrueHeadingDeg (true,
                    // not magnetic, to match what LiveFlightMap already renders for this same marker).
                    headingDeg = (double?)snapshot.TrueHeadingDeg,
                    phase = snapshot.Phase,
                    percentComplete = snapshot.PlannedBlockMinutes > 0
                        ? Math.Clamp(snapshot.ElapsedBlockMinutes / snapshot.PlannedBlockMinutes * 100.0, 0, 100)
                        : (double?)null,
                    departureUtc = activeFlight.PlannedDepartureUtc,
                    estimatedArrivalUtc = activeFlight.PlannedDepartureUtc.AddMinutes(activeFlight.PlannedBlockMinutes),
                    elapsedMinutes = snapshot.ElapsedBlockMinutes,
                    remainingMinutes = Math.Max(0, snapshot.PlannedBlockMinutes - snapshot.ElapsedBlockMinutes),
                });
            }
        }

        // Every virtual pilot's currently-airborne occurrence, interpolated from the schedule
        // template - see the class doc.
        var virtualPilotIds = await db.Pilots.Where(p => p.AirlineId == airline.Id && !p.IsPlayer).Select(p => p.Id).ToListAsync(ct);
        if (virtualPilotIds.Count > 0)
        {
            var schedules = await db.PilotSchedules.Where(s => virtualPilotIds.Contains(s.PilotId)).ToListAsync(ct);
            var pilotByScheduleId = schedules.ToDictionary(s => s.Id, s => s.PilotId);
            var scheduleIds = schedules.Select(s => s.Id).ToList();
            var entries = scheduleIds.Count == 0
                ? new List<PilotScheduleEntry>()
                : await db.PilotScheduleEntries.Where(e => scheduleIds.Contains(e.PilotScheduleId)).ToListAsync(ct);

            if (entries.Count > 0)
            {
                var pilotsById = await db.Pilots.Where(p => virtualPilotIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
                var routeIds = entries.Select(e => e.RouteId).Distinct().ToList();
                var routesById = await db.Routes.Where(r => routeIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
                var fleetIds = entries.Select(e => e.FleetAircraftId).Distinct().ToList();
                var fleetById = await db.FleetAircraft.Where(f => fleetIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);
                var typeIds = fleetById.Values.Select(f => f.AircraftTypeId).Distinct().ToList();
                var typesById = await db.AircraftTypes.Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
                var icaos = routesById.Values.SelectMany(r => new[] { r.DepartureIcao, r.ArrivalIcao }).Distinct().ToList();
                var airportsByIcao = await db.Airports.Where(a => icaos.Contains(a.Icao)).ToDictionaryAsync(a => a.Icao, ct);

                foreach (var entry in entries)
                {
                    if (!routesById.TryGetValue(entry.RouteId, out var route) ||
                        !fleetById.TryGetValue(entry.FleetAircraftId, out var aircraft) ||
                        !typesById.TryGetValue(aircraft.AircraftTypeId, out var type) ||
                        !airportsByIcao.TryGetValue(route.DepartureIcao, out var dep) ||
                        !airportsByIcao.TryGetValue(route.ArrivalIcao, out var arr) ||
                        !pilotByScheduleId.TryGetValue(entry.PilotScheduleId, out var pilotId) ||
                        !pilotsById.TryGetValue(pilotId, out var pilot))
                    {
                        continue;
                    }

                    var breakdown = BlockTimeEstimator.Estimate(
                        GreatCircle.DistanceNm(dep.Latitude, dep.Longitude, arr.Latitude, arr.Longitude), type.CruiseTasKts);

                    var departure = WeeklyOccurrenceCalculator.MostRecentOccurrenceAtOrBefore(entry.DayOfWeek, entry.DepartureTimeUtc, now);
                    var arrival = departure.AddMinutes(breakdown.TotalMinutes);
                    if (now >= arrival)
                    {
                        // Not currently airborne - either landed already (VirtualFlightResolverService
                        // will settle the economics for this occurrence once its pass reaches it) or
                        // this week's slot hasn't departed yet.
                        continue;
                    }

                    var elapsedMinutes = (now - departure).TotalMinutes;
                    var fraction = Math.Clamp(elapsedMinutes / breakdown.TotalMinutes, 0.0, 1.0);

                    // Position must advance along the AIRBORNE portion only, not the whole block.
                    // Block time includes taxi out and taxi in, so dividing by TotalMinutes started
                    // the aircraft sliding down the great circle from minute zero - it showed as
                    // "Taxi out" while already a tenth of the way across the country. An aircraft
                    // sits at its departure airport while taxiing out and at its arrival airport
                    // while taxiing in; only climb, cruise and descent actually cover the route.
                    var airborneMinutes = Math.Max(1.0, breakdown.TotalMinutes - breakdown.TaxiOutMinutes - breakdown.TaxiInMinutes);
                    var airborneElapsed = elapsedMinutes - breakdown.TaxiOutMinutes;
                    var routeFraction = Math.Clamp(airborneElapsed / airborneMinutes, 0.0, 1.0);

                    const int sampleCount = 101;
                    var path = GreatCircle.SamplePath(dep.Latitude, dep.Longitude, arr.Latitude, arr.Longitude, sampleCount);
                    var sampleIndex = Math.Clamp((int)Math.Round(routeFraction * (sampleCount - 1)), 0, sampleCount - 1);
                    var (lon, lat) = path[sampleIndex];
                    var nextIndex = Math.Min(sampleIndex + 1, sampleCount - 1);
                    var (nextLon, nextLat) = path[nextIndex];
                    var heading = sampleIndex == nextIndex
                        ? GreatCircle.InitialBearingDeg(lat, lon, arr.Latitude, arr.Longitude)
                        : GreatCircle.InitialBearingDeg(lat, lon, nextLat, nextLon);

                    aircraftList.Add(new
                    {
                        kind = "virtual",
                        flightId = (Guid?)null,
                        pilotName = pilot.Name,
                        registration = aircraft.Registration,
                        aircraftType = type.Name,
                        routeId = route.Id,
                        flightNumber = route.FlightNumber,
                        departureIcao = route.DepartureIcao,
                        arrivalIcao = route.ArrivalIcao,
                        latitudeDeg = lat,
                        longitudeDeg = lon,
                        headingDeg = heading,
                        phase = ResolvePhase(elapsedMinutes, breakdown),
                        percentComplete = Math.Round(fraction * 100.0, 1),
                        departureUtc = departure,
                        estimatedArrivalUtc = arrival,
                        elapsedMinutes = Math.Round(elapsedMinutes, 1),
                        remainingMinutes = Math.Round(breakdown.TotalMinutes - elapsedMinutes, 1),
                    });
                }
            }
        }

        var networkRoutes = await db.Routes.Where(r => r.AirlineId == airline.Id && r.IsActive).ToListAsync(ct);
        var networkIcaos = networkRoutes.SelectMany(r => new[] { r.DepartureIcao, r.ArrivalIcao }).Distinct().ToList();
        var networkAirports = await db.Airports.Where(a => networkIcaos.Contains(a.Icao)).ToDictionaryAsync(a => a.Icao, ct);

        var network = networkRoutes
            .Where(r => networkAirports.ContainsKey(r.DepartureIcao) && networkAirports.ContainsKey(r.ArrivalIcao))
            .Select(r =>
            {
                var dep = networkAirports[r.DepartureIcao];
                var arr = networkAirports[r.ArrivalIcao];
                return new
                {
                    routeId = r.Id,
                    departureIcao = r.DepartureIcao,
                    departureLat = dep.Latitude,
                    departureLon = dep.Longitude,
                    arrivalIcao = r.ArrivalIcao,
                    arrivalLat = arr.Latitude,
                    arrivalLon = arr.Longitude,
                    distanceNm = r.DistanceNm,
                };
            })
            .ToList();

        return Results.Ok(new { aircraft = aircraftList, network });
    }

    /// <summary>Maps elapsed minutes onto the same phase names FlightPhaseStateMachine uses for a
    /// real flight, so the live map's virtual and player markers read consistently.</summary>
    private static string ResolvePhase(double elapsedMinutes, BlockTimeBreakdown breakdown)
    {
        var afterTaxiOut = breakdown.TaxiOutMinutes;
        var afterClimb = afterTaxiOut + breakdown.ClimbMinutes;
        var afterCruise = afterClimb + breakdown.CruiseMinutes;
        var afterDescent = afterCruise + breakdown.DescentMinutes;

        return elapsedMinutes switch
        {
            var m when m < afterTaxiOut => "TaxiOut",
            var m when m < afterClimb => "Climb",
            var m when m < afterCruise => "Cruise",
            var m when m < afterDescent => "Descent",
            _ => "TaxiIn",
        };
    }
}
