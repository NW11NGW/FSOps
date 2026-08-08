using System.Text.Json;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Flights;
using FSOps.Core.Planning;
using FSOps.Data;
using FSOps.Server.Auth;
using FSOps.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Endpoints;

public static class FlightEndpoints
{
    public static void MapFlightEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapPost("/flights/start", StartAsync);
        group.MapPost("/flights/{id:guid}/abandon", AbandonAsync);
        group.MapPost("/flights/{id:guid}/complete-manual", CompleteManualAsync);
        group.MapGet("/flights/active", GetActiveAsync);
        group.MapGet("/flights/{id:guid}", GetByIdAsync);
        group.MapGet("/flights", ListAsync);
        group.MapGet("/flights/options", OptionsAsync);
    }

    internal static async Task<IResult> StartAsync(
        StartFlightRequest request, FsOpsDbContext db, ICurrentUser currentUser,
        FlightLifecycleService lifecycle, SimTelemetryService telemetry, EconomyConfig economyConfig, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline before starting a flight." });
        }

        if (await db.Flights.AnyAsync(f => f.AirlineId == airline.Id && f.Status == FlightStatus.InProgress, ct))
        {
            return Results.Conflict(new { error = "A flight is already in progress. Complete or abandon it before starting another." });
        }

        if (request.RouteId is not Guid routeId)
        {
            return Results.BadRequest(new { error = "routeId is required." });
        }

        var route = await db.Routes.FirstOrDefaultAsync(r => r.Id == routeId && r.AirlineId == airline.Id, ct);
        if (route is null)
        {
            return Results.NotFound(new { error = "Route not found." });
        }

        FleetAircraft? fleetAircraft;
        if (request.FleetAircraftId is Guid fleetAircraftId)
        {
            fleetAircraft = await db.FleetAircraft.FirstOrDefaultAsync(
                f => f.Id == fleetAircraftId && f.AirlineId == airline.Id && f.Status == FleetAircraftStatus.Active, ct);
            if (fleetAircraft is null)
            {
                return Results.BadRequest(new { error = "The selected aircraft is not an active member of your fleet." });
            }
        }
        else
        {
            var candidates = await db.FleetAircraft
                .Where(f => f.AirlineId == airline.Id && f.Status == FleetAircraftStatus.Active)
                .ToListAsync(ct);
            fleetAircraft = candidates.OrderBy(f => f.CreatedUtc).FirstOrDefault();
        }

        if (fleetAircraft is null)
        {
            return Results.BadRequest(new { error = "Your fleet has no active aircraft available to fly this route." });
        }

        var aircraftType = await db.AircraftTypes.FindAsync([fleetAircraft.AircraftTypeId], ct);
        if (aircraftType is null)
        {
            return Results.Problem("The selected aircraft's type could not be found.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var departure = await db.Airports.FirstOrDefaultAsync(a => a.Icao == route.DepartureIcao, ct);
        var arrival = await db.Airports.FirstOrDefaultAsync(a => a.Icao == route.ArrivalIcao, ct);
        if (departure is null || arrival is null)
        {
            return Results.Problem("This route's airports could not be found.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var pilot = await db.Pilots.FirstOrDefaultAsync(p => p.AirlineId == airline.Id && p.IsPlayer, ct)
            ?? await db.Pilots.FirstOrDefaultAsync(p => p.AirlineId == airline.Id, ct);
        if (pilot is null)
        {
            return Results.Problem("Your airline has no pilot on record.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var plan = RoutePreviewCalculator.Calculate(economyConfig, departure, arrival, aircraftType, airline.StrategyProfile);

        var currentAircraft = telemetry.CurrentAircraft;
        var titleFlown = currentAircraft?.Title ?? string.Empty;
        var atcModel = currentAircraft?.AtcModel;
        // An unrecognised (sim not connected / no aircraft loaded yet) aircraft is flagged the
        // same as a genuine mismatch - informational only, see AircraftTypeMatcher.
        var typeMismatch = !AircraftTypeMatcher.IsMatch(aircraftType.MatchPatterns, titleFlown, atcModel);

        var now = DateTimeOffset.UtcNow;
        var flight = new Flight
        {
            Id = Guid.NewGuid(),
            AirlineId = airline.Id,
            RouteId = route.Id,
            FleetAircraftId = fleetAircraft.Id,
            PilotId = pilot.Id,
            Status = FlightStatus.InProgress,
            PlannedDepartureUtc = now,
            PlannedBlockMinutes = plan.BlockTimeBreakdown.TotalMinutes,
            PaxBooked = aircraftType.PaxCapacity,
            FuelPlannedKg = plan.FuelBreakdown.TotalFuelKg,
            TitleFlown = titleFlown,
            TypeMismatch = typeMismatch,
            Revenue = 0m,
            TotalCost = 0m,
            CreatedUtc = now,
        };

        db.Flights.Add(flight);
        fleetAircraft.Status = FleetAircraftStatus.InFlight;

        // Fuel is charged when it's bought, not when it's burned - see docs/PLAN.md "Persistent
        // fuel state and tankering". FleetAircraft has no persisted tank state yet, so this treats
        // every flight as uplifting fuel at the departure airport, right now, rather than
        // detecting a real uplift event and carrying fuel over between flights. Posted
        // unconditionally, in the same SaveChangesAsync as the flight row below, so it stands even
        // if the flight is later abandoned (fuel already bought is never refunded).
        //
        // Charged on FuelBreakdown.ChargedFuelKg (trip + taxi + contingency), NOT TotalFuelKg: the
        // alternate and final-reserve allowances are loaded for safety and normally stay in the
        // tanks unburned, so billing the full block figure every sector would charge, in full,
        // for roughly 2,400 kg that's never actually consumed and then vanishes (no persisted tank
        // state to carry it forward) - see FuelBreakdown.ChargedFuelKg's doc for the full
        // rationale. FuelPlannedKg below still records the full realistic block-fuel figure a
        // pilot would actually load, for the flight brief and report card - only the charge is
        // limited to what the sector actually burns.
        var worldSeed = await FlightEconomicsPoster.ResolveWorldSeedAsync(db, ct);
        flight.TotalCost = FlightEconomicsPoster.PostFuelUplift(
            db, flight, economyConfig, departure, plan.FuelBreakdown.ChargedFuelKg, now, worldSeed);

        if (typeMismatch)
        {
            db.FlightEvents.Add(new FlightEvent
            {
                Id = Guid.NewGuid(),
                FlightId = flight.Id,
                Utc = now,
                Type = FlightEventType.Mismatch,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    titleFlown,
                    atcModel,
                    expectedFamily = aircraftType.Family,
                    expectedType = aircraftType.IcaoType,
                }),
            });
        }

        await db.SaveChangesAsync(ct);

        lifecycle.BeginTracking(flight.Id, airline.Id, fleetAircraft.Id, arrival.Icao, flight.PlannedBlockMinutes);

        return Results.Created($"/api/v1/flights/{flight.Id}", ToFlightDto(flight));
    }

    internal static async Task<IResult> AbandonAsync(Guid id, FsOpsDbContext db, ICurrentUser currentUser, FlightLifecycleService lifecycle, CancellationToken ct)
    {
        var flight = await LoadOwnedFlightAsync(db, currentUser, id, ct);
        if (flight is null)
        {
            return Results.NotFound();
        }

        if (flight.Status is not (FlightStatus.InProgress or FlightStatus.Interrupted))
        {
            return Results.BadRequest(new { error = $"Flight is {flight.Status} and cannot be abandoned." });
        }

        // Grab whatever telemetry position is still live before StopTracking drops it - an
        // abandoned flight never completed, so the aircraft normally stays exactly where it was,
        // but if the sim clearly shows it moved (it took off and got abandoned mid-air, say) that
        // move should still be reflected rather than pretending it's still at the gate.
        var lastSnapshot = lifecycle.GetActiveSnapshot(flight.Id);
        lifecycle.StopTracking(flight.Id);
        flight.Status = FlightStatus.Abandoned;
        await RevertFleetAircraftAsync(db, flight, lastSnapshot, ct);
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToFlightDto(flight));
    }

    internal static async Task<IResult> CompleteManualAsync(
        Guid id, FsOpsDbContext db, ICurrentUser currentUser, FlightLifecycleService lifecycle, EconomyConfig economyConfig, CancellationToken ct)
    {
        var flight = await LoadOwnedFlightAsync(db, currentUser, id, ct);
        if (flight is null)
        {
            return Results.NotFound();
        }

        if (flight.Status is not (FlightStatus.InProgress or FlightStatus.Interrupted))
        {
            return Results.BadRequest(new { error = $"Flight is {flight.Status} and cannot be manually completed." });
        }

        // Defensive: the Status guard above already stops a genuine HTTP retry (Status flips to
        // Completed below), but a flight somehow already processed is never reprocessed - see
        // Flight.RevenuePosted.
        if (flight.RevenuePosted)
        {
            return Results.Ok(ToFlightDto(flight));
        }

        lifecycle.StopTracking(flight.Id);

        // Best-available estimates for whatever OOOI the state machine never got to capture -
        // this path exists specifically for flights the state machine could not finish (sim
        // crashed, user stopped early), so some fields are necessarily approximate.
        var now = DateTimeOffset.UtcNow;
        flight.OutUtc ??= flight.CreatedUtc;
        flight.OffUtc ??= flight.OutUtc;
        flight.OnUtc ??= now;
        flight.InUtc ??= now;
        if (flight.FuelUsedKg <= 0)
        {
            flight.FuelUsedKg = flight.FuelPlannedKg;
        }

        flight.PaxFlown = flight.PaxBooked;
        flight.Status = FlightStatus.Completed;

        // No reliable telemetry for a manual completion (that's the whole reason this path
        // exists) - trust the planned arrival rather than guessing at a real position. That
        // applies to the economics too: OutUtc/InUtc above are best-effort stamps for the record,
        // but the actual wall-clock gap between them is whatever the user happened to take to
        // click "complete" (could be seconds), not how long the sector really took. Using it to
        // drive maintenance accrual/crew cost let a flight completed instantly accrue a few pence
        // of maintenance instead of a realistic full-sector figure. Planned block time is the
        // honest basis here, exactly as the real-telemetry completion path (FlightLifecycleService)
        // uses the flight's actual measured Out/In gap for the same purpose.
        var route = await db.Routes.FirstOrDefaultAsync(r => r.Id == flight.RouteId, ct);
        var flightHours = flight.PlannedBlockMinutes / 60.0;

        var fleetAircraft = await db.FleetAircraft.FirstOrDefaultAsync(f => f.Id == flight.FleetAircraftId, ct);
        if (fleetAircraft is not null)
        {
            if (fleetAircraft.Status == FleetAircraftStatus.InFlight)
            {
                fleetAircraft.Status = FleetAircraftStatus.Active;
            }

            if (route is not null)
            {
                fleetAircraft.LocationIcao = route.ArrivalIcao;
            }

            fleetAircraft.AirframeHours += flightHours;
        }

        // Manual completion posts ticket revenue and normal sector costs, but never a landing
        // quality or punctuality bonus - there is no such mechanism to begin with (see
        // FlightEconomicsResult), and this path exists precisely because no reliable telemetry
        // was ever captured to score one. Skips quietly (no ledger lines) if any of the data an
        // economics calculation needs isn't resolvable - better to post nothing than guess.
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.Id == flight.AirlineId, ct);
        var arrivalAirport = route is not null ? await db.Airports.FirstOrDefaultAsync(a => a.Icao == route.ArrivalIcao, ct) : null;
        var aircraftType = fleetAircraft is not null ? await db.AircraftTypes.FindAsync([fleetAircraft.AircraftTypeId], ct) : null;

        if (route is not null && airline is not null && arrivalAirport is not null && aircraftType is not null)
        {
            await FlightEconomicsPoster.PostCompletionAsync(
                db, flight, airline, route, aircraftType, arrivalAirport, economyConfig, flightHours, now, ct);
        }

        await db.SaveChangesAsync(ct);

        return Results.Ok(ToFlightDto(flight));
    }

    private static async Task<IResult> GetActiveAsync(FsOpsDbContext db, ICurrentUser currentUser, FlightLifecycleService lifecycle, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.NoContent();
        }

        var flight = await db.Flights.FirstOrDefaultAsync(
            f => f.AirlineId == airline.Id && (f.Status == FlightStatus.InProgress || f.Status == FlightStatus.Interrupted), ct);
        if (flight is null)
        {
            return Results.NoContent();
        }

        var snapshot = lifecycle.GetActiveSnapshot(flight.Id);
        return Results.Ok(new
        {
            flight = ToFlightDto(flight),
            needsResolution = flight.Status == FlightStatus.Interrupted,
            live = snapshot,
        });
    }

    private static async Task<IResult> GetByIdAsync(Guid id, FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var flight = await LoadOwnedFlightAsync(db, currentUser, id, ct);
        if (flight is null)
        {
            return Results.NotFound();
        }

        // Materialise first - the SQLite provider can't translate ORDER BY over DateTimeOffset.
        var events = (await db.FlightEvents.Where(e => e.FlightId == flight.Id).ToListAsync(ct))
            .OrderBy(e => e.Utc)
            .Select(e => new { e.Id, e.Utc, Type = e.Type.ToString(), e.PayloadJson });

        // The itemised financial outcome for the report card - the actual posted ledger rows,
        // never a recomputation. This is the same append-only source the airline's cash balance
        // sums, scoped to this one flight.
        var ledgerTransactions = (await db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync(ct))
            .OrderBy(t => t.Utc)
            .Select(t => new { t.Id, t.Utc, Category = t.Category.ToString(), t.Amount, t.Description });

        return Results.Ok(new { flight = ToFlightDto(flight), events, ledgerTransactions });
    }

    private static async Task<IResult> ListAsync(FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(Array.Empty<object>());
        }

        // Materialise first - the SQLite provider can't translate ORDER BY over DateTimeOffset.
        var flights = (await db.Flights.Where(f => f.AirlineId == airline.Id).ToListAsync(ct))
            .OrderByDescending(f => f.CreatedUtc)
            .Select(ToFlightDto);

        return Results.Ok(flights);
    }

    /// <summary>
    /// Backs the Fly screen: for every active route, reports its flight number/distance/block
    /// time plus which fleet aircraft (if any) is sitting at the route's departure airport ready
    /// to fly it "right now" - e.g. an aircraft that just landed at EGPH makes the EGPH-&gt;EGGD
    /// route flyable even though the outbound EGGD-&gt;EGPH route isn't (its aircraft is gone).
    /// Routes with nothing available get a human-readable reason instead.
    /// </summary>
    private static async Task<IResult> OptionsAsync(FsOpsDbContext db, ICurrentUser currentUser, EconomyConfig economyConfig, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(Array.Empty<object>());
        }

        // Materialise first - the SQLite provider can't translate ORDER BY over DateTimeOffset.
        var routes = (await db.Routes.Where(r => r.AirlineId == airline.Id && r.IsActive).ToListAsync(ct))
            .OrderBy(r => r.CreatedUtc)
            .ToList();
        if (routes.Count == 0)
        {
            return Results.Ok(Array.Empty<object>());
        }

        var fleet = await db.FleetAircraft.Where(f => f.AirlineId == airline.Id).ToListAsync(ct);
        var fleetTypeIds = fleet.Select(f => f.AircraftTypeId).Distinct().ToList();
        var aircraftTypesById = await db.AircraftTypes.Where(t => fleetTypeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);

        // Falls back to the airline's cheapest/first type by ICAO code when no specific aircraft
        // is under consideration for a route, purely so distance/block time still has something
        // sensible to show - mirrors RouteEndpoints.ResolveAircraftTypeAsync's own fallback.
        var fallbackAircraftType = aircraftTypesById.Values.OrderBy(t => t.IcaoType).FirstOrDefault()
            ?? await db.AircraftTypes.OrderBy(t => t.IcaoType).FirstOrDefaultAsync(ct);

        var icaos = routes.SelectMany(r => new[] { r.DepartureIcao, r.ArrivalIcao }).Distinct().ToList();
        var airportsByIcao = await db.Airports.Where(a => icaos.Contains(a.Icao)).ToDictionaryAsync(a => a.Icao, ct);

        // Starting a flight is blocked airline-wide while one is already in progress (see
        // StartAsync above), regardless of which aircraft or route would otherwise be used.
        var hasFlightInProgress = await db.Flights.AnyAsync(f => f.AirlineId == airline.Id && f.Status == FlightStatus.InProgress, ct);

        var options = new List<object>();
        foreach (var route in routes)
        {
            if (!airportsByIcao.TryGetValue(route.DepartureIcao, out var departure) ||
                !airportsByIcao.TryGetValue(route.ArrivalIcao, out var arrival))
            {
                // World data shouldn't be able to drift out from under an existing route, but
                // skip rather than fail the whole list if it somehow has.
                continue;
            }

            var readyAircraft = fleet
                .Where(f => f.LocationIcao == route.DepartureIcao && f.Status == FleetAircraftStatus.Active)
                .OrderBy(f => f.CreatedUtc)
                .FirstOrDefault();

            string? reason = null;
            var consideredAircraft = readyAircraft;

            if (readyAircraft is null)
            {
                var atDeparture = fleet.Where(f => f.LocationIcao == route.DepartureIcao).ToList();
                var inFlight = atDeparture.FirstOrDefault(f => f.Status == FleetAircraftStatus.InFlight);
                var inMaintenance = atDeparture.FirstOrDefault(f => f.Status == FleetAircraftStatus.InMaintenance);

                if (inFlight is not null)
                {
                    reason = $"Your aircraft at {route.DepartureIcao} is currently in flight.";
                    consideredAircraft = inFlight;
                }
                else if (inMaintenance is not null)
                {
                    reason = $"Your aircraft at {route.DepartureIcao} is in maintenance.";
                    consideredAircraft = inMaintenance;
                }
                else
                {
                    var activeLocations = fleet
                        .Where(f => f.Status == FleetAircraftStatus.Active)
                        .Select(f => f.LocationIcao)
                        .Distinct()
                        .ToList();
                    reason = activeLocations.Count > 0
                        ? $"No aircraft at {route.DepartureIcao} - your fleet is currently at {string.Join(", ", activeLocations)}."
                        : "No active aircraft is available in your fleet.";
                }
            }
            else if (hasFlightInProgress)
            {
                reason = "A flight is already in progress - complete or abandon it before starting another.";
            }

            var isFlyable = readyAircraft is not null && !hasFlightInProgress;

            var aircraftType = consideredAircraft is not null && aircraftTypesById.TryGetValue(consideredAircraft.AircraftTypeId, out var matchedType)
                ? matchedType
                : fallbackAircraftType;

            int? estimatedBlockMinutes = null;
            double? distanceNm = route.DistanceNm;
            if (aircraftType is not null)
            {
                var preview = RoutePreviewCalculator.Calculate(economyConfig, departure, arrival, aircraftType, airline.StrategyProfile);
                estimatedBlockMinutes = preview.BlockTimeBreakdown.TotalMinutes;
            }

            options.Add(new
            {
                routeId = route.Id,
                route.FlightNumber,
                route.DepartureIcao,
                DepartureName = departure.Name,
                route.ArrivalIcao,
                ArrivalName = arrival.Name,
                distanceNm,
                estimatedBlockMinutes,
                isFlyable,
                fleetAircraftId = readyAircraft?.Id,
                aircraftRegistration = readyAircraft?.Registration,
                // The Fly screen's brief needs this to show "N booked / capacity" - already
                // resolved above for the block-time estimate, so exposing it here is free.
                aircraftTypeId = aircraftType?.Id,
                paxCapacity = aircraftType?.PaxCapacity,
                reason = isFlyable ? null : reason,
            });
        }

        return Results.Ok(options);
    }

    private static async Task<Flight?> LoadOwnedFlightAsync(FsOpsDbContext db, ICurrentUser currentUser, Guid flightId, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return null;
        }

        return await db.Flights.FirstOrDefaultAsync(f => f.Id == flightId && f.AirlineId == airline.Id, ct);
    }

    internal static async Task RevertFleetAircraftAsync(FsOpsDbContext db, Flight flight, LiveFlightSnapshot? lastSnapshot, CancellationToken ct)
    {
        var fleetAircraft = await db.FleetAircraft.FirstOrDefaultAsync(f => f.Id == flight.FleetAircraftId, ct);
        if (fleetAircraft is null)
        {
            return;
        }

        if (fleetAircraft.Status == FleetAircraftStatus.InFlight)
        {
            fleetAircraft.Status = FleetAircraftStatus.Active;
        }

        if (lastSnapshot is null)
        {
            // No telemetry was ever received for this attempt - the aircraft never left where it
            // already was recorded, so there's nothing to resolve.
            return;
        }

        var candidateAirports = await AirportProximityQueries.NearbyAsync(db, lastSnapshot.LatitudeDeg, lastSnapshot.LongitudeDeg, ct);
        var landing = LandingAirportResolver.Resolve(
            candidateAirports, (lastSnapshot.LatitudeDeg, lastSnapshot.LongitudeDeg), fleetAircraft.LocationIcao);

        // Only overwrite the recorded location on a clear move away from it - "still there" and
        // "couldn't be determined" both mean leave it exactly where it already was, per the
        // "NEVER destroy the user's data" project rule of not guessing at state changes.
        if (landing.Decision == LandingAirportDecision.Diverted)
        {
            fleetAircraft.LocationIcao = landing.Icao;
        }
    }

    private static object ToFlightDto(Flight f) => new
    {
        f.Id,
        f.AirlineId,
        f.RouteId,
        f.FleetAircraftId,
        f.PilotId,
        Status = f.Status.ToString(),
        f.PlannedDepartureUtc,
        f.PlannedBlockMinutes,
        f.OutUtc,
        f.OffUtc,
        f.OnUtc,
        f.InUtc,
        f.PaxBooked,
        f.PaxFlown,
        f.FuelPlannedKg,
        f.FuelUsedKg,
        f.LandingFpmFirst,
        f.LandingFpmHardest,
        f.LandingGForce,
        f.CentrelineDeviationM,
        f.TitleFlown,
        f.TypeMismatch,
        f.SimRateElevated,
        f.MaxSimulationRateObserved,
        f.SlewDetected,
        f.PositionJumpDetected,
        f.Revenue,
        f.TotalCost,
        f.CreatedUtc,
    };
}

public sealed record StartFlightRequest(Guid? RouteId, Guid? FleetAircraftId);
