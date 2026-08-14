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

    // Stateless - safe to share as singletons rather than requiring DI registration. Ordered
    // SimBrief-first: ResolveFlightPlanAsync returns the first provider that succeeds, and
    // BuiltInFlightPlanProvider always does (see its own doc comment), so this pair is guaranteed
    // to produce a usable plan even when SimBrief is unreachable or no Pilot ID is configured.
    private static readonly SimBriefFlightPlanProvider SimBriefProvider = new();
    private static readonly BuiltInFlightPlanProvider BuiltInProvider = new();

    public static void MapFlightEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapPost("/flights/start", StartAsync);
        group.MapPost("/flights/{id:guid}/abandon", AbandonAsync);
        group.MapPost("/flights/{id:guid}/complete-manual", CompleteManualAsync);
        group.MapGet("/flights/active", GetActiveAsync);
        // Registered before the "/flights/{id:guid}" template so the literal segment is matched as
        // a literal - ASP.NET Core routing would prefer the literal anyway, but keeping the more
        // specific route first makes the intent obvious rather than implicit.
        group.MapGet("/flights/logbook", LogbookAsync);
        group.MapGet("/flights/{id:guid}/track", TrackAsync);
        group.MapGet("/flights/{id:guid}", GetByIdAsync);
        group.MapGet("/flights", ListAsync);
        group.MapGet("/flights/options", OptionsAsync);
        group.MapGet("/flights/plan-import", PlanImportAsync);
    }

    /// <summary>
    /// Composes the configured providers in priority order and returns the first plan any of them
    /// can actually produce - SimBrief when a Pilot ID is set and its latest OFP matches this
    /// route, the built-in estimator otherwise. Never throws and never returns a null outcome:
    /// BuiltInFlightPlanProvider is always included last and always succeeds.
    /// </summary>
    private static async Task<FlightPlanOutcome> ResolveFlightPlanAsync(FlightPlanRequest request, CancellationToken ct)
    {
        var simBriefOutcome = await SimBriefProvider.GetPlanAsync(request, ct);
        if (simBriefOutcome.Success)
        {
            return simBriefOutcome;
        }

        var builtInOutcome = await BuiltInProvider.GetPlanAsync(request, ct);
        return MergeFallback(simBriefOutcome, builtInOutcome);
    }

    /// <summary>
    /// Carries the failed provider's own reason for falling back (no Pilot ID, unknown Pilot ID, a
    /// timeout, a city-pair mismatch, ...) forward onto the plan that's actually used -
    /// BuiltInFlightPlanProvider itself never sets a message (it always succeeds outright), so
    /// without this the player would see a plan with no explanation of why it isn't from SimBrief.
    /// Saying so plainly means WHY, not just THAT. A pure,
    /// I/O-free function so this fallback contract is unit-testable without a network call.
    /// </summary>
    internal static FlightPlanOutcome MergeFallback(FlightPlanOutcome primaryOutcome, FlightPlanOutcome fallbackOutcome) =>
        primaryOutcome.Success ? primaryOutcome : fallbackOutcome with { Message = primaryOutcome.Message };

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

        // Loaded here (rather than just before the plan is built, as before) because the auto-pick
        // branch below needs the actual runway rows too - Include(Runways) is what
        // RunwaySuitabilityAssessor needs beyond the stamped LongestRunwayFt.
        var departure = await db.Airports.Include(a => a.Runways).FirstOrDefaultAsync(a => a.Icao == route.DepartureIcao, ct);
        var arrival = await db.Airports.Include(a => a.Runways).FirstOrDefaultAsync(a => a.Icao == route.ArrivalIcao, ct);
        if (departure is null || arrival is null)
        {
            return Results.Problem("This route's airports could not be found.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Reservation is the sole gate on both sides (decided 2026-08-09): the
        // player may ONLY fly an aircraft reserved to them. This is enforced here as well as by
        // OptionsAsync only ever OFFERING reserved airframes - never trust the client to have
        // respected what it was shown, the same way every other write endpoint in this app
        // re-validates server-side rather than assuming the UI enforced it. Position is checked here
        // too (it never was before this pass) - "you can only start a flight from where the aircraft
        // actually is" applies exactly as much to a
        // hand-crafted request as to one built by clicking through the Fly screen.
        FleetAircraft? fleetAircraft;
        if (request.FleetAircraftId is Guid fleetAircraftId)
        {
            fleetAircraft = await db.FleetAircraft.FirstOrDefaultAsync(
                f => f.Id == fleetAircraftId && f.AirlineId == airline.Id && f.Status == FleetAircraftStatus.Active, ct);
            if (fleetAircraft is null)
            {
                return Results.BadRequest(new { error = "The selected aircraft is not an active member of your fleet." });
            }

            if (!fleetAircraft.ReservedForPlayer)
            {
                return Results.BadRequest(new { error = $"{fleetAircraft.Registration} is not reserved to you - reserve it from the Fleet page before flying it." });
            }

            if (!string.Equals(fleetAircraft.LocationIcao, route.DepartureIcao, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = $"{fleetAircraft.Registration} is at {fleetAircraft.LocationIcao}, not {route.DepartureIcao} - choose another aircraft or fly it back." });
            }
        }
        else
        {
            var candidates = await db.FleetAircraft
                .Where(f => f.AirlineId == airline.Id && f.Status == FleetAircraftStatus.Active &&
                            f.ReservedForPlayer && f.LocationIcao == route.DepartureIcao)
                .ToListAsync(ct);

            // Only ever auto-pick an aircraft that can actually reach the destination AND physically
            // use both runways - otherwise "start a flight on this route" would pick the oldest
            // airframe and then refuse it on range or runway grounds below while a perfectly capable
            // one sat on the same apron.
            var candidateTypeIds = candidates.Select(f => f.AircraftTypeId).Distinct().ToList();
            var candidateTypesById = await db.AircraftTypes.Where(t => candidateTypeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);

            bool CandidateFits(FleetAircraft f) =>
                !candidateTypesById.TryGetValue(f.AircraftTypeId, out var t) ||
                (RouteRangeAssessor.CanReach(t.RangeNm, route.DistanceNm) &&
                 RunwaySuitabilityAssessor.AssessRoute(departure, arrival, t.MinRunwayFt, t.MtowTonnes, out _) == RunwaySuitabilityProblem.None);

            fleetAircraft = candidates
                .Where(CandidateFits)
                .OrderBy(f => f.CreatedUtc)
                .FirstOrDefault();

            // "Nothing is available here" would be actively misleading when aircraft ARE parked here
            // and reserved to you - they just can't do this leg. Say which problem it is: range if
            // NOTHING there can reach the destination at all, runway suitability otherwise (some
            // parked aircraft can reach it, but none of those can use these runways).
            if (fleetAircraft is null && candidates.Count > 0)
            {
                var canReachAny = candidates.Any(f => !candidateTypesById.TryGetValue(f.AircraftTypeId, out var t) || RouteRangeAssessor.CanReach(t.RangeNm, route.DistanceNm));
                var error = canReachAny
                    ? $"Nothing reserved to you at {route.DepartureIcao} can use {route.DepartureIcao} and {route.ArrivalIcao}'s runways - " +
                      "every aircraft you have there that can reach it needs a longer or more solid runway than one end has."
                    : $"Nothing reserved to you at {route.DepartureIcao} can reach {route.ArrivalIcao} - " +
                      $"{route.DistanceNm:F0} nm is beyond the range of every aircraft you have there.";
                return Results.BadRequest(new { error });
            }
        }

        if (fleetAircraft is null)
        {
            return Results.BadRequest(new { error = $"No aircraft reserved to you is available at {route.DepartureIcao} to fly this route." });
        }

        var aircraftType = await db.AircraftTypes.FindAsync([fleetAircraft.AircraftTypeId], ct);
        if (aircraftType is null)
        {
            return Results.Problem("The selected aircraft's type could not be found.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // Enforced here as well as by OptionsAsync marking an out-of-range airframe unflyable, for
        // exactly the reason reservation and position are: filtering a list is presentation, and a
        // stale client or a hand-made request must not be able to launch a sector the aircraft
        // physically cannot complete.
        if (!RouteRangeAssessor.CanReach(aircraftType.RangeNm, route.DistanceNm))
        {
            return Results.BadRequest(new
            {
                error = $"{fleetAircraft.Registration} ({aircraftType.Name}) can't reach {route.ArrivalIcao} - " +
                        $"{route.DistanceNm:F0} nm is beyond its practical range of about " +
                        $"{RouteRangeAssessor.OperationalRangeNm(aircraftType.RangeNm):F0} nm.",
            });
        }

        // Runway suitability mirrors range exactly: guidance at route planning, a hard refusal here.
        // Length and surface are both physical facts about this specific airframe and this specific
        // piece of ground - never a type-matching penalty (see RunwaySuitabilityAssessor's class doc).
        var runwayProblem = RunwaySuitabilityAssessor.AssessRoute(departure, arrival, aircraftType.MinRunwayFt, aircraftType.MtowTonnes, out var blockingAirport);
        if (runwayProblem != RunwaySuitabilityProblem.None)
        {
            var runwayError = runwayProblem == RunwaySuitabilityProblem.TooShort
                ? $"{fleetAircraft.Registration} ({aircraftType.Name}) can't use {blockingAirport.Icao} - its longest runway is " +
                  $"{blockingAirport.LongestRunwayFt:N0} ft, short of the {aircraftType.MinRunwayFt:N0} ft it needs."
                : $"{fleetAircraft.Registration} ({aircraftType.Name}) is too heavy for {blockingAirport.Icao}'s runways - none of them " +
                  "are paved, and a heavy aircraft can't use grass, gravel, dirt or water regardless of length.";
            return Results.BadRequest(new { error = runwayError });
        }

        var pilot = await db.Pilots.FirstOrDefaultAsync(p => p.AirlineId == airline.Id && p.IsPlayer, ct)
            ?? await db.Pilots.FirstOrDefaultAsync(p => p.AirlineId == airline.Id, ct);
        if (pilot is null)
        {
            return Results.Problem("Your airline has no pilot on record.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var plan = RoutePreviewCalculator.Calculate(economyConfig, departure, arrival, aircraftType, airline.StrategyProfile);

        // Enrichment only - never touches the route, the aircraft, or the schedule, and never
        // affects the fuel-uplift reconciliation below (that stays anchored to `plan`, the
        // deterministic estimate, exactly as before). This purely decides what PlannedBlockMinutes
        // and FuelPlannedKg record as "the plan" for this sector: a real SimBrief OFP when the
        // player has a Pilot ID set and its latest plan matches this route, the same built-in
        // estimate as before otherwise. Which plan was used never affects payment: FSOps bills
        // from what the sim actually reported, so planning elsewhere costs and earns the same.
        var userSettings = await db.UserSettings.FirstOrDefaultAsync(s => s.OwnerUserId == currentUser.UserId, ct);
        var flightPlanRequest = new FlightPlanRequest(departure, arrival, aircraftType, economyConfig, airline.StrategyProfile, userSettings?.SimBriefPilotId);
        var flightPlanOutcome = await ResolveFlightPlanAsync(flightPlanRequest, ct);
        var plannedBlockMinutes = flightPlanOutcome.Plan?.BlockTimeMinutes ?? plan.BlockTimeBreakdown.TotalMinutes;
        var fuelPlannedKg = flightPlanOutcome.Plan?.BlockFuelKg ?? plan.FuelBreakdown.TotalFuelKg;

        var currentAircraft = telemetry.CurrentAircraft;
        var titleFlown = currentAircraft?.Title ?? string.Empty;
        var atcModel = currentAircraft?.AtcModel;
        // Null (unknown) when the sim hasn't told us anything to check - not connected, or no
        // aircraft loaded yet - rather than treating "nothing to compare" as a failed comparison.
        // Only when there IS a title/model do we ask whether it matches the route's expected family.
        bool? typeMismatch = AircraftTypeMatcher.HasAircraftData(titleFlown, atcModel)
            ? !AircraftTypeMatcher.IsMatch(aircraftType.MatchPatterns, titleFlown, atcModel)
            : null;

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
            PlannedBlockMinutes = plannedBlockMinutes,
            PaxBooked = aircraftType.PaxCapacity,
            FuelPlannedKg = fuelPlannedKg,
            TitleFlown = titleFlown,
            TypeMismatch = typeMismatch,
            Revenue = 0m,
            TotalCost = 0m,
            CreatedUtc = now,
        };

        db.Flights.Add(flight);
        fleetAircraft.Status = FleetAircraftStatus.InFlight;

        // Fuel is no longer billed here at all - a sector is billed for what it actually burns, at
        // the departure airport's price, once the burn is known (see
        // FlightLifecycleService.FinalizeFlightAsync, FlightEndpoints.CompleteManualAsync, and
        // AbandonAsync below). FuelOnBoardKg stays informational only (see its own doc): synced
        // from a recent telemetry reading when one is available, so the Fleet page shows something
        // real, but never invented and never a source of a charge in its own right.
        var recentSample = telemetry.LastSample;
        var sampleIsRecent = telemetry.LastSampleUtc is { } lastSampleUtc && now - lastSampleUtc <= TelemetryReconciliationWindow;
        if (recentSample is not null && sampleIsRecent)
        {
            fleetAircraft.FuelOnBoardKg = recentSample.TotalFuelKg;
        }

        if (typeMismatch == true)
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

        lifecycle.BeginTracking(
            flight.Id, airline.Id, fleetAircraft.Id, arrival.Icao, flight.PlannedBlockMinutes,
            (departure.Latitude, departure.Longitude));

        // planSource/planMessage are informational only, alongside the flight's own fields - never
        // persisted (no schema change needed), just what the player has to be told plainly about
        // where this sector's plan came from, and why, when it isn't the one they expected.
        return Results.Created($"/api/v1/flights/{flight.Id}", ToFlightStartDto(flight, flightPlanOutcome));
    }

    internal static async Task<IResult> AbandonAsync(
        Guid id, FsOpsDbContext db, ICurrentUser currentUser, FlightLifecycleService lifecycle, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
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

        // Grab whatever telemetry position - and measured fuel burn - is still live before
        // StopTracking drops them. An abandoned flight never completed, so the aircraft normally
        // stays exactly where it was, but if the sim clearly shows it moved (it took off and got
        // abandoned mid-air, say) that move should still be reflected rather than pretending it's
        // still at the gate. The measured burn is what lets the fuel-burn billing below charge for
        // whatever was actually burned before the abandon, rather than nothing at all - an abandon
        // before the engines ever started correctly measures as "nothing yet" (see
        // FuelBurnResolver.Measure: no engine-start baseline was ever set).
        var lastSnapshot = lifecycle.GetActiveSnapshot(flight.Id);
        var measuredBurnKg = lifecycle.GetActiveMeasuredBurnKg(flight.Id);
        lifecycle.StopTracking(flight.Id);
        flight.Status = FlightStatus.Abandoned;
        await RevertFleetAircraftAsync(db, flight, lastSnapshot, ct);

        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.Id == flight.AirlineId, ct);
        if (airline is not null)
        {
            var economyConfig = economyConfigCatalog.Get(airline.Playstyle);

            // Fuel is still charged for what was actually burned before the abandon - "escaping a
            // bad sector by abandoning it" must still cost something real, and the aircraft really
            // did consume whatever it consumed. Unlike a normal completion, an unusable or missing
            // reading here falls back to ZERO rather than the sector's full planned figure: the
            // sector didn't necessarily go anywhere, so "we don't know how much was burned" must
            // never be billed as "the whole sector's worth" - an abandon seconds after start bills
            // approximately nothing, not a guess. Both paths share the exact same plausibility
            // ceiling (the sector's own planned charge), so a genuinely bad reading - a sim reset,
            // say - is caught identically either way; see FuelBurnResolver's own doc for why the
            // ceiling and the fallback amount are separate parameters.
            var route = await db.Routes.FirstOrDefaultAsync(r => r.Id == flight.RouteId, ct);
            var fleetAircraft = await db.FleetAircraft.FirstOrDefaultAsync(f => f.Id == flight.FleetAircraftId, ct);
            var aircraftType = fleetAircraft is not null ? await db.AircraftTypes.FindAsync([fleetAircraft.AircraftTypeId], ct) : null;
            var departureAirport = route is not null ? await db.Airports.FirstOrDefaultAsync(a => a.Icao == route.DepartureIcao, ct) : null;
            var arrivalAirport = route is not null ? await db.Airports.FirstOrDefaultAsync(a => a.Icao == route.ArrivalIcao, ct) : null;

            if (route is not null && aircraftType is not null && departureAirport is not null && arrivalAirport is not null)
            {
                var plan = RoutePreviewCalculator.Calculate(economyConfig, departureAirport, arrivalAirport, aircraftType, airline.StrategyProfile);
                var resolution = FuelBurnResolver.Resolve(measuredBurnKg, plan.FuelBreakdown.ChargedFuelKg, fallbackKg: 0);

                flight.FuelUsedKg = resolution.BilledKg;
                if (resolution.BilledKg > 0)
                {
                    var worldSeed = await FlightEconomicsPoster.ResolveWorldSeedAsync(db, ct);
                    var fuelCost = FlightEconomicsPoster.PostFuelBurn(
                        db, flight, economyConfig, departureAirport, resolution.BilledKg, DateTimeOffset.UtcNow, worldSeed);
                    flight.TotalCost += fuelCost;
                }
            }

            // Reputation. From a passenger's
            // point of view an abandoned sector never happened, exactly like a virtual pilot's
            // occurrence that could never fly, so this reuses ReputationPoster.PostCancelledOrSkipped
            // unchanged rather than inventing a fourth shape. Whatever fuel was actually burned
            // before the abandon (above) is a separate financial consequence and is never treated
            // as a substitute for a real reputation cost - without this, a flight running badly
            // (late, heading for a hard landing) could always be abandoned instead of finished or
            // even manually completed, taking zero reputation damage regardless of what it cost.
            ReputationPoster.PostCancelledOrSkipped(airline, economyConfig);
        }

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
        // A courtesy default in case route/aircraft data can't be resolved below - overwritten
        // with the sector's own planned charge (FuelBreakdown.ChargedFuelKg, not the full
        // FuelPlannedKg total, which includes alternate/reserve fuel a normal flight never burns)
        // once the fuel-billing block further down can compute it properly.
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

        // Resolved regardless of whether airline lookup succeeds below - see the matching comment
        // in FlightLifecycleService.FinalizeFlightAsync, which this manual path mirrors.
        var pilot = await db.Pilots.FirstOrDefaultAsync(p => p.Id == flight.PilotId, ct);

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
                var economyConfigForCompletion = economyConfigCatalog.Get(airline.Playstyle);
                MaintenancePoster.PostFlightHours(db, fleetAircraft, pilot, airline, economyConfigForCompletion, flightHours, now);

                // Reputation - a flat, timing-independent penalty, never derived from the wall
                // clock or any telemetry - see ReputationConfig.ManualCompletionAlphaMultiplier's
                // own doc. An EARLIER version of this comment derived an on-time score from
                // `now - (PlannedDepartureUtc + PlannedBlockMinutes)`; that was wrong and has been
                // removed - a flight completed within seconds of starting reads as an enormous
                // EARLY arrival under that formula (now is roughly the planned DEPARTURE, not
                // arrival), which scored as a perfect sector and, paired with the full ticket
                // revenue this path already posts, made "start, immediately complete, repeat" a
                // reputation-and-revenue farm. The wall clock on this path measures how long the
                // player took to click a button, not how the sector went, so nothing derived from
                // it belongs in this call - the sector is simply UNVERIFIED, which is the one fact
                // actually known, and that is worth a small fixed cost regardless of timing.
                ReputationPoster.PostUnverifiedManualCompletion(airline, economyConfigForCompletion);
            }
            else
            {
                fleetAircraft.AirframeHours += flightHours;
                if (pilot is not null)
                {
                    pilot.HoursFlown += flightHours;
                }
            }

            // No reliable telemetry means no reliable fuel reading either (that's the whole
            // reason this path exists) - informational only now (see FleetAircraft.FuelOnBoardKg's
            // own doc), so rather than let it drift from best-effort arithmetic, treat it as
            // consumed and let the next flight sync it fresh from whatever the sim reports then.
            fleetAircraft.FuelOnBoardKg = 0;
        }

        // Manual completion posts ticket revenue and normal sector costs, but never a landing
        // quality or punctuality bonus - there is no such mechanism to begin with (see
        // FlightEconomicsResult), and this path exists precisely because no reliable telemetry
        // was ever captured to score one. Skips quietly (no ledger lines) if any of the data an
        // economics calculation needs isn't resolvable - better to post nothing than guess.
        var arrivalAirport = route is not null ? await db.Airports.FirstOrDefaultAsync(a => a.Icao == route.ArrivalIcao, ct) : null;
        var departureAirport = route is not null ? await db.Airports.FirstOrDefaultAsync(a => a.Icao == route.DepartureIcao, ct) : null;
        var aircraftType = fleetAircraft is not null ? await db.AircraftTypes.FindAsync([fleetAircraft.AircraftTypeId], ct) : null;

        if (route is not null && airline is not null && arrivalAirport is not null && aircraftType is not null)
        {
            var economyConfig = economyConfigCatalog.Get(airline.Playstyle);

            // No telemetry on this path means no measured burn either - bill the sector's own
            // planned charge outright, the same conservative assumption every other no-telemetry
            // path in this app makes (see FuelBreakdown.ChargedFuelKg's own doc).
            if (departureAirport is not null)
            {
                var plan = RoutePreviewCalculator.Calculate(economyConfig, departureAirport, arrivalAirport, aircraftType, airline.StrategyProfile);
                flight.FuelUsedKg = plan.FuelBreakdown.ChargedFuelKg;
                var worldSeed = await FlightEconomicsPoster.ResolveWorldSeedAsync(db, ct);
                var fuelCost = FlightEconomicsPoster.PostFuelBurn(
                    db, flight, economyConfig, departureAirport, flight.FuelUsedKg, now, worldSeed);
                flight.TotalCost += fuelCost;
            }

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
        // gone (sold/removed) rather than guessing at a figure the app can no longer verify.
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
    /// Flight statuses the logbook covers: sectors that were actually attempted. The
    /// <see cref="FlightStatus.Skipped"/>/<see cref="FlightStatus.Cancelled"/>/
    /// <see cref="FlightStatus.Suspended"/> statuses are virtual-pilot occurrences that never
    /// flew at all - none of them belongs in a record of flying done. There is no "planned"
    /// status to exclude: a flight row only ever exists once the sector has begun.
    /// <see cref="FlightStatus.Abandoned"/> and <see cref="FlightStatus.Interrupted"/> do: the
    /// aircraft left the gate, and a logbook that quietly omitted the sectors that went wrong would
    /// be flattering rather than accurate.
    /// </summary>
    private static readonly FlightStatus[] LogbookStatuses =
    [
        FlightStatus.Completed, FlightStatus.Interrupted, FlightStatus.Abandoned,
    ];

    /// <summary>
    /// Most recent sectors the logbook will return in one call. A logbook is for browsing, and the
    /// whole list is sorted and filtered in the browser, so it is all sent at once rather than
    /// paged - but an airline with years of history must not be able to turn one page load into a
    /// multi-megabyte response, so the newest 500 sectors are sent and <c>totalSectors</c> reports
    /// the real total so the UI can say plainly that it is showing a slice.
    /// </summary>
    private const int LogbookMaxRows = 500;

    /// <summary>
    /// The flight logbook: one row per sector actually flown, with everything a pilot looks up
    /// afterwards - route, aircraft, when, block time against plan, how the landing went, and what
    /// the sector earned.
    /// <para>
    /// <b>Money comes from the ledger, never from a cache column.</b> <c>revenue</c>/<c>cost</c>/
    /// <c>net</c> are summed from the flight's own posted <see cref="LedgerTransaction"/> rows, the
    /// same append-only source the airline's cash balance sums - so a logbook row can never claim a
    /// figure that did not actually move the bank balance. <c>net</c> is the sum of <b>every</b>
    /// line posted against the flight, which is deliberately the identical figure the flight's own
    /// report card shows as "Net": a player clicking a row must not find a different number on the
    /// other side of the click. It is also the same figure the sector contributes to
    /// <c>FinanceEndpoints.RoutesAsync</c>'s per-route <c>profit</c>: that endpoint counted ticket
    /// revenue alone until 2026-08-14, so a sector flown online reported a bigger Net here than the
    /// route it was flown on reported earning - one flight, two screens, two numbers. Its revenue now
    /// includes the <see cref="LedgerCategory.VatsimOnlineBonus"/> line, which is posted against this
    /// very flight, and <c>FinanceEndpointsTests</c> asserts the two agree.
    /// </para>
    /// <para>
    /// <b>Fixed query count, whatever the row count.</b> Routes, aircraft, types, pilots, ledger
    /// lines and snapshot counts are each fetched once for the whole page and joined in memory -
    /// six queries for 500 sectors exactly as for one. <c>hasTrack</c> is a snapshot COUNT rather
    /// than a fetch of the points themselves; the track is only ever loaded when a flight is opened.
    /// </para>
    /// <para>
    /// <b>Block time is measured, never assumed.</b> <c>actualBlockMinutes</c> is the flight's own
    /// Out-to-In gap and is null when either stamp is missing. It is additionally null when
    /// <see cref="Flight.SimRateElevated"/> is set: elapsed wall time means nothing once the sim
    /// clock has run faster than real time, so the honest answer is "not measured", not a number
    /// that would read as an impossibly fast sector.
    /// </para>
    /// </summary>
    internal static async Task<IResult> LogbookAsync(FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(new { totalSectors = 0, returnedSectors = 0, sectors = Array.Empty<object>() });
        }

        // Materialise before ordering - the SQLite provider can't translate ORDER BY over
        // DateTimeOffset (see the project's EF/SQLite notes).
        var allFlights = await db.Flights
            .Where(f => f.AirlineId == airline.Id && f.DeletedUtc == null && LogbookStatuses.Contains(f.Status))
            .ToListAsync(ct);

        var totalSectors = allFlights.Count;
        if (totalSectors == 0)
        {
            return Results.Ok(new { totalSectors = 0, returnedSectors = 0, sectors = Array.Empty<object>() });
        }

        var flights = allFlights
            .OrderByDescending(f => f.InUtc ?? f.OutUtc ?? f.PlannedDepartureUtc)
            .Take(LogbookMaxRows)
            .ToList();

        var flightIds = flights.Select(f => f.Id).ToList();

        var routesById = (await db.Routes
                .Where(r => r.AirlineId == airline.Id)
                .ToListAsync(ct))
            .ToDictionary(r => r.Id);

        var fleetById = (await db.FleetAircraft
                .Where(f => f.AirlineId == airline.Id)
                .ToListAsync(ct))
            .ToDictionary(f => f.Id);

        var typeIds = fleetById.Values.Select(f => f.AircraftTypeId).Distinct().ToList();
        var typesById = await db.AircraftTypes.Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);

        var pilotsById = (await db.Pilots
                .Where(p => p.AirlineId == airline.Id)
                .ToListAsync(ct))
            .ToDictionary(p => p.Id);

        var ledgerByFlight = (await db.LedgerTransactions
                .Where(t => t.FlightId != null && flightIds.Contains(t.FlightId.Value))
                .ToListAsync(ct))
            .GroupBy(t => t.FlightId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        // One grouped COUNT for the whole page: whether a flight has a drawable track, without
        // loading a single point of it.
        var snapshotCountByFlight = (await db.FlightEvents
                .Where(e => e.Type == FlightEventType.PositionSnapshot && flightIds.Contains(e.FlightId))
                .GroupBy(e => e.FlightId)
                .Select(g => new { FlightId = g.Key, Count = g.Count() })
                .ToListAsync(ct))
            .ToDictionary(x => x.FlightId, x => x.Count);

        var sectors = flights.Select(f =>
        {
            var route = routesById.GetValueOrDefault(f.RouteId);
            var aircraft = fleetById.GetValueOrDefault(f.FleetAircraftId);
            var type = aircraft is not null ? typesById.GetValueOrDefault(aircraft.AircraftTypeId) : null;
            var pilot = pilotsById.GetValueOrDefault(f.PilotId);
            var lines = ledgerByFlight.GetValueOrDefault(f.Id) ?? new List<LedgerTransaction>();

            var revenue = lines.Where(l => l.Amount > 0).Sum(l => l.Amount);
            var cost = -lines.Where(l => l.Amount < 0).Sum(l => l.Amount);

            var blockHours = BlockTimeCalculator.BlockHours(f.OutUtc, f.InUtc);
            double? actualBlockMinutes = f.OutUtc is not null && f.InUtc is not null && !f.SimRateElevated
                ? Math.Round(blockHours * 60.0, 1)
                : null;

            var seats = type?.PaxCapacity ?? 0;
            double? loadFactorPercent = seats > 0 ? Math.Round(100.0 * f.PaxFlown / seats, 1) : null;

            var snapshotCount = snapshotCountByFlight.GetValueOrDefault(f.Id);

            return new
            {
                flightId = f.Id,
                status = f.Status.ToString(),
                routeId = f.RouteId,
                departureIcao = route?.DepartureIcao ?? string.Empty,
                arrivalIcao = route?.ArrivalIcao ?? string.Empty,
                flightNumber = route?.FlightNumber,
                registration = aircraft?.Registration,
                aircraftTypeName = type?.Name,
                aircraftIcaoType = type?.IcaoType,
                pilotName = pilot?.Name,
                isPlayerFlight = pilot?.IsPlayer ?? false,
                // The sector's own timeline. `dateUtc` is what the logbook sorts and groups by: the
                // moment it finished if it did, otherwise when it left, otherwise when it was
                // planned for - never a fabricated stand-in.
                dateUtc = f.InUtc ?? f.OutUtc ?? f.PlannedDepartureUtc,
                outUtc = f.OutUtc,
                inUtc = f.InUtc,
                plannedBlockMinutes = f.PlannedBlockMinutes,
                actualBlockMinutes,
                blockTimeNotMeasured = f.SimRateElevated,
                paxFlown = f.PaxFlown,
                paxBooked = f.PaxBooked,
                seats = seats > 0 ? seats : (int?)null,
                loadFactorPercent,
                landingFpmFirst = f.LandingFpmFirst,
                fuelUsedKg = f.FuelUsedKg,
                revenue,
                cost,
                net = revenue - cost,
                simRateElevated = f.SimRateElevated,
                slewDetected = f.SlewDetected,
                positionJumpDetected = f.PositionJumpDetected,
                vatsimOnline = f.VatsimOnline,
                // Whether this sector has a flown track to draw at all. False for every flight that
                // predates position snapshots and for every virtual-pilot sector - those never had a
                // simulator attached and write no events - so the UI can offer the track only where
                // one genuinely exists instead of opening an empty map.
                hasTrack = snapshotCount > 0,
                trackPointCount = snapshotCount,
            };
        }).ToList();

        return Results.Ok(new { totalSectors, returnedSectors = sectors.Count, sectors });
    }

    /// <summary>
    /// The path a flight actually flew - the roughly-15-second
    /// <see cref="FlightEventType.PositionSnapshot"/> stream FSOps has always recorded and never
    /// shown, read straight off the append-only event rows (see
    /// <see cref="FlownTrackBuilder"/> for the parsing rules and how each awkward case degrades).
    /// <para>
    /// A separate endpoint rather than another field on <c>GET /flights/{id}</c> on purpose: a
    /// long-haul sector's track is a few hundred kilobytes, and the report card is opened far more
    /// often than the track is looked at. This is fetched only when a flight's map is actually
    /// shown.
    /// </para>
    /// <para>
    /// An empty <c>points</c> array is a legitimate, expected answer - see FlownTrackBuilder's doc.
    /// It is never a 404: the flight exists, it simply has no recorded track, and the two must not
    /// be conflated.
    /// </para>
    /// </summary>
    internal static async Task<IResult> TrackAsync(Guid id, FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var flight = await LoadOwnedFlightAsync(db, currentUser, id, ct);
        if (flight is null)
        {
            return Results.NotFound();
        }

        var events = await db.FlightEvents
            .Where(e => e.FlightId == flight.Id && e.Type == FlightEventType.PositionSnapshot)
            .ToListAsync(ct);

        var track = FlownTrackBuilder.Build(events);

        return Results.Ok(new
        {
            flightId = flight.Id,
            recordedPointCount = track.RecordedPointCount,
            thinned = track.Thinned,
            points = track.Points.Select(p => new
            {
                utc = p.Utc,
                lat = p.Latitude,
                lon = p.Longitude,
                altMslFt = p.AltitudeMslFt,
                gsKt = p.GroundSpeedKt,
                phase = p.Phase,
            }).ToList(),
        });
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
        // Include(Runways) - the runway-suitability reason below needs the actual runway rows.
        var airportsByIcao = await db.Airports.Include(a => a.Runways).Where(a => icaos.Contains(a.Icao)).ToDictionaryAsync(a => a.Icao, ct);

        // Starting a flight is blocked airline-wide while one is already in progress (see
        // StartAsync above), regardless of which aircraft or route would otherwise be used.
        var hasFlightInProgress = await db.Flights.AnyAsync(f => f.AirlineId == airline.Id && f.Status == FlightStatus.InProgress, ct);

        // 3a: reservation is the sole gate - the Fly screen offers exactly the RESERVED airframes
        // that are at the departure airport and serviceable. Every aircraft actually present at the
        // departure airport is still LISTED (never silently omitted, per 2b's "disabled with one
        // short reason" rule and the 2026-08-09 defect this fixes - a player who cannot fly an
        // aircraft needs to see it and know why, not wonder why it vanished), just marked unflyable
        // with a reason when it isn't reserved, is busy, or is grounded. This also fixes the
        // unrelated root cause behind "only one of four aircraft at EGLL was offered": the previous
        // version picked a single `.FirstOrDefault()` candidate per ROUTE regardless of how many
        // aircraft were actually parked there - every aircraft at the departure airport is now its
        // own option.
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

            var atDeparture = fleet
                .Where(f => f.LocationIcao == route.DepartureIcao)
                .OrderBy(f => f.CreatedUtc)
                .ToList();

            var aircraftOptions = atDeparture.Select(aircraft =>
            {
                var aircraftType = aircraftTypesById.TryGetValue(aircraft.AircraftTypeId, out var matchedType) ? matchedType : null;

                // Priority matters here (2b: "one short reason ... ending in an action that fixes
                // it"): a hard physical blocker must be reported BEFORE reservation, because
                // reservation is a one-click fix and the others are not. Leading with "not reserved
                // to you" on an aircraft that is ALSO in maintenance sends the player to reserve it,
                // then back here to find it still can't fly - telling them to do something that
                // will not actually work is the same class of bug as the old "you'd need a route"
                // wording: every reason shown must end in an action that actually works.
                // Only say "not reserved" when reserving genuinely is
                // sufficient to let them fly.
                string? reason = null;
                if (aircraft.Status == FleetAircraftStatus.InFlight)
                {
                    reason = $"{aircraft.Registration} is currently in flight.";
                }
                else if (aircraftType is not null && !RouteRangeAssessor.CanReach(aircraftType.RangeNm, route.DistanceNm))
                {
                    // The most permanent blocker of the lot, so it comes before the transient ones:
                    // nothing the player does to this airframe - reserving it, waiting out a
                    // grounding, finishing the flight in progress - will ever let it reach that
                    // airport. Reported rather than the aircraft being silently dropped from the
                    // list, per the same rule as every other unflyable reason here.
                    reason =
                        $"{aircraft.Registration} ({aircraftType.Name}) can't reach {route.ArrivalIcao} - " +
                        $"{route.DistanceNm:F0} nm is beyond its practical range of about " +
                        $"{RouteRangeAssessor.OperationalRangeNm(aircraftType.RangeNm):F0} nm.";
                }
                else if (aircraftType is not null &&
                         RunwaySuitabilityAssessor.AssessRoute(departure, arrival, aircraftType.MinRunwayFt, aircraftType.MtowTonnes, out var blockingAirport) is var runwayProblem &&
                         runwayProblem != RunwaySuitabilityProblem.None)
                {
                    // Also permanent, same reason range is checked first: nothing the player does to
                    // this airframe changes whether it can physically use this ground.
                    reason = runwayProblem == RunwaySuitabilityProblem.TooShort
                        ? $"{aircraft.Registration} ({aircraftType.Name}) can't use {blockingAirport.Icao} - its longest runway is " +
                          $"{blockingAirport.LongestRunwayFt:N0} ft, short of the {aircraftType.MinRunwayFt:N0} ft it needs."
                        : $"{aircraft.Registration} ({aircraftType.Name}) is too heavy for {blockingAirport.Icao}'s runways - none of " +
                          "them are paved, and a heavy aircraft can't use grass, gravel, dirt or water regardless of length.";
                }
                else if (aircraft.Status == FleetAircraftStatus.InMaintenance)
                {
                    // "Why and until when", not merely "in maintenance", which tells the player
                    // nothing they can plan around.
                    // GroundedUntilUtc should always be set whenever Status is InMaintenance
                    // (MaintenancePoster sets both together), but a plain fallback covers the
                    // theoretical gap rather than showing a broken/missing date to the player.
                    reason = aircraft.GroundedUntilUtc is { } until
                        ? $"{aircraft.Registration} is in maintenance until {until:yyyy-MM-dd HH:mm} UTC."
                        : $"{aircraft.Registration} is in maintenance.";
                }
                else if (hasFlightInProgress)
                {
                    // Also a hard blocker independent of THIS aircraft's own reservation - reserving
                    // it would not let a second flight start while one is already in progress.
                    reason = "A flight is already in progress - complete or abandon it before starting another.";
                }
                else if (!aircraft.ReservedForPlayer)
                {
                    reason = "Not reserved to you - reserve it from the Fleet page to fly it.";
                }

                int? aircraftBlockMinutes = null;
                if (aircraftType is not null)
                {
                    var aircraftPreview = RoutePreviewCalculator.Calculate(economyConfig, departure, arrival, aircraftType, airline.StrategyProfile);
                    aircraftBlockMinutes = aircraftPreview.BlockTimeBreakdown.TotalMinutes;
                }

                return new
                {
                    fleetAircraftId = aircraft.Id,
                    aircraft.Registration,
                    aircraftTypeId = aircraftType?.Id,
                    aircraftTypeName = aircraftType?.Name,
                    paxCapacity = aircraftType?.PaxCapacity,
                    estimatedBlockMinutes = aircraftBlockMinutes,
                    isFlyable = reason is null,
                    reason,
                };
            }).ToList();

            var isFlyable = aircraftOptions.Any(a => a.isFlyable);

            string? routeReason = null;
            if (!isFlyable)
            {
                routeReason = aircraftOptions.Count == 0
                    ? NoAircraftAtDepartureReason(fleet, route.DepartureIcao)
                    : null; // per-aircraft reasons already cover it when at least one aircraft is present
            }

            // Route-level distance/block-time preview: whichever aircraft type is actually flyable
            // (so the preview matches what the player can pick), falling back to the first present
            // type, then the airline-wide fallback - purely for a sensible preview figure, never
            // used to pick which aircraft is offered.
            var previewType = aircraftOptions.FirstOrDefault(a => a.isFlyable) is { } flyableOption
                ? aircraftTypesById.GetValueOrDefault(flyableOption.aircraftTypeId ?? Guid.Empty)
                : atDeparture.Count > 0 && aircraftTypesById.TryGetValue(atDeparture[0].AircraftTypeId, out var firstType)
                    ? firstType
                    : fallbackAircraftType;

            int? estimatedBlockMinutes = null;
            double? distanceNm = route.DistanceNm;
            if (previewType is not null)
            {
                var preview = RoutePreviewCalculator.Calculate(economyConfig, departure, arrival, previewType, airline.StrategyProfile);
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
                reason = isFlyable ? null : routeReason,
                aircraftOptions,
            });
        }

        return Results.Ok(options);
    }

    /// <summary>
    /// The SimBrief import hand-off for the Fly screen's pre-flight brief: resolves the same plan
    /// StartAsync would use for this route/aircraft pair (SimBrief when the player's Pilot ID has
    /// a matching OFP, the built-in estimate otherwise) so the player can see - and be told about
    /// a fallback - before committing to "Start flight". Purely a read: never creates, modifies,
    /// or deletes a route, aircraft, or schedule, and starting nothing itself.
    /// </summary>
    internal static async Task<IResult> PlanImportAsync(
        Guid routeId, Guid? fleetAircraftId, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline before importing a flight plan." });
        }

        var route = await db.Routes.FirstOrDefaultAsync(r => r.Id == routeId && r.AirlineId == airline.Id, ct);
        if (route is null)
        {
            return Results.NotFound(new { error = "Route not found." });
        }

        var departure = await db.Airports.FirstOrDefaultAsync(a => a.Icao == route.DepartureIcao, ct);
        var arrival = await db.Airports.FirstOrDefaultAsync(a => a.Icao == route.ArrivalIcao, ct);
        if (departure is null || arrival is null)
        {
            return Results.Problem("This route's airports could not be found.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        AircraftType? aircraftType = null;
        if (fleetAircraftId is Guid requestedAircraftId)
        {
            var requestedAircraft = await db.FleetAircraft.FirstOrDefaultAsync(f => f.Id == requestedAircraftId && f.AirlineId == airline.Id, ct);
            if (requestedAircraft is not null)
            {
                aircraftType = await db.AircraftTypes.FindAsync([requestedAircraft.AircraftTypeId], ct);
            }
        }

        // No specific aircraft given (or it didn't resolve) - fall back to whatever's physically
        // at the departure airport, then the airline's cheapest/first type, mirroring OptionsAsync's
        // own fallback purely so there's still something sensible to plan against for display.
        if (aircraftType is null)
        {
            // Materialise first - the SQLite provider can't translate ORDER BY over DateTimeOffset.
            var atDeparture = (await db.FleetAircraft
                    .Where(f => f.AirlineId == airline.Id && f.LocationIcao == route.DepartureIcao)
                    .ToListAsync(ct))
                .OrderBy(f => f.CreatedUtc)
                .FirstOrDefault();
            if (atDeparture is not null)
            {
                aircraftType = await db.AircraftTypes.FindAsync([atDeparture.AircraftTypeId], ct);
            }
        }

        aircraftType ??= await db.AircraftTypes.OrderBy(t => t.IcaoType).FirstOrDefaultAsync(ct);
        if (aircraftType is null)
        {
            return Results.Ok(new { available = false, source = (string?)null, message = "No aircraft type is available to plan against yet." });
        }

        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);
        var userSettings = await db.UserSettings.FirstOrDefaultAsync(s => s.OwnerUserId == currentUser.UserId, ct);
        var request = new FlightPlanRequest(departure, arrival, aircraftType, economyConfig, airline.StrategyProfile, userSettings?.SimBriefPilotId);
        var outcome = await ResolveFlightPlanAsync(request, ct);

        return Results.Ok(new
        {
            available = true,
            source = outcome.ProviderName,
            fromSimBrief = outcome.ProviderName == SimBriefProvider.Name,
            message = outcome.Message,
            blockFuelKg = outcome.Plan?.BlockFuelKg,
            cruiseAltitudeFt = outcome.Plan?.CruiseAltitudeFt,
            blockTimeMinutes = outcome.Plan?.BlockTimeMinutes,
            routeString = outcome.Plan?.RouteString,
        });
    }

    /// <summary>Route-level fallback reason when NO fleet aircraft at all is physically at the
    /// departure airport - the per-aircraft reasons in <c>aircraftOptions</c> cover every other
    /// unflyable case, since there is at least one aircraft present to explain.</summary>
    private static string NoAircraftAtDepartureReason(IReadOnlyList<FleetAircraft> fleet, string departureIcao)
    {
        var knownLocations = fleet.Select(f => f.LocationIcao).Distinct().ToList();
        return knownLocations.Count > 0
            ? $"No aircraft at {departureIcao} - your fleet is currently at {string.Join(", ", knownLocations)}."
            : "Your fleet has no aircraft.";
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
        // topped up again) before it was abandoned. Informational only (see
        // FleetAircraft.FuelOnBoardKg's own doc) - AbandonAsync bills whatever was actually
        // burned separately, before this method runs.
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

    /// <summary>StartAsync's response: the flight's own fields (unchanged shape - ToFlightDto)
    /// plus which provider's plan was actually used and, on a SimBrief fallback, why.</summary>
    private static object ToFlightStartDto(Flight f, FlightPlanOutcome planOutcome) => new
    {
        flight = ToFlightDto(f),
        planSource = planOutcome.ProviderName,
        planMessage = planOutcome.Message,
    };

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
        // G8 VATSIM corroboration. These four were declared on the frontend's own Flight type and
        // read by ReportCard ("Flown online" badge and card) from the day that feature shipped, but
        // were never actually serialised here - so `flight.vatsimOnline` was always `undefined` in
        // the browser while TypeScript promised `boolean | null`, and the online card could never
        // appear for a real flight no matter how well corroborated it was. Adding them is the fix
        // rather than deleting the fields from the type: the data is recorded on every flight, the
        // UI that consumes it already exists and is already tested, and "was this flown online" is
        // exactly the kind of fact a flight's own record is for. VatsimOnline stays three-valued on
        // the wire (true/false/null) - see Flight.VatsimOnline's own doc for why null must never be
        // collapsed into false.
        f.VatsimOnline,
        f.VatsimCallsign,
        f.VatsimOnlineFraction,
        f.VatsimControllersWorked,
        f.CreatedUtc,
    };
}

public sealed record StartFlightRequest(Guid? RouteId, Guid? FleetAircraftId);
