using System.Text.Json;
using FSOps.Core.Entities;
using FSOps.Server.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Tests;

/// <summary>
/// Covers GET /flights/logbook and GET /flights/{id}/track.
///
/// The logbook's job is to be honest about sectors that went wrong as well as ones that went well,
/// and never to quote a money figure that did not actually move the cash balance - so these tests
/// pin which statuses appear, and that revenue/cost/net come from posted ledger rows rather than
/// from the Flight.Revenue/TotalCost cache columns (which are deliberately seeded to WRONG values
/// here, so a regression that reads them cannot pass).
/// </summary>
public class FlightLogbookAndTrackEndpointTests
{
    private static T OkValueOf<T>(IResult result)
    {
        var value = ((IValueHttpResult)result).Value;
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    private sealed record LogbookProbe(int TotalSectors, int ReturnedSectors, List<SectorProbe> Sectors);

    private sealed record SectorProbe(
        Guid FlightId, string Status, string DepartureIcao, string ArrivalIcao, string? FlightNumber,
        string? Registration, string? AircraftTypeName, string? AircraftIcaoType, string? PilotName, bool IsPlayerFlight,
        DateTimeOffset DateUtc, int PlannedBlockMinutes, double? ActualBlockMinutes, bool BlockTimeNotMeasured,
        int PaxFlown, int? Seats, double? LoadFactorPercent, double? LandingFpmFirst,
        decimal Revenue, decimal Cost, decimal Net, bool? VatsimOnline, bool HasTrack, int TrackPointCount);

    private sealed record TrackProbe(
        Guid FlightId, int RecordedPointCount, int DiscardedLeadingPointCount, bool Thinned, List<TrackPointProbe> Points);

    private sealed record TrackPointProbe(DateTimeOffset Utc, double Lat, double Lon, double? AltMslFt, double? GsKt, string? Phase);

    private static readonly DateTimeOffset Base = new(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);

    private static Route SeedRoute(RouteTestContext ctx)
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
            CreatedUtc = Base,
        };
        ctx.Db.Routes.Add(route);
        return route;
    }

    private static Pilot SeedPilot(RouteTestContext ctx, string name, bool isPlayer)
    {
        var pilot = new Pilot
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            Name = name,
            IsPlayer = isPlayer,
            MonthlySalary = 9000m,
            SkillRating = 50,
            CreatedUtc = Base,
        };
        ctx.Db.Pilots.Add(pilot);
        return pilot;
    }

    private static Flight SeedFlight(
        RouteTestContext ctx, Route route, Guid fleetAircraftId, Guid pilotId, FlightStatus status,
        DateTimeOffset departure, int plannedBlockMinutes = 90, double actualExtraMinutes = 0, int paxFlown = 90,
        double? landingFpm = -150, bool simRateElevated = false, bool withInTime = true)
    {
        var flight = new Flight
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            RouteId = route.Id,
            FleetAircraftId = fleetAircraftId,
            PilotId = pilotId,
            Status = status,
            PlannedDepartureUtc = departure,
            PlannedBlockMinutes = plannedBlockMinutes,
            OutUtc = departure,
            InUtc = withInTime ? departure.AddMinutes(plannedBlockMinutes + actualExtraMinutes) : null,
            PaxBooked = paxFlown,
            PaxFlown = paxFlown,
            LandingFpmFirst = landingFpm,
            SimRateElevated = simRateElevated,
            RevenuePosted = true,
            TitleFlown = "Airbus A320neo",
            // Deliberately wrong. Nothing the logbook reports may come from these cache columns -
            // the ledger is the only source of truth for money in this app - so a regression that
            // starts trusting them fails loudly here instead of shipping a plausible wrong number.
            Revenue = 999_999m,
            TotalCost = 999_999m,
            CreatedUtc = departure,
        };
        ctx.Db.Flights.Add(flight);
        return flight;
    }

    private static void AddFlightLedger(RouteTestContext ctx, Flight flight, LedgerCategory category, decimal amount)
    {
        ctx.Db.LedgerTransactions.Add(new LedgerTransaction
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            FlightId = flight.Id,
            Utc = flight.InUtc ?? flight.PlannedDepartureUtc,
            Category = category,
            Amount = amount,
            Description = category.ToString(),
        });
    }

    private static void AddSnapshot(RouteTestContext ctx, Flight flight, int secondsIn, double lat, double lon)
    {
        ctx.Db.FlightEvents.Add(new FlightEvent
        {
            Id = Guid.NewGuid(),
            FlightId = flight.Id,
            Utc = flight.PlannedDepartureUtc.AddSeconds(secondsIn),
            Type = FlightEventType.PositionSnapshot,
            PayloadJson = JsonSerializer.Serialize(new { lat, lon, altMslFt = 15000.0, altAglFt = 14800.0, iasKt = 280.0, gsKt = 310.0, vsFpm = 0.0, headingTrue = 20.0, fuelKg = 4000.0, phase = "Cruise" }),
        });
    }

    // ---------- Logbook ----------

    [Fact]
    public async Task Logbook_WithNoFlights_ReturnsAnEmptyResult()
    {
        using var ctx = await RouteTestContext.CreateAsync();

        var body = OkValueOf<LogbookProbe>(await FlightEndpoints.LogbookAsync(ctx.Db, ctx.CurrentUser, CancellationToken.None));

        Assert.Equal(0, body.TotalSectors);
        Assert.Empty(body.Sectors);
    }

    [Fact]
    public async Task Logbook_IncludesAttemptedSectors_AndExcludesOnesThatNeverLeftTheGate()
    {
        // A logbook that quietly dropped the abandoned and interrupted sectors would be flattering
        // rather than accurate. Skipped/Cancelled/Suspended never flew at all, so they are not
        // flying done and do not belong here. (Planned was removed from FlightStatus in the same
        // release as this test was written - nothing ever persisted it, and a flight is created
        // directly as InProgress.)
        using var ctx = await RouteTestContext.CreateAsync();
        var route = SeedRoute(ctx);
        var pilot = SeedPilot(ctx, "Player", isPlayer: true);
        var aircraft = await ctx.Db.FleetAircraft.FirstAsync();

        SeedFlight(ctx, route, aircraft.Id, pilot.Id, FlightStatus.Completed, Base);
        SeedFlight(ctx, route, aircraft.Id, pilot.Id, FlightStatus.Abandoned, Base.AddHours(3));
        SeedFlight(ctx, route, aircraft.Id, pilot.Id, FlightStatus.Interrupted, Base.AddHours(6));
        SeedFlight(ctx, route, aircraft.Id, pilot.Id, FlightStatus.Cancelled, Base.AddHours(12));
        SeedFlight(ctx, route, aircraft.Id, pilot.Id, FlightStatus.Skipped, Base.AddHours(15));
        SeedFlight(ctx, route, aircraft.Id, pilot.Id, FlightStatus.Suspended, Base.AddHours(18));
        await ctx.Db.SaveChangesAsync();

        var body = OkValueOf<LogbookProbe>(await FlightEndpoints.LogbookAsync(ctx.Db, ctx.CurrentUser, CancellationToken.None));

        Assert.Equal(3, body.TotalSectors);
        Assert.Equal(["Completed", "Abandoned", "Interrupted"], body.Sectors.Select(s => s.Status).OrderBy(s => s switch
        {
            "Completed" => 0,
            "Abandoned" => 1,
            _ => 2,
        }));
    }

    [Fact]
    public async Task Logbook_MoneyComesFromPostedLedgerRows_NotFromTheFlightsCacheColumns()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = SeedRoute(ctx);
        var pilot = SeedPilot(ctx, "Player", isPlayer: true);
        var aircraft = await ctx.Db.FleetAircraft.FirstAsync();

        var flight = SeedFlight(ctx, route, aircraft.Id, pilot.Id, FlightStatus.Completed, Base);
        AddFlightLedger(ctx, flight, LedgerCategory.TicketRevenue, 18_500m);
        AddFlightLedger(ctx, flight, LedgerCategory.Fuel, -4_200m);
        AddFlightLedger(ctx, flight, LedgerCategory.LandingFees, -900m);
        AddFlightLedger(ctx, flight, LedgerCategory.VatsimOnlineBonus, 400m);
        await ctx.Db.SaveChangesAsync();

        var body = OkValueOf<LogbookProbe>(await FlightEndpoints.LogbookAsync(ctx.Db, ctx.CurrentUser, CancellationToken.None));
        var sector = Assert.Single(body.Sectors);

        // Revenue is every positive line, cost every negative one - so `net` is the sum of ALL lines
        // posted against the flight, exactly the figure the report card shows as "Net". A player
        // clicking through from the logbook must not find a different number on the other side.
        Assert.Equal(18_900m, sector.Revenue);
        Assert.Equal(5_100m, sector.Cost);
        Assert.Equal(13_800m, sector.Net);
    }

    [Fact]
    public async Task Logbook_JoinsRouteAircraftAndPilot()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = SeedRoute(ctx);
        var pilot = SeedPilot(ctx, "Robin Hayes", isPlayer: false);
        var aircraft = await ctx.Db.FleetAircraft.FirstAsync();

        SeedFlight(ctx, route, aircraft.Id, pilot.Id, FlightStatus.Completed, Base, paxFlown: 90);
        await ctx.Db.SaveChangesAsync();

        var sector = Assert.Single(OkValueOf<LogbookProbe>(await FlightEndpoints.LogbookAsync(ctx.Db, ctx.CurrentUser, CancellationToken.None)).Sectors);

        Assert.Equal("EGGD", sector.DepartureIcao);
        Assert.Equal("EGPH", sector.ArrivalIcao);
        Assert.Equal("101", sector.FlightNumber);
        Assert.Equal("G-TEST", sector.Registration);
        Assert.Equal("A320", sector.AircraftIcaoType);
        Assert.Equal("Robin Hayes", sector.PilotName);
        Assert.False(sector.IsPlayerFlight);
        // 90 of the A320's 180 seats.
        Assert.Equal(180, sector.Seats);
        Assert.Equal(50.0, sector.LoadFactorPercent);
    }

    [Fact]
    public async Task Logbook_BlockTimeIsMeasuredFromOutAndIn_AndIsNotMeasuredWhenTheSimRanFast()
    {
        // Elapsed wall time is meaningless once the sim clock has run faster than real time, so the
        // only honest answer is "not measured" - never a number that would read as an impossibly
        // quick sector.
        using var ctx = await RouteTestContext.CreateAsync();
        var route = SeedRoute(ctx);
        var pilot = SeedPilot(ctx, "Player", isPlayer: true);
        var aircraft = await ctx.Db.FleetAircraft.FirstAsync();

        SeedFlight(ctx, route, aircraft.Id, pilot.Id, FlightStatus.Completed, Base, plannedBlockMinutes: 90, actualExtraMinutes: 12);
        SeedFlight(ctx, route, aircraft.Id, pilot.Id, FlightStatus.Completed, Base.AddHours(4), plannedBlockMinutes: 90, simRateElevated: true);
        SeedFlight(ctx, route, aircraft.Id, pilot.Id, FlightStatus.Abandoned, Base.AddHours(8), plannedBlockMinutes: 90, withInTime: false);
        await ctx.Db.SaveChangesAsync();

        var sectors = OkValueOf<LogbookProbe>(await FlightEndpoints.LogbookAsync(ctx.Db, ctx.CurrentUser, CancellationToken.None)).Sectors;

        var measured = sectors.Single(s => s.ActualBlockMinutes is not null);
        Assert.Equal(102.0, measured.ActualBlockMinutes);
        Assert.False(measured.BlockTimeNotMeasured);

        var accelerated = sectors.Single(s => s.BlockTimeNotMeasured);
        Assert.Null(accelerated.ActualBlockMinutes);

        var abandoned = sectors.Single(s => s.Status == "Abandoned");
        Assert.Null(abandoned.ActualBlockMinutes);
        Assert.False(abandoned.BlockTimeNotMeasured);
    }

    [Fact]
    public async Task Logbook_ReportsWhetherASectorHasAFlownTrack_WithoutLoadingIt()
    {
        // hasTrack is what stops the UI offering a track view that opens an empty map. False is the
        // normal answer for a virtual-pilot sector: no simulator was ever attached to record from.
        using var ctx = await RouteTestContext.CreateAsync();
        var route = SeedRoute(ctx);
        var player = SeedPilot(ctx, "Player", isPlayer: true);
        var crew = SeedPilot(ctx, "Robin Hayes", isPlayer: false);
        var aircraft = await ctx.Db.FleetAircraft.FirstAsync();

        var tracked = SeedFlight(ctx, route, aircraft.Id, player.Id, FlightStatus.Completed, Base);
        AddSnapshot(ctx, tracked, 0, 51.38, -2.72);
        AddSnapshot(ctx, tracked, 15, 51.60, -2.80);
        SeedFlight(ctx, route, aircraft.Id, crew.Id, FlightStatus.Completed, Base.AddHours(4));
        await ctx.Db.SaveChangesAsync();

        var sectors = OkValueOf<LogbookProbe>(await FlightEndpoints.LogbookAsync(ctx.Db, ctx.CurrentUser, CancellationToken.None)).Sectors;

        var withTrack = sectors.Single(s => s.FlightId == tracked.Id);
        Assert.True(withTrack.HasTrack);
        Assert.Equal(2, withTrack.TrackPointCount);

        var virtualSector = sectors.Single(s => s.FlightId != tracked.Id);
        Assert.False(virtualSector.HasTrack);
        Assert.Equal(0, virtualSector.TrackPointCount);
    }

    [Fact]
    public async Task Logbook_IsNewestFirst()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = SeedRoute(ctx);
        var pilot = SeedPilot(ctx, "Player", isPlayer: true);
        var aircraft = await ctx.Db.FleetAircraft.FirstAsync();

        SeedFlight(ctx, route, aircraft.Id, pilot.Id, FlightStatus.Completed, Base);
        SeedFlight(ctx, route, aircraft.Id, pilot.Id, FlightStatus.Completed, Base.AddDays(2));
        SeedFlight(ctx, route, aircraft.Id, pilot.Id, FlightStatus.Completed, Base.AddDays(1));
        await ctx.Db.SaveChangesAsync();

        var sectors = OkValueOf<LogbookProbe>(await FlightEndpoints.LogbookAsync(ctx.Db, ctx.CurrentUser, CancellationToken.None)).Sectors;

        Assert.Equal(sectors.OrderByDescending(s => s.DateUtc).Select(s => s.FlightId), sectors.Select(s => s.FlightId));
    }

    [Fact]
    public async Task Logbook_DoesNotLeakAnotherAirlinesFlights()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = SeedRoute(ctx);
        var pilot = SeedPilot(ctx, "Player", isPlayer: true);
        var aircraft = await ctx.Db.FleetAircraft.FirstAsync();

        SeedFlight(ctx, route, aircraft.Id, pilot.Id, FlightStatus.Completed, Base);
        var foreign = SeedFlight(ctx, route, aircraft.Id, pilot.Id, FlightStatus.Completed, Base.AddHours(2));
        foreign.AirlineId = Guid.NewGuid();
        await ctx.Db.SaveChangesAsync();

        var body = OkValueOf<LogbookProbe>(await FlightEndpoints.LogbookAsync(ctx.Db, ctx.CurrentUser, CancellationToken.None));

        Assert.Equal(1, body.TotalSectors);
        Assert.DoesNotContain(body.Sectors, s => s.FlightId == foreign.Id);
    }

    // ---------- Track ----------

    [Fact]
    public async Task Track_ForAFlightWithNoSnapshots_IsAnEmptySuccess_NotANotFound()
    {
        // The flight exists; it simply has no recorded track. Returning 404 would conflate "no such
        // flight" with "nothing was recorded", and the UI needs to tell those apart to explain the
        // second one.
        using var ctx = await RouteTestContext.CreateAsync();
        var route = SeedRoute(ctx);
        var pilot = SeedPilot(ctx, "Robin Hayes", isPlayer: false);
        var aircraft = await ctx.Db.FleetAircraft.FirstAsync();
        var flight = SeedFlight(ctx, route, aircraft.Id, pilot.Id, FlightStatus.Completed, Base);
        await ctx.Db.SaveChangesAsync();

        var result = await FlightEndpoints.TrackAsync(flight.Id, ctx.Db, ctx.CurrentUser, CancellationToken.None);
        var body = OkValueOf<TrackProbe>(result);

        Assert.Equal(flight.Id, body.FlightId);
        Assert.Empty(body.Points);
        Assert.Equal(0, body.RecordedPointCount);
        Assert.False(body.Thinned);
    }

    [Fact]
    public async Task Track_ReturnsRecordedPointsInTimeOrder()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = SeedRoute(ctx);
        var pilot = SeedPilot(ctx, "Player", isPlayer: true);
        var aircraft = await ctx.Db.FleetAircraft.FirstAsync();
        var flight = SeedFlight(ctx, route, aircraft.Id, pilot.Id, FlightStatus.Completed, Base);

        AddSnapshot(ctx, flight, 30, 52.5, -3.0);
        AddSnapshot(ctx, flight, 0, 51.38, -2.72);
        AddSnapshot(ctx, flight, 15, 51.9, -2.9);
        await ctx.Db.SaveChangesAsync();

        var body = OkValueOf<TrackProbe>(await FlightEndpoints.TrackAsync(flight.Id, ctx.Db, ctx.CurrentUser, CancellationToken.None));

        Assert.Equal(3, body.RecordedPointCount);
        Assert.Equal([51.38, 51.9, 52.5], body.Points.Select(p => p.Lat));
        Assert.Equal("Cruise", body.Points[0].Phase);
        Assert.Equal(15000.0, body.Points[0].AltMslFt);
    }

    /// <summary>
    /// End to end through the real endpoint, with the opening rows of the player's 2026-08-13
    /// EGGD-EGPH sector exactly as they sit in FlightEvent. This is what proves the departure anchor
    /// is actually resolved from the route and the airport table rather than only working in the
    /// builder's own unit tests - break the lookup and this goes red while everything else stays
    /// green.
    /// </summary>
    [Fact]
    public async Task Track_DiscardsTheOpeningFixesTheSimReportedBeforeItKnewWhereTheAircraftWas()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = SeedRoute(ctx);
        var pilot = SeedPilot(ctx, "Player", isPlayer: true);
        var aircraft = await ctx.Db.FleetAircraft.FirstAsync();
        var flight = SeedFlight(ctx, route, aircraft.Id, pilot.Id, FlightStatus.Completed, Base);

        AddSnapshot(ctx, flight, 0, -2.1556893808986427E-07, 90.00032277330374);
        AddSnapshot(ctx, flight, 15, -2.1556893808986427E-07, 90.00032277330374);
        AddSnapshot(ctx, flight, 30, 51.38534252774989, -2.7070546666672604);
        AddSnapshot(ctx, flight, 45, 53.0, -3.0);
        AddSnapshot(ctx, flight, 60, 55.94836445882101, -3.3665372600875436);
        await ctx.Db.SaveChangesAsync();

        var body = OkValueOf<TrackProbe>(await FlightEndpoints.TrackAsync(flight.Id, ctx.Db, ctx.CurrentUser, CancellationToken.None));

        Assert.Equal(2, body.DiscardedLeadingPointCount);
        Assert.Equal(3, body.Points.Count);
        Assert.DoesNotContain(body.Points, p => Math.Abs(p.Lon - 90.00032277330374) < 1.0);

        // The honest total still counts every recorded row - nothing was deleted, only left undrawn.
        Assert.Equal(5, body.RecordedPointCount);
        Assert.Equal(51.38534252774989, body.Points[0].Lat, precision: 9);
    }

    [Fact]
    public async Task Track_ForAnotherAirlinesFlight_IsNotFound()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var route = SeedRoute(ctx);
        var pilot = SeedPilot(ctx, "Player", isPlayer: true);
        var aircraft = await ctx.Db.FleetAircraft.FirstAsync();
        var flight = SeedFlight(ctx, route, aircraft.Id, pilot.Id, FlightStatus.Completed, Base);
        flight.AirlineId = Guid.NewGuid();
        await ctx.Db.SaveChangesAsync();

        var result = await FlightEndpoints.TrackAsync(flight.Id, ctx.Db, ctx.CurrentUser, CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(result);
    }
}
