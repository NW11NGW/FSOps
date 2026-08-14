using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Core.Planning;
using FSOps.Server.Endpoints;
using FSOps.Server.Services;
using FSOps.Server.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FSOps.Server.Tests;

/// <summary>
/// The test the whole fare feature stands or falls on: <b>a prediction must never silently disagree
/// with what actually happens.</b> A preview that lies is worse than no preview - the player commits
/// on the strength of it.
///
/// <para>The claim is proved in two links, and both are here:</para>
/// <list type="number">
/// <item><b>The ledger equals the projection.</b> A sector is posted through
/// <see cref="FlightEconomicsPoster"/> - the one and only place a flight's money becomes real
/// <c>LedgerTransaction</c> rows - and every row is compared, line by line, with what
/// <see cref="SectorProjector"/> said before the flight. The rows are then summed: the money the
/// airline actually has moved by exactly the profit that was predicted.</item>
/// <item><b>The endpoint equals the projection.</b> Proved next door in
/// <see cref="PlanningEndpointsTests.RoutePricing_QuotesTheSameFiguresTheProjectorProduces"/>, which
/// recomputes the endpoint's whole answer through the projector.</item>
/// </list>
///
/// <para><b>The one honest gap, tested rather than hidden.</b> A virtual pilot's sector is delayed
/// by a seeded amount drawn from the pilot's skill, and that delay is billed as extra crew and
/// maintenance time. A projection made beforehand cannot know it, so a virtual sector posts slightly
/// LESS profit than predicted - never more, and never because of anything to do with the fare. The
/// end-to-end test below measures that difference and pins it to exactly the delay's own crew and
/// maintenance cost, so it can never quietly become something else. (This is pre-existing behaviour
/// shared with the schedule builder's own profit figures, not something this feature introduced.)</para>
/// </summary>
public class FarePredictionMatchesLedgerTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public FarePredictionMatchesLedgerTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    /// <summary>A Sunday. 06:00 departure on a ~1 h sector, so departure and arrival land on the same
    /// UTC day - fuel is priced on the departure day and demand on the arrival day, and this is what
    /// makes the two the same day rather than a coincidence the test relies on silently.</summary>
    private static readonly DateTimeOffset Base = new(2026, 1, 4, 0, 0, 0, TimeSpan.Zero);

    private const int WorldSeed = 1;

    [Fact]
    public async Task PostedLedgerRows_AreExactlyTheFiguresThePreviewQuoted()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var economyConfig = catalog.Get(ctx.Airline.Playstyle);

        var route = await SeedRouteAsync(ctx, "EGGD", "EGPH");

        // Set a fare the player chose, well away from the suggestion, so the assertion cannot pass
        // by accident on a default.
        var chosenFare = 137.50m;
        var update = await RouteEndpoints.UpdateAsync(
            route.Id, new UpdateRouteRequest(null, chosenFare, null), ctx.Db, ctx.CurrentUser, catalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, ((IStatusCodeHttpResult)update).StatusCode);
        Assert.Equal(chosenFare, (await ctx.Db.Routes.AsNoTracking().SingleAsync(r => r.Id == route.Id)).BaseFare);

        var departure = await ctx.Db.Airports.SingleAsync(a => a.Icao == "EGGD");
        var arrival = await ctx.Db.Airports.SingleAsync(a => a.Icao == "EGPH");
        var postedAt = Base.AddHours(6);

        // THE PREDICTION - made before a single row is written.
        var predicted = SectorProjector.Project(
            economyConfig, ctx.Airline.StrategyProfile, ctx.Airline.ReputationScore, departure, arrival, ctx.AircraftType,
            route.DistanceNm, chosenFare, postedAt, WorldSeed);

        // THE POSTING - the real poster, exactly as a no-telemetry sector (a virtual pilot's
        // occurrence, a manual completion) drives it: the planned burn billed at the departure
        // airport, then every other line posted from the flight's own block hours.
        var flight = new Flight
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            RouteId = route.Id,
            FleetAircraftId = await ctx.Db.FleetAircraft.Where(f => f.AirlineId == ctx.Airline.Id).Select(f => f.Id).FirstAsync(),
            Status = FlightStatus.Completed,
            PlannedDepartureUtc = postedAt,
            PlannedBlockMinutes = predicted.Plan.BlockMinutes,
            TitleFlown = ctx.AircraftType.Name,
            CreatedUtc = postedAt,
        };
        ctx.Db.Flights.Add(flight);

        FlightEconomicsPoster.PostFuelBurn(
            ctx.Db, flight, economyConfig, departure, predicted.Plan.ChargedFuelKg, postedAt, WorldSeed);
        var posted = await FlightEconomicsPoster.PostCompletionAsync(
            ctx.Db, flight, ctx.Airline, route, ctx.AircraftType, arrival, economyConfig,
            predicted.Plan.BlockHours, postedAt, CancellationToken.None);
        await ctx.Db.SaveChangesAsync();

        Assert.NotNull(posted);

        var rows = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync();

        decimal Row(LedgerCategory category) => rows.Where(t => t.Category == category).Sum(t => t.Amount);

        // Printed, not just asserted: "the preview does not lie" is the claim this whole feature
        // rests on, and a reader of the test output should be able to see the two columns line up
        // rather than take the asserts' word for it.
        _output.WriteLine($"Fare set by the player: {chosenFare:F2} on {route.DepartureIcao}-{route.ArrivalIcao} ({route.DistanceNm:F1} nm)");
        _output.WriteLine($"{"line",-20} {"predicted",14} {"posted to ledger",18}");
        _output.WriteLine($"{"ticket revenue",-20} {predicted.Revenue,14:F2} {Row(LedgerCategory.TicketRevenue),18:F2}");
        _output.WriteLine($"{"fuel",-20} {-predicted.Economics.FuelCost,14:F2} {Row(LedgerCategory.Fuel),18:F2}");
        _output.WriteLine($"{"landing",-20} {-predicted.Economics.LandingFee,14:F2} {Row(LedgerCategory.LandingFees),18:F2}");
        _output.WriteLine($"{"handling",-20} {-predicted.Economics.HandlingFee,14:F2} {Row(LedgerCategory.Handling),18:F2}");
        _output.WriteLine($"{"parking",-20} {-predicted.Economics.ParkingFee,14:F2} {Row(LedgerCategory.ParkingFees),18:F2}");
        _output.WriteLine($"{"passenger charges",-20} {-predicted.Economics.PassengerCharge,14:F2} {Row(LedgerCategory.PassengerCharges),18:F2}");
        _output.WriteLine($"{"turnaround",-20} {-predicted.Economics.TurnaroundFee,14:F2} {Row(LedgerCategory.TurnaroundFees),18:F2}");
        _output.WriteLine($"{"maintenance",-20} {-predicted.Economics.MaintenanceAccrual,14:F2} {Row(LedgerCategory.Maintenance),18:F2}");
        _output.WriteLine($"{"crew",-20} {-predicted.Economics.CrewCost,14:F2} {Row(LedgerCategory.CrewCost),18:F2}");
        _output.WriteLine($"{"NET",-20} {predicted.NetProfit,14:F2} {rows.Sum(t => t.Amount),18:F2}");
        _output.WriteLine($"passengers: predicted {predicted.PaxBooked}, booked {flight.PaxBooked}");

        // Every single line, compared with what the player was shown.
        Assert.Equal(predicted.Revenue, Row(LedgerCategory.TicketRevenue));
        Assert.Equal(-predicted.Economics.FuelCost, Row(LedgerCategory.Fuel));
        Assert.Equal(-predicted.Economics.LandingFee, Row(LedgerCategory.LandingFees));
        Assert.Equal(-predicted.Economics.HandlingFee, Row(LedgerCategory.Handling));
        Assert.Equal(-predicted.Economics.ParkingFee, Row(LedgerCategory.ParkingFees));
        Assert.Equal(-predicted.Economics.PassengerCharge, Row(LedgerCategory.PassengerCharges));
        Assert.Equal(-predicted.Economics.TurnaroundFee, Row(LedgerCategory.TurnaroundFees));
        Assert.Equal(-predicted.Economics.MaintenanceAccrual, Row(LedgerCategory.Maintenance));
        Assert.Equal(-predicted.Economics.CrewCost, Row(LedgerCategory.CrewCost));

        // And the bottom line: the airline's cash moved by exactly the predicted profit.
        Assert.Equal(predicted.NetProfit, rows.Sum(t => t.Amount));
        Assert.Equal(predicted.PaxBooked, flight.PaxBooked);
        Assert.Equal(predicted.Revenue, flight.Revenue);
    }

    /// <summary>
    /// The same claim, driven end to end through <see cref="VirtualFlightResolverService"/> rather
    /// than through the poster by hand - a real scheduled sector, resolved by the real service, on
    /// the real ledger. Revenue must match to the penny (nothing about the fare or the market depends
    /// on how the sector was flown), and the profit gap must be exactly the delay's own crew and
    /// maintenance cost and nothing else.
    /// </summary>
    [Fact]
    public async Task VirtualPilotSector_PostsThePredictedRevenue_AndDiffersOnlyByTheDelayItCouldNotKnow()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var catalog = EconomyConfigCatalog.Default();
        var economyConfig = catalog.Get(ctx.Airline.Playstyle);

        var route = await SeedRouteAsync(ctx, "EGGD", "EGPH");
        var chosenFare = 137.50m;
        route.BaseFare = chosenFare;

        // The seeded aircraft is reserved to the player; a virtual pilot can only be rostered onto a
        // released one.
        var aircraft = await ctx.Db.FleetAircraft.SingleAsync(f => f.AirlineId == ctx.Airline.Id);
        aircraft.ReservedForPlayer = false;

        var pilot = new Pilot
        {
            Id = Guid.NewGuid(), AirlineId = ctx.Airline.Id, Name = "First Officer Test", IsPlayer = false,
            MonthlySalary = 9_000m, SkillRating = 50, Status = PilotStatus.Available, CreatedUtc = Base,
        };
        ctx.Db.Pilots.Add(pilot);

        var schedule = new PilotSchedule { Id = Guid.NewGuid(), PilotId = pilot.Id, AirlineId = ctx.Airline.Id, CreatedUtc = Base };
        ctx.Db.PilotSchedules.Add(schedule);
        ctx.Db.PilotScheduleEntries.Add(new PilotScheduleEntry
        {
            Id = Guid.NewGuid(), PilotScheduleId = schedule.Id, DayOfWeek = DayOfWeek.Sunday,
            DepartureTimeUtc = new TimeSpan(6, 0, 0), RouteId = route.Id, FleetAircraftId = aircraft.Id, CreatedUtc = Base,
        });
        ctx.Db.EconomyStates.Add(new EconomyState
        {
            Id = Guid.NewGuid(), LastProcessedUtc = Base, LastScheduleResolvedUtc = Base,
            WorldSeed = WorldSeed, FuelPricePerKg = 0m,
        });
        await ctx.Db.SaveChangesAsync();

        var departure = await ctx.Db.Airports.SingleAsync(a => a.Icao == "EGGD");
        var arrival = await ctx.Db.Airports.SingleAsync(a => a.Icao == "EGPH");
        var departureUtc = Base.AddHours(6);

        // What the fare workbench would have told the player, before the sector ran.
        var predicted = SectorProjector.Project(
            economyConfig, ctx.Airline.StrategyProfile, ctx.Airline.ReputationScore, departure, arrival, ctx.AircraftType,
            route.DistanceNm, chosenFare, departureUtc, WorldSeed);

        // Resolve it for real.
        var services = new ServiceCollection();
        services.AddDbContext<FSOps.Data.FsOpsDbContext>(o => o.UseSqlite(ctx.Connection));
        using var provider = services.BuildServiceProvider();
        var resolver = new VirtualFlightResolverService(
            provider.GetRequiredService<IServiceScopeFactory>(), catalog, new FakeClock(Base.AddDays(1)),
            NullLogger<VirtualFlightResolverService>.Instance);

        var run = await resolver.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, run.FlightsCompleted);

        var flight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.RouteId == route.Id);
        var rows = await ctx.Db.LedgerTransactions.AsNoTracking().Where(t => t.FlightId == flight.Id).ToListAsync();
        Assert.NotEmpty(rows);

        // Departure and arrival on the same UTC day - the premise that makes fuel price and market
        // season/day-of-week identical either side of the flight.
        Assert.Equal(departureUtc.UtcDateTime.Date, flight.InUtc!.Value.UtcDateTime.Date);

        // Revenue: exactly what was predicted. A sector's revenue is passengers x fare and nothing
        // else - no delay, no landing quality and no clock can touch it (see FlightEconomicsResult's
        // "integrity by construction" note).
        Assert.Equal(predicted.Revenue, rows.Where(t => t.Category == LedgerCategory.TicketRevenue).Sum(t => t.Amount));
        Assert.Equal(predicted.PaxBooked, flight.PaxBooked);

        // Profit: short of the prediction by exactly the crew and maintenance billed for the delay
        // the prediction could not have known about, and by nothing else.
        var actualHours = (flight.InUtc!.Value - flight.OutUtc!.Value).TotalHours;
        var delayHours = actualHours - predicted.Plan.BlockHours;
        Assert.True(delayHours >= 0, "A virtual sector is never quicker than its own plan.");

        var expectedExtraCost =
            FlightCostCalculator.CrewCost(economyConfig.Costs, actualHours) - FlightCostCalculator.CrewCost(economyConfig.Costs, predicted.Plan.BlockHours) +
            FlightCostCalculator.MaintenanceAccrual(economyConfig.Costs, actualHours) - FlightCostCalculator.MaintenanceAccrual(economyConfig.Costs, predicted.Plan.BlockHours);

        _output.WriteLine($"virtual sector on {route.DepartureIcao}-{route.ArrivalIcao} at fare {chosenFare:F2}");
        _output.WriteLine($"  predicted revenue {predicted.Revenue:F2}, posted {rows.Where(t => t.Category == LedgerCategory.TicketRevenue).Sum(t => t.Amount):F2}");
        _output.WriteLine($"  predicted profit  {predicted.NetProfit:F2}, posted {rows.Sum(t => t.Amount):F2}");
        _output.WriteLine($"  block {predicted.Plan.BlockHours:F4} h, actual {actualHours:F4} h (delay {delayHours * 60:F1} min), extra crew+maintenance {expectedExtraCost:F2}");

        Assert.Equal(predicted.NetProfit - expectedExtraCost, rows.Sum(t => t.Amount));
    }

    private static async Task<Route> SeedRouteAsync(RouteTestContext ctx, string departureIcao, string arrivalIcao)
    {
        var departure = await ctx.Db.Airports.SingleAsync(a => a.Icao == departureIcao);
        var arrival = await ctx.Db.Airports.SingleAsync(a => a.Icao == arrivalIcao);
        var route = new Route
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            DepartureIcao = departureIcao,
            ArrivalIcao = arrivalIcao,
            FlightNumber = "101",
            // The stored distance a route is created with: the great circle between its airports.
            // The projector prices against this, and so does FlightEconomicsPoster.
            DistanceNm = GreatCircle.DistanceNm(departure.Latitude, departure.Longitude, arrival.Latitude, arrival.Longitude),
            BaseFare = 89.00m,
            IsActive = true,
            CreatedUtc = Base,
        };
        ctx.Db.Routes.Add(route);
        await ctx.Db.SaveChangesAsync();
        return route;
    }
}
