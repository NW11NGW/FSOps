using FSOps.Core.Entities;
using FSOps.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace FSOps.Server.Tests;

/// <summary>
/// Proves the E2 migration (AddVirtualPilotScheduling) is non-destructive: migrates a database to
/// the previous migration, seeds realistic pre-E2 rows by hand (raw SQL against the OLD schema -
/// using the current FsOpsDbContext to insert would fail, since its model already has the new
/// columns/tables), migrates forward, then reads every field back through the current context and
/// asserts nothing was lost or corrupted. This is the check docs/PLAN.md and the E2 brief both
/// require before trusting a migration against the user's real flight history.
/// <para>
/// Also proves the hand-corrected <see cref="FleetAircraft.ReservedForPlayer"/> backfill actually
/// runs: two pre-existing fleet aircraft are seeded (one at the airline's home base, one away), and
/// after migrating forward exactly the hub aircraft must be flagged reserved - never the scaffolded
/// "everyone defaults to false", which would silently strip the "always keep one aircraft free for
/// the human" protection from every airline that already had a fleet before E2 shipped.
/// </para>
/// </summary>
public class MigrationNonDestructiveTests
{
    private const string PreviousMigration = "20260808185859_AddMaintenanceGrounding";

    [Fact]
    public async Task Migration_AppliedToAPreE2Database_PreservesEveryExistingFieldAndBackfillsReservation()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<FsOpsDbContext>().UseSqlite(connection).Options;
        await using (var bootstrapDb = new FsOpsDbContext(options))
        {
            var migrator = bootstrapDb.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
        }

        var airlineId = Guid.NewGuid();
        var hubAircraftId = Guid.NewGuid();
        var awayAircraftId = Guid.NewGuid();
        var aircraftTypeId = Guid.NewGuid();
        var routeId = Guid.NewGuid();
        var flightId = Guid.NewGuid();
        var pilotId = Guid.NewGuid();
        var leaseId = Guid.NewGuid();
        var economyStateId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

        await using (var seedConnectionCmd = connection.CreateCommand())
        {
            seedConnectionCmd.CommandText = BuildSeedSql();
            BindSeedParameters(
                seedConnectionCmd, airlineId, hubAircraftId, awayAircraftId, aircraftTypeId,
                routeId, flightId, pilotId, leaseId, economyStateId, now);
            await seedConnectionCmd.ExecuteNonQueryAsync();
        }

        // The migration under test - applied on top of hand-seeded pre-E2 data, exactly as it will
        // be against the user's real database.
        await using (var migrateDb = new FsOpsDbContext(options))
        {
            await migrateDb.Database.MigrateAsync();
        }

        await using var verifyDb = new FsOpsDbContext(options);

        var airline = await verifyDb.Airlines.SingleAsync(a => a.Id == airlineId);
        Assert.Equal("Migration Test Air", airline.Name);
        Assert.Equal("MGT", airline.IcaoCode);
        Assert.Equal("EGGD", airline.HomeAirportIcao);
        Assert.Equal(AirlineStrategyProfile.Domestic, airline.StrategyProfile);
        Assert.Equal(AirlinePlaystyle.Casual, airline.Playstyle);

        var hubAircraft = await verifyDb.FleetAircraft.SingleAsync(f => f.Id == hubAircraftId);
        Assert.Equal("G-HUBB", hubAircraft.Registration);
        Assert.Equal("EGGD", hubAircraft.LocationIcao);
        Assert.Equal(1234.5, hubAircraft.AirframeHours);
        Assert.Equal(87.5, hubAircraft.ConditionPercent);
        Assert.Equal(3200.0, hubAircraft.FuelOnBoardKg);
        Assert.Equal(FleetAircraftStatus.Active, hubAircraft.Status);
        // The hand-corrected backfill: the aircraft at the airline's home base wins the single
        // reservation slot, never the scaffolded "false for everyone".
        Assert.True(hubAircraft.ReservedForPlayer);

        var awayAircraft = await verifyDb.FleetAircraft.SingleAsync(f => f.Id == awayAircraftId);
        Assert.Equal("G-AWAY", awayAircraft.Registration);
        Assert.Equal("EGPH", awayAircraft.LocationIcao);
        Assert.False(awayAircraft.ReservedForPlayer);

        var route = await verifyDb.Routes.SingleAsync(r => r.Id == routeId);
        Assert.Equal("EGGD", route.DepartureIcao);
        Assert.Equal("EGPH", route.ArrivalIcao);
        Assert.Equal(275.2, route.DistanceNm);
        Assert.Equal(89.00m, route.BaseFare);

        var flight = await verifyDb.Flights.SingleAsync(f => f.Id == flightId);
        Assert.Equal(FlightStatus.Completed, flight.Status);
        Assert.Equal(142, flight.PaxBooked);
        Assert.Equal(142, flight.PaxFlown);
        Assert.Equal(6231.75m, flight.Revenue);
        Assert.Equal(3814.20m, flight.TotalCost);
        Assert.True(flight.RevenuePosted);
        // New nullable column - must read back as null for a pre-existing flight, never a
        // scaffolded non-null default (there is no reason for one - see the entity's own doc).
        Assert.Null(flight.UnflyableReason);
        Assert.Null(flight.ScheduleId);

        var ledgerTotal = (await verifyDb.LedgerTransactions.Where(t => t.AirlineId == airlineId).ToListAsync())
            .Sum(t => t.Amount);
        // 2,000,000 starting capital - 30,000 lease deposit - 1,186 fuel + 6,231.75 revenue -
        // 1,200.50 landing fee - 615.30 handling fee.
        Assert.Equal(1_973_229.95m, ledgerTotal);

        var pilot = await verifyDb.Pilots.SingleAsync(p => p.Id == pilotId);
        Assert.Equal("Captain Migration", pilot.Name);
        Assert.True(pilot.IsPlayer);
        Assert.Equal(9_000m, pilot.MonthlySalary);
        Assert.Equal(50.0, pilot.SkillRating);

        var lease = await verifyDb.Leases.SingleAsync(l => l.Id == leaseId);
        Assert.Equal(30_000m, lease.MonthlyRate);
        Assert.True(lease.IsActive);

        var economyState = await verifyDb.EconomyStates.SingleAsync(e => e.Id == economyStateId);
        Assert.Equal(now, economyState.LastProcessedUtc);
        Assert.Equal(0.85m, economyState.FuelPricePerKg);
        Assert.Equal(42, economyState.WorldSeed);
        // New nullable watermark - must start null (never bootstrapped by the migration itself;
        // VirtualFlightResolverService's own first pass does that) so a pre-existing economy state
        // never appears to have already caught up on virtual-flight resolution it never ran.
        Assert.Null(economyState.LastScheduleResolvedUtc);

        // The new tables exist and are empty - no phantom rows conjured for pre-existing pilots.
        Assert.Empty(await verifyDb.PilotSchedules.ToListAsync());
        Assert.Empty(await verifyDb.PilotScheduleEntries.ToListAsync());
    }

    private static string BuildSeedSql() => """
        INSERT INTO Airlines (Id, Name, IcaoCode, HomeAirportIcao, StrategyProfile, Playstyle, AccentColour, ReputationScore, OwnerUserId, CreatedUtc, DeletedUtc)
        VALUES ($airlineId, 'Migration Test Air', 'MGT', 'EGGD', 'Domestic', 'Casual', '#3b82f6', 50, $airlineId, $now, NULL);

        INSERT INTO Airports (Icao, Iata, Name, Municipality, Country, Latitude, Longitude, ElevationFt, SizeCategory, HasScheduledService, LongestRunwayFt)
        VALUES ('EGGD', 'BRS', 'Bristol Airport', 'Bristol', 'GB', 51.3827, -2.7191, 622, 'Medium', 1, 8000);

        INSERT INTO Airports (Icao, Iata, Name, Municipality, Country, Latitude, Longitude, ElevationFt, SizeCategory, HasScheduledService, LongestRunwayFt)
        VALUES ('EGPH', 'EDI', 'Edinburgh Airport', 'Edinburgh', 'GB', 55.9500, -3.3725, 136, 'Medium', 1, 8500);

        INSERT INTO AircraftTypes (Id, IcaoType, Family, Manufacturer, Name, PaxCapacity, RangeNm, CruiseTasKts, FuelBurnKgPerHour, MtowTonnes, MinRunwayFt, ServiceCeilingFt, PurchasePrice, MonthlyLeaseRate, MatchPatterns)
        VALUES ($aircraftTypeId, 'A320', 'A320', 'Airbus', 'A320neo', 180, 3400, 450, 2400, 78.0, 5500, 39000, 47500000, 30000, '[]');

        INSERT INTO FleetAircraft (Id, AirlineId, AircraftTypeId, Registration, Ownership, AirframeHours, HoursSinceACheck, HoursSinceCCheck, ConditionPercent, FuelOnBoardKg, LocationIcao, Status, GroundedUntilUtc, CreatedUtc, DeletedUtc)
        VALUES ($hubAircraftId, $airlineId, $aircraftTypeId, 'G-HUBB', 'Leased', 1234.5, 34.5, 1234.5, 87.5, 3200.0, 'EGGD', 'Active', NULL, $now, NULL);

        INSERT INTO FleetAircraft (Id, AirlineId, AircraftTypeId, Registration, Ownership, AirframeHours, HoursSinceACheck, HoursSinceCCheck, ConditionPercent, FuelOnBoardKg, LocationIcao, Status, GroundedUntilUtc, CreatedUtc, DeletedUtc)
        VALUES ($awayAircraftId, $airlineId, $aircraftTypeId, 'G-AWAY', 'Leased', 40.0, 40.0, 40.0, 98.0, 500.0, 'EGPH', 'Active', NULL, $awayCreatedUtc, NULL);

        INSERT INTO Routes (Id, AirlineId, DepartureIcao, ArrivalIcao, FlightNumber, DistanceNm, BaseFare, IsActive, CreatedUtc, DeletedUtc)
        VALUES ($routeId, $airlineId, 'EGGD', 'EGPH', '101', 275.2, 89.00, 1, $now, NULL);

        INSERT INTO Flights (Id, AirlineId, RouteId, ScheduleId, FleetAircraftId, PilotId, Status, PlannedDepartureUtc, PlannedBlockMinutes,
            OutUtc, OffUtc, OnUtc, InUtc, PaxBooked, PaxFlown, FuelPlannedKg, FuelUsedKg, LandingFpmFirst, LandingFpmHardest, LandingGForce,
            CentrelineDeviationM, TitleFlown, TypeMismatch, SimRateElevated, MaxSimulationRateObserved, SlewDetected, PositionJumpDetected,
            Revenue, TotalCost, RevenuePosted, CreatedUtc, DeletedUtc)
        VALUES ($flightId, $airlineId, $routeId, NULL, $hubAircraftId, $pilotId, 'Completed', $now, 65,
            $now, $now, $now, $now, 142, 142, 2100.0, 2050.0, 145.2, 210.5, 1.18,
            12.4, 'Airbus A320neo', 0, 0, 1.0, 0, 0,
            6231.75, 3814.20, 1, $now, NULL);

        INSERT INTO LedgerTransactions (Id, AirlineId, Utc, Category, Amount, FlightId, Description)
        VALUES ($ledgerId1, $airlineId, $now, 'StartingCapital', 2000000.00, NULL, 'Starting capital');
        INSERT INTO LedgerTransactions (Id, AirlineId, Utc, Category, Amount, FlightId, Description)
        VALUES ($ledgerId2, $airlineId, $now, 'LeasePayment', -30000.00, NULL, 'Lease deposit');
        INSERT INTO LedgerTransactions (Id, AirlineId, Utc, Category, Amount, FlightId, Description)
        VALUES ($ledgerId3, $airlineId, $now, 'Fuel', -1186.00, $flightId, 'Fuel uplift at EGGD');
        INSERT INTO LedgerTransactions (Id, AirlineId, Utc, Category, Amount, FlightId, Description)
        VALUES ($ledgerId4, $airlineId, $now, 'TicketRevenue', 6231.75, $flightId, 'Ticket revenue: 142 pax x 89.00');
        INSERT INTO LedgerTransactions (Id, AirlineId, Utc, Category, Amount, FlightId, Description)
        VALUES ($ledgerId5, $airlineId, $now, 'LandingFees', -1200.50, $flightId, 'Landing fee at EGPH');
        INSERT INTO LedgerTransactions (Id, AirlineId, Utc, Category, Amount, FlightId, Description)
        VALUES ($ledgerId6, $airlineId, $now, 'Handling', -615.30, $flightId, 'Handling fee at EGPH');

        INSERT INTO Pilots (Id, AirlineId, Name, IsPlayer, MonthlySalary, HoursFlown, SkillRating, Status, CreatedUtc, DeletedUtc)
        VALUES ($pilotId, $airlineId, 'Captain Migration', 1, 9000, 65.0, 50.0, 'Available', $now, NULL);

        INSERT INTO Leases (Id, AirlineId, FleetAircraftId, MonthlyRate, StartUtc, EndUtc, IsActive, CreatedUtc, DeletedUtc)
        VALUES ($leaseId, $airlineId, $hubAircraftId, 30000, $now, NULL, 1, $now, NULL);

        INSERT INTO EconomyStates (Id, LastProcessedUtc, FuelPricePerKg, WorldSeed)
        VALUES ($economyStateId, $now, 0.85, 42);
        """;

    private static void BindSeedParameters(
        SqliteCommand cmd, Guid airlineId, Guid hubAircraftId, Guid awayAircraftId, Guid aircraftTypeId,
        Guid routeId, Guid flightId, Guid pilotId, Guid leaseId, Guid economyStateId, DateTimeOffset now)
    {
        cmd.Parameters.AddWithValue("$airlineId", airlineId.ToString().ToUpperInvariant());
        cmd.Parameters.AddWithValue("$hubAircraftId", hubAircraftId.ToString().ToUpperInvariant());
        cmd.Parameters.AddWithValue("$awayAircraftId", awayAircraftId.ToString().ToUpperInvariant());
        cmd.Parameters.AddWithValue("$aircraftTypeId", aircraftTypeId.ToString().ToUpperInvariant());
        cmd.Parameters.AddWithValue("$routeId", routeId.ToString().ToUpperInvariant());
        cmd.Parameters.AddWithValue("$flightId", flightId.ToString().ToUpperInvariant());
        cmd.Parameters.AddWithValue("$pilotId", pilotId.ToString().ToUpperInvariant());
        cmd.Parameters.AddWithValue("$leaseId", leaseId.ToString().ToUpperInvariant());
        cmd.Parameters.AddWithValue("$economyStateId", economyStateId.ToString().ToUpperInvariant());
        cmd.Parameters.AddWithValue("$now", now);
        // Older CreatedUtc than the hub aircraft's, so the migration's tie-break-by-CreatedUtc rule
        // is exercised too, not just the hub-location rule - both point the same direction here
        // (older AND away-from-hub), so a bug that used only one criterion would still pass; the
        // real discriminator is that G-AWAY is not at HomeAirportIcao at all.
        cmd.Parameters.AddWithValue("$awayCreatedUtc", now.AddDays(-10));
        for (var i = 1; i <= 6; i++)
        {
            cmd.Parameters.AddWithValue($"$ledgerId{i}", Guid.NewGuid().ToString().ToUpperInvariant());
        }
    }
}
