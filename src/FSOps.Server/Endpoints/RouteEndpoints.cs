using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Planning;
using FSOps.Core.Routes;
using FSOps.Data;
using FSOps.Server.Auth;
using FSOps.Server.Services;
using Microsoft.EntityFrameworkCore;
using Route = FSOps.Core.Entities.Route;

namespace FSOps.Server.Endpoints;

public static class RouteEndpoints
{
    /// <summary>
    /// Bounds for a user-supplied fare override, expressed as a multiple of the calculated
    /// suggested fare so the check scales with route distance instead of using a fixed band.
    /// </summary>
    private const decimal MinFareMultiplierOfSuggested = 0.1m;
    private const decimal MaxFareMultiplierOfSuggested = 10m;

    /// <summary>
    /// The band above, exposed so the fare workbench can bound its own input rather than letting the
    /// player discover the limit by being refused. Deliberately not a second copy of the numbers:
    /// creation, editing and the workbench all read these two methods, so the input, the validation
    /// and the error message can never describe three different bands.
    /// <para>
    /// The suggested fare a route is created with is exactly
    /// <see cref="ReferenceFareCalculator"/>'s output for its distance and strategy (see
    /// <see cref="RoutePreviewCalculator"/>), so passing the reference fare here is the same band the
    /// route was created under.
    /// </para>
    /// </summary>
    public static decimal MinimumFareFor(decimal suggestedFare) =>
        Math.Round(suggestedFare * MinFareMultiplierOfSuggested, 2, MidpointRounding.AwayFromZero);

    public static decimal MaximumFareFor(decimal suggestedFare) =>
        Math.Round(suggestedFare * MaxFareMultiplierOfSuggested, 2, MidpointRounding.AwayFromZero);

    public static void MapRouteEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapPost("/routes/preview", PreviewAsync);
        group.MapPost("/routes", CreateAsync);
        group.MapGet("/routes", ListAsync);
        group.MapGet("/routes/{id:guid}", GetByIdAsync);
        group.MapPut("/routes/{id:guid}", UpdateAsync);
        group.MapDelete("/routes/{id:guid}", DeleteAsync);
    }

    /// <summary>
    /// Must never throw - the UI calls this on every keystroke while the user is picking
    /// airports, so problems are reported through validation/warnings instead of error
    /// responses. The try/catch is a deliberate last line of defence on top of the null-checks
    /// below, not a substitute for them.
    /// </summary>
    private static async Task<IResult> PreviewAsync(
        RoutePreviewRequest request, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        try
        {
            var warnings = new List<string>();
            var departureIcao = (request.DepartureIcao ?? string.Empty).Trim().ToUpperInvariant();
            var arrivalIcao = (request.ArrivalIcao ?? string.Empty).Trim().ToUpperInvariant();

            Airport? departure = null;
            if (departureIcao.Length == 0)
            {
                warnings.Add("Departure ICAO code is required.");
            }
            else
            {
                // Include(Runways) - RunwaySuitabilityAssessor needs the actual runway rows (length,
                // surface, closed) to assess this airport, not just its stamped LongestRunwayFt.
                departure = await db.Airports.Include(a => a.Runways).FirstOrDefaultAsync(a => a.Icao == departureIcao, ct);
                if (departure is null)
                {
                    warnings.Add($"Departure airport '{departureIcao}' was not found.");
                }
            }

            Airport? arrival = null;
            if (arrivalIcao.Length == 0)
            {
                warnings.Add("Arrival ICAO code is required.");
            }
            else
            {
                arrival = await db.Airports.Include(a => a.Runways).FirstOrDefaultAsync(a => a.Icao == arrivalIcao, ct);
                if (arrival is null)
                {
                    warnings.Add($"Arrival airport '{arrivalIcao}' was not found.");
                }
            }

            var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
            var aircraftType = await ResolveAircraftTypeAsync(db, request.AircraftTypeId, airline, warnings, ct);

            // Preview can run before an airline exists (onboarding's route picker) - Casual is a
            // safe, neutral default there since nothing this endpoint reads (fares, demand, fuel,
            // costs) actually varies by playstyle; only AirlineStartup/FleetFinance do, and neither
            // is touched here. See EconomyConfigCatalog's class doc.
            var economyConfig = economyConfigCatalog.Get(airline?.Playstyle ?? AirlinePlaystyle.Casual);

            if (departure is null || arrival is null || aircraftType is null)
            {
                return Results.Ok(EmptyPreview(departureIcao, arrivalIcao, warnings));
            }

            // The range and runway verdicts are about the whole fleet, never about the one type these
            // figures were planned with - see RouteRangeAssessor/RunwaySuitabilityAssessor. An
            // airline that is not created yet has no fleet to assess, and gets no verdict at all
            // rather than a made-up one.
            var fleetWithTypes = airline is null
                ? Array.Empty<(FleetAircraft Aircraft, AircraftType Type)>()
                : await LoadFleetWithTypesAsync(db, airline.Id, ct);
            var rangeCandidates = ToRangeCandidates(fleetWithTypes);
            var runwayCandidates = ToRunwayCandidates(fleetWithTypes);

            var result = RoutePreviewCalculator.Calculate(
                economyConfig, departure, arrival, aircraftType, airline?.StrategyProfile, rangeCandidates, runwayCandidates);
            warnings.AddRange(result.Validation.Warnings);

            // Fuel price must be visible before departure - this is what the sector will actually
            // be billed per kg of burn, since a sector is charged at the departure airport's price.
            // Priced "today" (UtcNow) since this is what the player would be charged departing
            // right now, not at the scheduled departure time. World seed falls back the same way
            // FlightEconomicsPoster does - see its doc.
            var worldSeed = await FlightEconomicsPoster.ResolveWorldSeedAsync(db, ct);
            var priceNowUtc = DateTimeOffset.UtcNow;
            var fuelPricePerKg = FuelPricing.PricePerKg(economyConfig.Fuel, departure.Icao, departure.Country, priceNowUtc, worldSeed);

            // The live "at this fare, expect ~N passengers (X% load factor), ~£Y revenue per
            // sector" readout - the real DemandCalculator/FareDemandModel engine, not the rough
            // distance-only estimate above. Needs an airline (for strategy profile and
            // reputation) and a real sector, so it's null for onboarding or a same-airport/no
            // distance pick rather than guessing at either. No fare validation beyond ">0" per
            // design: the simulation is the guardrail, not an input cap. A player who prices badly
            // watches the load factor collapse in the readout instead of being refused.
            object? economics = null;
            if (airline is not null && !result.Validation.SameAirport && result.DistanceNm > 0)
            {
                var fare = request.Fare is decimal requestedFare && requestedFare > 0 ? requestedFare : result.SuggestedFare;
                // One projector, shared with the schedule leg picker, the pilot weekly summary, the
                // fare workbench and FlightEconomicsPoster itself - see SectorProjector's class doc
                // for why a second copy of this arithmetic is not allowed to exist.
                var plan = SectorProjector.Plan(
                    economyConfig, airline.StrategyProfile, airline.ReputationScore, departure, arrival, aircraftType,
                    result.DistanceNm, priceNowUtc, worldSeed);
                var projection = SectorProjector.AtFare(economyConfig, airline.StrategyProfile, plan, fare);

                economics = new
                {
                    fare,
                    referenceFare = plan.ReferenceFare,
                    expectedPassengers = projection.PaxBooked,
                    loadFactorPercent = Math.Round(projection.LoadFactor * 100, 1),
                    expectedRevenuePerSector = projection.Revenue,
                    // New alongside the revenue figure that was always here: a fare decision the
                    // player can only see one side of is not a decision. Costs are every non-ticket
                    // line the ledger will post for this sector, fuel included at the departure
                    // airport's price.
                    expectedCostPerSector = Math.Round(projection.TotalCost, 2),
                    expectedProfitPerSector = Math.Round(projection.NetProfit, 2),
                };
            }

            return Results.Ok(new
            {
                distanceNm = Math.Round(result.DistanceNm, 1),
                initialBearingDeg = Math.Round(result.InitialBearingDeg, 1),
                estimatedBlockMinutes = result.BlockTimeBreakdown.TotalMinutes,
                blockTimeBreakdown = result.BlockTimeBreakdown,
                cruiseAltitudeFt = result.CruiseAltitudeFt,
                blockFuelKg = Math.Round(result.FuelBreakdown.TotalFuelKg, 0),
                fuelBreakdown = result.FuelBreakdown,
                fuelPricePerKg,
                suggestedFare = result.SuggestedFare,
                economics,
                greatCirclePath = result.GreatCirclePath.Select(p => new[] { p.Lon, p.Lat }),
                validation = new
                {
                    withinRange = result.Validation.WithinRange,
                    sameAirport = result.Validation.SameAirport,
                    warnings,
                    range = RangeDto(result.Validation.Range),
                    runway = RunwayDto(result.Validation.Runway),
                },
            });
        }
        catch
        {
            return Results.Ok(EmptyPreview(string.Empty, string.Empty, new List<string> { "Could not compute a preview for this route." }));
        }
    }

    /// <summary>
    /// Every aircraft the airline owns, paired with its resolved <see cref="AircraftType"/> -
    /// regardless of where it is, what it is doing or whether it is reserved, since "could this
    /// airline ever fly this sector" is a question about capability, not about today's availability,
    /// and a route is a permanent thing being planned rather than a flight being started right now.
    /// Loaded once and shared by <see cref="ToRangeCandidates"/> and <see cref="ToRunwayCandidates"/>
    /// so a caller that needs both verdicts (every one that does) pays for the fleet/type query only
    /// once.
    /// </summary>
    private static async Task<IReadOnlyList<(FleetAircraft Aircraft, AircraftType Type)>> LoadFleetWithTypesAsync(
        FsOpsDbContext db, Guid airlineId, CancellationToken ct)
    {
        var fleet = await db.FleetAircraft.Where(f => f.AirlineId == airlineId).ToListAsync(ct);
        if (fleet.Count == 0)
        {
            return Array.Empty<(FleetAircraft, AircraftType)>();
        }

        var typeIds = fleet.Select(f => f.AircraftTypeId).Distinct().ToList();
        var typesById = await db.AircraftTypes.Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);

        return fleet
            .Where(f => typesById.ContainsKey(f.AircraftTypeId))
            .Select(f => (f, typesById[f.AircraftTypeId]))
            .ToList();
    }

    private static IReadOnlyList<RangeCandidateAircraft> ToRangeCandidates(IReadOnlyList<(FleetAircraft Aircraft, AircraftType Type)> fleet) =>
        fleet.Select(x => new RangeCandidateAircraft(x.Aircraft.Registration, x.Type.Name, x.Type.RangeNm, x.Aircraft.ReservedForPlayer)).ToList();

    private static IReadOnlyList<RunwayCandidateAircraft> ToRunwayCandidates(IReadOnlyList<(FleetAircraft Aircraft, AircraftType Type)> fleet) =>
        fleet.Select(x => new RunwayCandidateAircraft(x.Aircraft.Registration, x.Type.Name, x.Type.MinRunwayFt, x.Type.MtowTonnes, x.Aircraft.ReservedForPlayer)).ToList();

    private static object RangeDto(RouteRangeAssessment assessment) => new
    {
        verdict = assessment.Verdict.ToString(),
        blocking = assessment.Blocking,
        message = assessment.Message,
        aircraftRegistration = assessment.AircraftRegistration,
        aircraftTypeName = assessment.AircraftTypeName,
        operationalRangeNm = assessment.OperationalRangeNm is double nm ? Math.Round(nm, 0) : (double?)null,
    };

    private static object RunwayDto(RunwaySuitabilityAssessment assessment) => new
    {
        verdict = assessment.Verdict.ToString(),
        blocking = assessment.Blocking,
        message = assessment.Message,
        aircraftRegistration = assessment.AircraftRegistration,
        aircraftTypeName = assessment.AircraftTypeName,
    };

    private static object EmptyPreview(string departureIcao, string arrivalIcao, List<string> warnings) => new
    {
        distanceNm = 0.0,
        initialBearingDeg = 0.0,
        estimatedBlockMinutes = 0,
        blockTimeBreakdown = (object?)null,
        cruiseAltitudeFt = 0,
        blockFuelKg = 0.0,
        fuelBreakdown = (object?)null,
        fuelPricePerKg = (decimal?)null,
        suggestedFare = 0m,
        economics = (object?)null,
        greatCirclePath = Array.Empty<double[]>(),
        validation = new
        {
            withinRange = false,
            sameAirport = departureIcao.Length > 0 && departureIcao == arrivalIcao,
            warnings,
            // Nothing was computed, so there is no verdict to give - and specifically nothing
            // blocking, so an incomplete airport pick is never reported as out of range or unsuitable.
            range = RangeDto(RouteRangeAssessment.NotAssessed),
            runway = RunwayDto(RunwaySuitabilityAssessment.NotAssessed),
        },
    };

    private static async Task<AircraftType?> ResolveAircraftTypeAsync(
        FsOpsDbContext db, Guid? requestedTypeId, Airline? airline, List<string> warnings, CancellationToken ct)
    {
        if (requestedTypeId is Guid typeId)
        {
            var requested = await db.AircraftTypes.FindAsync([typeId], ct);
            if (requested is not null)
            {
                return requested;
            }

            warnings.Add("The specified aircraft type was not found; using your fleet's default instead.");
        }

        if (airline is not null)
        {
            var fleetTypeId = await db.FleetAircraft
                .Where(f => f.AirlineId == airline.Id)
                .Select(f => f.AircraftTypeId)
                .FirstOrDefaultAsync(ct);

            if (fleetTypeId != Guid.Empty)
            {
                var fleetType = await db.AircraftTypes.FindAsync([fleetTypeId], ct);
                if (fleetType is not null)
                {
                    return fleetType;
                }
            }
        }

        return await db.AircraftTypes.OrderBy(t => t.IcaoType).FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// A route is always a there-and-back pair: creating EGGD-&gt;EGPH always creates EGPH-&gt;EGGD
    /// alongside it, in the same save, so an aircraft that has flown the outbound leg is never
    /// stranded at the outstation and no ferry/positioning flight is ever needed. The two legs
    /// stay separate rows with their own flight numbers (outbound odd, return the next even - see
    /// <see cref="FlightNumberGenerator"/>) and share the same fare unless overridden. If the
    /// reverse direction already exists (most likely a route created before pairing was
    /// mandatory), it's reused as the return leg instead of being rejected as a conflict.
    /// </summary>
    internal static async Task<IResult> CreateAsync(
        CreateRouteRequest request, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline before adding routes." });
        }

        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);

        var departureIcao = (request.DepartureIcao ?? string.Empty).Trim().ToUpperInvariant();
        var arrivalIcao = (request.ArrivalIcao ?? string.Empty).Trim().ToUpperInvariant();

        var (outbound, error) = await BuildRouteAsync(
            db, airline, departureIcao, arrivalIcao, request.AircraftTypeId, request.BaseFare, request.FlightNumber,
            existing => FlightNumberGenerator.SuggestOutbound(airline.IcaoCode, existing), economyConfig, ct);
        if (error is not null)
        {
            return error;
        }

        var existingReverse = await db.Routes.FirstOrDefaultAsync(
            r => r.AirlineId == airline.Id && r.DepartureIcao == arrivalIcao && r.ArrivalIcao == departureIcao, ct);

        Route inbound;
        if (existingReverse is not null)
        {
            inbound = existingReverse;
        }
        else
        {
            var (built, returnError) = await BuildRouteAsync(
                db, airline, arrivalIcao, departureIcao, aircraftTypeId: null, outbound!.BaseFare, requestedFlightNumber: null,
                existing => FlightNumberGenerator.SuggestReturn(airline.IcaoCode, outbound.FlightNumber, existing), economyConfig, ct);
            if (returnError is not null)
            {
                return returnError;
            }

            inbound = built!;
            db.Routes.Add(inbound);
        }

        db.Routes.Add(outbound!);
        // One SaveChangesAsync call for both new rows - EF Core wraps every pending change in a
        // single implicit transaction, so the pair either lands together or not at all.
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/routes/{outbound!.Id}", new
        {
            outbound = await ToRouteDtoAsync(outbound, db, ct),
            inbound = await ToRouteDtoAsync(inbound, db, ct),
        });
    }

    /// <summary>
    /// Shared route-construction logic for <see cref="CreateAsync"/> and
    /// <see cref="ListAsync"/>'s backfill: validates the airport pair, enforces the directional
    /// duplicate check, resolves an aircraft type and fare, and resolves a flight number. Returns
    /// a fully-populated but not-yet-saved <see cref="Route"/>, or an error result to return
    /// as-is.
    /// </summary>
    private static async Task<(Route? Route, IResult? Error)> BuildRouteAsync(
        FsOpsDbContext db,
        Airline airline,
        string departureIcao,
        string arrivalIcao,
        Guid? aircraftTypeId,
        decimal? requestedFare,
        string? requestedFlightNumber,
        Func<List<string?>, string> suggestFlightNumber,
        EconomyConfig economyConfig,
        CancellationToken ct)
    {
        if (departureIcao.Length == 0 || arrivalIcao.Length == 0)
        {
            return (null, Results.BadRequest(new { error = "Departure and arrival ICAO codes are required." }));
        }

        if (departureIcao == arrivalIcao)
        {
            return (null, Results.BadRequest(new { error = "Departure and arrival airports must be different." }));
        }

        // Include(Runways) - the runway-suitability block below needs the actual runway rows.
        var departure = await db.Airports.Include(a => a.Runways).FirstOrDefaultAsync(a => a.Icao == departureIcao, ct);
        if (departure is null)
        {
            return (null, Results.BadRequest(new { error = $"Departure airport '{departureIcao}' was not found." }));
        }

        var arrival = await db.Airports.Include(a => a.Runways).FirstOrDefaultAsync(a => a.Icao == arrivalIcao, ct);
        if (arrival is null)
        {
            return (null, Results.BadRequest(new { error = $"Arrival airport '{arrivalIcao}' was not found." }));
        }

        // Routes are directional: an airline flying LHR->JFK doesn't automatically also fly
        // JFK->LHR, so only an exact match in the *same* direction is a conflict. Creating the
        // reverse direction is created deliberately, as the pair that CreateAsync always makes.
        var duplicateExists = await db.Routes.AnyAsync(r =>
            r.AirlineId == airline.Id && r.DepartureIcao == departureIcao && r.ArrivalIcao == arrivalIcao, ct);
        if (duplicateExists)
        {
            return (null, Results.Conflict(new { error = $"A route from {departureIcao} to {arrivalIcao} already exists." }));
        }

        AircraftType? aircraftType = null;
        if (aircraftTypeId is Guid requestedTypeId)
        {
            aircraftType = await db.AircraftTypes.FindAsync([requestedTypeId], ct);
        }

        var fleetTypeIds = await db.FleetAircraft
            .Where(f => f.AirlineId == airline.Id)
            .Select(f => f.AircraftTypeId)
            .Distinct()
            .ToListAsync(ct);

        if (fleetTypeIds.Count > 0)
        {
            var fleetAircraftTypes = await db.AircraftTypes.Where(t => fleetTypeIds.Contains(t.Id)).ToListAsync(ct);
            aircraftType ??= fleetAircraftTypes.FirstOrDefault();

            // Creating a route is guidance, not a gate, and only ONE answer on either question is a
            // genuine block: nothing the airline owns can fly it, or nothing the airline owns can
            // physically use these runways. "Nothing you have reserved can fly it, but that 787 over
            // there can" is a route worth having - it just needs the aircraft reserved, or a virtual
            // pilot rostered onto it - and refusing to create it made that a dead end. See
            // RouteRangeAssessor and RunwaySuitabilityAssessor - one mental model for both.
            var distanceNm = GreatCircle.DistanceNm(departure.Latitude, departure.Longitude, arrival.Latitude, arrival.Longitude);
            var fleetWithTypes = await LoadFleetWithTypesAsync(db, airline.Id, ct);
            var rangeAssessment = RouteRangeAssessor.Assess(distanceNm, ToRangeCandidates(fleetWithTypes));
            if (rangeAssessment.Blocking)
            {
                return (null, Results.BadRequest(new { error = rangeAssessment.Message }));
            }

            var runwayAssessment = RunwaySuitabilityAssessor.Assess(departure, arrival, ToRunwayCandidates(fleetWithTypes));
            if (runwayAssessment.Blocking)
            {
                return (null, Results.BadRequest(new { error = runwayAssessment.Message }));
            }
        }

        aircraftType ??= await db.AircraftTypes.OrderBy(t => t.IcaoType).FirstOrDefaultAsync(ct);
        if (aircraftType is null)
        {
            return (null, Results.Problem("No aircraft type is available to plan this route.", statusCode: StatusCodes.Status503ServiceUnavailable));
        }

        var result = RoutePreviewCalculator.Calculate(economyConfig, departure, arrival, aircraftType, airline.StrategyProfile);

        decimal baseFare;
        if (requestedFare is decimal fareValue)
        {
            // Guard against fat-finger / garbage input while still letting the user meaningfully
            // undercut or beat the suggested fare - "sane" is defined relative to the suggestion
            // rather than as a fixed currency band, since suggested fares vary a lot by distance.
            var minAllowedFare = MinimumFareFor(result.SuggestedFare);
            var maxAllowedFare = MaximumFareFor(result.SuggestedFare);
            if (fareValue <= 0m || fareValue < minAllowedFare || fareValue > maxAllowedFare)
            {
                return (null, Results.BadRequest(new
                {
                    error = $"Fare {fareValue:F2} is outside the allowed range " +
                            $"({minAllowedFare:F2}-{maxAllowedFare:F2}) for this route (suggested fare {result.SuggestedFare:F2}).",
                }));
            }

            baseFare = fareValue;
        }
        else
        {
            baseFare = result.SuggestedFare;
        }

        var (flightNumber, flightNumberError) = await ResolveFlightNumberAsync(db, airline, requestedFlightNumber, suggestFlightNumber, ct);
        if (flightNumberError is not null)
        {
            return (null, flightNumberError);
        }

        var route = new Route
        {
            Id = Guid.NewGuid(),
            AirlineId = airline.Id,
            DepartureIcao = departureIcao,
            ArrivalIcao = arrivalIcao,
            FlightNumber = flightNumber,
            DistanceNm = result.DistanceNm,
            BaseFare = baseFare,
            IsActive = true,
            CreatedUtc = DateTimeOffset.UtcNow,
        };

        return (route, null);
    }

    /// <summary>
    /// Validates a caller-supplied flight number and checks it isn't already used elsewhere on
    /// this airline, or - when none was supplied - generates one with <paramref name="suggestDefault"/>.
    /// </summary>
    private static async Task<(string? FlightNumber, IResult? Error)> ResolveFlightNumberAsync(
        FsOpsDbContext db, Airline airline, string? requestedFlightNumber, Func<List<string?>, string> suggestDefault, CancellationToken ct)
    {
        var existing = await db.Routes.Where(r => r.AirlineId == airline.Id).Select(r => r.FlightNumber).ToListAsync(ct);

        var trimmed = requestedFlightNumber?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(trimmed))
        {
            return (suggestDefault(existing), null);
        }

        if (!FlightNumberGenerator.IsValidFormat(trimmed))
        {
            return (null, Results.BadRequest(new
            {
                error = "flightNumber must be 1-4 digits with an optional single letter suffix, e.g. '101' or '204A'.",
            }));
        }

        if (existing.Any(fn => string.Equals(fn, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return (null, Results.Conflict(new { error = $"Flight number {trimmed} is already used by another route on this airline." }));
        }

        return (trimmed, null);
    }

    internal static async Task<IResult> UpdateAsync(
        Guid id, UpdateRouteRequest request, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.NotFound();
        }

        var route = await db.Routes.FirstOrDefaultAsync(r => r.Id == id && r.AirlineId == airline.Id, ct);
        if (route is null)
        {
            return Results.NotFound();
        }

        if (request.FlightNumber is not null)
        {
            var trimmed = request.FlightNumber.Trim().ToUpperInvariant();
            if (trimmed.Length == 0)
            {
                route.FlightNumber = null;
            }
            else
            {
                if (!FlightNumberGenerator.IsValidFormat(trimmed))
                {
                    return Results.BadRequest(new
                    {
                        error = "flightNumber must be 1-4 digits with an optional single letter suffix, e.g. '101' or '204A'.",
                    });
                }

                var collision = await db.Routes.AnyAsync(r => r.AirlineId == airline.Id && r.Id != route.Id && r.FlightNumber == trimmed, ct);
                if (collision)
                {
                    return Results.Conflict(new { error = $"Flight number {trimmed} is already used by another route on this airline." });
                }

                route.FlightNumber = trimmed;
            }
        }

        if (request.BaseFare is decimal fareValue)
        {
            if (fareValue <= 0m)
            {
                return Results.BadRequest(new { error = "baseFare must be greater than zero." });
            }

            // The SAME band route creation enforces. Until the fare workbench there was no UI that
            // could reach this path at all, so the gap between "creating a route refuses a fare 40x
            // the suggestion" and "editing one accepts it silently" never showed; giving the player
            // a fare control makes the two visible side by side, and a limit that applies only to
            // the fare you never revisit is not a limit. A route whose airports have since gone
            // missing from world data cannot be re-priced against a suggestion, so it keeps the old
            // ">0" rule rather than being frozen at whatever it holds.
            var suggestedFare = SuggestedFareFor(airline, route, economyConfigCatalog);
            if (suggestedFare is decimal suggested)
            {
                var minAllowedFare = MinimumFareFor(suggested);
                var maxAllowedFare = MaximumFareFor(suggested);
                if (fareValue < minAllowedFare || fareValue > maxAllowedFare)
                {
                    return Results.BadRequest(new
                    {
                        error = $"Fare {fareValue:F2} is outside the allowed range " +
                                $"({minAllowedFare:F2}-{maxAllowedFare:F2}) for this route (suggested fare {suggested:F2}).",
                    });
                }
            }

            route.BaseFare = fareValue;
        }

        if (request.IsActive is bool isActive)
        {
            route.IsActive = isActive;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(await ToRouteDtoAsync(route, db, ct));
    }

    /// <summary>
    /// What the app would charge for this saved route if the player expressed no opinion - the same
    /// <see cref="ReferenceFareCalculator"/> figure a brand-new route is created at, recomputed from
    /// the route's own stored distance. Null when the route has no usable distance, which is the
    /// only case where "there is no suggestion to compare against" is the honest answer.
    /// </summary>
    private static decimal? SuggestedFareFor(Airline airline, Route route, EconomyConfigCatalog economyConfigCatalog)
    {
        if (route.DistanceNm <= 0)
        {
            return null;
        }

        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);
        return ReferenceFareCalculator.Calculate(economyConfig, airline.StrategyProfile, route.DistanceNm);
    }

    internal static async Task<IResult> ListAsync(FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(Array.Empty<object>());
        }

        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);

        // SQLite's EF provider can't translate ORDER BY over DateTimeOffset into SQL, so the
        // (small, per-airline) route list is ordered client-side after fetching.
        var routes = (await db.Routes.Where(r => r.AirlineId == airline.Id).ToListAsync(ct))
            .OrderBy(r => r.CreatedUtc)
            .ToList();

        // Self-heals routes created before pairing was mandatory: any route missing its reverse
        // leg gets one now, so the list this returns is always fully paired without the owner
        // having to notice or do anything. Best-effort - a leg that can't be repaired right now
        // (e.g. the reverse is beyond the fleet's range) is left as a single leg rather than
        // failing the whole list, and is retried on every subsequent listing - so acquiring an
        // aircraft with the range for it is enough to complete the pair, with nothing to trigger
        // by hand.
        var backfilled = await BackfillMissingReturnLegsAsync(db, airline, routes, economyConfig, ct);
        if (backfilled.Count > 0)
        {
            routes.AddRange(backfilled);
        }

        var icaos = routes.SelectMany(r => new[] { r.DepartureIcao, r.ArrivalIcao }).Distinct().ToList();
        var airports = await db.Airports.Where(a => icaos.Contains(a.Icao)).ToDictionaryAsync(a => a.Icao, ct);

        // Same aircraft type the preview falls back to when the caller doesn't pin one: the
        // airline's fleet default. Resolved once for the whole list rather than per-route.
        var discardedWarnings = new List<string>();
        var aircraftType = await ResolveAircraftTypeAsync(db, requestedTypeId: null, airline, discardedWarnings, ct);

        // A route's return leg (if any) is just the other active route on this airline flying
        // the reverse direction - the directional duplicate check means there's at most one, so
        // this lookup is unambiguous and can be built once in memory instead of a query per row.
        var idByDirection = routes.ToDictionary(r => (r.DepartureIcao, r.ArrivalIcao), r => r.Id);

        var dtos = routes.Select(r =>
        {
            int? estimatedBlockMinutes = null;
            if (aircraftType is not null &&
                airports.TryGetValue(r.DepartureIcao, out var dep) &&
                airports.TryGetValue(r.ArrivalIcao, out var arr))
            {
                var preview = RoutePreviewCalculator.Calculate(economyConfig, dep, arr, aircraftType, airline.StrategyProfile);
                estimatedBlockMinutes = preview.BlockTimeBreakdown.TotalMinutes;
            }

            Guid? returnRouteId = idByDirection.TryGetValue((r.ArrivalIcao, r.DepartureIcao), out var reverseId) ? reverseId : null;

            return new
            {
                r.Id,
                r.DepartureIcao,
                DepartureName = airports.TryGetValue(r.DepartureIcao, out var depAirport) ? depAirport.Name : null,
                r.ArrivalIcao,
                ArrivalName = airports.TryGetValue(r.ArrivalIcao, out var arrAirport) ? arrAirport.Name : null,
                r.FlightNumber,
                returnRouteId,
                r.DistanceNm,
                r.BaseFare,
                estimatedBlockMinutes,
                r.IsActive,
                r.CreatedUtc,
            };
        });

        return Results.Ok(dtos);
    }

    private static async Task<IResult> GetByIdAsync(Guid id, FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.NotFound();
        }

        var route = await db.Routes.FirstOrDefaultAsync(r => r.Id == id && r.AirlineId == airline.Id, ct);
        return route is null ? Results.NotFound() : Results.Ok(await ToRouteDtoAsync(route, db, ct));
    }

    /// <summary>
    /// Deletes a route and its return leg together, atomically - routes are always a there-and-back
    /// pair, so leaving the surviving leg behind would strand an aircraft at the outstation with no
    /// route back. If no reverse leg exists (a legacy single leg that hasn't been repaired yet),
    /// only the one route is removed.
    /// </summary>
    internal static async Task<IResult> DeleteAsync(Guid id, FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.NotFound();
        }

        var route = await db.Routes.FirstOrDefaultAsync(r => r.Id == id && r.AirlineId == airline.Id, ct);
        if (route is null)
        {
            return Results.NotFound();
        }

        var reverse = await db.Routes.FirstOrDefaultAsync(
            r => r.AirlineId == airline.Id && r.DepartureIcao == route.ArrivalIcao && r.ArrivalIcao == route.DepartureIcao, ct);

        // Routes are deleted as a pair, so a flight on EITHER leg is a flight on what is about to
        // disappear. Refusing is not tidiness: a route deleted while its flight is airborne used to
        // leave a cleanly flown sector completed with no pay and no way to recover it, because
        // finalisation resolves the route to price the sector and finds nothing. Completion now
        // declines to finish such a flight at all, but the player should never reach that either -
        // losing an hour's flying to tidying a list is not a trade anybody would knowingly make.
        // A List, deliberately, NOT an array. On an array, `.Contains(x)` inside an expression tree
        // binds to the ReadOnlySpan extension rather than Enumerable.Contains, and EF's parameter
        // evaluator cannot construct that generic - it throws "violates the constraint of type
        // parameter 'TRet'" at query time rather than failing to compile. It passes locally and dies
        // in CI, which is the same trap the fleet-reversal code hit once already: on an array,
        // `.Reverse()` binds to the Span extension, reverses in place and returns void.
        var affectedRouteIds = reverse is not null
            ? new List<Guid> { route.Id, reverse.Id }
            : new List<Guid> { route.Id };
        // f.RouteId != null keeps a contract sector out of this check, which is correct in both
        // directions: a contract flight has no route, so deleting a route can never strand one, and
        // refusing a route deletion because an unrelated contract leg happened to be in the air would
        // be a refusal the player could not act on.
        var flightInProgress = await db.Flights
            .Where(f => f.RouteId != null && affectedRouteIds.Contains(f.RouteId.Value) && f.Status == FlightStatus.InProgress)
            .Select(f => new { f.RouteId })
            .FirstOrDefaultAsync(ct);

        if (flightInProgress is not null)
        {
            var leg = flightInProgress.RouteId == route.Id
                ? $"{route.DepartureIcao} → {route.ArrivalIcao}"
                : $"{route.ArrivalIcao} → {route.DepartureIcao}";
            return Results.BadRequest(new
            {
                message = $"There is a flight in progress on {leg}. Finish or abandon it before deleting this route - deleting it now would leave that sector unable to be paid.",
            });
        }

        // A scheduled leg on the pair is a different matter: it is not in the air, but deleting the
        // route it depends on strands whatever aircraft was going to fly it, which is the same
        // stranding the schedule builder already refuses to create by hand.
        var scheduledLeg = await db.PilotScheduleEntries
            .Where(e => affectedRouteIds.Contains(e.RouteId))
            .Select(e => new { e.RouteId })
            .FirstOrDefaultAsync(ct);

        if (scheduledLeg is not null)
        {
            return Results.BadRequest(new
            {
                message = $"{route.DepartureIcao} ↔ {route.ArrivalIcao} is still on a pilot's schedule. Remove those legs first, or the aircraft flying them would be left with nowhere to go.",
            });
        }

        var now = DateTimeOffset.UtcNow;
        route.DeletedUtc = now;
        if (reverse is not null)
        {
            reverse.DeletedUtc = now;
        }

        // One SaveChangesAsync call for both soft deletes - atomic in the same way pair creation is.
        await db.SaveChangesAsync(ct);

        var deletedRouteIds = reverse is not null ? new[] { route.Id, reverse.Id } : new[] { route.Id };
        var message = reverse is not null
            ? $"Deleted both legs of the route: {route.DepartureIcao} ↔ {route.ArrivalIcao}."
            : $"Deleted {route.DepartureIcao} → {route.ArrivalIcao}. It had no return leg to remove.";

        return Results.Ok(new { deletedRouteIds, message });
    }

    /// <summary>
    /// Repairs a pre-pairing route list in two passes. Pass 1 gives every route missing its
    /// reverse leg one, so a legacy single-leg route ends up fully paired (saving each repair
    /// immediately, rather than batching, so a later route repaired in the same pass sees the
    /// flight number and direction the previous repair just took). Pass 2 gives every route still
    /// missing a flight number one - this covers legs saved back when routes had no flight
    /// numbers at all, which pass 1 alone never touches because their reverse leg already exists.
    /// Never renumbers a route that already has one, and is idempotent - once every route is
    /// paired and numbered, calling this again is a no-op query with no writes.
    /// Returns the routes pass 1 created (for the caller to add to its own list); mutates
    /// <paramref name="routes"/>' entities in place for pass 2's flight-number assignments,
    /// but never adds/removes entries from that list itself, since the caller (ListAsync) does
    /// that with the returned list to avoid double-counting.
    /// </summary>
    private static async Task<List<Route>> BackfillMissingReturnLegsAsync(
        FsOpsDbContext db, Airline airline, List<Route> routes, EconomyConfig economyConfig, CancellationToken ct)
    {
        // Local working copy - pass 2 needs visibility into legs pass 1 just created, but this
        // method must not mutate the `routes` parameter's membership itself, or the caller's own
        // `routes.AddRange(created)` afterwards would add those legs a second time.
        var allRoutes = new List<Route>(routes);
        var byDirection = new Dictionary<(string DepartureIcao, string ArrivalIcao), Route>();
        foreach (var r in allRoutes)
        {
            // TryAdd rather than indexer assignment: odd/duplicate data (two rows with the exact
            // same direction) must not throw here - the first one found just wins.
            byDirection.TryAdd((r.DepartureIcao, r.ArrivalIcao), r);
        }

        var created = new List<Route>();

        foreach (var route in routes)
        {
            if (byDirection.ContainsKey((route.ArrivalIcao, route.DepartureIcao)))
            {
                continue;
            }

            var (reverse, error) = await BuildRouteAsync(
                db, airline, route.ArrivalIcao, route.DepartureIcao, aircraftTypeId: null, route.BaseFare, requestedFlightNumber: null,
                existing => FlightNumberGenerator.SuggestReturn(airline.IcaoCode, route.FlightNumber, existing), economyConfig, ct);

            if (error is not null || reverse is null)
            {
                // Can't repair this one right now (e.g. the reverse is beyond the fleet's range) -
                // leave it as a single leg instead of failing the whole list.
                continue;
            }

            db.Routes.Add(reverse);
            await db.SaveChangesAsync(ct);

            created.Add(reverse);
            allRoutes.Add(reverse);
            byDirection[(reverse.DepartureIcao, reverse.ArrivalIcao)] = reverse;
        }

        // Pass 2: number every route still missing a flight number (e.g. legs saved before
        // flight numbers existed). Walks pairs in creation order, not individual routes, so both
        // legs of a still-unnumbered pair are resolved together: the earlier-created leg becomes
        // the odd outbound and its partner the even return. If one side of a pair already has a
        // number, the other is generated to sit right alongside it instead of independently.
        var existingNumbers = await db.Routes
            .Where(r => r.AirlineId == airline.Id)
            .Select(r => r.FlightNumber)
            .ToListAsync(ct);
        var handledPairs = new HashSet<(string, string)>();
        var anyNumberAssigned = false;

        foreach (var route in allRoutes.OrderBy(r => r.CreatedUtc))
        {
            var pairKey = string.CompareOrdinal(route.DepartureIcao, route.ArrivalIcao) <= 0
                ? (route.DepartureIcao, route.ArrivalIcao)
                : (route.ArrivalIcao, route.DepartureIcao);
            if (!handledPairs.Add(pairKey))
            {
                continue;
            }

            byDirection.TryGetValue((route.ArrivalIcao, route.DepartureIcao), out var partner);

            if (route.FlightNumber is null)
            {
                var newNumber = partner?.FlightNumber is not null
                    ? FlightNumberGenerator.SuggestReturn(airline.IcaoCode, partner.FlightNumber, existingNumbers)
                    : FlightNumberGenerator.SuggestOutbound(airline.IcaoCode, existingNumbers);
                route.FlightNumber = newNumber;
                existingNumbers.Add(newNumber);
                anyNumberAssigned = true;
            }

            if (partner is not null && partner.FlightNumber is null)
            {
                var newNumber = FlightNumberGenerator.SuggestReturn(airline.IcaoCode, route.FlightNumber, existingNumbers);
                partner.FlightNumber = newNumber;
                existingNumbers.Add(newNumber);
                anyNumberAssigned = true;
            }
        }

        if (anyNumberAssigned)
        {
            await db.SaveChangesAsync(ct);
        }

        return created;
    }

    private static async Task<object> ToRouteDtoAsync(Route route, FsOpsDbContext db, CancellationToken ct)
    {
        var departure = await db.Airports.FirstOrDefaultAsync(a => a.Icao == route.DepartureIcao, ct);
        var arrival = await db.Airports.FirstOrDefaultAsync(a => a.Icao == route.ArrivalIcao, ct);
        var returnRouteId = await db.Routes
            .Where(r => r.AirlineId == route.AirlineId && r.DepartureIcao == route.ArrivalIcao && r.ArrivalIcao == route.DepartureIcao)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(ct);

        return new
        {
            route.Id,
            route.DepartureIcao,
            DepartureName = departure?.Name,
            route.ArrivalIcao,
            ArrivalName = arrival?.Name,
            route.FlightNumber,
            returnRouteId,
            route.DistanceNm,
            route.BaseFare,
            route.IsActive,
            route.CreatedUtc,
        };
    }
}

public record RoutePreviewRequest(string? DepartureIcao, string? ArrivalIcao, Guid? AircraftTypeId, decimal? Fare);

public record CreateRouteRequest(string? DepartureIcao, string? ArrivalIcao, Guid? AircraftTypeId, decimal? BaseFare, string? FlightNumber);

public record UpdateRouteRequest(string? FlightNumber, decimal? BaseFare, bool? IsActive);

