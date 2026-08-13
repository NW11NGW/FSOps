using System.Collections;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Server.Endpoints;
using FSOps.Server.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Tests;

/// <summary>
/// Moving an idle aircraft to another airport the airline serves, without flying it there. Drives
/// <see cref="FleetRepositionEndpoints"/>' handlers directly against an isolated in-memory
/// <see cref="RouteTestContext"/>, same convention as FleetEndpointsTests/FleetDisposalEndpointsTests,
/// with a <see cref="FakeClock"/> so the posted ledger timestamp is deterministic.
/// <para>
/// The refusal cases carry as much weight here as the happy path: every one of them asserts that
/// <b>nothing at all was written</b> - not the location, not a ledger line - because a refusal that
/// half-commits is worse than one that never fires.
/// </para>
/// </summary>
public class FleetRepositionEndpointsTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const decimal RepositionCost = 2_000m;

    private static int StatusCodeOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode!.Value;

    private static object BodyOf(IResult result) => Assert.IsAssignableFrom<IValueHttpResult>(result).Value!;

    private static T Prop<T>(object body, string name) => (T)body.GetType().GetProperty(name)!.GetValue(body)!;

    private static object? PropOrNull(object body, string name) => body.GetType().GetProperty(name)!.GetValue(body);

    private static async Task SeedCashAsync(RouteTestContext ctx, decimal amount)
    {
        ctx.Db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Utc = Base,
            Category = LedgerCategory.StartingCapital,
            Amount = amount,
            Description = "Test seed capital",
        });
        await ctx.Db.SaveChangesAsync();
    }

    private static async Task<decimal> CashBalanceAsync(RouteTestContext ctx)
    {
        var amounts = await ctx.Db.LedgerTransactions.Where(t => t.AirlineId == ctx.Airline.Id).Select(t => t.Amount).ToListAsync();
        return amounts.Sum();
    }

    /// <summary>Seeds the little network the fixture's airports support: EGGD-EGPH and EGGD-EGSS,
    /// both directions, exactly as RouteEndpoints creates bidirectional pairs. EGPF is deliberately
    /// left unserved so "an airport that exists but isn't on your network" is testable.</summary>
    private static async Task SeedNetworkAsync(RouteTestContext ctx)
    {
        void AddRoute(string departure, string arrival)
        {
            ctx.Db.Routes.Add(new Route
            {
                Id = Guid.NewGuid(),
                AirlineId = ctx.Airline.Id,
                DepartureIcao = departure,
                ArrivalIcao = arrival,
                DistanceNm = 300,
                BaseFare = 90m,
                IsActive = true,
                CreatedUtc = Base,
            });
        }

        AddRoute("EGGD", "EGPH");
        AddRoute("EGPH", "EGGD");
        AddRoute("EGGD", "EGSS");
        AddRoute("EGSS", "EGGD");
        await ctx.Db.SaveChangesAsync();
    }

    private static async Task<FleetAircraft> TheAircraftAsync(RouteTestContext ctx) =>
        await ctx.Db.FleetAircraft.SingleAsync(f => f.AirlineId == ctx.Airline.Id);

    private static Task<IResult> OptionsAsync(RouteTestContext ctx, Guid aircraftId) =>
        FleetRepositionEndpoints.RepositionOptionsAsync(
            aircraftId, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);

    private static Task<IResult> RepositionAsync(RouteTestContext ctx, Guid aircraftId, string? destination, decimal? expectedCost = null) =>
        FleetRepositionEndpoints.RepositionAsync(
            aircraftId,
            new RepositionAircraftRequest(destination, expectedCost ?? RepositionCost),
            ctx.Db,
            ctx.CurrentUser,
            EconomyConfigCatalog.Default(),
            new FakeClock(Base),
            CancellationToken.None);

    /// <summary>No ledger line of the repositioning category exists, and the aircraft is still where
    /// it started - the assertion every refusal test makes, so a refusal can never be a partial
    /// commit.</summary>
    private static async Task AssertNothingHappenedAsync(RouteTestContext ctx, Guid aircraftId, string expectedIcao)
    {
        var aircraft = await ctx.Db.FleetAircraft.SingleAsync(f => f.Id == aircraftId);
        Assert.Equal(expectedIcao, aircraft.LocationIcao);
        Assert.False(await ctx.Db.LedgerTransactions.AnyAsync(
            t => t.AirlineId == ctx.Airline.Id && t.Category == LedgerCategory.AircraftRepositioning));
    }

    // ----- the happy path ---------------------------------------------------------------------

    [Fact]
    public async Task Repositioning_MovesTheAircraft_AndPostsExactlyTwoThousand()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedNetworkAsync(ctx);
        await SeedCashAsync(ctx, 60_000m);
        var aircraft = await TheAircraftAsync(ctx);

        var result = await RepositionAsync(ctx, aircraft.Id, "EGPH");
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

        var body = BodyOf(result);
        Assert.Equal("EGGD", Prop<string>(body, "fromIcao"));
        Assert.Equal("EGPH", Prop<string>(body, "toIcao"));
        Assert.Equal(RepositionCost, Prop<decimal>(body, "cost"));
        // The exact figure the confirmation promised: 60,000 - 2,000.
        Assert.Equal(58_000m, Prop<decimal>(body, "cashBalance"));

        var moved = await ctx.Db.FleetAircraft.SingleAsync(f => f.Id == aircraft.Id);
        Assert.Equal("EGPH", moved.LocationIcao);

        // Exactly one ledger line, in its own category, signed negative - cash comes out of
        // SUM(Amount) like everything else, never a mutable balance column.
        var line = await ctx.Db.LedgerTransactions.SingleAsync(
            t => t.AirlineId == ctx.Airline.Id && t.Category == LedgerCategory.AircraftRepositioning);
        Assert.Equal(-2_000m, line.Amount);
        Assert.Equal(Base, line.Utc);
        Assert.Null(line.FlightId);
        Assert.Contains("G-TEST", line.Description);
        Assert.Contains("EGGD", line.Description);
        Assert.Contains("EGPH", line.Description);
        Assert.Equal(58_000m, await CashBalanceAsync(ctx));
    }

    [Fact]
    public async Task Repositioning_IsInstant_AndChangesNothingButLocation()
    {
        // The instant-vs-ferry decision, pinned as a test: the move costs the fee and nothing else.
        // No airframe hours, no maintenance-cycle progress, no condition loss, no fuel burn, and no
        // Flight row - a positioning move must never quietly push the aircraft toward its next
        // A-check, which would be a second, unstated cost on top of the fee.
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedNetworkAsync(ctx);
        await SeedCashAsync(ctx, 60_000m);

        var before = await TheAircraftAsync(ctx);
        var (hours, sinceA, sinceC, condition, fuel, status) =
            (before.AirframeHours, before.HoursSinceACheck, before.HoursSinceCCheck, before.ConditionPercent, before.FuelOnBoardKg, before.Status);

        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(await RepositionAsync(ctx, before.Id, "EGSS")));

        var after = await ctx.Db.FleetAircraft.SingleAsync(f => f.Id == before.Id);
        Assert.Equal("EGSS", after.LocationIcao);
        Assert.Equal(hours, after.AirframeHours);
        Assert.Equal(sinceA, after.HoursSinceACheck);
        Assert.Equal(sinceC, after.HoursSinceCCheck);
        Assert.Equal(condition, after.ConditionPercent);
        Assert.Equal(fuel, after.FuelOnBoardKg);
        Assert.Equal(status, after.Status);
        Assert.True(after.ReservedForPlayer, "Repositioning must not quietly change who the aircraft is held for.");

        Assert.False(await ctx.Db.Flights.AnyAsync(f => f.AirlineId == ctx.Airline.Id));
        Assert.False(await ctx.Db.MaintenanceEvents.AnyAsync(m => m.FleetAircraftId == before.Id));
    }

    [Fact]
    public async Task AfterRepositioning_TheFlyScreenOffersTheAircraftAtItsNewAirport_Immediately()
    {
        // "Any screen that filters by aircraft-at-this-airport must reflect it immediately." The Fly
        // screen is that screen, and it reads LocationIcao directly - so this proves the move is
        // visible with no background pass, cache invalidation or restart in between.
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedNetworkAsync(ctx);
        await SeedCashAsync(ctx, 60_000m);
        var aircraft = await TheAircraftAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(await RepositionAsync(ctx, aircraft.Id, "EGPH")));

        var options = await FlightEndpoints.OptionsAsync(ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);
        var routes = ((IEnumerable)BodyOf(options)).Cast<object>().ToList();

        // The EGPH->EGGD leg is now flyable (the aircraft is sitting at EGPH)...
        var outboundFromEgph = routes.Single(r => Prop<string>(r, "DepartureIcao") == "EGPH");
        Assert.True(Prop<bool>(outboundFromEgph, "isFlyable"));

        // ...and the legs departing EGGD are not, because nothing is parked there any more.
        Assert.All(
            routes.Where(r => Prop<string>(r, "DepartureIcao") == "EGGD"),
            r => Assert.False(Prop<bool>(r, "isFlyable")));
    }

    // ----- the destination list ---------------------------------------------------------------

    [Fact]
    public async Task Options_OfferOnlyAirportsTheAirlineHasARouteToOrFrom()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedNetworkAsync(ctx);
        await SeedCashAsync(ctx, 60_000m);
        var aircraft = await TheAircraftAsync(ctx);

        var body = BodyOf(await OptionsAsync(ctx, aircraft.Id));

        Assert.True(Prop<bool>(body, "canReposition"));
        Assert.Null(PropOrNull(body, "blockReason"));
        Assert.Equal("EGGD", Prop<string>(body, "currentIcao"));
        Assert.Equal(RepositionCost, Prop<decimal>(body, "cost"));
        Assert.Equal(60_000m, Prop<decimal>(body, "cashBalance"));
        Assert.Equal(58_000m, Prop<decimal>(body, "cashAfter"));

        var destinations = Prop<List<RepositionDestination>>(body, "destinations");
        // EGPF is seeded as an airport but carries no route, so it must not be offered; EGGD is
        // excluded because that is where the aircraft already is.
        Assert.Equal(new[] { "EGPH", "EGSS" }, destinations.Select(d => d.Icao).ToArray());
        Assert.Equal("Edinburgh Airport", destinations.Single(d => d.Icao == "EGPH").Name);
        Assert.Equal(2, destinations.Single(d => d.Icao == "EGPH").RouteCount);
    }

    [Fact]
    public async Task Options_ForAnAirlineWithNoRoutes_SayNothingIsPossible_RatherThanOfferAnEmptyPicker()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedCashAsync(ctx, 60_000m);
        var aircraft = await TheAircraftAsync(ctx);

        var body = BodyOf(await OptionsAsync(ctx, aircraft.Id));

        Assert.False(Prop<bool>(body, "canReposition"));
        Assert.Empty(Prop<List<RepositionDestination>>(body, "destinations"));
        var reason = Prop<string>(body, "blockReason");
        Assert.Contains("no active routes", reason);
        // Every refusal must end in something the player can actually do.
        Assert.Contains("Create a route", reason);
    }

    [Fact]
    public async Task AnInactiveRoute_DoesNotKeepOfferingItsAirports()
    {
        // An aircraft parked at an airport only a retired route ever touched is exactly as stranded
        // as one parked anywhere else - a deactivated route must not still be a way out.
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedNetworkAsync(ctx);
        await SeedCashAsync(ctx, 60_000m);
        foreach (var route in await ctx.Db.Routes.Where(r => r.ArrivalIcao == "EGSS" || r.DepartureIcao == "EGSS").ToListAsync())
        {
            route.IsActive = false;
        }

        await ctx.Db.SaveChangesAsync();
        var aircraft = await TheAircraftAsync(ctx);

        var destinations = Prop<List<RepositionDestination>>(BodyOf(await OptionsAsync(ctx, aircraft.Id)), "destinations");
        Assert.Equal(new[] { "EGPH" }, destinations.Select(d => d.Icao).ToArray());

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(await RepositionAsync(ctx, aircraft.Id, "EGSS")));
        await AssertNothingHappenedAsync(ctx, aircraft.Id, "EGGD");
    }

    // ----- refusals ---------------------------------------------------------------------------

    [Fact]
    public async Task AnAircraftAvailableToVirtualPilots_CannotBeRepositioned()
    {
        // Repositioning is player-only (user's decision, 2026-08-13). The refusal has to name the
        // rule AND where reservation happens, or it is a dead end rather than something actionable.
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedNetworkAsync(ctx);
        await SeedCashAsync(ctx, 60_000m);
        var aircraft = await TheAircraftAsync(ctx);
        aircraft.ReservedForPlayer = false;
        await ctx.Db.SaveChangesAsync();

        var options = BodyOf(await OptionsAsync(ctx, aircraft.Id));
        Assert.False(Prop<bool>(options, "canReposition"));
        var reason = Prop<string>(options, "blockReason");
        Assert.Contains("available to virtual pilots", reason);
        Assert.Contains("Reserve it for yourself", reason);
        Assert.Contains("Fleet page", reason);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(await RepositionAsync(ctx, aircraft.Id, "EGPH")));
        await AssertNothingHappenedAsync(ctx, aircraft.Id, "EGGD");
    }

    [Fact]
    public async Task ReservingTheAircraftFirst_MakesTheSameMoveSucceed()
    {
        // The other half of the rule above: the refusal is a gate the player can pass, not a wall.
        // Asserted end to end so the wording's promised fix is proven to actually work.
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedNetworkAsync(ctx);
        await SeedCashAsync(ctx, 60_000m);
        var aircraft = await TheAircraftAsync(ctx);
        aircraft.ReservedForPlayer = false;
        await ctx.Db.SaveChangesAsync();

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(await RepositionAsync(ctx, aircraft.Id, "EGPH")));

        var reserved = await FleetEndpoints.SetReservationAsync(
            aircraft.Id, new SetReservationRequest(Reserved: true), ctx.Db, ctx.CurrentUser, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(reserved));

        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(await RepositionAsync(ctx, aircraft.Id, "EGPH")));
        Assert.Equal("EGPH", (await ctx.Db.FleetAircraft.SingleAsync(f => f.Id == aircraft.Id)).LocationIcao);
    }

    [Fact]
    public async Task AnAircraftInFlight_CannotBeRepositioned()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedNetworkAsync(ctx);
        await SeedCashAsync(ctx, 60_000m);
        var aircraft = await TheAircraftAsync(ctx);
        aircraft.Status = FleetAircraftStatus.InFlight;
        await ctx.Db.SaveChangesAsync();

        var options = BodyOf(await OptionsAsync(ctx, aircraft.Id));
        Assert.False(Prop<bool>(options, "canReposition"));
        Assert.Contains("currently in flight", Prop<string>(options, "blockReason"));

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(await RepositionAsync(ctx, aircraft.Id, "EGPH")));
        await AssertNothingHappenedAsync(ctx, aircraft.Id, "EGGD");
    }

    [Fact]
    public async Task AnAircraftGroundedForMaintenance_CannotBeRepositioned()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedNetworkAsync(ctx);
        await SeedCashAsync(ctx, 60_000m);
        var aircraft = await TheAircraftAsync(ctx);
        aircraft.Status = FleetAircraftStatus.InMaintenance;
        // Comfortably in the future, so MaintenanceReleaser doesn't legitimately release it first.
        aircraft.GroundedUntilUtc = DateTimeOffset.UtcNow.AddDays(3);
        await ctx.Db.SaveChangesAsync();

        var options = BodyOf(await OptionsAsync(ctx, aircraft.Id));
        Assert.False(Prop<bool>(options, "canReposition"));
        // "Why and until when", never just "in maintenance" - the same standard the Fly screen holds.
        Assert.Contains("grounded for maintenance until", Prop<string>(options, "blockReason"));

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(await RepositionAsync(ctx, aircraft.Id, "EGPH")));
        await AssertNothingHappenedAsync(ctx, aircraft.Id, "EGGD");
    }

    [Fact]
    public async Task WithoutEnoughCash_TheMoveIsRefused_AndNothingIsPosted()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedNetworkAsync(ctx);
        // One penny short of the fee - the boundary, not a comfortably-broke airline.
        await SeedCashAsync(ctx, 1_999.99m);
        var aircraft = await TheAircraftAsync(ctx);

        var options = BodyOf(await OptionsAsync(ctx, aircraft.Id));
        Assert.False(Prop<bool>(options, "canReposition"));
        Assert.Contains("Insufficient funds", Prop<string>(options, "blockReason"));

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(await RepositionAsync(ctx, aircraft.Id, "EGPH")));
        await AssertNothingHappenedAsync(ctx, aircraft.Id, "EGGD");
        Assert.Equal(1_999.99m, await CashBalanceAsync(ctx));
    }

    [Fact]
    public async Task ExactlyTheFee_IsEnough()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedNetworkAsync(ctx);
        await SeedCashAsync(ctx, 2_000m);
        var aircraft = await TheAircraftAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(await RepositionAsync(ctx, aircraft.Id, "EGPH")));
        Assert.Equal(0m, await CashBalanceAsync(ctx));
    }

    [Fact]
    public async Task AnAirportTheAirlineDoesNotServe_IsRefused()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedNetworkAsync(ctx);
        await SeedCashAsync(ctx, 60_000m);
        var aircraft = await TheAircraftAsync(ctx);

        // EGPF exists in the fixture's airport table but carries no route - a real airport this
        // airline simply does not fly to, which is exactly the case the restriction is for.
        var result = await RepositionAsync(ctx, aircraft.Id, "EGPF");
        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        Assert.Contains("no active route to or from EGPF", Prop<string>(BodyOf(result), "error"));
        await AssertNothingHappenedAsync(ctx, aircraft.Id, "EGGD");
    }

    [Fact]
    public async Task MovingAnAircraftToWhereItAlreadyIs_IsRefused()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedNetworkAsync(ctx);
        await SeedCashAsync(ctx, 60_000m);
        var aircraft = await TheAircraftAsync(ctx);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(await RepositionAsync(ctx, aircraft.Id, "EGGD")));
        await AssertNothingHappenedAsync(ctx, aircraft.Id, "EGGD");
    }

    [Fact]
    public async Task AMissingDestination_IsRefused()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedNetworkAsync(ctx);
        await SeedCashAsync(ctx, 60_000m);
        var aircraft = await TheAircraftAsync(ctx);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(await RepositionAsync(ctx, aircraft.Id, "   ")));
        await AssertNothingHappenedAsync(ctx, aircraft.Id, "EGGD");
    }

    [Fact]
    public async Task AStaleConfirmedCost_IsRefused_AndTheCurrentFigureIsReturned()
    {
        // The same optimistic-concurrency guard the sale/lease-termination commits carry: this
        // action spends the player's money irreversibly, so the figure they confirmed has to be the
        // figure that gets charged - never "close enough".
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedNetworkAsync(ctx);
        await SeedCashAsync(ctx, 60_000m);
        var aircraft = await TheAircraftAsync(ctx);

        var result = await RepositionAsync(ctx, aircraft.Id, "EGPH", expectedCost: 1_500m);
        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        Assert.Equal(RepositionCost, Prop<decimal>(BodyOf(result), "currentCost"));
        await AssertNothingHappenedAsync(ctx, aircraft.Id, "EGGD");
    }

    [Fact]
    public async Task AnUnknownAircraft_IsNotFound()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedNetworkAsync(ctx);

        Assert.Equal(StatusCodes.Status404NotFound, StatusCodeOf(await OptionsAsync(ctx, Guid.NewGuid())));
        Assert.Equal(StatusCodes.Status404NotFound, StatusCodeOf(await RepositionAsync(ctx, Guid.NewGuid(), "EGPH")));
    }

    [Fact]
    public async Task RepositioningTwice_PostsTwoSeparateLines_AndLeavesTheAircraftAtTheLastAirport()
    {
        // The ledger is append-only, so a second move is a second line rather than an edit to the
        // first - and the running cash balance stays SUM(Amount) throughout.
        using var ctx = await RouteTestContext.CreateAsync();
        await SeedNetworkAsync(ctx);
        await SeedCashAsync(ctx, 60_000m);
        var aircraft = await TheAircraftAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(await RepositionAsync(ctx, aircraft.Id, "EGPH")));
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(await RepositionAsync(ctx, aircraft.Id, "EGSS")));

        var lines = await ctx.Db.LedgerTransactions
            .Where(t => t.AirlineId == ctx.Airline.Id && t.Category == LedgerCategory.AircraftRepositioning)
            .ToListAsync();
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal(-2_000m, l.Amount));
        Assert.Equal(56_000m, await CashBalanceAsync(ctx));
        Assert.Equal("EGSS", (await ctx.Db.FleetAircraft.SingleAsync(f => f.Id == aircraft.Id)).LocationIcao);
    }
}
