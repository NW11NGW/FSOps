using System.Text.RegularExpressions;
using FSOps.Core.Airlines;
using FSOps.Core.Entities;
using FSOps.Core.Finance;
using FSOps.Core.Money;
using FSOps.Data;
using FSOps.Server.Auth;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Endpoints;

public static class AirlineEndpoints
{
    public static void MapAirlineEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapPost("/airline", CreateAsync);
        group.MapGet("/airline", GetAsync);
        group.MapPut("/airline", UpdateAsync);
        group.MapGet("/airline/summary", GetSummaryAsync);
        group.MapDelete("/airline", DeleteAsync);
    }

    private static async Task<IResult> CreateAsync(CreateAirlineRequest request, FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length is < 2 or > 40)
        {
            return Results.BadRequest(new { error = "Airline name must be between 2 and 40 characters." });
        }

        var icaoCode = (request.IcaoCode ?? string.Empty).Trim().ToUpperInvariant();
        if (!Regex.IsMatch(icaoCode, "^[A-Z]{2,3}$"))
        {
            return Results.BadRequest(new { error = "ICAO code must be 2-3 letters." });
        }

        if (await db.Airlines.AnyAsync(a => a.IcaoCode == icaoCode, ct))
        {
            return Results.BadRequest(new { error = $"An airline with ICAO code '{icaoCode}' already exists." });
        }

        if (await db.Airlines.AnyAsync(a => a.OwnerUserId == currentUser.UserId, ct))
        {
            return Results.Conflict(new { error = "You already have an airline. Delete it first to start over." });
        }

        var homeAirportIcao = (request.HomeAirportIcao ?? string.Empty).Trim().ToUpperInvariant();
        var homeAirport = await db.Airports.FirstOrDefaultAsync(a => a.Icao == homeAirportIcao, ct);
        if (homeAirport is null)
        {
            return Results.BadRequest(new { error = $"Home airport '{homeAirportIcao}' was not found." });
        }

        if (!homeAirport.HasScheduledService && homeAirport.LongestRunwayFt < 5000)
        {
            return Results.BadRequest(new
            {
                error = $"{homeAirportIcao} is too small to base an airline at - it needs scheduled service or a runway of at least 5,000 ft.",
            });
        }

        if (!Enum.TryParse<AirlineStrategyProfile>(request.StrategyProfile, ignoreCase: true, out var strategyProfile))
        {
            return Results.BadRequest(new { error = "strategyProfile must be one of: International, Domestic, LowCost, Premium." });
        }

        var accentColour = (request.AccentColour ?? string.Empty).Trim();
        if (!Regex.IsMatch(accentColour, "^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{3})$"))
        {
            return Results.BadRequest(new { error = "accentColour must be a valid hex colour, e.g. #3b82f6." });
        }

        if (!Enum.TryParse<StarterAircraftFamily>(request.StarterAircraftFamily, ignoreCase: true, out var starterFamily))
        {
            return Results.BadRequest(new { error = "starterAircraftFamily must be one of: A320, B737." });
        }

        var currency = CurrencyCatalogue.TryGet(request.CurrencyCode);
        if (currency is null)
        {
            return Results.BadRequest(new { error = $"Unsupported currency code '{request.CurrencyCode}'." });
        }

        var starterIcaoType = starterFamily == StarterAircraftFamily.A320 ? "A320" : "B738";
        var familyName = starterFamily == StarterAircraftFamily.A320 ? "A320" : "B737";
        var aircraftType = await db.AircraftTypes.FirstOrDefaultAsync(t => t.IcaoType == starterIcaoType, ct)
            ?? await db.AircraftTypes.FirstOrDefaultAsync(t => t.Family == familyName, ct);
        if (aircraftType is null)
        {
            return Results.Problem("No starter aircraft type is available yet - world data may still be seeding.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        Loan? loan = null;
        if (request.StartingLoan is { } loanRequest)
        {
            if (loanRequest.Amount <= 0)
            {
                return Results.BadRequest(new { error = "startingLoan.amount must be greater than zero." });
            }

            if (loanRequest.TermMonths is < 1 or > 360)
            {
                return Results.BadRequest(new { error = "startingLoan.termMonths must be between 1 and 360." });
            }

            if (loanRequest.AnnualRatePct is < 0 or > 40)
            {
                return Results.BadRequest(new { error = "startingLoan.annualRatePct must be between 0 and 40." });
            }

            loan = new Loan
            {
                Id = Guid.NewGuid(),
                Principal = loanRequest.Amount,
                AnnualInterestRate = loanRequest.AnnualRatePct,
                TermMonths = loanRequest.TermMonths,
                MonthlyPayment = LoanCalculator.MonthlyPayment(loanRequest.Amount, loanRequest.AnnualRatePct, loanRequest.TermMonths),
                RemainingBalance = loanRequest.Amount,
                StartUtc = DateTimeOffset.UtcNow,
                IsPaidOff = false,
                CreatedUtc = DateTimeOffset.UtcNow,
            };
        }

        var now = DateTimeOffset.UtcNow;
        var airline = new Airline
        {
            Id = Guid.NewGuid(),
            Name = name,
            IcaoCode = icaoCode,
            HomeAirportIcao = homeAirportIcao,
            StrategyProfile = strategyProfile,
            AccentColour = accentColour,
            ReputationScore = 50,
            OwnerUserId = currentUser.UserId,
            CreatedUtc = now,
        };

        var registration = AircraftRegistrationGenerator.Generate(homeAirport.Country, icaoCode);
        var fleetAircraft = new FleetAircraft
        {
            Id = Guid.NewGuid(),
            AirlineId = airline.Id,
            AircraftTypeId = aircraftType.Id,
            Registration = registration,
            Ownership = AircraftOwnership.Owned,
            AirframeHours = 0,
            HoursSinceACheck = 0,
            HoursSinceCCheck = 0,
            ConditionPercent = 100,
            LocationIcao = homeAirportIcao,
            Status = FleetAircraftStatus.Active,
            CreatedUtc = now,
        };

        var pilot = new Pilot
        {
            Id = Guid.NewGuid(),
            AirlineId = airline.Id,
            Name = currentUser.DisplayName,
            IsPlayer = true,
            MonthlySalary = AirlineCreationDefaults.StartingPilotMonthlySalary,
            HoursFlown = 0,
            SkillRating = 50,
            Status = PilotStatus.Available,
            CreatedUtc = now,
        };

        var ledgerEntries = new List<LedgerTransaction>
        {
            new()
            {
                Id = Guid.NewGuid(), AirlineId = airline.Id, Utc = now,
                Category = LedgerCategory.StartingCapital, Amount = AirlineCreationDefaults.StartingCapital,
                Description = "Starting capital",
            },
            new()
            {
                Id = Guid.NewGuid(), AirlineId = airline.Id, Utc = now,
                Category = LedgerCategory.AircraftPurchase, Amount = -aircraftType.PurchasePrice,
                Description = $"Purchase: {aircraftType.Name} ({registration})",
            },
        };

        if (loan is not null)
        {
            loan.AirlineId = airline.Id;
            ledgerEntries.Add(new LedgerTransaction
            {
                Id = Guid.NewGuid(), AirlineId = airline.Id, Utc = now,
                Category = LedgerCategory.LoanProceeds, Amount = loan.Principal,
                Description = "Loan proceeds",
            });
        }

        db.Airlines.Add(airline);
        db.FleetAircraft.Add(fleetAircraft);
        db.Pilots.Add(pilot);
        db.LedgerTransactions.AddRange(ledgerEntries);
        if (loan is not null)
        {
            db.Loans.Add(loan);
        }

        await UpsertCurrencyAsync(db, currentUser.UserId, currency.Code, ct);

        // A single SaveChangesAsync call commits every add above in one SQLite transaction,
        // so the airline, fleet, pilot, ledger rows and settings either all land or none do.
        await db.SaveChangesAsync(ct);

        return Results.Created("/api/v1/airline", await BuildSummaryAsync(db, airline, ct));
    }

    private static async Task UpsertCurrencyAsync(FsOpsDbContext db, Guid ownerUserId, string currencyCode, CancellationToken ct)
    {
        var settings = await db.UserSettings.FirstOrDefaultAsync(s => s.OwnerUserId == ownerUserId, ct);
        if (settings is null)
        {
            db.UserSettings.Add(new UserSettings { Id = Guid.NewGuid(), OwnerUserId = ownerUserId, CurrencyCode = currencyCode });
        }
        else
        {
            settings.CurrencyCode = currencyCode;
        }
    }

    private static async Task<IResult> GetAsync(FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        return airline is null ? Results.NoContent() : Results.Ok(airline);
    }

    private static async Task<IResult> UpdateAsync(UpdateAirlineRequest request, FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.NotFound(new { error = "You don't have an airline yet." });
        }

        // The ICAO code identifies the airline in flight records and the home base is an
        // economic anchor - neither can be changed after creation. A caller echoing back the
        // airline's current values (e.g. spreading the existing object into the request) is
        // fine; only an actual attempted change is rejected.
        if (request.IcaoCode is not null && !string.Equals(request.IcaoCode.Trim(), airline.IcaoCode, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = "icaoCode cannot be changed after your airline is created." });
        }

        if (request.HomeAirportIcao is not null && !string.Equals(request.HomeAirportIcao.Trim(), airline.HomeAirportIcao, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = "homeAirportIcao cannot be changed after your airline is created." });
        }

        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (name.Length is < 2 or > 40)
            {
                return Results.BadRequest(new { error = "Airline name must be between 2 and 40 characters." });
            }

            airline.Name = name;
        }

        if (request.AccentColour is not null)
        {
            var accentColour = request.AccentColour.Trim();
            if (!Regex.IsMatch(accentColour, "^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{3})$"))
            {
                return Results.BadRequest(new { error = "accentColour must be a valid hex colour, e.g. #3b82f6." });
            }

            airline.AccentColour = accentColour;
        }

        if (request.StrategyProfile is not null)
        {
            if (!Enum.TryParse<AirlineStrategyProfile>(request.StrategyProfile, ignoreCase: true, out var strategyProfile))
            {
                return Results.BadRequest(new { error = "strategyProfile must be one of: International, Domestic, LowCost, Premium." });
            }

            airline.StrategyProfile = strategyProfile;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(airline);
    }

    internal static async Task<IResult> GetSummaryAsync(FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        return airline is null ? Results.NoContent() : Results.Ok(await BuildSummaryAsync(db, airline, ct));
    }

    private static async Task<object> BuildSummaryAsync(FsOpsDbContext db, Airline airline, CancellationToken ct)
    {
        // Cash balance is never a stored column - it's always the sum of the append-only ledger,
        // so it can never drift out of sync with how it got there. SQLite has no native decimal
        // type, and EF's Sqlite provider can't translate Sum() over decimal into SQL, so the
        // (small, per-airline) set of amounts is pulled into memory and summed there instead.
        var amounts = await db.LedgerTransactions
            .Where(t => t.AirlineId == airline.Id)
            .Select(t => t.Amount)
            .ToListAsync(ct);
        var cashBalance = amounts.Sum();
        var fleetCount = await db.FleetAircraft.CountAsync(f => f.AirlineId == airline.Id, ct);

        // Routes are always a there-and-back pair (see RouteEndpoints.CreateAsync): each direction
        // is stored as its own row so it can carry its own flight number, but from the owner's
        // point of view EGGD<->EGPH is *one* route, not two. Counting raw rows would double-count
        // every pair, so this counts distinct unordered airport pairs instead - a leg that hasn't
        // been paired yet (e.g. momentarily, before ListAsync's self-heal runs) still counts as
        // one route rather than zero.
        var legDirections = await db.Routes
            .Where(r => r.AirlineId == airline.Id)
            .Select(r => new { r.DepartureIcao, r.ArrivalIcao })
            .ToListAsync(ct);
        var routeCount = legDirections
            .Select(d => string.CompareOrdinal(d.DepartureIcao, d.ArrivalIcao) <= 0
                ? (d.DepartureIcao, d.ArrivalIcao)
                : (d.ArrivalIcao, d.DepartureIcao))
            .Distinct()
            .Count();

        var pilotCount = await db.Pilots.CountAsync(p => p.AirlineId == airline.Id, ct);

        return new { airline, cashBalance, fleetCount, routeCount, pilotCount };
    }

    private static async Task<IResult> DeleteAsync(bool? confirm, FsOpsDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        if (confirm != true)
        {
            return Results.BadRequest(new { error = "Pass ?confirm=true to delete your airline. This cannot be undone." });
        }

        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.NoContent();
        }

        var now = DateTimeOffset.UtcNow;
        airline.DeletedUtc = now;

        // Cascade the soft-delete to the airline's own fleet/pilots/routes so "start over"
        // leaves a genuinely clean slate rather than orphaned-but-still-visible rows. The
        // ledger itself is left alone - it's an append-only historical record, harmless once
        // its airline is gone.
        var fleet = await db.FleetAircraft.Where(f => f.AirlineId == airline.Id).ToListAsync(ct);
        foreach (var aircraft in fleet)
        {
            aircraft.DeletedUtc = now;
        }

        var pilots = await db.Pilots.Where(p => p.AirlineId == airline.Id).ToListAsync(ct);
        foreach (var pilot in pilots)
        {
            pilot.DeletedUtc = now;
        }

        var routes = await db.Routes.Where(r => r.AirlineId == airline.Id).ToListAsync(ct);
        foreach (var route in routes)
        {
            route.DeletedUtc = now;
        }

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}

public record CreateAirlineRequest(
    string? Name,
    string? IcaoCode,
    string? HomeAirportIcao,
    string? StrategyProfile,
    string? AccentColour,
    string? StarterAircraftFamily,
    string? CurrencyCode,
    StartingLoanRequest? StartingLoan);

public record StartingLoanRequest(decimal Amount, int TermMonths, double AnnualRatePct);

public record UpdateAirlineRequest(
    string? Name,
    string? AccentColour,
    string? StrategyProfile,
    string? IcaoCode,
    string? HomeAirportIcao);
