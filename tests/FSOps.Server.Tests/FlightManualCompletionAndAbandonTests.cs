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

        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var lifecycle = new FlightLifecycleService(null!, null!, null!, economyConfigCatalog, null, null!);
        var result = await FlightEndpoints.CompleteManualAsync(flight.Id, ctx.Db, ctx.CurrentUser, lifecycle, economyConfigCatalog, CancellationToken.None);

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

        var economyConfigCatalog = EconomyConfigCatalog.Default();
        // ctx.Airline defaults to AirlinePlaystyle.Casual (never set explicitly by RouteTestContext).
        var economyConfig = economyConfigCatalog.Get(AirlinePlaystyle.Casual);
        var lifecycle = new FlightLifecycleService(null!, null!, null!, economyConfigCatalog, null, null!);
        var result = await FlightEndpoints.CompleteManualAsync(flight.Id, ctx.Db, ctx.CurrentUser, lifecycle, economyConfigCatalog, CancellationToken.None);

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
        var crewLine = Assert.Single(ledgerLines, t => t.Category == LedgerCategory.CrewCost);

        Assert.Equal(-expectedMaintenance, maintenanceLine.Amount);
        Assert.Equal(-expectedCrew, crewLine.Amount);

        // The regression, stated as a number rather than just "not the bug formula": a real sector
        // of maintenance/crew cost is comfortably in the hundreds of pounds, not pennies.
        Assert.True(maintenanceLine.Amount < -100m,
            $"Expected a realistic maintenance accrual for a {flight.PlannedBlockMinutes}-minute planned sector, got {maintenanceLine.Amount:C2}.");
    }

    /// <summary>
    /// Shared setup for the reputation tests below: an InProgress flight with a given planned
    /// departure, ready for CompleteManualAsync. <paramref name="plannedDepartureUtc"/> lets each
    /// test control how much (or how little) wall-clock time will have "elapsed" by the time the
    /// endpoint runs, which is exactly the input the removed on-time formula used to read and the
    /// fixed penalty now deliberately ignores.
    /// </summary>
    private static async Task<(RouteTestContext Ctx, Flight Flight, EconomyConfigCatalog Catalog)> SeedInProgressManualCompletionCandidateAsync(
        DateTimeOffset plannedDepartureUtc, int plannedBlockMinutes = 120)
    {
        var ctx = await RouteTestContext.CreateAsync();
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

        var flight = new Flight
        {
            Id = Guid.NewGuid(),
            AirlineId = ctx.Airline.Id,
            RouteId = route.Id,
            FleetAircraftId = fleetAircraft.Id,
            PilotId = Guid.NewGuid(),
            Status = FlightStatus.InProgress,
            PlannedDepartureUtc = plannedDepartureUtc,
            PlannedBlockMinutes = plannedBlockMinutes,
            OutUtc = plannedDepartureUtc,
            PaxBooked = 150,
            FuelPlannedKg = 3000,
            TitleFlown = "Test Aircraft",
            CreatedUtc = plannedDepartureUtc,
        };
        ctx.Db.Flights.Add(flight);
        await ctx.Db.SaveChangesAsync();

        return (ctx, flight, EconomyConfigCatalog.Default());
    }

    /// <summary>
    /// Replaces the removed on-time-derived test - see
    /// ReputationConfig.ManualCompletionAlphaMultiplier's own doc for why deriving anything from
    /// the wall clock on this path was wrong. Runs the SAME scenario (a flight 25 minutes "late" by
    /// the old formula's reckoning) and a completely different one (a flight completed within
    /// seconds of starting) and asserts both land on EXACTLY the same reputation figure - proving
    /// the penalty really is flat and carries no dependence on timing at all.
    /// </summary>
    [Fact]
    public async Task CompleteManualAsync_AppliesTheFixedPenalty_RegardlessOfTiming()
    {
        var lateStart = DateTimeOffset.UtcNow.AddMinutes(-(120 + 25)); // "25 minutes late" by the old (now-removed) formula
        var (lateCtx, lateFlight, lateCatalog) = await SeedInProgressManualCompletionCandidateAsync(lateStart);
        using (lateCtx)
        {
            var lifecycle = new FlightLifecycleService(null!, null!, null!, lateCatalog, null, null!);
            var result = await FlightEndpoints.CompleteManualAsync(lateFlight.Id, lateCtx.Db, lateCtx.CurrentUser, lifecycle, lateCatalog, CancellationToken.None);
            Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

            var reputationConfig = lateCatalog.Get(AirlinePlaystyle.Casual).Reputation;
            var expected = ReputationCalculator.AdvanceForUnverifiedManualCompletion(50.0, reputationConfig);

            var updatedAirline = await lateCtx.Db.Airlines.AsNoTracking().SingleAsync(a => a.Id == lateCtx.Airline.Id);
            Assert.Equal(expected, updatedAirline.ReputationScore);
        }

        var immediateStart = DateTimeOffset.UtcNow; // completed essentially the instant it started
        var (immediateCtx, immediateFlight, immediateCatalog) = await SeedInProgressManualCompletionCandidateAsync(immediateStart);
        using (immediateCtx)
        {
            var lifecycle = new FlightLifecycleService(null!, null!, null!, immediateCatalog, null, null!);
            var result = await FlightEndpoints.CompleteManualAsync(immediateFlight.Id, immediateCtx.Db, immediateCtx.CurrentUser, lifecycle, immediateCatalog, CancellationToken.None);
            Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

            var reputationConfig = immediateCatalog.Get(AirlinePlaystyle.Casual).Reputation;
            var expected = ReputationCalculator.AdvanceForUnverifiedManualCompletion(50.0, reputationConfig);

            var updatedAirline = await immediateCtx.Db.Airlines.AsNoTracking().SingleAsync(a => a.Id == immediateCtx.Airline.Id);
            // The point of the test: identical to the "25 minutes late" scenario above, even though
            // the two flights' actual timing could not be more different.
            Assert.Equal(expected, updatedAirline.ReputationScore);
        }
    }

    /// <summary>
    /// THE EXPLOIT TEST - named so nobody reintroduces the bug this guards. The original on-time
    /// formula read <c>now - (PlannedDepartureUtc + PlannedBlockMinutes)</c>; completing within
    /// seconds of starting put "now" at roughly the planned DEPARTURE, which is very nearly one
    /// full block time EARLY by that arithmetic - scored as a perfect on-time sector, and paired
    /// with the full ticket revenue this path already posts, made "start a flight, immediately
    /// complete it, collect the money and a reputation gain, repeat" a real, reachable loop (see
    /// CompleteManualAsync_CompletedSecondsAfterStarting_StillAccruesARealisticSectorsWorthOfMaintenanceAndCrewCost
    /// above for proof this exact scenario is reachable). The fixed penalty removes the possibility
    /// entirely: there is no timing input left for an instant completion to exploit.
    /// </summary>
    [Fact]
    public async Task CompleteManualAsync_ImmediatelyAfterStarting_CannotGainReputation()
    {
        var (ctx, flight, catalog) = await SeedInProgressManualCompletionCandidateAsync(DateTimeOffset.UtcNow);
        using (ctx)
        {
            Assert.Equal(50.0, ctx.Airline.ReputationScore); // the starting baseline, before this sector

            var lifecycle = new FlightLifecycleService(null!, null!, null!, catalog, null, null!);
            var result = await FlightEndpoints.CompleteManualAsync(flight.Id, ctx.Db, ctx.CurrentUser, lifecycle, catalog, CancellationToken.None);
            Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

            var updatedAirline = await ctx.Db.Airlines.AsNoTracking().SingleAsync(a => a.Id == ctx.Airline.Id);
            Assert.True(updatedAirline.ReputationScore <= 50.0,
                $"An instant manual completion must never gain reputation - expected <= 50.0, was {updatedAirline.ReputationScore}.");
            Assert.True(updatedAirline.ReputationScore < 50.0, "An instant manual completion must actually cost reputation, not merely leave it unchanged.");
        }
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
        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var lifecycle = new FlightLifecycleService(null!, null!, null!, economyConfigCatalog, null, null!);
        var result = await FlightEndpoints.AbandonAsync(flight.Id, ctx.Db, ctx.CurrentUser, lifecycle, economyConfigCatalog, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

        var updatedAircraft = await ctx.Db.FleetAircraft.AsNoTracking().SingleAsync(f => f.Id == fleetAircraft.Id);
        Assert.Equal("EGGD", updatedAircraft.LocationIcao); // unchanged - still where the fleet was seeded
        Assert.Equal(FleetAircraftStatus.Active, updatedAircraft.Status);
        Assert.Equal(0, updatedAircraft.AirframeHours); // never flew far enough to count

        var updatedFlight = await ctx.Db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
        Assert.Equal(FlightStatus.Abandoned, updatedFlight.Status);
    }

    /// <summary>
    /// Closes the other half of the exploit found in review: abandoning was previously free of any
    /// reputation consequence (only manual completion was fixed), which meant a flight running
    /// badly could simply be abandoned instead - a real, if not cost-free, escape (the revenue and
    /// any fuel already bought is still lost). Treated identically to a virtual pilot's
    /// Skipped/Cancelled occurrence: from a passenger's point of view the sector never happened.
    /// </summary>
    [Fact]
    public async Task AbandonAsync_CostsReputation_SameAsACancelledOrSkippedSector()
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

        Assert.Equal(50.0, ctx.Airline.ReputationScore);

        var economyConfigCatalog = EconomyConfigCatalog.Default();
        var lifecycle = new FlightLifecycleService(null!, null!, null!, economyConfigCatalog, null, null!);
        var result = await FlightEndpoints.AbandonAsync(flight.Id, ctx.Db, ctx.CurrentUser, lifecycle, economyConfigCatalog, CancellationToken.None);
        Assert.Equal(StatusCodes.Status200OK, StatusCodeOf(result));

        var reputationConfig = economyConfigCatalog.Get(AirlinePlaystyle.Casual).Reputation;
        var expected = ReputationCalculator.AdvanceForCancelledOrSkipped(50.0, reputationConfig);

        var updatedAirline = await ctx.Db.Airlines.AsNoTracking().SingleAsync(a => a.Id == ctx.Airline.Id);
        Assert.Equal(expected, updatedAirline.ReputationScore);
        Assert.True(updatedAirline.ReputationScore < 50.0, "Abandoning a flight must cost reputation.");
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
