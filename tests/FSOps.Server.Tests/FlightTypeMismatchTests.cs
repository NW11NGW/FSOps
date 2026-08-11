using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Server.Endpoints;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using FSOps.Sim;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSOps.Server.Tests;

/// <summary>
/// Regression coverage for the defect where "no sim connected" was reported as "wrong aircraft".
/// Aircraft type matching is family-level and purely informational (docs/PLAN.md) - a genuine
/// mismatch must still be flagged, but an absent TITLE/ATC MODEL (sim not connected, or no aircraft
/// loaded yet) must produce <c>Flight.TypeMismatch == null</c> (unknown), never <c>true</c>.
/// Drives <c>FlightEndpoints.StartAsync</c> for real, exactly like FlightLedgerPostingTests, using
/// the same isolated in-memory RouteTestContext - never the real database.
/// </summary>
public class FlightTypeMismatchTests
{
    [Fact]
    public async Task StartAsync_NoSimConnected_TypeMismatchIsNullNotTrue()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = await SeedRouteAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog, new NoOpSimSource());

        var startResult = await FlightEndpoints.StartAsync(
            new StartFlightRequest(route.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startResult));

        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == route.Id);

        // The honest answer with nothing to compare against is "we don't know" - not "wrong
        // aircraft". Collapsing this to true is exactly the defect being fixed here.
        Assert.Null(flight.TypeMismatch);

        // An unknown result must never be reported to the player as a flagged mismatch - no
        // Mismatch FlightEvent should have been written.
        var events = await ctx.Db.FlightEvents.Where(e => e.FlightId == flight.Id).ToListAsync();
        Assert.DoesNotContain(events, e => e.Type == FlightEventType.Mismatch);
    }

    [Fact]
    public async Task StartAsync_GenuineFamilyMismatch_TypeMismatchIsTrueAndEventIsRecorded()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = await SeedRouteAsync(ctx);
        var economyConfigCatalog = EconomyConfigCatalog.Default();

        // RouteTestContext's AircraftType.MatchPatterns is "[]" (matches nothing), so any reported
        // aircraft identity is a mismatch by construction - the sim DID tell us something, it just
        // isn't the expected family.
        var simSource = new StubSimSource(new AircraftIdentity("Boeing 737-800 Asobo", "B738", "B738"));
        var (lifecycle, telemetry) = CreateLifecycleAndTelemetry(ctx, economyConfigCatalog, simSource);

        var startResult = await FlightEndpoints.StartAsync(
            new StartFlightRequest(route.Id, null), ctx.Db, ctx.CurrentUser, lifecycle, telemetry, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(startResult));

        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == route.Id);

        // A genuine, informational mismatch must still be flagged - fixing the false positive must
        // not silently lose the real signal.
        Assert.True(flight.TypeMismatch);
        Assert.Equal("Boeing 737-800 Asobo", flight.TitleFlown);

        var events = await ctx.Db.FlightEvents.Where(e => e.FlightId == flight.Id).ToListAsync();
        Assert.Contains(events, e => e.Type == FlightEventType.Mismatch);
    }

    private static int StatusCodeOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    /// <summary>Seeds a route and the pilot FlightEndpoints.StartAsync requires - neither is part
    /// of RouteTestContext's own baseline seed. Mirrors FlightLedgerPostingTests.SeedRouteAsync.</summary>
    private static async Task<Route> SeedRouteAsync(RouteTestContext ctx)
    {
        var route = new Route
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            DepartureIcao = "EGGD",
            ArrivalIcao = "EGPH",
            FlightNumber = "101",
            DistanceNm = 280,
            BaseFare = 90m,
            IsActive = true,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.Routes.Add(route);

        if (!await ctx.Db.Pilots.AnyAsync(p => p.AirlineId == ctx.Airline.Id))
        {
            ctx.Db.Pilots.Add(new Pilot
            {
                Id = Guid.NewGuid(),
                AirlineId = ctx.Airline.Id,
                Name = "Test Pilot",
                IsPlayer = true,
                MonthlySalary = 9000m,
                SkillRating = 50,
                CreatedUtc = DateTimeOffset.UtcNow,
            });
        }

        await ctx.Db.SaveChangesAsync();
        return route;
    }

    /// <summary>Same shape as FlightLedgerPostingTests.CreateLifecycleAndTelemetry, but takes the
    /// ISimSource explicitly so each test can control what CurrentAircraft reports.</summary>
    private static (FlightLifecycleService Lifecycle, SimTelemetryService Telemetry) CreateLifecycleAndTelemetry(
        RouteTestContext ctx, EconomyConfigCatalog economyConfigCatalog, ISimSource simSource)
    {
        var services = new ServiceCollection();
        services.AddDbContext<FSOps.Data.FsOpsDbContext>(o => o.UseSqlite(ctx.Connection));
        var provider = services.BuildServiceProvider();

        var telemetry = new SimTelemetryService(simSource, new NoOpHubContext(), NullLogger<SimTelemetryService>.Instance);
        var lifecycle = new FlightLifecycleService(
            provider.GetRequiredService<IServiceScopeFactory>(), telemetry, new NoOpHubContext(),
            economyConfigCatalog, NullLogger<FlightLifecycleService>.Instance);
        return (lifecycle, telemetry);
    }
}
