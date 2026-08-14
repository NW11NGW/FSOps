using System.Text.Json;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Server.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Tests;

/// <summary>
/// A stalled weekly pattern has to reach the player without them doing anything: before this, the
/// only trace of a schedule that had quietly stopped flying was an advisory shown if they happened
/// to open the builder and save. These cover the reporting path - that GET /pilots carries it, that
/// it is attributed to every pilot the stalled airframe belongs to, and that it disappears the
/// moment the aircraft is back.
/// </summary>
public class ScheduleStallReportingTests
{
    [Fact]
    public async Task APatternWhoseAircraftIsParkedSomewhereItNeverVisits_IsReportedOnTheRoster()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await SeedPilotAsync(ctx, "F. Oborne");
        var aircraftId = await SoleAircraftIdAsync(ctx);
        await SeedWeekAsync(ctx, pilot.Id, aircraftId, ("EGGD", "EGPH"), ("EGPH", "EGGD"));

        await MoveAircraftAsync(ctx, aircraftId, "EGPF");

        var stall = Assert.Single(await StallsForAsync(ctx, catalog, pilot.Id));
        Assert.Equal("G-TEST", stall.Registration);
        Assert.Equal("EGPF", stall.LocationIcao);
        Assert.Equal("EGGD", stall.PatternStartIcao);
        Assert.Contains("Fleet page", stall.Message);
    }

    [Fact]
    public async Task NothingIsReportedWhileThePatternCanStillRepairItself()
    {
        // Parked at EGPH, which the week does depart from - the EGPH -> EGGD leg flies and the loop
        // resynchronises. The player is told nothing, because nothing is wrong.
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await SeedPilotAsync(ctx, "F. Oborne");
        var aircraftId = await SoleAircraftIdAsync(ctx);
        await SeedWeekAsync(ctx, pilot.Id, aircraftId, ("EGGD", "EGPH"), ("EGPH", "EGGD"));

        await MoveAircraftAsync(ctx, aircraftId, "EGPH");

        Assert.Empty(await StallsForAsync(ctx, catalog, pilot.Id));
    }

    [Fact]
    public async Task BringingTheAircraftBack_ClearsTheNoticeWithNothingElseTouched()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await SeedPilotAsync(ctx, "F. Oborne");
        var aircraftId = await SoleAircraftIdAsync(ctx);
        await SeedWeekAsync(ctx, pilot.Id, aircraftId, ("EGGD", "EGPH"), ("EGPH", "EGGD"));

        await MoveAircraftAsync(ctx, aircraftId, "EGPF");
        Assert.NotEmpty(await StallsForAsync(ctx, catalog, pilot.Id));

        await MoveAircraftAsync(ctx, aircraftId, "EGGD");
        Assert.Empty(await StallsForAsync(ctx, catalog, pilot.Id));
    }

    [Fact]
    public async Task AStalledAircraftIsReportedToEveryPilotScheduledOnIt()
    {
        // An airframe's chain spans every pilot that touches it, so it has stopped both their weeks
        // equally - and either row is a reasonable place for the player to notice.
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var first = await SeedPilotAsync(ctx, "F. Oborne");
        var second = await SeedPilotAsync(ctx, "A. Newbold");
        var aircraftId = await SoleAircraftIdAsync(ctx);

        await SeedWeekAsync(ctx, first.Id, aircraftId, ("EGGD", "EGPH"));
        await SeedWeekAsync(ctx, second.Id, aircraftId, ("EGPH", "EGGD"));

        await MoveAircraftAsync(ctx, aircraftId, "EGPF");

        Assert.Single(await StallsForAsync(ctx, catalog, first.Id));
        Assert.Single(await StallsForAsync(ctx, catalog, second.Id));
    }

    [Fact]
    public async Task APilotWithNoScheduleIsNeverReported()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var scheduled = await SeedPilotAsync(ctx, "F. Oborne");
        var unscheduled = await SeedPilotAsync(ctx, "A. Newbold");
        var aircraftId = await SoleAircraftIdAsync(ctx);
        await SeedWeekAsync(ctx, scheduled.Id, aircraftId, ("EGGD", "EGPH"));

        await MoveAircraftAsync(ctx, aircraftId, "EGPF");

        Assert.NotEmpty(await StallsForAsync(ctx, catalog, scheduled.Id));
        Assert.Empty(await StallsForAsync(ctx, catalog, unscheduled.Id));
    }

    [Fact]
    public async Task AnAirlineWithNoSchedulesAtAll_ReportsNothing()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await SeedPilotAsync(ctx, "F. Oborne");

        Assert.Empty(await StallsForAsync(ctx, catalog, pilot.Id));
    }

    private static async Task<List<StallDto>> StallsForAsync(RouteTestContext ctx, EconomyConfigCatalog catalog, Guid pilotId)
    {
        var result = await PilotEndpoints.ListAsync(ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, ((IStatusCodeHttpResult)result).StatusCode ?? 0);

        var value = ((IValueHttpResult)result).Value;
        var json = JsonSerializer.Serialize(value);
        var pilots = JsonSerializer.Deserialize<List<PilotRowDto>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return pilots.Single(p => p.Id == pilotId).ScheduleStalls;
    }

    private static async Task<Pilot> SeedPilotAsync(RouteTestContext ctx, string name)
    {
        var pilot = new Pilot
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Name = name,
            IsPlayer = false,
            MonthlySalary = 9_000m,
            SkillRating = 50,
            LastFlewUtc = DateTimeOffset.UtcNow.AddHours(-4),
            CreatedUtc = DateTimeOffset.UtcNow.AddDays(-30),
        };
        ctx.Db.Pilots.Add(pilot);
        await ctx.Db.SaveChangesAsync();
        return pilot;
    }

    private static async Task<Guid> SoleAircraftIdAsync(RouteTestContext ctx) =>
        await ctx.Db.FleetAircraft.Where(f => f.AirlineId == ctx.Airline.Id).Select(f => f.Id).SingleAsync();

    private static async Task MoveAircraftAsync(RouteTestContext ctx, Guid aircraftId, string icao)
    {
        var aircraft = await ctx.Db.FleetAircraft.SingleAsync(f => f.Id == aircraftId);
        aircraft.LocationIcao = icao;
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
    }

    /// <summary>Saves a schedule straight to the database rather than through PUT /schedule - the
    /// point here is what the roster reports about an ALREADY-SAVED week, and going through the
    /// validator would refuse some of these fixtures for reasons that are not what is under test.</summary>
    private static async Task SeedWeekAsync(
        RouteTestContext ctx, Guid pilotId, Guid aircraftId, params (string Departure, string Arrival)[] legs)
    {
        var schedule = new PilotSchedule
        {
            Id = Guid.NewGuid(),
            PilotId = pilotId,
            AirlineId = ctx.Airline.Id,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.PilotSchedules.Add(schedule);

        var hour = 6;
        foreach (var (departure, arrival) in legs)
        {
            var route = new Core.Entities.Route
            {
                Id = Guid.NewGuid(),
                AirlineId = ctx.Airline.Id,
                DepartureIcao = departure,
                ArrivalIcao = arrival,
                DistanceNm = 275.2,
                BaseFare = 89.00m,
                IsActive = true,
                CreatedUtc = DateTimeOffset.UtcNow,
            };
            ctx.Db.Routes.Add(route);

            ctx.Db.PilotScheduleEntries.Add(new PilotScheduleEntry
            {
                Id = Guid.NewGuid(),
                PilotScheduleId = schedule.Id,
                DayOfWeek = DayOfWeek.Monday,
                DepartureTimeUtc = TimeSpan.FromHours(hour),
                RouteId = route.Id,
                FleetAircraftId = aircraftId,
                CreatedUtc = DateTimeOffset.UtcNow,
            });

            hour += 4;
        }

        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
    }

    private sealed record PilotRowDto(Guid Id, string Name, List<StallDto> ScheduleStalls);

    private sealed record StallDto(
        Guid FleetAircraftId, string Registration, string LocationIcao, string PatternStartIcao, string Message);
}
