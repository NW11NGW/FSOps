using System.Reflection;
using FSOps.Server.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Tests;

/// <summary>
/// A route is always a there-and-back pair stored as two directional rows (see
/// RouteEndpoints.CreateAsync), but the owner sees a pair as *one* route. GET /airline/summary's
/// routeCount must reflect that - three round trips should report 3, not 6 - while GET /routes
/// still returns all 6 individual legs. Uses the same isolated in-memory RouteTestContext as
/// RoutePairingTests, never the real database.
/// </summary>
public class AirlineSummaryRouteCountTests
{
    private static T? GetProp<T>(object obj, string name)
    {
        var property = obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
            ?? throw new InvalidOperationException($"Property '{name}' not found on {obj.GetType()}.");
        return (T?)property.GetValue(obj);
    }

    private static object ValueOf(IResult result) => ((IValueHttpResult)result).Value!;

    [Fact]
    public async Task GetSummaryAsync_ThreeRoundTrips_ReportsRouteCountThree_WhileRoutesTableHasSixLegs()
    {
        using var ctx = await RouteTestContext.CreateAsync();

        await RouteEndpoints.CreateAsync(new CreateRouteRequest("EGGD", "EGPH", null, null, null), ctx.Db, ctx.CurrentUser, CancellationToken.None);
        await RouteEndpoints.CreateAsync(new CreateRouteRequest("EGGD", "EGSS", null, null, null), ctx.Db, ctx.CurrentUser, CancellationToken.None);
        await RouteEndpoints.CreateAsync(new CreateRouteRequest("EGGD", "EGPF", null, null, null), ctx.Db, ctx.CurrentUser, CancellationToken.None);

        // Six directional rows in the table...
        Assert.Equal(6, await ctx.Db.Routes.CountAsync());

        // ...but the summary reports three round trips.
        var summaryResult = await AirlineEndpoints.GetSummaryAsync(ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var summary = ValueOf(summaryResult);
        Assert.Equal(3, GetProp<int>(summary, "routeCount"));
    }

    [Fact]
    public async Task GetSummaryAsync_UnpairedLegacyLeg_StillCountsAsOneRoute()
    {
        using var ctx = await RouteTestContext.CreateAsync();

        // A legacy single leg with no reverse yet - the summary must not report 0 or crash just
        // because ListAsync's self-heal hasn't run against this endpoint.
        ctx.Db.Routes.Add(new FSOps.Core.Entities.Route
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
        });
        await ctx.Db.SaveChangesAsync();

        var summaryResult = await AirlineEndpoints.GetSummaryAsync(ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var summary = ValueOf(summaryResult);
        Assert.Equal(1, GetProp<int>(summary, "routeCount"));
    }

    [Fact]
    public async Task GetSummaryAsync_NoRoutes_ReportsZero()
    {
        using var ctx = await RouteTestContext.CreateAsync();

        var summaryResult = await AirlineEndpoints.GetSummaryAsync(ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var summary = ValueOf(summaryResult);
        Assert.Equal(0, GetProp<int>(summary, "routeCount"));
    }
}
