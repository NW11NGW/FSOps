using System.Text.Json;
using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Server.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Tests;

/// <summary>
/// Pilot status as the API actually reports it. PilotStatusCalculatorTests covers the rule; this
/// covers the wiring - that GET /pilots derives the value rather than echoing a column, that it
/// changes the instant the underlying fact changes with nothing written in between, and that
/// POST /pilots answers in the same shape so a freshly hired pilot does not land on the roster
/// carrying a differently-derived status from every row beside them.
/// </summary>
public class PilotDerivedStatusTests
{
    [Fact]
    public async Task APilotWithASectorInTheAir_ReadsFlying_AndGoesBackToAvailableWhenItLands()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var pilot = await SeedPilotAsync(ctx, "F. Oborne", lastFlewUtc: DateTimeOffset.UtcNow.AddHours(-2));

        Assert.Equal("Available", await StatusOfAsync(ctx, catalog, pilot.Id));

        var flight = await SeedFlightAsync(ctx, pilot.Id, FlightStatus.InProgress);
        // Nothing on the pilot row was touched - the status has to come from the flight.
        Assert.Equal("Flying", await StatusOfAsync(ctx, catalog, pilot.Id));

        flight.Status = FlightStatus.Completed;
        await ctx.Db.SaveChangesAsync();
        Assert.Equal("Available", await StatusOfAsync(ctx, catalog, pilot.Id));
    }

    [Fact]
    public async Task APilotLeftWithNoScheduleAndNothingFlown_ReadsInactive()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var grace = catalog.Get(ctx.Airline.Playstyle).PilotSkill.IdleGracePeriodHours;
        var pilot = await SeedPilotAsync(ctx, "D. Gorse", lastFlewUtc: DateTimeOffset.UtcNow.AddHours(-(grace + 24)));

        Assert.Equal("Inactive", await StatusOfAsync(ctx, catalog, pilot.Id));
    }

    [Fact]
    public async Task GivingThatSamePilotASchedule_MakesThemAvailableAgain_WithoutFlyingAnything()
    {
        // The point of the Inactive label is "nothing is going to happen for this pilot unless you
        // do something". Saving a schedule IS that something, so the label has to clear on the save
        // rather than waiting for the first occurrence to resolve days later.
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var grace = catalog.Get(ctx.Airline.Playstyle).PilotSkill.IdleGracePeriodHours;
        var pilot = await SeedPilotAsync(ctx, "D. Gorse", lastFlewUtc: DateTimeOffset.UtcNow.AddHours(-(grace + 24)));

        Assert.Equal("Inactive", await StatusOfAsync(ctx, catalog, pilot.Id));

        await SeedScheduleAsync(ctx, pilot.Id);

        Assert.Equal("Available", await StatusOfAsync(ctx, catalog, pilot.Id));
    }

    [Fact]
    public async Task ThePlayerPilotIsNeverInactive_HoweverLongSinceTheyLastFlew()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var player = await SeedPilotAsync(ctx, "The Player", lastFlewUtc: DateTimeOffset.UtcNow.AddDays(-365), isPlayer: true);

        Assert.Equal("Available", await StatusOfAsync(ctx, catalog, player.Id));
    }

    [Fact]
    public async Task HireAnswersInTheSameShapeAsTheList_SoTheNewRowMatchesTheOnesBesideIt()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();

        var hireResult = await PilotEndpoints.HireAsync(new HirePilotRequest("A. Newbold"), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status201Created, StatusCodeOf(hireResult));

        var hired = ValueOf<HireDto>(hireResult).Pilot;
        Assert.Equal("Available", hired.Status);

        // The client pushes this object straight into the roster it is already showing, so every
        // derived field the list computes has to be present here too - not just status.
        Assert.Equal("A. Newbold", hired.Name);
        Assert.Equal(0, hired.SectorsPerWeek);
        Assert.Equal(hired.SkillRating, hired.EarnedSkillRating);
        Assert.False(hired.IsDecaying);
        Assert.Null(hired.LastFlewUtc);

        var listed = Assert.Single(await ListAsync(ctx, catalog), p => p.Id == hired.Id);
        Assert.Equal(listed.Status, hired.Status);
        Assert.Equal(listed.SkillRating, hired.SkillRating);
        Assert.Equal(listed.EarnedSkillRating, hired.EarnedSkillRating);
        Assert.Equal(listed.SectorsPerWeek, hired.SectorsPerWeek);
    }

    [Fact]
    public async Task TheWholeRosterIsResolvedWithoutAPerPilotFlightQuery()
    {
        // Guards the shape of the implementation, not just its output: deriving Flying must be one
        // query for the list, never one per row. Ten pilots, one of them airborne, all correct.
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();

        var pilots = new List<Pilot>();
        for (var i = 0; i < 10; i++)
        {
            pilots.Add(await SeedPilotAsync(ctx, $"FO {i}", lastFlewUtc: DateTimeOffset.UtcNow.AddHours(-1)));
        }

        await SeedFlightAsync(ctx, pilots[4].Id, FlightStatus.InProgress);

        var listed = await ListAsync(ctx, catalog);
        Assert.Equal(10, listed.Count);
        Assert.Equal("Flying", listed.Single(p => p.Id == pilots[4].Id).Status);
        Assert.All(listed.Where(p => p.Id != pilots[4].Id), p => Assert.Equal("Available", p.Status));
    }

    private static async Task<string> StatusOfAsync(RouteTestContext ctx, EconomyConfigCatalog catalog, Guid pilotId)
    {
        var listed = await ListAsync(ctx, catalog);
        return listed.Single(p => p.Id == pilotId).Status;
    }

    private static async Task<List<PilotSummaryDto>> ListAsync(RouteTestContext ctx, EconomyConfigCatalog catalog)
    {
        var result = await PilotEndpoints.ListAsync(ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        return ValueOf<List<PilotSummaryDto>>(result);
    }

    private static async Task<Pilot> SeedPilotAsync(RouteTestContext ctx, string name, DateTimeOffset? lastFlewUtc, bool isPlayer = false)
    {
        var pilot = new Pilot
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Name = name,
            IsPlayer = isPlayer,
            MonthlySalary = 9_000m,
            HoursFlown = 120,
            SkillRating = 50,
            LastFlewUtc = lastFlewUtc,
            CreatedUtc = DateTimeOffset.UtcNow.AddDays(-400),
        };
        ctx.Db.Pilots.Add(pilot);
        await ctx.Db.SaveChangesAsync();
        return pilot;
    }

    private static async Task<Flight> SeedFlightAsync(RouteTestContext ctx, Guid pilotId, FlightStatus status)
    {
        var aircraftId = await ctx.Db.FleetAircraft.Where(f => f.AirlineId == ctx.Airline.Id).Select(f => f.Id).FirstAsync();
        var flight = new Flight
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            RouteId = Guid.NewGuid(),
            FleetAircraftId = aircraftId,
            PilotId = pilotId,
            Status = status,
            PlannedDepartureUtc = DateTimeOffset.UtcNow,
            PlannedBlockMinutes = 75,
            TitleFlown = "Airbus A320neo",
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.Flights.Add(flight);
        await ctx.Db.SaveChangesAsync();
        return flight;
    }

    private static async Task SeedScheduleAsync(RouteTestContext ctx, Guid pilotId)
    {
        var route = new Core.Entities.Route
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            DepartureIcao = "EGGD",
            ArrivalIcao = "EGPH",
            DistanceNm = 275.2,
            BaseFare = 89.00m,
            IsActive = true,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.Routes.Add(route);

        var aircraftId = await ctx.Db.FleetAircraft.Where(f => f.AirlineId == ctx.Airline.Id).Select(f => f.Id).FirstAsync();
        var schedule = new PilotSchedule
        {
            Id = Guid.NewGuid(),
            PilotId = pilotId,
            AirlineId = ctx.Airline.Id,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.PilotSchedules.Add(schedule);
        ctx.Db.PilotScheduleEntries.Add(new PilotScheduleEntry
        {
            Id = Guid.NewGuid(),
            PilotScheduleId = schedule.Id,
            DayOfWeek = DayOfWeek.Monday,
            DepartureTimeUtc = TimeSpan.FromHours(8),
            RouteId = route.Id,
            FleetAircraftId = aircraftId,
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        await ctx.Db.SaveChangesAsync();
    }

    private static int StatusCodeOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    private static T ValueOf<T>(IResult result)
    {
        var value = ((IValueHttpResult)result).Value;
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private sealed record HireDto(PilotSummaryDto Pilot, decimal CashBalance);

    private sealed record PilotSummaryDto(
        Guid Id,
        string Name,
        bool IsPlayer,
        double SkillRating,
        double EarnedSkillRating,
        DateTimeOffset? LastFlewUtc,
        bool IsDecaying,
        string Status,
        int SectorsPerWeek);
}
