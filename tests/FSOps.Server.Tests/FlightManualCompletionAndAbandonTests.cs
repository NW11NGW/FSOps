using FSOps.Core.Economy;
using FSOps.Core.Entities;
using FSOps.Server.Endpoints;
using FSOps.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FSOps.Server.Tests;

/// <summary>
/// Covers the two flight-completion paths that don't go through live telemetry (see
/// FlightCompletionAircraftLocationTests for the FinalizeFlightAsync/normal-and-diverted-landing
/// path): a manual "complete with estimates" always trusts the planned arrival since there's no
/// reliable telemetry to check against, and an abandoned flight leaves the aircraft exactly where
/// it was unless telemetry clearly shows it moved. Uses the same isolated in-memory
/// RouteTestContext as the route tests - never the real database.
/// </summary>
public class FlightManualCompletionAndAbandonTests
{
    private static int StatusCodeOf(IResult result) => ((IStatusCodeHttpResult)result).StatusCode ?? 0;

    [Fact]
    public async Task CompleteManualAsync_UsesThePlannedArrival_AndAddsAirframeHoursFromTheEstimatedBlockTime()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();
        fleetAircraft.Status = FleetAircraftStatus.InFlight;

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

        var outUtc = DateTimeOffset.UtcNow.AddHours(-2);
        var flight = new Flight
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            RouteId = route.Id,
            FleetAircraftId = fleetAircraft.Id,
            PilotId = Guid.NewGuid(),
            Status = FlightStatus.InProgress,
            PlannedDepartureUtc = outUtc,
            PlannedBlockMinutes = 120,
            OutUtc = outUtc, // the state machine got this far before the sim went away
            PaxBooked = 150,
            FuelPlannedKg = 3000,
            TitleFlown = "Test Aircraft",
            CreatedUtc = outUtc,
        };
        ctx.Db.Flights.Add(flight);
        await ctx.Db.SaveChangesAsync();

        var economyConfig = EconomyConfig.Default();
        var lifecycle = new FlightLifecycleService(null!, null!, null!, economyConfig, null!);
        var result = await FlightEndpoints.CompleteManualAsync(flight.Id, ctx.Db, ctx.CurrentUser, lifecycle, economyConfig, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

        var updatedAircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == fleetAircraft.Id);
        Assert.Equal("EGPH", updatedAircraft.LocationIcao);
        Assert.Equal(FleetAircraftStatus.Active, updatedAircraft.Status);
        // OutUtc was ~2 hours ago; InUtc gets stamped "now" by CompleteManualAsync itself.
        Assert.InRange(updatedAircraft.AirframeHours, 1.9, 2.1);

        var updatedFlight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
        Assert.Equal(FlightStatus.Completed, updatedFlight.Status);
    }

    /// <summary>
    /// The regression this exists to guard: a flight manually completed within seconds of starting
    /// (OutUtc left unset, so CompleteManualAsync stamps both Out and In at essentially "now") used
    /// to accrue maintenance/crew cost from that near-zero real elapsed time - a few pence instead
    /// of a realistic sector's worth. Both must now come from the flight's PLANNED block time
    /// instead, exactly like the real-telemetry completion path uses the flight's actual measured
    /// Out/In gap for the same purpose.
    /// </summary>
    [Fact]
    public async Task CompleteManualAsync_CompletedSecondsAfterStarting_StillAccruesARealisticSectorsWorthOfMaintenanceAndCrewCost()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();
        fleetAircraft.Status = FleetAircraftStatus.InFlight;

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

        var now = DateTimeOffset.UtcNow;
        var flight = new Flight
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            RouteId = route.Id,
            FleetAircraftId = fleetAircraft.Id,
            PilotId = Guid.NewGuid(),
            Status = FlightStatus.InProgress,
            PlannedDepartureUtc = now,
            PlannedBlockMinutes = 120, // a 2-hour sector - what the real elapsed time must be ignored in favour of.
            // OutUtc deliberately left unset, exactly as a genuine "started, then immediately hit
            // complete-with-estimates" flight would arrive here - CompleteManualAsync stamps both
            // Out and In at essentially the same instant below.
            PaxBooked = 150,
            FuelPlannedKg = 3000,
            TitleFlown = "Test Aircraft",
            CreatedUtc = now,
        };
        ctx.Db.Flights.Add(flight);
        await ctx.Db.SaveChangesAsync();

        var economyConfig = EconomyConfig.Default();
        var lifecycle = new FlightLifecycleService(null!, null!, null!, economyConfig, null!);
        var result = await FlightEndpoints.CompleteManualAsync(flight.Id, ctx.Db, ctx.CurrentUser, lifecycle, economyConfig, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

        var updatedFlight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
        // The real elapsed wall-clock gap is a few milliseconds - if maintenance/crew were still
        // driven by that, InUtc - OutUtc would round to zero hours and both lines would be at or
        // near £0.
        Assert.True((updatedFlight.InUtc!.Value - updatedFlight.OutUtc!.Value).TotalSeconds < 5);

        var plannedHours = flight.PlannedBlockMinutes / 60.0;
        var expectedMaintenance = economyConfig.Costs.MaintenanceAccrualPerHour * (decimal)plannedHours;
        var expectedCrew = economyConfig.Costs.CrewCostPerHour * (decimal)Math.Max(plannedHours, economyConfig.Costs.MinimumCrewDutyHours);

        var ledgerLines = await ctx.Db.LedgerTransactions.Where(t => t.FlightId == flight.Id).ToListAsync();
        var maintenanceLine = Assert.Single(ledgerLines, t => t.Category == LedgerCategory.Maintenance);
        var crewLine = Assert.Single(ledgerLines, t => t.Category == LedgerCategory.Salary);

        Assert.Equal(-expectedMaintenance, maintenanceLine.Amount);
        Assert.Equal(-expectedCrew, crewLine.Amount);

        // The regression, stated as a number rather than just "not the bug formula": a real sector
        // of maintenance/crew cost is comfortably in the hundreds of pounds, not pennies.
        Assert.True(maintenanceLine.Amount < -100m,
            $"Expected a realistic maintenance accrual for a {flight.PlannedBlockMinutes}-minute planned sector, got {maintenanceLine.Amount:C2}.");
    }

    [Fact]
    public async Task AbandonAsync_NoTelemetryWasEverReceived_LeavesTheAircraftExactlyWhereItWas()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync();
        fleetAircraft.Status = FleetAircraftStatus.InFlight;

        var flight = new Flight
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            RouteId = Guid.NewGuid(),
            FleetAircraftId = fleetAircraft.Id,
            PilotId = Guid.NewGuid(),
            Status = FlightStatus.InProgress,
            PlannedDepartureUtc = DateTimeOffset.UtcNow,
            PlannedBlockMinutes = 90,
            PaxBooked = 150,
            FuelPlannedKg = 3000,
            TitleFlown = "Test Aircraft",
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.Flights.Add(flight);
        await ctx.Db.SaveChangesAsync();

        // A bare service with no active tracking for this flight - GetActiveSnapshot returns null,
        // exactly as if the sim never sent a single sample before the user gave up and abandoned.
        var lifecycle = new FlightLifecycleService(null!, null!, null!, EconomyConfig.Default(), null!);
        var result = await FlightEndpoints.AbandonAsync(flight.Id, ctx.Db, ctx.CurrentUser, lifecycle, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

        var updatedAircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == fleetAircraft.Id);
        Assert.Equal("EGGD", updatedAircraft.LocationIcao); // unchanged - still where the fleet was seeded
        Assert.Equal(FleetAircraftStatus.Active, updatedAircraft.Status);
        Assert.Equal(0, updatedAircraft.AirframeHours); // never flew far enough to count

        var updatedFlight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
        Assert.Equal(FlightStatus.Abandoned, updatedFlight.Status);
    }

    [Fact]
    public async Task RevertFleetAircraftAsync_TelemetryStillNearTheRecordedLocation_LeavesItUnchanged()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync(); // seeded at EGGD, 51.3827, -2.7191

        var flight = await SeedAbandonedFlightAsync(ctx, fleetAircraft.Id);

        // Still basically at the gate - taxied a few hundred metres, nowhere near another airport.
        var snapshot = new LiveFlightSnapshot(
            flight.Id, "TaxiOut", 51.3827, -2.7191, 0, 0, 0, 0, 0, 0, 0, 90, false, DateTimeOffset.UtcNow);

        await FlightEndpoints.RevertFleetAircraftAsync(ctx.Db, flight, snapshot, CancellationToken.None);
        await ctx.Db.SaveChangesAsync();

        var updatedAircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == fleetAircraft.Id);
        Assert.Equal("EGGD", updatedAircraft.LocationIcao);
    }

    [Fact]
    public async Task RevertFleetAircraftAsync_TelemetryShowsItMovedToAnotherAirport_UpdatesTheRecordedLocation()
    {
        using var ctx = await RouteTestContext.CreateAsync();
        var fleetAircraft = await ctx.Db.FleetAircraft.FirstAsync(); // seeded at EGGD

        var flight = await SeedAbandonedFlightAsync(ctx, fleetAircraft.Id);

        // The flight got abandoned after it had already flown to and landed at EGPH.
        var snapshot = new LiveFlightSnapshot(
            flight.Id, "TaxiIn", 55.9500, -3.3725, 0, 0, 0, 0, 0, 0, 0, 90, false, DateTimeOffset.UtcNow);

        await FlightEndpoints.RevertFleetAircraftAsync(ctx.Db, flight, snapshot, CancellationToken.None);
        await ctx.Db.SaveChangesAsync();

        var updatedAircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == fleetAircraft.Id);
        Assert.Equal("EGPH", updatedAircraft.LocationIcao);
    }

    private static async Task<Flight> SeedAbandonedFlightAsync(RouteTestContext ctx, Guid fleetAircraftId)
    {
        var flight = new Flight
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            RouteId = Guid.NewGuid(),
            FleetAircraftId = fleetAircraftId,
            PilotId = Guid.NewGuid(),
            Status = FlightStatus.Abandoned,
            PlannedDepartureUtc = DateTimeOffset.UtcNow,
            PlannedBlockMinutes = 90,
            PaxBooked = 150,
            FuelPlannedKg = 3000,
            TitleFlown = "Test Aircraft",
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        ctx.Db.Flights.Add(flight);
        await ctx.Db.SaveChangesAsync();
        return flight;
    }
}
