using FSOps.Core.Entities;
using FSOps.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace FSOps.Server.Tests;

/// <summary>
/// Proves AddContractFlying is non-destructive, field by field, against a real file on disk.
///
/// <para><b>Why this test is more thorough than the others of its kind.</b> Every previous migration
/// test in this repo covers a table that can be rebuilt by playing - settings, aircraft types, a
/// reputation snapshot. This one covers <c>Flights</c>, which cannot: it is a record of evenings
/// somebody actually spent flying, and there is no way to reconstruct one. Worse, this migration is
/// not an additive <c>ALTER TABLE</c>. Widening two columns from NOT NULL to NULL makes SQLite
/// <b>rebuild the entire table</b> and copy every row across, which is the single most dangerous
/// shape of migration this project has ever shipped.</para>
///
/// <para>So this seeds realistic history - a flight in every status, both landing outcomes, all three
/// states of the three-valued <c>TypeMismatch</c>, the VATSIM corroboration fields present and
/// absent, elevated sim rate, slew, position jump, OOOI stamps present and missing - and reads back
/// <b>every column of every row</b>, not a sample. It also attaches FlightEvent and LedgerTransaction
/// rows and proves those still point at the flights they belong to afterwards, because a table
/// rebuild changes the identity of nothing but is exactly when a relationship would quietly break.
/// </para>
///
/// <para>Read through <see cref="PinnedSchemaRead"/> rather than EF, per the rule stated on that
/// class. The database here is pinned to this migration; an EF read would name every column the
/// CURRENT model knows about and would start failing with "no such column" the moment somebody adds
/// a field to Flight - a failure that misdirects, because it reads as a fault in the new migration
/// rather than in how this test reads.</para>
/// </summary>
public class ContractFlyingMigrationTests : IDisposable
{
    private const string PreviousMigration = "20260820173217_AddSimAircraftSettings";

    private const string MigrationUnderTest = "20260820183206_AddContractFlying";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fsops-contract-migration-" + Guid.NewGuid().ToString("N"));

    public ContractFlyingMigrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a passing test over.
        }
    }

    /// <summary>
    /// Every seeded flight, in the shape it was written. Deliberately not the entity - see the class
    /// doc and <see cref="PinnedSchemaRead"/>. Enum-backed and money columns come back as raw text so
    /// this asserts what is <b>physically</b> in the file rather than what a converter is willing to
    /// make of it.
    /// </summary>
    private sealed record FlightRow(
        string Id, string AirlineId, string? RouteId, string? FleetAircraftId, string? ScheduleId, string PilotId,
        string Status, DateTimeOffset PlannedDepartureUtc, int PlannedBlockMinutes,
        DateTimeOffset? OutUtc, DateTimeOffset? OffUtc, DateTimeOffset? OnUtc, DateTimeOffset? InUtc,
        int PaxBooked, int PaxFlown, double FuelPlannedKg, double FuelUsedKg,
        double? LandingFpmFirst, double? LandingFpmHardest, double? LandingGForce, double? CentrelineDeviationM,
        string TitleFlown, bool? TypeMismatch,
        bool SimRateElevated, double MaxSimulationRateObserved, bool SlewDetected, bool PositionJumpDetected,
        string Revenue, string TotalCost, bool RevenuePosted, string? UnflyableReason,
        bool? VatsimOnline, string? VatsimCallsign, double? VatsimOnlineFraction, string? VatsimControllersWorked,
        DateTimeOffset CreatedUtc, DateTimeOffset? DeletedUtc);

    [Fact]
    public async Task AddingContractFlying_RebuildsFlights_AndChangesNotOneFieldOfExistingHistory()
    {
        var databasePath = Path.Combine(_directory, "fsops.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        var options = new DbContextOptionsBuilder<FsOpsDbContext>().UseSqlite(connectionString).Options;

        await using (var bootstrapDb = new FsOpsDbContext(options))
        {
            var migrator = bootstrapDb.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
        }

        var seeded = BuildSeedRows();

        // Raw SQL, with every GUID UPPER-CASED. Guid.ToString() is lower-case and SQLite text
        // comparison is case-sensitive, so a lower-case GUID inserts a row that is physically present
        // and that every EF Where/Single lookup silently fails to find - a project trap that has cost
        // real debugging time, and one this test would otherwise walk straight into.
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();

            foreach (var row in seeded)
            {
                await using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO Flights (
                        Id, AirlineId, RouteId, FleetAircraftId, ScheduleId, PilotId, Status,
                        PlannedDepartureUtc, PlannedBlockMinutes, OutUtc, OffUtc, OnUtc, InUtc,
                        PaxBooked, PaxFlown, FuelPlannedKg, FuelUsedKg,
                        LandingFpmFirst, LandingFpmHardest, LandingGForce, CentrelineDeviationM,
                        TitleFlown, TypeMismatch, SimRateElevated, MaxSimulationRateObserved,
                        SlewDetected, PositionJumpDetected, Revenue, TotalCost, RevenuePosted,
                        UnflyableReason, VatsimOnline, VatsimCallsign, VatsimOnlineFraction,
                        VatsimControllersWorked, CreatedUtc, DeletedUtc)
                    VALUES (
                        $Id, $AirlineId, $RouteId, $FleetAircraftId, $ScheduleId, $PilotId, $Status,
                        $PlannedDepartureUtc, $PlannedBlockMinutes, $OutUtc, $OffUtc, $OnUtc, $InUtc,
                        $PaxBooked, $PaxFlown, $FuelPlannedKg, $FuelUsedKg,
                        $LandingFpmFirst, $LandingFpmHardest, $LandingGForce, $CentrelineDeviationM,
                        $TitleFlown, $TypeMismatch, $SimRateElevated, $MaxSimulationRateObserved,
                        $SlewDetected, $PositionJumpDetected, $Revenue, $TotalCost, $RevenuePosted,
                        $UnflyableReason, $VatsimOnline, $VatsimCallsign, $VatsimOnlineFraction,
                        $VatsimControllersWorked, $CreatedUtc, $DeletedUtc);
                    """;

                Bind(insert, "$Id", row.Id);
                Bind(insert, "$AirlineId", row.AirlineId);
                Bind(insert, "$RouteId", row.RouteId);
                Bind(insert, "$FleetAircraftId", row.FleetAircraftId);
                Bind(insert, "$ScheduleId", row.ScheduleId);
                Bind(insert, "$PilotId", row.PilotId);
                Bind(insert, "$Status", row.Status);
                Bind(insert, "$PlannedDepartureUtc", Text(row.PlannedDepartureUtc));
                Bind(insert, "$PlannedBlockMinutes", row.PlannedBlockMinutes);
                Bind(insert, "$OutUtc", Text(row.OutUtc));
                Bind(insert, "$OffUtc", Text(row.OffUtc));
                Bind(insert, "$OnUtc", Text(row.OnUtc));
                Bind(insert, "$InUtc", Text(row.InUtc));
                Bind(insert, "$PaxBooked", row.PaxBooked);
                Bind(insert, "$PaxFlown", row.PaxFlown);
                Bind(insert, "$FuelPlannedKg", row.FuelPlannedKg);
                Bind(insert, "$FuelUsedKg", row.FuelUsedKg);
                Bind(insert, "$LandingFpmFirst", row.LandingFpmFirst);
                Bind(insert, "$LandingFpmHardest", row.LandingFpmHardest);
                Bind(insert, "$LandingGForce", row.LandingGForce);
                Bind(insert, "$CentrelineDeviationM", row.CentrelineDeviationM);
                Bind(insert, "$TitleFlown", row.TitleFlown);
                Bind(insert, "$TypeMismatch", row.TypeMismatch is null ? null : row.TypeMismatch.Value ? 1 : 0);
                Bind(insert, "$SimRateElevated", row.SimRateElevated ? 1 : 0);
                Bind(insert, "$MaxSimulationRateObserved", row.MaxSimulationRateObserved);
                Bind(insert, "$SlewDetected", row.SlewDetected ? 1 : 0);
                Bind(insert, "$PositionJumpDetected", row.PositionJumpDetected ? 1 : 0);
                Bind(insert, "$Revenue", row.Revenue);
                Bind(insert, "$TotalCost", row.TotalCost);
                Bind(insert, "$RevenuePosted", row.RevenuePosted ? 1 : 0);
                Bind(insert, "$UnflyableReason", row.UnflyableReason);
                Bind(insert, "$VatsimOnline", row.VatsimOnline is null ? null : row.VatsimOnline.Value ? 1 : 0);
                Bind(insert, "$VatsimCallsign", row.VatsimCallsign);
                Bind(insert, "$VatsimOnlineFraction", row.VatsimOnlineFraction);
                Bind(insert, "$VatsimControllersWorked", row.VatsimControllersWorked);
                Bind(insert, "$CreatedUtc", Text(row.CreatedUtc));
                Bind(insert, "$DeletedUtc", Text(row.DeletedUtc));

                await insert.ExecuteNonQueryAsync();
            }

            // Attach the two things that hang off a flight. A table rebuild renames nothing and
            // changes no key, but it is precisely the operation during which a relationship would
            // silently break, so it is checked rather than assumed.
            await using var related = connection.CreateCommand();
            related.CommandText = """
                INSERT INTO FlightEvents (Id, FlightId, Utc, Type, PayloadJson)
                VALUES ($e1, $flight, '2026-03-04 09:15:00+00:00', 'PhaseChange', '{"from":"TaxiOut","to":"Climb"}'),
                       ($e2, $flight, '2026-03-04 10:41:00+00:00', 'Touchdown', '{"fpm":-142.5}');

                INSERT INTO LedgerTransactions (Id, AirlineId, Utc, Category, Amount, FlightId, Description)
                VALUES ($l1, $airline, '2026-03-04 10:45:00+00:00', 'TicketRevenue', '18450.75', $flight, 'Ticket revenue'),
                       ($l2, $airline, '2026-03-04 10:45:00+00:00', 'Fuel', '-2210.40', $flight, 'Fuel');
                """;
            Bind(related, "$e1", Upper(Guid.NewGuid()));
            Bind(related, "$e2", Upper(Guid.NewGuid()));
            Bind(related, "$l1", Upper(Guid.NewGuid()));
            Bind(related, "$l2", Upper(Guid.NewGuid()));
            Bind(related, "$flight", seeded[0].Id);
            Bind(related, "$airline", seeded[0].AirlineId);
            await related.ExecuteNonQueryAsync();
        }

        // Migrated to exactly this migration rather than to the head, so a migration added later can
        // never quietly become part of what this test claims to have proved.
        await using (var migrateDb = new FsOpsDbContext(options))
        {
            Assert.Contains(MigrationUnderTest, await migrateDb.Database.GetPendingMigrationsAsync());
            var migrator = migrateDb.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync(MigrationUnderTest);
        }

        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();

            // Nothing was lost and nothing was invented. Checked before the field-by-field reads so a
            // catastrophic rebuild fails with "the rows are gone" rather than with a confusing
            // mismatch on the first column of the first row.
            Assert.Equal(seeded.Count, await PinnedSchemaRead.CountAsync(connection, "SELECT COUNT(*) FROM Flights;"));

            const string selectById = """
                SELECT Id, AirlineId, RouteId, FleetAircraftId, ScheduleId, PilotId, Status,
                       PlannedDepartureUtc, PlannedBlockMinutes, OutUtc, OffUtc, OnUtc, InUtc,
                       PaxBooked, PaxFlown, FuelPlannedKg, FuelUsedKg,
                       LandingFpmFirst, LandingFpmHardest, LandingGForce, CentrelineDeviationM,
                       TitleFlown, TypeMismatch, SimRateElevated, MaxSimulationRateObserved,
                       SlewDetected, PositionJumpDetected, Revenue, TotalCost, RevenuePosted,
                       UnflyableReason, VatsimOnline, VatsimCallsign, VatsimOnlineFraction,
                       VatsimControllersWorked, CreatedUtc, DeletedUtc
                FROM Flights WHERE Id = $id;
                """;

            foreach (var expected in seeded)
            {
                var actual = await PinnedSchemaRead.SingleAsync(
                    connection, selectById, c => c.Parameters.AddWithValue("$id", expected.Id), ReadFlightRow);

                // The whole record in one comparison. A field-at-a-time assertion would let a column
                // added to the seed later be quietly omitted from the checking; comparing the records
                // means every field this test knows about has to match, by construction.
                Assert.Equal(expected, actual);
            }

            // The new column exists and every historical row is NULL in it. Read as raw text, so a
            // stored "" or a zero GUID would be visible rather than being converted into something
            // that reads as sensible.
            var contractLegValues = await PinnedSchemaRead.DistinctTextAsync(
                connection, "SELECT DISTINCT ContractLegId FROM Flights;");
            Assert.Equal(new[] { "<null>" }, contractLegValues);

            // And no historical row acquired a NULL route or a NULL aircraft. Widening a column must
            // not blank it: every one of these flights was flown by the airline, on its own route, in
            // its own aeroplane, and must still say so.
            Assert.Equal(0, await PinnedSchemaRead.CountAsync(
                connection, "SELECT COUNT(*) FROM Flights WHERE RouteId IS NULL OR FleetAircraftId IS NULL;"));

            // The whole shape of the table, by name. The reads above would catch a dropped column
            // they happen to cover; this catches one they do not.
            var columns = await PinnedSchemaRead.ColumnNamesAsync(connection, "Flights");
            foreach (var expectedColumn in new[]
                     {
                         "Id", "AirlineId", "RouteId", "FleetAircraftId", "ScheduleId", "PilotId", "Status",
                         "PlannedDepartureUtc", "PlannedBlockMinutes", "OutUtc", "OffUtc", "OnUtc", "InUtc",
                         "PaxBooked", "PaxFlown", "FuelPlannedKg", "FuelUsedKg", "LandingFpmFirst",
                         "LandingFpmHardest", "LandingGForce", "CentrelineDeviationM", "TitleFlown",
                         "TypeMismatch", "SimRateElevated", "MaxSimulationRateObserved", "SlewDetected",
                         "PositionJumpDetected", "Revenue", "TotalCost", "RevenuePosted", "UnflyableReason",
                         "VatsimOnline", "VatsimCallsign", "VatsimOnlineFraction", "VatsimControllersWorked",
                         "CreatedUtc", "DeletedUtc", "ContractLegId",
                     })
            {
                Assert.Contains(expectedColumn, columns);
            }

            // The index survived the rebuild. An index quietly missing is invisible in the data and
            // only surfaces much later, as a query that got slow or a duplicate nothing rejected.
            Assert.Contains("IX_Flights_AirlineId", await PinnedSchemaRead.IndexNamesAsync(connection, "Flights"));

            // The relationships. Both children still point at a flight that is still there.
            Assert.Equal(2, await PinnedSchemaRead.CountAsync(
                connection,
                $"SELECT COUNT(*) FROM FlightEvents e JOIN Flights f ON f.Id = e.FlightId WHERE f.Id = '{seeded[0].Id}';"));
            Assert.Equal(2, await PinnedSchemaRead.CountAsync(
                connection,
                $"SELECT COUNT(*) FROM LedgerTransactions t JOIN Flights f ON f.Id = t.FlightId WHERE f.Id = '{seeded[0].Id}';"));

            // Money is stored as text in SQLite, so a rebuild that round-tripped it through a numeric
            // type would silently reformat it. The exact strings have to come back.
            var amounts = await PinnedSchemaRead.DistinctTextAsync(
                connection, $"SELECT Amount FROM LedgerTransactions WHERE FlightId = '{seeded[0].Id}' ORDER BY Amount;");
            Assert.Equal(new[] { "-2210.40", "18450.75" }, amounts);

            // The new tables are there and start empty - a migration that arrived with rows in it
            // would mean something seeded during the upgrade.
            Assert.Equal(0, await PinnedSchemaRead.CountAsync(connection, "SELECT COUNT(*) FROM Contracts;"));
            Assert.Equal(0, await PinnedSchemaRead.CountAsync(connection, "SELECT COUNT(*) FROM ContractLegs;"));
            Assert.Contains(
                "IX_Contracts_AirlineId_BoardBucket_BoardSlot",
                await PinnedSchemaRead.IndexNamesAsync(connection, "Contracts"));
        }
    }

    /// <summary>
    /// The other half: once the schema is current, EF can actually read and write the new shape. The
    /// test above proves nothing was lost; this proves the result is usable.
    /// </summary>
    [Fact]
    public async Task AfterTheMigration_AContractFlightRoundTripsThroughEf()
    {
        var databasePath = Path.Combine(_directory, "fresh.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        var options = new DbContextOptionsBuilder<FsOpsDbContext>().UseSqlite(connectionString).Options;

        var contractId = Guid.NewGuid();
        var legId = Guid.NewGuid();
        var flightId = Guid.NewGuid();
        var airlineId = Guid.NewGuid();

        await using (var db = new FsOpsDbContext(options))
        {
            await db.Database.MigrateAsync();

            db.Contracts.Add(new Contract
            {
                Id = contractId,
                AirlineId = airlineId,
                Kind = ContractKind.Ferry,
                Status = ContractStatus.Accepted,
                BoardBucket = 20_400,
                BoardSlot = 3,
                OperatorName = "Meridian Aircraft Sales",
                AircraftTypeDesignator = "C172",
                LoadDescription = "Positioning flight - Cessna 172 Skyhawk, empty",
                Fee = 14_250.55m,
                TotalDistanceNm = 3_120.4,
                TotalPlannedBlockMinutes = 1_680,
                OfferedUtc = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero),
                DeadlineUtc = new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero),
                CreatedUtc = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero),
                Legs =
                {
                    new ContractLeg
                    {
                        Id = legId,
                        ContractId = contractId,
                        Sequence = 1,
                        DepartureIcao = "EGGD",
                        ArrivalIcao = "EGPC",
                        DistanceNm = 425.3,
                        PlannedBlockMinutes = 232,
                        FeeShare = 1_967.42m,
                    },
                },
            });

            db.Flights.Add(new Flight
            {
                Id = flightId,
                AirlineId = airlineId,
                RouteId = null,
                FleetAircraftId = null,
                ContractLegId = legId,
                PilotId = Guid.NewGuid(),
                Status = FlightStatus.Completed,
                PlannedDepartureUtc = new DateTimeOffset(2026, 8, 21, 8, 0, 0, TimeSpan.Zero),
                PlannedBlockMinutes = 232,
                TitleFlown = "Cessna 172 Skyhawk",
                CreatedUtc = new DateTimeOffset(2026, 8, 21, 8, 0, 0, TimeSpan.Zero),
            });

            await db.SaveChangesAsync();
        }

        await using (var verify = new FsOpsDbContext(options))
        {
            var flight = await verify.Flights.AsNoTracking().SingleAsync(f => f.Id == flightId);
            Assert.Null(flight.RouteId);
            Assert.Null(flight.FleetAircraftId);
            Assert.Equal(legId, flight.ContractLegId);

            var contract = await verify.Contracts.Include(c => c.Legs).AsNoTracking().SingleAsync(c => c.Id == contractId);
            Assert.Equal(ContractKind.Ferry, contract.Kind);
            Assert.Equal(ContractStatus.Accepted, contract.Status);
            Assert.Equal(14_250.55m, contract.Fee);
            var leg = Assert.Single(contract.Legs);
            Assert.Equal(1_967.42m, leg.FeeShare);
            Assert.Equal("EGGD", leg.DepartureIcao);
        }

        // Read as raw text too: the enums are stored as TEXT, and a converter round-trip would hide a
        // stored value nothing could parse - the exact failure this project shipped once with an
        // empty-string enum default.
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            Assert.Equal(
                new[] { nameof(ContractKind.Ferry) },
                await PinnedSchemaRead.DistinctTextAsync(connection, "SELECT DISTINCT Kind FROM Contracts;"));
            Assert.Equal(
                new[] { nameof(ContractStatus.Accepted) },
                await PinnedSchemaRead.DistinctTextAsync(connection, "SELECT DISTINCT Status FROM Contracts;"));
        }
    }

    // ---------- Seed data ----------

    /// <summary>
    /// Realistic history, chosen so that every awkward field has at least one row where it is set to
    /// something other than its default and at least one where it is null. Seeding defaults would let
    /// a migration that silently reset the table still pass, because the reset value and the seeded
    /// value would be the same value.
    /// </summary>
    private static List<FlightRow> BuildSeedRows()
    {
        var airline = Upper(Guid.NewGuid());
        var route = Upper(Guid.NewGuid());
        var aircraft = Upper(Guid.NewGuid());
        var pilot = Upper(Guid.NewGuid());
        var schedule = Upper(Guid.NewGuid());

        return
        [
            // An ordinary completed sector with a full landing record and full VATSIM corroboration.
            new FlightRow(
                Upper(Guid.NewGuid()), airline, route, aircraft, null, pilot, nameof(FlightStatus.Completed),
                Utc(2026, 3, 4, 9, 0), 96,
                Utc(2026, 3, 4, 9, 5), Utc(2026, 3, 4, 9, 18), Utc(2026, 3, 4, 10, 41), Utc(2026, 3, 4, 10, 49),
                PaxBooked: 174, PaxFlown: 151, FuelPlannedKg: 5_412.5, FuelUsedKg: 4_988.25,
                LandingFpmFirst: -142.5, LandingFpmHardest: -168.25, LandingGForce: 1.31, CentrelineDeviationM: 3.75,
                TitleFlown: "Airbus A320neo Asobo", TypeMismatch: false,
                SimRateElevated: false, MaxSimulationRateObserved: 1.0, SlewDetected: false, PositionJumpDetected: false,
                Revenue: "18450.75", TotalCost: "12210.40", RevenuePosted: true, UnflyableReason: null,
                VatsimOnline: true, VatsimCallsign: "OLA502", VatsimOnlineFraction: 0.9375,
                VatsimControllersWorked: "EGGD_TWR, EGPH_APP",
                CreatedUtc: Utc(2026, 3, 4, 8, 55), DeletedUtc: null),

            // TypeMismatch TRUE and an elevated sim rate - block time is "not measured" for this one,
            // which is a distinction the column has to keep.
            new FlightRow(
                Upper(Guid.NewGuid()), airline, route, aircraft, null, pilot, nameof(FlightStatus.Completed),
                Utc(2026, 3, 5, 14, 0), 88,
                Utc(2026, 3, 5, 14, 3), Utc(2026, 3, 5, 14, 15), Utc(2026, 3, 5, 14, 52), Utc(2026, 3, 5, 14, 58),
                PaxBooked: 174, PaxFlown: 160, FuelPlannedKg: 5_100.0, FuelUsedKg: 4_770.5,
                LandingFpmFirst: -298.0, LandingFpmHardest: -311.5, LandingGForce: 1.62, CentrelineDeviationM: 11.5,
                TitleFlown: "Boeing 737 MAX 8", TypeMismatch: true,
                SimRateElevated: true, MaxSimulationRateObserved: 8.0, SlewDetected: false, PositionJumpDetected: false,
                Revenue: "17200.00", TotalCost: "11980.10", RevenuePosted: true, UnflyableReason: null,
                VatsimOnline: false, VatsimCallsign: null, VatsimOnlineFraction: 0.0,
                VatsimControllersWorked: null,
                CreatedUtc: Utc(2026, 3, 5, 13, 50), DeletedUtc: null),

            // TypeMismatch NULL - "the sim reported nothing to compare", which is a third state and
            // must never collapse into false. Slew and position jump both set, so this sector is
            // unpayable; VATSIM never checked.
            new FlightRow(
                Upper(Guid.NewGuid()), airline, route, aircraft, null, pilot, nameof(FlightStatus.Completed),
                Utc(2026, 3, 6, 7, 30), 75,
                Utc(2026, 3, 6, 7, 34), null, null, Utc(2026, 3, 6, 8, 40),
                PaxBooked: 174, PaxFlown: 0, FuelPlannedKg: 4_800.0, FuelUsedKg: 0.0,
                LandingFpmFirst: null, LandingFpmHardest: null, LandingGForce: null, CentrelineDeviationM: null,
                TitleFlown: "", TypeMismatch: null,
                SimRateElevated: false, MaxSimulationRateObserved: 1.0, SlewDetected: true, PositionJumpDetected: true,
                Revenue: "0.00", TotalCost: "1840.20", RevenuePosted: true, UnflyableReason: null,
                VatsimOnline: null, VatsimCallsign: null, VatsimOnlineFraction: null, VatsimControllersWorked: null,
                CreatedUtc: Utc(2026, 3, 6, 7, 25), DeletedUtc: null),

            // A virtual pilot's occurrence that could not fly, with a schedule id and a reason.
            new FlightRow(
                Upper(Guid.NewGuid()), airline, route, aircraft, schedule, pilot, nameof(FlightStatus.Cancelled),
                Utc(2026, 3, 7, 6, 0), 90,
                null, null, null, null,
                PaxBooked: 0, PaxFlown: 0, FuelPlannedKg: 0.0, FuelUsedKg: 0.0,
                LandingFpmFirst: null, LandingFpmHardest: null, LandingGForce: null, CentrelineDeviationM: null,
                TitleFlown: "", TypeMismatch: null,
                SimRateElevated: false, MaxSimulationRateObserved: 1.0, SlewDetected: false, PositionJumpDetected: false,
                Revenue: "0.00", TotalCost: "450.00", RevenuePosted: true,
                UnflyableReason: "G-OLAF is still at EGPF from Tuesday",
                VatsimOnline: null, VatsimCallsign: null, VatsimOnlineFraction: null, VatsimControllersWorked: null,
                CreatedUtc: Utc(2026, 3, 7, 5, 55), DeletedUtc: null),

            new FlightRow(
                Upper(Guid.NewGuid()), airline, route, aircraft, schedule, pilot, nameof(FlightStatus.Skipped),
                Utc(2026, 3, 8, 6, 0), 90,
                null, null, null, null,
                0, 0, 0.0, 0.0, null, null, null, null, "", null,
                false, 1.0, false, false, "0.00", "0.00", true, "Aircraft in maintenance",
                null, null, null, null, Utc(2026, 3, 8, 5, 55), null),

            new FlightRow(
                Upper(Guid.NewGuid()), airline, route, aircraft, schedule, pilot, nameof(FlightStatus.Suspended),
                Utc(2026, 3, 9, 6, 0), 90,
                null, null, null, null,
                0, 0, 0.0, 0.0, null, null, null, null, "", null,
                false, 1.0, false, false, "0.00", "0.00", true, "Suspended for a C-check",
                null, null, null, null, Utc(2026, 3, 9, 5, 55), null),

            // Abandoned part-way, with an Out but no In.
            new FlightRow(
                Upper(Guid.NewGuid()), airline, route, aircraft, null, pilot, nameof(FlightStatus.Abandoned),
                Utc(2026, 3, 10, 11, 0), 105,
                Utc(2026, 3, 10, 11, 6), Utc(2026, 3, 10, 11, 21), null, null,
                174, 0, 5_600.0, 812.75, null, null, null, null, "Airbus A320neo Asobo", false,
                false, 1.0, false, false, "0.00", "690.34", true, null,
                null, null, null, null, Utc(2026, 3, 10, 10, 55), null),

            // Interrupted - the sim dropped and never came back.
            new FlightRow(
                Upper(Guid.NewGuid()), airline, route, aircraft, null, pilot, nameof(FlightStatus.Interrupted),
                Utc(2026, 3, 11, 15, 0), 110,
                Utc(2026, 3, 11, 15, 4), Utc(2026, 3, 11, 15, 19), null, null,
                174, 0, 5_700.0, 0.0, null, null, null, null, "Airbus A320neo Asobo", false,
                false, 1.0, false, false, "0.00", "0.00", false, null,
                true, "OLA118", 0.44, "EGPH_APP", Utc(2026, 3, 11, 14, 55), null),

            // Still in progress at the moment of the upgrade - the case a player hits by updating the
            // app mid-flight, which is exactly when a migration must be at its most careful.
            new FlightRow(
                Upper(Guid.NewGuid()), airline, route, aircraft, null, pilot, nameof(FlightStatus.InProgress),
                Utc(2026, 3, 12, 9, 0), 95,
                Utc(2026, 3, 12, 9, 7), null, null, null,
                174, 0, 5_350.5, 0.0, null, null, null, null, "", null,
                false, 1.0, false, false, "0.00", "0.00", false, null,
                null, null, null, null, Utc(2026, 3, 12, 8, 58), null),

            // Soft-deleted. DeletedUtc is how this app recovers from a mistake, so a migration that
            // dropped or cleared it would remove the only route back.
            new FlightRow(
                Upper(Guid.NewGuid()), airline, route, aircraft, null, pilot, nameof(FlightStatus.Completed),
                Utc(2026, 3, 13, 12, 0), 80,
                Utc(2026, 3, 13, 12, 4), Utc(2026, 3, 13, 12, 16), Utc(2026, 3, 13, 13, 14), Utc(2026, 3, 13, 13, 20),
                174, 149, 4_900.0, 4_512.0, -119.5, -119.5, 1.22, 1.5, "Airbus A320neo Asobo", false,
                false, 1.0, false, false, "16980.00", "11040.55", true, null,
                null, null, null, null, Utc(2026, 3, 13, 11, 55), Utc(2026, 3, 14, 9, 0)),
        ];
    }

    private static FlightRow ReadFlightRow(SqliteDataReader r) => new(
        r.GetString(r.GetOrdinal("Id")),
        r.GetString(r.GetOrdinal("AirlineId")),
        r.TextOrNull("RouteId"),
        r.TextOrNull("FleetAircraftId"),
        r.TextOrNull("ScheduleId"),
        r.GetString(r.GetOrdinal("PilotId")),
        r.GetString(r.GetOrdinal("Status")),
        r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("PlannedDepartureUtc")),
        r.GetInt32(r.GetOrdinal("PlannedBlockMinutes")),
        r.TimestampOrNull("OutUtc"),
        r.TimestampOrNull("OffUtc"),
        r.TimestampOrNull("OnUtc"),
        r.TimestampOrNull("InUtc"),
        r.GetInt32(r.GetOrdinal("PaxBooked")),
        r.GetInt32(r.GetOrdinal("PaxFlown")),
        r.GetDouble(r.GetOrdinal("FuelPlannedKg")),
        r.GetDouble(r.GetOrdinal("FuelUsedKg")),
        DoubleOrNull(r, "LandingFpmFirst"),
        DoubleOrNull(r, "LandingFpmHardest"),
        DoubleOrNull(r, "LandingGForce"),
        DoubleOrNull(r, "CentrelineDeviationM"),
        r.GetString(r.GetOrdinal("TitleFlown")),
        BoolOrNull(r, "TypeMismatch"),
        r.GetBoolean(r.GetOrdinal("SimRateElevated")),
        r.GetDouble(r.GetOrdinal("MaxSimulationRateObserved")),
        r.GetBoolean(r.GetOrdinal("SlewDetected")),
        r.GetBoolean(r.GetOrdinal("PositionJumpDetected")),
        r.GetString(r.GetOrdinal("Revenue")),
        r.GetString(r.GetOrdinal("TotalCost")),
        r.GetBoolean(r.GetOrdinal("RevenuePosted")),
        r.TextOrNull("UnflyableReason"),
        BoolOrNull(r, "VatsimOnline"),
        r.TextOrNull("VatsimCallsign"),
        DoubleOrNull(r, "VatsimOnlineFraction"),
        r.TextOrNull("VatsimControllersWorked"),
        r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("CreatedUtc")),
        r.TimestampOrNull("DeletedUtc"));

    private static double? DoubleOrNull(SqliteDataReader r, string column)
    {
        var ordinal = r.GetOrdinal(column);
        return r.IsDBNull(ordinal) ? null : r.GetDouble(ordinal);
    }

    private static bool? BoolOrNull(SqliteDataReader r, string column)
    {
        var ordinal = r.GetOrdinal(column);
        return r.IsDBNull(ordinal) ? null : r.GetBoolean(ordinal);
    }

    private static void Bind(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    /// <summary>UPPER-CASE, always - see the comment at the insert for the trap this avoids.</summary>
    private static string Upper(Guid value) => value.ToString().ToUpperInvariant();

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    /// <summary>The format EF's SQLite provider writes a DateTimeOffset in, so the seeded text and
    /// what the app would have written are the same bytes.</summary>
    private static string? Text(DateTimeOffset? value) =>
        value?.ToString("yyyy-MM-dd HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
}
