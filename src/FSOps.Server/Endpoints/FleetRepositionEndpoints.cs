using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Fleet;
using FSOps.Core.Money;
using FSOps.Core.Time;
using FSOps.Data;
using FSOps.Server.Auth;
using FSOps.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Endpoints;

/// <summary>
/// Moving an idle aircraft to another airport the airline already serves, without flying it there.
/// Exists to unstick a stranded airframe: an aircraft that ends up somewhere with no route out of it
/// otherwise has nothing useful to do, and no way back short of hand-flying an empty sector.
/// <para>
/// <b>Instant, not a ferry flight.</b> The move applies the moment it is confirmed - no block time,
/// no airframe hours, no fuel burn, no maintenance-cycle progression, and no
/// <see cref="Flight"/> row. A ferry sector would be more realistic, but this feature's whole
/// purpose is to unstick an aircraft, and a fix that itself takes a sector to apply is barely a fix.
/// Charging airframe hours would also mean a positioning move quietly moved the aircraft toward its
/// next A-check, which is a second, unstated cost on top of the fee. The fee is what the move costs;
/// nothing else about the aircraft changes.
/// </para>
/// <para>
/// <b>Player-only.</b> Only an aircraft reserved for the player may be repositioned (user's
/// decision, 2026-08-13) - see <see cref="RepositionRefusal.NotReservedForPlayer"/>. Every refusal
/// rule lives in <see cref="AircraftRepositionEvaluator"/>, which is pure; this class only supplies
/// it with database state and turns its verdict into wording the player can act on.
/// </para>
/// <para>
/// Quote-then-commit, exactly like <see cref="FleetDisposalEndpoints"/>: the cost the player
/// confirmed travels with the commit as <see cref="RepositionAircraftRequest.ExpectedCost"/> and is
/// compared against a freshly-resolved figure, so a config reload between the dialog opening and the
/// player clicking confirm refuses the move and re-quotes rather than silently charging a different
/// number than the one on screen.
/// </para>
/// </summary>
public static class FleetRepositionEndpoints
{
    public static void MapFleetRepositionEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/fleet/{id:guid}/reposition-options", RepositionOptionsAsync);
        group.MapPost("/fleet/{id:guid}/reposition", RepositionAsync);
    }

    /// <summary>
    /// Everything the reposition dialog needs before the player picks anywhere: where the aircraft
    /// is, every airport it may be moved to, what the move costs, and what the cash balance would be
    /// afterwards - plus, when the move is impossible, a reason that ends in something the player
    /// can actually do. Read-only and side-effect-free (bar releasing maintenance groundings whose
    /// downtime has already elapsed, exactly as GET /fleet does), so it is safe to call on every
    /// open of the dialog.
    /// </summary>
    internal static async Task<IResult> RepositionOptionsAsync(
        Guid id, FsOpsDbContext db, ICurrentUser currentUser, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline before repositioning aircraft." });
        }

        // A grounding whose downtime has already elapsed is released first, so "grounded for
        // maintenance" here can never be stale state a background pass hasn't caught up to - the
        // same guarantee GET /fleet and the Fly screen's options both give.
        await MaintenanceReleaser.ReleaseDueAsync(db, airline.Id, DateTimeOffset.UtcNow, ct);

        var aircraft = await db.FleetAircraft.FirstOrDefaultAsync(f => f.Id == id && f.AirlineId == airline.Id, ct);
        if (aircraft is null)
        {
            return Results.NotFound(new { error = "Aircraft not found." });
        }

        var aircraftType = await db.AircraftTypes.FindAsync([aircraft.AircraftTypeId], ct);
        var context = await BuildContextAsync(db, airline, aircraft, economyConfigCatalog, ct);
        var assessment = context.Assess(destinationIcao: null);

        var currency = await ResolveCurrencyAsync(db, currentUser.UserId, ct);
        var airportsByIcao = await AirportsByIcaoAsync(db, context.Destinations.Append(aircraft.LocationIcao), ct);

        return Results.Ok(new
        {
            fleetAircraftId = aircraft.Id,
            registration = aircraft.Registration,
            aircraftTypeName = aircraftType?.Name ?? "Unknown type",
            currentIcao = aircraft.LocationIcao,
            currentAirportName = airportsByIcao.TryGetValue(aircraft.LocationIcao, out var here) ? here.Name : null,
            cost = context.Cost,
            cashBalance = context.CashBalance,
            cashAfter = assessment.CashAfter,
            destinations = context.Destinations
                .Select(icao => new RepositionDestination(
                    icao,
                    airportsByIcao.TryGetValue(icao, out var airport) ? airport.Name : icao,
                    airportsByIcao.TryGetValue(icao, out var located) ? located.Municipality : null,
                    context.RouteCountFor(icao)))
                .ToList(),
            canReposition = assessment.CanReposition,
            blockReason = Describe(assessment.Refusal, aircraft, context, destinationIcao: null, currency),
        });
    }

    /// <summary>
    /// Commits the move: writes the aircraft's new <see cref="FleetAircraft.LocationIcao"/> and posts
    /// a single <see cref="LedgerCategory.AircraftRepositioning"/> line for the fee, in one
    /// transaction. Nothing else about the aircraft is touched - see this class's own doc for why the
    /// move is instant and free of airframe hours.
    /// </summary>
    internal static async Task<IResult> RepositionAsync(
        Guid id,
        RepositionAircraftRequest request,
        FsOpsDbContext db,
        ICurrentUser currentUser,
        EconomyConfigCatalog economyConfigCatalog,
        IClock clock,
        CancellationToken ct)
    {
        var airline = await db.Airlines.FirstOrDefaultAsync(a => a.OwnerUserId == currentUser.UserId, ct);
        if (airline is null)
        {
            return Results.BadRequest(new { error = "Create an airline before repositioning aircraft." });
        }

        await MaintenanceReleaser.ReleaseDueAsync(db, airline.Id, DateTimeOffset.UtcNow, ct);

        var aircraft = await db.FleetAircraft.FirstOrDefaultAsync(f => f.Id == id && f.AirlineId == airline.Id, ct);
        if (aircraft is null)
        {
            return Results.NotFound(new { error = "Aircraft not found." });
        }

        if (string.IsNullOrWhiteSpace(request.DestinationIcao))
        {
            return Results.BadRequest(new { error = "destinationIcao is required." });
        }

        var destinationIcao = request.DestinationIcao.Trim().ToUpperInvariant();
        var context = await BuildContextAsync(db, airline, aircraft, economyConfigCatalog, ct);
        var currency = await ResolveCurrencyAsync(db, currentUser.UserId, ct);
        var assessment = context.Assess(destinationIcao);

        if (!assessment.CanReposition)
        {
            return Results.BadRequest(new { error = Describe(assessment.Refusal, aircraft, context, destinationIcao, currency) });
        }

        // The cost is config-resolved rather than time-dependent, so it should not move between
        // quote and commit - but "should not" is not "cannot" (a config reload is enough), and this
        // action spends the player's money irreversibly. Same guard, and the same re-quote response,
        // as FleetDisposalEndpoints' sale/lease-termination commits.
        if (assessment.Cost != request.ExpectedCost)
        {
            return Results.BadRequest(new
            {
                error = $"The repositioning fee has changed since you last checked (was {request.ExpectedCost:F2}, now {assessment.Cost:F2}) - please confirm the new figure.",
                currentCost = assessment.Cost,
            });
        }

        var now = clock.UtcNow;
        var fromIcao = aircraft.LocationIcao;

        aircraft.LocationIcao = destinationIcao;

        db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            AirlineId = airline.Id,
            Utc = now,
            Category = LedgerCategory.AircraftRepositioning,
            Amount = -assessment.Cost,
            Description = $"Repositioned {aircraft.Registration}: {fromIcao} to {destinationIcao}",
        });

        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            fleetAircraftId = aircraft.Id,
            registration = aircraft.Registration,
            fromIcao,
            toIcao = destinationIcao,
            cost = assessment.Cost,
            cashBalance = await CashBalanceAsync(db, airline.Id, ct),
        });
    }

    /// <summary>
    /// Everything the pure <see cref="AircraftRepositionEvaluator"/> needs, gathered once so the
    /// options endpoint and the commit endpoint can never disagree about what they looked at.
    /// </summary>
    private sealed record RepositionContext(
        FleetAircraft Aircraft,
        IReadOnlyList<string> Destinations,
        IReadOnlyDictionary<string, int> RouteCounts,
        bool AirlineHasRoutes,
        decimal Cost,
        decimal CashBalance)
    {
        public AircraftRepositionAssessment Assess(string? destinationIcao) =>
            AircraftRepositionEvaluator.Evaluate(
                Aircraft.LocationIcao,
                destinationIcao,
                Aircraft.Status == FleetAircraftStatus.InFlight,
                Aircraft.Status == FleetAircraftStatus.InMaintenance,
                Aircraft.ReservedForPlayer,
                Destinations,
                AirlineHasRoutes,
                Cost,
                CashBalance);

        public int RouteCountFor(string icao) => RouteCounts.TryGetValue(icao, out var count) ? count : 0;
    }

    private static async Task<RepositionContext> BuildContextAsync(
        FsOpsDbContext db, Airline airline, FleetAircraft aircraft, EconomyConfigCatalog economyConfigCatalog, CancellationToken ct)
    {
        // Active routes only: an aircraft parked at an airport only a retired route ever touched is
        // exactly as stranded as one parked anywhere else, so a deactivated route must not keep
        // offering its airports as somewhere to go.
        var routes = await db.Routes
            .Where(r => r.AirlineId == airline.Id && r.IsActive)
            .Select(r => new { r.DepartureIcao, r.ArrivalIcao })
            .ToListAsync(ct);

        var destinations = AircraftRepositionEvaluator.DestinationsFor(
            routes.Select(r => (r.DepartureIcao, r.ArrivalIcao)), aircraft.LocationIcao);

        // How many of the airline's routes touch each airport - shown in the picker so the player can
        // tell a hub from a single-route outstation at a glance, rather than reading a bare ICAO list.
        var routeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes)
        {
            foreach (var icao in new[] { route.DepartureIcao, route.ArrivalIcao })
            {
                if (string.IsNullOrWhiteSpace(icao))
                {
                    continue;
                }

                var key = icao.Trim().ToUpperInvariant();
                routeCounts[key] = routeCounts.TryGetValue(key, out var existing) ? existing + 1 : 1;
            }
        }

        var economyConfig = economyConfigCatalog.Get(airline.Playstyle);

        return new RepositionContext(
            aircraft,
            destinations,
            routeCounts,
            routes.Count > 0,
            economyConfig.AircraftRepositioning.Cost,
            await CashBalanceAsync(db, airline.Id, ct));
    }

    /// <summary>
    /// Turns a refusal into wording the player can act on. Every message names the aircraft and ends
    /// in a next step that actually works - the project's standing rule for refusals, and the reason
    /// the evaluator orders its checks the way it does (see its own doc): telling someone to reserve
    /// an aircraft that is also in maintenance sends them to do something that will not help.
    /// </summary>
    private static string? Describe(
        RepositionRefusal refusal, FleetAircraft aircraft, RepositionContext context, string? destinationIcao, Currency currency) => refusal switch
        {
            RepositionRefusal.None => null,

            RepositionRefusal.InFlight =>
                $"{aircraft.Registration} is currently in flight - it can't be repositioned mid-sector. " +
                "Finish or abandon the flight first; wherever it lands becomes its new location anyway.",

            RepositionRefusal.GroundedForMaintenance =>
                $"{aircraft.Registration} is grounded for maintenance" +
                (aircraft.GroundedUntilUtc is { } until ? $" until {until:yyyy-MM-dd HH:mm} UTC" : string.Empty) +
                " - an aircraft that can't be flown can't be repositioned either. Wait for the check to finish, then move it.",

            RepositionRefusal.NotReservedForPlayer =>
                $"{aircraft.Registration} is available to virtual pilots, and only aircraft reserved for you can be " +
                "repositioned. Reserve it for yourself from the Fleet page (the \"Reserve for you\" button on its row), " +
                "then move it.",

            RepositionRefusal.NoRoutesAtAll =>
                $"Your airline has no active routes, so there is nowhere to reposition {aircraft.Registration} to - " +
                "an aircraft can only be moved to an airport you already fly to or from. Create a route first.",

            RepositionRefusal.NowhereElseToGo =>
                $"{aircraft.Registration} is already at {aircraft.LocationIcao}, and every airport on your route network " +
                "is that same airport - there is nowhere else to move it to. Create a route to somewhere new first.",

            RepositionRefusal.AlreadyThere =>
                $"{aircraft.Registration} is already at {aircraft.LocationIcao} - pick a different airport.",

            RepositionRefusal.DestinationNotServed =>
                $"You have no active route to or from {destinationIcao ?? "that airport"}, so {aircraft.Registration} can't be " +
                "repositioned there. Aircraft can only be moved between airports your airline already serves.",

            RepositionRefusal.InsufficientCash =>
                $"Insufficient funds - repositioning {aircraft.Registration} costs " +
                $"{MoneyFormatter.Format(context.Cost, currency)}, you have {MoneyFormatter.Format(context.CashBalance, currency)}.",

            _ => "This aircraft can't be repositioned right now.",
        };

    private static async Task<IReadOnlyDictionary<string, Airport>> AirportsByIcaoAsync(
        FsOpsDbContext db, IEnumerable<string> icaos, CancellationToken ct)
    {
        var wanted = icaos.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Count == 0)
        {
            return new Dictionary<string, Airport>(StringComparer.OrdinalIgnoreCase);
        }

        var airports = await db.Airports.Where(a => wanted.Contains(a.Icao)).ToListAsync(ct);
        return airports
            .GroupBy(a => a.Icao, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Cash is never a stored column - see the project convention. Same materialise-then-sum
    /// pattern as everywhere else (SQLite can't translate SumAsync over decimal).</summary>
    private static async Task<decimal> CashBalanceAsync(FsOpsDbContext db, Guid airlineId, CancellationToken ct)
    {
        var amounts = await db.LedgerTransactions.Where(t => t.AirlineId == airlineId).Select(t => t.Amount).ToListAsync(ct);
        return amounts.Sum();
    }

    /// <summary>The player's own display currency, for formatting a money figure inside a refusal
    /// message - every stored figure stays in the base unit forever (see <see cref="MoneyFormatter"/>).
    /// Mirrors FleetEndpoints.ResolveCurrencyAsync.</summary>
    private static async Task<Currency> ResolveCurrencyAsync(FsOpsDbContext db, Guid ownerUserId, CancellationToken ct)
    {
        var settings = await db.UserSettings.FirstOrDefaultAsync(s => s.OwnerUserId == ownerUserId, ct);
        return CurrencyCatalogue.TryGet(settings?.CurrencyCode) ?? CurrencyCatalogue.TryGet(CurrencyCatalogue.BaseCurrencyCode)!;
    }
}

/// <summary>One airport a stranded aircraft may be repositioned to - see
/// FleetRepositionEndpoints.RepositionOptionsAsync. <paramref name="RouteCount"/> is how many of the
/// airline's active routes touch this airport, in either direction.</summary>
public record RepositionDestination(string Icao, string Name, string? Municipality, int RouteCount);

/// <summary>
/// Body for FleetRepositionEndpoints.RepositionAsync. <see cref="ExpectedCost"/> is the exact figure
/// the player was shown and confirmed on the options response, so the commit can detect drift rather
/// than silently charging a different number - see FleetRepositionEndpoints' class doc.
/// </summary>
public record RepositionAircraftRequest(string? DestinationIcao, decimal ExpectedCost);
