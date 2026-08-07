using FSOps.Core.Entities;
using FSOps.Core.Planning;
using FSOps.Data;
using FSOps.Server.Auth;
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

    public static void MapRouteEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapPost("/routes/preview", PreviewAsync);
        group.MapPost("/routes", CreateAsync);
        group.MapGet("/routes", ListAsync);
        group.MapGet("/routes/{id:guid}", GetByIdAsync);
        group.MapDelete("/routes/{id:guid}", DeleteAsync);
    }

    /// <summary>
    /// Must never throw - the UI calls this on every keystroke while the user is picking
    /// airports, so problems are reported through validation/warnings instead of error
    /// responses. The try/catch is a deliberate last line of defence on top of the null-checks
    /// below, not a substitute for them.
    /// </summary>
    private static async Task<IResult> PreviewAsync(RoutePreviewRequest request, FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
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
                departure = await db.Airports.FirstOrDefaultAsync(a => a.Icao == departureIcao, ct);
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
                arrival = await db.Airports.FirstOrDefaultAsync(a => a.Icao == arrivalIcao, ct);
                if (arrival is null)
                {
                    warnings.Add($"Arrival airport '{arrivalIcao}' was not found.");
                }
            }

            var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
            var aircraftType = await ResolveAircraftTypeAsync(db, request.AircraftTypeId, airline, warnings, ct);

            if (departure is null || arrival is null || aircraftType is null)
            {
                return Results.Ok(EmptyPreview(departureIcao, arrivalIcao, warnings));
            }

            var result = RoutePreviewCalculator.Calculate(departure, arrival, aircraftType, airline?.StrategyProfile);
            warnings.AddRange(result.Validation.Warnings);

            return Results.Ok(new
            {
                distanceNm = Math.Round(result.DistanceNm, 1),
                initialBearingDeg = Math.Round(result.InitialBearingDeg, 1),
                estimatedBlockMinutes = result.BlockTimeBreakdown.TotalMinutes,
                blockTimeBreakdown = result.BlockTimeBreakdown,
                cruiseAltitudeFt = result.CruiseAltitudeFt,
                blockFuelKg = Math.Round(result.FuelBreakdown.TotalFuelKg, 0),
                fuelBreakdown = result.FuelBreakdown,
                suggestedFare = result.SuggestedFare,
                greatCirclePath = result.GreatCirclePath.Select(p => new[] { p.Lon, p.Lat }),
                validation = new
                {
                    withinRange = result.Validation.WithinRange,
                    departureRunwayAdequate = result.Validation.DepartureRunwayAdequate,
                    arrivalRunwayAdequate = result.Validation.ArrivalRunwayAdequate,
                    sameAirport = result.Validation.SameAirport,
                    warnings,
                },
            });
        }
        catch
        {
            return Results.Ok(EmptyPreview(string.Empty, string.Empty, new List<string> { "Could not compute a preview for this route." }));
        }
    }

    private static object EmptyPreview(string departureIcao, string arrivalIcao, List<string> warnings) => new
    {
        distanceNm = 0.0,
        initialBearingDeg = 0.0,
        estimatedBlockMinutes = 0,
        blockTimeBreakdown = (object?)null,
        cruiseAltitudeFt = 0,
        blockFuelKg = 0.0,
        fuelBreakdown = (object?)null,
        suggestedFare = 0m,
        greatCirclePath = Array.Empty<double[]>(),
        validation = new
        {
            withinRange = false,
            departureRunwayAdequate = false,
            arrivalRunwayAdequate = false,
            sameAirport = departureIcao.Length > 0 && departureIcao == arrivalIcao,
            warnings,
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

    private static async Task<IResult> CreateAsync(CreateRouteRequest request, FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline before adding routes." });
        }

        var departureIcao = (request.DepartureIcao ?? string.Empty).Trim().ToUpperInvariant();
        var arrivalIcao = (request.ArrivalIcao ?? string.Empty).Trim().ToUpperInvariant();

        if (departureIcao.Length == 0 || arrivalIcao.Length == 0)
        {
            return Results.BadRequest(new { error = "Departure and arrival ICAO codes are required." });
        }

        if (departureIcao == arrivalIcao)
        {
            return Results.BadRequest(new { error = "Departure and arrival airports must be different." });
        }

        var departure = await db.Airports.FirstOrDefaultAsync(a => a.Icao == departureIcao, ct);
        if (departure is null)
        {
            return Results.BadRequest(new { error = $"Departure airport '{departureIcao}' was not found." });
        }

        var arrival = await db.Airports.FirstOrDefaultAsync(a => a.Icao == arrivalIcao, ct);
        if (arrival is null)
        {
            return Results.BadRequest(new { error = $"Arrival airport '{arrivalIcao}' was not found." });
        }

        // "Same city pair" is treated as either direction between the same two airports -
        // an airline offering LHR->JFK already covers the JFK->LHR leg conceptually.
        var duplicateExists = await db.Routes.AnyAsync(r =>
            r.AirlineId == airline.Id &&
            ((r.DepartureIcao == departureIcao && r.ArrivalIcao == arrivalIcao) ||
             (r.DepartureIcao == arrivalIcao && r.ArrivalIcao == departureIcao)), ct);
        if (duplicateExists)
        {
            return Results.Conflict(new { error = $"A route between {departureIcao} and {arrivalIcao} already exists." });
        }

        AircraftType? aircraftType = null;
        if (request.AircraftTypeId is Guid requestedTypeId)
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

            var distanceNm = GreatCircle.DistanceNm(departure.Latitude, departure.Longitude, arrival.Latitude, arrival.Longitude);
            var anyTypeInRange = fleetAircraftTypes.Any(t => distanceNm <= t.RangeNm * RoutePreviewCalculator.OperationalRangeFactor);
            if (!anyTypeInRange)
            {
                return Results.BadRequest(new { error = $"This route ({distanceNm:F0} nm) is beyond your fleet's range." });
            }
        }

        aircraftType ??= await db.AircraftTypes.OrderBy(t => t.IcaoType).FirstOrDefaultAsync(ct);
        if (aircraftType is null)
        {
            return Results.Problem("No aircraft type is available to plan this route.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var result = RoutePreviewCalculator.Calculate(departure, arrival, aircraftType, airline.StrategyProfile);

        decimal baseFare;
        if (request.BaseFare is decimal requestedFare)
        {
            // Guard against fat-finger / garbage input while still letting the user meaningfully
            // undercut or beat the suggested fare - "sane" is defined relative to the suggestion
            // rather than as a fixed currency band, since suggested fares vary a lot by distance.
            var minAllowedFare = result.SuggestedFare * MinFareMultiplierOfSuggested;
            var maxAllowedFare = result.SuggestedFare * MaxFareMultiplierOfSuggested;
            if (requestedFare <= 0m || requestedFare < minAllowedFare || requestedFare > maxAllowedFare)
            {
                return Results.BadRequest(new
                {
                    error = $"Fare {requestedFare:F2} is outside the allowed range " +
                            $"({minAllowedFare:F2}-{maxAllowedFare:F2}) for this route (suggested fare {result.SuggestedFare:F2}).",
                });
            }

            baseFare = requestedFare;
        }
        else
        {
            baseFare = result.SuggestedFare;
        }

        var route = new Route
        {
            Id = Guid.NewGuid(),
            AirlineId = airline.Id,
            DepartureIcao = departureIcao,
            ArrivalIcao = arrivalIcao,
            DistanceNm = result.DistanceNm,
            BaseFare = baseFare,
            IsActive = true,
            CreatedUtc = DateTimeOffset.UtcNow,
        };

        db.Routes.Add(route);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/routes/{route.Id}", await ToRouteDtoAsync(route, db, ct));
    }

    private static async Task<IResult> ListAsync(FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.Ok(Array.Empty<object>());
        }

        // SQLite's EF provider can't translate ORDER BY over DateTimeOffset into SQL, so the
        // (small, per-airline) route list is ordered client-side after fetching.
        var routes = (await db.Routes.Where(r => r.AirlineId == airline.Id).ToListAsync(ct))
            .OrderBy(r => r.CreatedUtc)
            .ToList();
        var icaos = routes.SelectMany(r => new[] { r.DepartureIcao, r.ArrivalIcao }).Distinct().ToList();
        var airports = await db.Airports.Where(a => icaos.Contains(a.Icao)).ToDictionaryAsync(a => a.Icao, ct);

        // Same aircraft type the preview falls back to when the caller doesn't pin one: the
        // airline's fleet default. Resolved once for the whole list rather than per-route.
        var discardedWarnings = new List<string>();
        var aircraftType = await ResolveAircraftTypeAsync(db, requestedTypeId: null, airline, discardedWarnings, ct);

        var dtos = routes.Select(r =>
        {
            int? estimatedBlockMinutes = null;
            if (aircraftType is not null &&
                airports.TryGetValue(r.DepartureIcao, out var dep) &&
                airports.TryGetValue(r.ArrivalIcao, out var arr))
            {
                var preview = RoutePreviewCalculator.Calculate(dep, arr, aircraftType, airline.StrategyProfile);
                estimatedBlockMinutes = preview.BlockTimeBreakdown.TotalMinutes;
            }

            return new
            {
                r.Id,
                r.DepartureIcao,
                DepartureName = airports.TryGetValue(r.DepartureIcao, out var depAirport) ? depAirport.Name : null,
                r.ArrivalIcao,
                ArrivalName = airports.TryGetValue(r.ArrivalIcao, out var arrAirport) ? arrAirport.Name : null,
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

    private static async Task<IResult> DeleteAsync(Guid id, FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
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

        route.DeletedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<object> ToRouteDtoAsync(Route route, FsOpsDbContext db, CancellationToken ct)
    {
        var departure = await db.Airports.FirstOrDefaultAsync(a => a.Icao == route.DepartureIcao, ct);
        var arrival = await db.Airports.FirstOrDefaultAsync(a => a.Icao == route.ArrivalIcao, ct);

        return new
        {
            route.Id,
            route.DepartureIcao,
            DepartureName = departure?.Name,
            route.ArrivalIcao,
            ArrivalName = arrival?.Name,
            route.DistanceNm,
            route.BaseFare,
            route.IsActive,
            route.CreatedUtc,
        };
    }
}

public record RoutePreviewRequest(string? DepartureIcao, string? ArrivalIcao, Guid? AircraftTypeId);

public record CreateRouteRequest(string? DepartureIcao, string? ArrivalIcao, Guid? AircraftTypeId, decimal? BaseFare);
