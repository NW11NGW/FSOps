using System.Collections;
using System.Reflection;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Server.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Tests;

/// <summary>
/// Routes are always a there-and-back pair (see docs/PLAN.md and RouteEndpoints.CreateAsync's
/// summary): POST /routes must create both legs atomically, DELETE must remove both, and a
/// pre-existing single-leg route (from before pairing was mandatory) must be handled without
/// crashing or losing data. These tests call the endpoint handlers directly against an isolated
/// in-memory database (see RouteTestContext) rather than over HTTP, since that's enough to
/// exercise the pairing logic without needing a full running server.
/// </summary>
public class RoutePairingTests
{
    private static T? GetProp<T>(object obj, string name)
    {
        var property = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
            ?? throw new InvalidOperationException($"Property '{name}' not found on {obj.GetType()}.");
        return (T?)property.GetValue(obj);
    }

    private static int StatusCodeOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static object ValueOf(IResult result) => ((IValueHttpResult)result).Value!;

    [Fact]
    public async Task CreateAsync_CreatesBothLegs_WithPairedFlightNumbersAndSharedFare()
    {
        using var ctx = await RouteTestContext.CreateAsync();

        var request = new CreateRouteRequest("EGGD", "EGPH", null, null, null);
        var result = await RouteEndpoints.CreateAsync(request, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(result));

        var value = ValueOf(result);
        var outbound = GetProp<object>(value, "outbound")!;
        var inbound = GetProp<object>(value, "inbound")!;

        Assert.Equal("EGGD", GetProp<string>(outbound, "DepartureIcao"));
        Assert.Equal("EGPH", GetProp<string>(outbound, "ArrivalIcao"));
        Assert.Equal("EGPH", GetProp<string>(inbound, "DepartureIcao"));
        Assert.Equal("EGGD", GetProp<string>(inbound, "ArrivalIcao"));

        var outboundFlightNumber = int.Parse(GetProp<string>(outbound, "FlightNumber")!);
        var inboundFlightNumber = int.Parse(GetProp<string>(inbound, "FlightNumber")!);
        Assert.True(outboundFlightNumber % 2 == 1, "Outbound flight numbers should be odd.");
        Assert.True(inboundFlightNumber % 2 == 0, "Return flight numbers should be even.");
        Assert.True(inboundFlightNumber > outboundFlightNumber);

        Assert.Equal(GetProp<decimal>(outbound, "BaseFare"), GetProp<decimal>(inbound, "BaseFare"));

        var outboundId = GetProp<Guid>(outbound, "Id");
        var inboundId = GetProp<Guid>(inbound, "Id");
        Assert.Equal(inboundId, GetProp<Guid?>(outbound, "returnRouteId"));
        Assert.Equal(outboundId, GetProp<Guid?>(inbound, "returnRouteId"));

        Assert.Equal(2, await ctx.Db.Routes.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_SameDirectionTwice_Returns409AndCreatesNothingMore()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var request = new CreateRouteRequest("EGGD", "EGPH", null, null, null);

        await RouteEndpoints.CreateAsync(request, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);
        var second = await RouteEndpoints.CreateAsync(request, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status409Conflict, StatusCodeOf(second));
        Assert.Equal(2, await ctx.Db.Routes.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_WhenReverseLegAlreadyExists_ReusesItInsteadOfDuplicating()
    {
        using var ctx = await RouteTestContext.CreateAsync();

        // A legacy single-leg route, as if it was created before pairing was mandatory.
        var legacy = new Route
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            DepartureIcao = "EGPH",
            ArrivalIcao = "EGGD",
            FlightNumber = "150",
            DistanceNm = 280,
            BaseFare = 90m,
            IsActive = true,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.Routes.Add(legacy);
        await ctx.Db.SaveChangesAsync();

        var request = new CreateRouteRequest("EGGD", "EGPH", null, null, null);
        var result = await RouteEndpoints.CreateAsync(request, ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(result));
        Assert.Equal(2, await ctx.Db.Routes.CountAsync());

        var inbound = GetProp<object>(ValueOf(result), "inbound")!;
        Assert.Equal(legacy.Id, GetProp<Guid>(inbound, "Id"));
    }

    [Fact]
    public async Task DeleteAsync_DeletesBothLegsAtomically()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var created = await RouteEndpoints.CreateAsync(
            new CreateRouteRequest("EGGD", "EGPH", null, null, null), ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);
        var createdValue = ValueOf(created);
        var outboundId = GetProp<Guid>(GetProp<object>(createdValue, "outbound")!, "Id");
        var inboundId = GetProp<Guid>(GetProp<object>(createdValue, "inbound")!, "Id");

        var deleteResult = await RouteEndpoints.DeleteAsync(outboundId, ctx.Db, ctx.CurrentUser, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(deleteResult));
        var deletedIds = GetProp<Guid[]>(ValueOf(deleteResult), "deletedRouteIds")!;
        Assert.Equal(2, deletedIds.Length);
        Assert.Contains(outboundId, deletedIds);
        Assert.Contains(inboundId, deletedIds);

        // The soft-delete query filter hides both rows from normal queries...
        Assert.Equal(0, await ctx.Db.Routes.CountAsync());
        // ...but they're still there with DeletedUtc set, not gone.
        Assert.Equal(2, await ctx.Db.Routes.IgnoreQueryFilters().CountAsync());
        Assert.True(await ctx.Db.Routes.IgnoreQueryFilters().AllAsync(r => r.DeletedUtc != null));
    }

    [Fact]
    public async Task DeleteAsync_LegacySingleLegWithNoPartner_DeletesJustThatOne()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var legacy = new Route
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            DepartureIcao = "EGGD",
            ArrivalIcao = "EGPH",
            FlightNumber = "401",
            DistanceNm = 280,
            BaseFare = 95m,
            IsActive = true,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.Routes.Add(legacy);
        await ctx.Db.SaveChangesAsync();

        var result = await RouteEndpoints.DeleteAsync(legacy.Id, ctx.Db, ctx.CurrentUser, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));
        var deletedIds = GetProp<Guid[]>(ValueOf(result), "deletedRouteIds")!;
        Assert.Single(deletedIds);
        Assert.Equal(legacy.Id, deletedIds[0]);
        Assert.Equal(0, await ctx.Db.Routes.CountAsync());
    }

    [Fact]
    public async Task ListAsync_BackfillsMissingReturnLeg_ForLegacySingleLegRoutes()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var legacy = new Route
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            DepartureIcao = "EGGD",
            ArrivalIcao = "EGPH",
            FlightNumber = "401",
            DistanceNm = 280,
            BaseFare = 95m,
            IsActive = true,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.Routes.Add(legacy);
        await ctx.Db.SaveChangesAsync();

        Assert.Equal(1, await ctx.Db.Routes.CountAsync());

        var result = await RouteEndpoints.ListAsync(ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));
        // The missing reverse leg was created as a side effect of listing - this is exactly the
        // "must not crash, must not silently delete anything" legacy-repair path.
        Assert.Equal(2, await ctx.Db.Routes.CountAsync());

        var list = ((IEnumerable)ValueOf(result)).Cast<object>().ToList();
        Assert.Equal(2, list.Count);
        Assert.All(list, item => Assert.NotNull(GetProp<Guid?>(item, "returnRouteId")));

        var repaired = list.Single(item => GetProp<string>(item, "DepartureIcao") == "EGPH");
        Assert.Equal("EGGD", GetProp<string>(repaired, "ArrivalIcao"));
        Assert.Equal(95m, GetProp<decimal>(repaired, "BaseFare"));
    }

    [Fact]
    public async Task ListAsync_NoCrash_WhenAirlineHasNoRoutes()
    {
        using var ctx = await RouteTestContext.CreateAsync();

        var result = await RouteEndpoints.ListAsync(ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));
        Assert.Empty((IEnumerable)ValueOf(result));
    }

    /// <summary>
    /// The owner's real situation: legacy routes were saved with no flight number at all, and a
    /// later backfill created their return legs with a number but left the original leg blank.
    /// ListAsync's backfill must give the blank leg a number too - adjacent to its partner's - and
    /// must leave the partner's already-set number untouched.
    /// </summary>
    [Fact]
    public async Task ListAsync_AssignsFlightNumber_ToLegWithNoneWhosePartnerAlreadyHasOne()
    {
        using var ctx = await RouteTestContext.CreateAsync();

        var original = new Route
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            DepartureIcao = "EGGD",
            ArrivalIcao = "EGPH",
            FlightNumber = null,
            DistanceNm = 280,
            BaseFare = 95m,
            IsActive = true,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        var autoCreatedReturn = new Route
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            DepartureIcao = "EGPH",
            ArrivalIcao = "EGGD",
            FlightNumber = "501",
            DistanceNm = 280,
            BaseFare = 95m,
            IsActive = true,
            CreatedUtc = DateTimeOffset.UtcNow.AddSeconds(1),
        };
        ctx.Db.Routes.AddRange(original, autoCreatedReturn);
        await ctx.Db.SaveChangesAsync();

        var result = await RouteEndpoints.ListAsync(ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

        var list = ((IEnumerable)ValueOf(result)).Cast<object>().ToList();
        Assert.Equal(2, list.Count);

        var repairedOriginal = list.Single(item => GetProp<Guid>(item, "Id") == original.Id);
        var untouchedReturn = list.Single(item => GetProp<Guid>(item, "Id") == autoCreatedReturn.Id);

        // The already-set number must not be renumbered.
        Assert.Equal("501", GetProp<string>(untouchedReturn, "FlightNumber"));

        // The blank leg gets a number, adjacent to its partner's, not an unrelated one.
        var newNumber = GetProp<string>(repairedOriginal, "FlightNumber");
        Assert.NotNull(newNumber);
        Assert.Equal(502, int.Parse(newNumber!));

        // Idempotent: repairing an already-fully-numbered pair a second time changes nothing.
        var second = await RouteEndpoints.ListAsync(ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);
        var secondList = ((IEnumerable)ValueOf(second)).Cast<object>().ToList();
        Assert.Equal(2, secondList.Count);
        Assert.Equal("501", GetProp<string>(secondList.Single(item => GetProp<Guid>(item, "Id") == autoCreatedReturn.Id), "FlightNumber"));
        Assert.Equal("502", GetProp<string>(secondList.Single(item => GetProp<Guid>(item, "Id") == original.Id), "FlightNumber"));
    }

    /// <summary>
    /// A pair where neither leg ever got a flight number (the oldest possible legacy data): both
    /// get numbered in one backfill pass, the earlier-created leg as the odd outbound and its
    /// partner as the next even return, matching the same convention new routes are created with.
    /// </summary>
    [Fact]
    public async Task ListAsync_AssignsFlightNumbers_ToPairWhereNeitherLegHasOne()
    {
        using var ctx = await RouteTestContext.CreateAsync();

        var outbound = new Route
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            DepartureIcao = "EGGD",
            ArrivalIcao = "EGSS",
            FlightNumber = null,
            DistanceNm = 250,
            BaseFare = 80m,
            IsActive = true,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        var inbound = new Route
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            DepartureIcao = "EGSS",
            ArrivalIcao = "EGGD",
            FlightNumber = null,
            DistanceNm = 250,
            BaseFare = 80m,
            IsActive = true,
            CreatedUtc = DateTimeOffset.UtcNow.AddSeconds(1),
        };
        ctx.Db.Routes.AddRange(outbound, inbound);
        await ctx.Db.SaveChangesAsync();

        var result = await RouteEndpoints.ListAsync(ctx.Db, ctx.CurrentUser, EconomyConfigCatalog.Default(), CancellationToken.None);
        var list = ((IEnumerable)ValueOf(result)).Cast<object>().ToList();

        var repairedOutbound = list.Single(item => GetProp<Guid>(item, "Id") == outbound.Id);
        var repairedInbound = list.Single(item => GetProp<Guid>(item, "Id") == inbound.Id);

        var outboundNumber = int.Parse(GetProp<string>(repairedOutbound, "FlightNumber")!);
        var inboundNumber = int.Parse(GetProp<string>(repairedInbound, "FlightNumber")!);

        Assert.True(outboundNumber % 2 == 1, "The earlier-created leg should become the odd outbound.");
        Assert.True(inboundNumber % 2 == 0, "The later-created leg should become the even return.");
        Assert.True(inboundNumber > outboundNumber);
    }
}
