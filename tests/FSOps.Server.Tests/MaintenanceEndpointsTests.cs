using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Server.Endpoints;
using FSOps.Server.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Tests;

/// <summary>
/// "Perform maintenance now" - a button on the Fleet page letting the player trigger an A-check or
/// C-check early, at a moment of their choosing:
/// full cost, full downtime, forfeits remaining hours, blocked while airborne or already grounded,
/// and shows the cost/downtime/affected schedules before the player commits. Drives
/// MaintenanceEndpoints' handlers directly against an isolated in-memory RouteTestContext with a
/// FakeClock, same convention as FleetDisposalEndpointsTests.
/// </summary>
public class MaintenanceEndpointsTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static int StatusCodeOf(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode!.Value;

    private static T ValueOf<T>(IResult result) => (T)Assert.IsAssignableFrom<IValueHttpResult>(result).Value!;

    [Fact]
    public async Task Quote_ReturnsFullCostAndDowntimeForBothCheckTypes()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var economyConfig = catalog.Get(AirlinePlaystyle.Casual);

        var aircraft = await ctx.Db.FleetAircraft.FirstAsync();
        aircraft.HoursSinceACheck = 50;
        aircraft.HoursSinceCCheck = 50;
        await ctx.Db.SaveChangesAsync();

        var result = await MaintenanceEndpoints.MaintenanceQuoteAsync(aircraft.Id, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

        var quote = ValueOf<MaintenanceQuoteResponse>(result);
        Assert.True(quote.CanPerform);
        Assert.Null(quote.BlockReason);
        Assert.Equal(2, quote.Quotes.Count);

        var aCheck = quote.Quotes.Single(q => q.Type == "ACheck");
        Assert.Equal(economyConfig.Maintenance.ACheckCost, aCheck.Cost);
        Assert.Equal(economyConfig.Maintenance.ACheckDowntimeHours, aCheck.DowntimeHours);
        Assert.Equal(450, aCheck.HoursRemainingUntilNatural);

        var cCheck = quote.Quotes.Single(q => q.Type == "CCheck");
        Assert.Equal(economyConfig.Maintenance.CCheckCost, cCheck.Cost);
        Assert.Equal(economyConfig.Maintenance.CCheckDowntimeHours, cCheck.DowntimeHours);
    }

    [Fact]
    public async Task Quote_BlockedWhileAirborne()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var aircraft = await ctx.Db.FleetAircraft.FirstAsync();
        aircraft.Status = FleetAircraftStatus.InFlight;
        await ctx.Db.SaveChangesAsync();

        var result = await MaintenanceEndpoints.MaintenanceQuoteAsync(aircraft.Id, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        var quote = ValueOf<MaintenanceQuoteResponse>(result);

        Assert.False(quote.CanPerform);
        Assert.Contains("flying", quote.BlockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PerformNow_ChargesFullCost_GroundsTheAircraft_ForfeitsRemainingHours_PostsLedgerAndEvent()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var economyConfig = catalog.Get(AirlinePlaystyle.Casual);
        var clock = new FakeClock(Base);

        var aircraft = await ctx.Db.FleetAircraft.FirstAsync();
        aircraft.HoursSinceACheck = 50; // well short of the natural 500h threshold
        aircraft.HoursSinceCCheck = 50;
        aircraft.ConditionPercent = 80;
        await ctx.Db.SaveChangesAsync();

        var request = new PerformMaintenanceRequest(MaintenanceEventType.ACheck, economyConfig.Maintenance.ACheckCost, economyConfig.Maintenance.ACheckDowntimeHours);
        var result = await MaintenanceEndpoints.PerformMaintenanceAsync(aircraft.Id, request, ctx.Db, ctx.CurrentUser, catalog, clock, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

        var updated = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == aircraft.Id);
        Assert.Equal(FleetAircraftStatus.InMaintenance, updated.Status);
        Assert.Equal(Base.AddHours(economyConfig.Maintenance.ACheckDowntimeHours), updated.GroundedUntilUtc);
        // Forfeited: the 50 hours already accrued since the last A-check are gone, same as a
        // naturally-triggered check.
        Assert.Equal(0, updated.HoursSinceACheck);

        var maintenanceEvent = await ctx.Db.MaintenanceEvents.AsNoTracking().SingleAsync(m => m.FleetAircraftId == aircraft.Id);
        Assert.Equal(MaintenanceEventType.ACheck, maintenanceEvent.Type);
        Assert.Equal(economyConfig.Maintenance.ACheckCost, maintenanceEvent.Cost);

        var ledgerLine = await ctx.Db.LedgerTransactions.AsNoTracking().SingleAsync(t => t.AirlineId == ctx.Airline.Id && t.Category == LedgerCategory.Maintenance);
        Assert.Equal(-economyConfig.Maintenance.ACheckCost, ledgerLine.Amount);
        Assert.Contains("performed early", ledgerLine.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PerformNow_BlockedWhileAirborne_PostsNothing()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var economyConfig = catalog.Get(AirlinePlaystyle.Casual);
        var clock = new FakeClock(Base);

        var aircraft = await ctx.Db.FleetAircraft.FirstAsync();
        aircraft.Status = FleetAircraftStatus.InFlight;
        await ctx.Db.SaveChangesAsync();

        var request = new PerformMaintenanceRequest(MaintenanceEventType.ACheck, economyConfig.Maintenance.ACheckCost, economyConfig.Maintenance.ACheckDowntimeHours);
        var result = await MaintenanceEndpoints.PerformMaintenanceAsync(aircraft.Id, request, ctx.Db, ctx.CurrentUser, catalog, clock, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
        Assert.Empty(await ctx.Db.MaintenanceEvents.ToListAsync());
        Assert.Empty(await ctx.Db.LedgerTransactions.Where(t => t.Category == LedgerCategory.Maintenance).ToListAsync());

        var updated = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == aircraft.Id);
        Assert.Equal(FleetAircraftStatus.InFlight, updated.Status);
    }

    [Fact]
    public async Task PerformNow_BlockedWhileAlreadyInMaintenance()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var economyConfig = catalog.Get(AirlinePlaystyle.Casual);
        var clock = new FakeClock(Base);

        var aircraft = await ctx.Db.FleetAircraft.FirstAsync();
        aircraft.Status = FleetAircraftStatus.InMaintenance;
        aircraft.GroundedUntilUtc = Base.AddDays(1);
        await ctx.Db.SaveChangesAsync();

        var request = new PerformMaintenanceRequest(MaintenanceEventType.ACheck, economyConfig.Maintenance.ACheckCost, economyConfig.Maintenance.ACheckDowntimeHours);
        var result = await MaintenanceEndpoints.PerformMaintenanceAsync(aircraft.Id, request, ctx.Db, ctx.CurrentUser, catalog, clock, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusCodeOf(result));
    }

    [Fact]
    public async Task Quote_ListsAffectedPilotSchedules()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var aircraft = await ctx.Db.FleetAircraft.FirstAsync();

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
            CreatedUtc = Base,
        };
        ctx.Db.Routes.Add(route);

        var pilot = new Pilot
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Name = "Rostered Pilot",
            IsPlayer = false,
            MonthlySalary = 9000m,
            SkillRating = 50,
            CreatedUtc = Base,
        };
        ctx.Db.Pilots.Add(pilot);

        var schedule = new PilotSchedule { Id = Guid.NewGuid(), PilotId = pilot.Id, AirlineId = ctx.Airline.Id, CreatedUtc = Base };
        ctx.Db.PilotSchedules.Add(schedule);
        ctx.Db.PilotScheduleEntries.Add(new PilotScheduleEntry
        {
            Id = Guid.NewGuid(),
            PilotScheduleId = schedule.Id,
            DayOfWeek = DayOfWeek.Monday,
            DepartureTimeUtc = new TimeSpan(9, 30, 0),
            RouteId = route.Id,
            FleetAircraftId = aircraft.Id,
            CreatedUtc = Base,
        });
        await ctx.Db.SaveChangesAsync();

        var result = await MaintenanceEndpoints.MaintenanceQuoteAsync(aircraft.Id, ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        var quote = ValueOf<MaintenanceQuoteResponse>(result);

        var affected = Assert.Single(quote.AffectedSchedules);
        Assert.Equal(pilot.Id, affected.PilotId);
        Assert.Equal("Rostered Pilot", affected.PilotName);
        Assert.Equal("Monday", affected.DayOfWeek);
        Assert.Equal("EGGD-EGPH", affected.RouteLabel);
    }
}
