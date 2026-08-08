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
    /// <summary>
    /// How stale a telemetry sample can be at flight start and still count as "the sim just told
    /// us the real fuel figure" for reconciliation - generous enough to cover the gap between a
    /// pilot finishing the pre-flight fuel load and pressing "start flight" in FSOps, tight enough
    /// that a sample from hours ago (sim since disconnected) never gets treated as current.
    /// </summary>
    private static readonly TimeSpan TelemetryReconciliationWindow = TimeSpan.FromMinutes(10);

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
        FlightLifecycleService lifecycle, SimTelemetryService telemetry, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline before starting a flight." });
        }

        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);

        // Same lazy-release as OptionsAsync - an aircraft whose grounding has already elapsed must
        // be selectable the moment a player tries to fly it, not only once the Fly screen has been
        // reloaded since the release moment.
        await MaintenanceReleaser.ReleaseDueAsync(db, airline.Id, DateTimeOffset.UtcNow, ct);

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

        // Fuel is a persisted asset on FleetAircraft.FuelOnBoardKg, charged when it's bought
        // (uplifted), never on burn - see docs/PLAN.md "Persistent fuel state and tankering".
        // Whatever's left in the tank from the last flight carries forward, so a return leg (or
        // any sector) flown on fuel already on board posts no fuel charge at all here.
        //
        // RECONCILIATION (real path): if the sim is connected and has reported a sample recently,
        // that reading is the one true source of "how much fuel is actually in the tank right
        // now" - see FuelUpliftDetector. A rise since the tracked figure is charged as an uplift,
        // at THIS airport's price (reconciliation happens at flight start, so "this airport" is
        // the departure airport); a fall is silently absorbed as consumed, never credited. This is
        // what catches fuel that changed while FSOps wasn't watching - the sim restarted, a menu
        // fuel set, or (most commonly) the pilot topping off the tank before pressing "start
        // flight" here. Once tracking begins, further live uplifts/defuels on the ground are
        // caught the same way by FlightLifecycleService.ProcessSample.
        //
        // NO-TELEMETRY FALLBACK: with nothing to observe (sim not connected, or manual completion
        // is this flight's only realistic path), this makes the same conservative assumption the
        // interim fuel-honesty fix made: top up to exactly this sector's own normal requirement
        // (FuelBreakdown.ChargedFuelKg - trip, taxi, contingency; deliberately NOT the
        // alternate/reserve a real pilot would also load) if the tank doesn't already hold that
        // much. This keeps every pre-existing balance figure unchanged for a fresh/untracked
        // aircraft - a bigger top-up here would double-count a benefit (carrying genuinely-bought
        // reserve fuel forward) that only a real, observed uplift is entitled to.
        var worldSeed = await FlightEconomicsPoster.ResolveWorldSeedAsync(db, ct);
        var fuelUpliftCost = 0m;
        var recentSample = telemetry.LastSample;
        var sampleIsRecent = telemetry.LastSampleUtc is { } lastSampleUtc && now - lastSampleUtc <= TelemetryReconciliationWindow;

        if (recentSample is not null && sampleIsRecent)
        {
            var change = FuelUpliftDetector.Classify(fleetAircraft.FuelOnBoardKg, recentSample.TotalFuelKg);
            if (change == GroundFuelChangeKind.Uplift)
            {
                var deltaKg = FuelUpliftDetector.MagnitudeKg(fleetAircraft.FuelOnBoardKg, recentSample.TotalFuelKg);
                fuelUpliftCost = FlightEconomicsPoster.PostFuelUplift(db, flight, economyConfig, departure, deltaKg, now, worldSeed);
            }

            // Uplift, defuel, or no change - the tracked figure now matches reality either way.
            fleetAircraft.FuelOnBoardKg = recentSample.TotalFuelKg;
        }
        else if (fleetAircraft.FuelOnBoardKg < plan.FuelBreakdown.ChargedFuelKg)
        {
            var shortfallKg = plan.FuelBreakdown.ChargedFuelKg - fleetAircraft.FuelOnBoardKg;
            fuelUpliftCost = FlightEconomicsPoster.PostFuelUplift(db, flight, economyConfig, departure, shortfallKg, now, worldSeed);
            fleetAircraft.FuelOnBoardKg += shortfallKg;
        }

        flight.TotalCost = fuelUpliftCost;

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

        lifecycle.BeginTracking(flight.Id, airline.Id, fleetAircraft.Id, arrival.Icao, flight.PlannedBlockMinutes, fleetAircraft.FuelOnBoardKg);

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
        Guid id, FsOpsDbContext db, ICurrentUser currentUser, FlightLifecycleService lifecycle, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
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

        // Fetched before the fleet-aircraft block (rather than alongside arrivalAirport/aircraftType
        // below) because MaintenancePoster needs it too - see the matching comment in
        // FlightLifecycleService's telemetry completion path, which this manual path mirrors.
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.Id == flight.AirlineId, ct);

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

            if (airline is not null)
            {
                MaintenancePoster.PostFlightHours(db, fleetAircraft, airline, economyConfigCatalog.Get(airline.Playstyle), flightHours, now);
            }
            else
            {
                fleetAircraft.AirframeHours += flightHours;
            }

            // No reliable telemetry means no reliable fuel reading either (that's the whole
            // reason this path exists) - rather than let the persisted asset drift from
            // best-effort arithmetic, treat it as consumed and let the next flight's own
            // StartAsync reconciliation (or its no-telemetry fallback) start the tank fresh. This
            // stays conservative and honest instead of guessing at how much of the fuel bought at
            // start actually got burned.
            fleetAircraft.FuelOnBoardKg = 0;
        }

        // Manual completion posts ticket revenue and normal sector costs, but never a landing
        // quality or punctuality bonus - there is no such mechanism to begin with (see
        // FlightEconomicsResult), and this path exists precisely because no reliable telemetry
        // was ever captured to score one. Skips quietly (no ledger lines) if any of the data an
        // economics calculation needs isn't resolvable - better to post nothing than guess.
        var arrivalAirport = route is not null ? await db.Airports.FirstOrDefaultAsync(a => a.Icao == route.ArrivalIcao, ct) : null;
        var aircraftType = fleetAircraft is not null ? await db.AircraftTypes.FindAsync([fleetAircraft.AircraftTypeId], ct) : null;

        if (route is not null && airline is not null && arrivalAirport is not null && aircraftType is not null)
        {
            var economyConfig = economyConfigCatalog.Get(airline.Playstyle);
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

        // The persisted asset's CURRENT value - accurate as "fuel remaining after this flight"
        // when it's the aircraft's most recent one (the common case: viewing the report card right
        // after landing), but drifts once a later flight has flown. Null if the aircraft record is
        // gone (sold/removed) rather than guessing - see docs/PLAN.md "Persistent fuel state and
        // tankering".
        var aircraftFuelOnBoardKg = await db.FleetAircraft
            .Where(f => f.Id == flight.FleetAircraftId)
            .Select(f => (double?)f.FuelOnBoardKg)
            .FirstOrDefaultAsync(ct);

        return Results.Ok(new { flight = ToFlightDto(flight), events, ledgerTransactions, aircraftFuelOnBoardKg });
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
    internal static async Task<IResult> OptionsAsync(FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(Array.Empty<object>());
        }

        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);

        // A grounding whose downtime has already elapsed is released before this list is built, so
        // "in maintenance" here only ever means genuinely still grounded, never stale state a
        // background pass hasn't caught up to yet - see MaintenanceReleaser's class doc.
        await MaintenanceReleaser.ReleaseDueAsync(db, airline.Id, DateTimeOffset.UtcNow, ct);

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
                    // "Why and until when", not merely "in maintenance" - see docs/PLAN.md's E1
                    // brief. GroundedUntilUtc should always be set whenever Status is InMaintenance
                    // (MaintenancePoster sets both together), but a plain fallback covers the
                    // theoretical gap rather than showing a broken/missing date to the player.
                    reason = inMaintenance.GroundedUntilUtc is { } until
                        ? $"Your aircraft at {route.DepartureIcao} is in maintenance until {until:yyyy-MM-dd HH:mm} UTC."
                        : $"Your aircraft at {route.DepartureIcao} is in maintenance.";
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
            // already was recorded, and its fuel is whatever StartAsync's reconciliation already
            // set - so there's nothing further to resolve.
            return;
        }

        // The most recent reading actually observed for this attempt - more accurate than
        // whatever was known at flight start, since the aircraft may have burned fuel (or been
        // topped up again) before it was abandoned. Fuel already bought is never refunded, so
        // this only ever syncs the tracked figure to reality, never reverses a charge.
        fleetAircraft.FuelOnBoardKg = Math.Max(0, lastSnapshot.FuelRemainingKg);

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
        f.UnflyableReason,
        f.CreatedUtc,
    };
}

public sealed record StartFlightRequest(Guid? RouteId, Guid? FleetAircraftId);
