using FSOps.Core.Entities;
using FSOps.Core.SimAircraft;
using FSOps.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace FSOps.Server.Tests;

/// <summary>
/// Proves AddSimAircraftSettings is non-destructive, and proves what it leaves behind on rows that
/// already existed.
///
/// <para>Adding columns is the least alarming migration there is, which is exactly why the defaults
/// are worth testing rather than eyeballing. This project has twice shipped a scaffolded default
/// that was wrong: <c>defaultValue: ""</c> for an enum stored as text, which parses as no member at
/// all, and <c>0.0</c> for a sim rate, which would have read as a frozen clock for every historical
/// flight. Here the failure would be quiet in a new way - a settings row that came back on Premium
/// Deluxe would start offering contracts in aircraft the player does not own, and nothing would look
/// broken while it happened.</para>
///
/// <para>The three nullable columns matter for the opposite reason. NULL means "never scanned" and
/// "no folder configured"; <c>""</c> would mean "configured, to nothing", and a rebuild that turned
/// the first into the second would have FSOps scanning a folder called nothing for ever.</para>
///
/// <para>Run against a real file on disk rather than <c>:memory:</c>, because that is what the user
/// has. An in-memory database cannot demonstrate that the file survives being opened, migrated and
/// reopened, and the whole point of this test is a claim about somebody's real save.</para>
/// </summary>
public class SimAircraftColumnsMigrationTests : IDisposable
{
    private const string PreviousMigration = "20260814112207_AddUpdateChannel";

    private const string MigrationUnderTest = "20260820173217_AddSimAircraftSettings";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fsops-simaircraft-migration-" + Guid.NewGuid().ToString("N"));

    public SimAircraftColumnsMigrationTests() => Directory.CreateDirectory(_directory);

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

    [Fact]
    public async Task AddingTheSimAircraftColumns_LeavesEveryExistingSettingIntact_AndPutsExistingRowsOnStandard()
    {
        var databasePath = Path.Combine(_directory, "fsops.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();

        var options = new DbContextOptionsBuilder<FsOpsDbContext>().UseSqlite(connectionString).Options;
        await using (var bootstrapDb = new FsOpsDbContext(options))
        {
            var migrator = bootstrapDb.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
        }

        var populatedId = Guid.NewGuid();
        var sparseId = Guid.NewGuid();
        var populatedOwner = Guid.NewGuid();
        var sparseOwner = Guid.NewGuid();

        // Every field seeded to a NON-default value, for the reason the sibling migration tests give:
        // seeding the defaults would let a migration that silently reset the table still pass,
        // because the reset value and the seeded value would be the same value.
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO UserSettings (Id, OwnerUserId, CurrencyCode, DistanceUnit, AltitudeUnit, WeightUnit,
                    TimeDisplay, Use24HourClock, Theme, SimBriefPilotId, VatsimCid, UpdateChannel)
                VALUES ($populatedId, $populatedOwner, 'JPY', 'Km', 'Metres', 'Lb', 'Local', 0, 'light',
                    '884422', '1275309', 'Development');

                INSERT INTO UserSettings (Id, OwnerUserId, CurrencyCode, DistanceUnit, AltitudeUnit, WeightUnit,
                    TimeDisplay, Use24HourClock, Theme, SimBriefPilotId, VatsimCid, UpdateChannel)
                VALUES ($sparseId, $sparseOwner, 'GBP', 'Nm', 'Feet', 'Kg', 'Utc', 1, 'dark', NULL, NULL, 'Stable');
                """;
            seed.Parameters.AddWithValue("$populatedId", populatedId.ToString().ToUpperInvariant());
            seed.Parameters.AddWithValue("$sparseId", sparseId.ToString().ToUpperInvariant());
            seed.Parameters.AddWithValue("$populatedOwner", populatedOwner.ToString().ToUpperInvariant());
            seed.Parameters.AddWithValue("$sparseOwner", sparseOwner.ToString().ToUpperInvariant());
            await seed.ExecuteNonQueryAsync();
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

            // Read column by column rather than through verifyDb.UserSettings - see PinnedSchemaRead,
            // and read the rule stated there before writing the next one of these. This test pins the
            // database to one migration, but FsOpsDbContext only ever has the CURRENT model, so an EF
            // read names every column the entity knows about and fails with "no such column" the
            // moment somebody adds a field to UserSettings. Today this migration IS the head, so an
            // EF read would pass by coincidence - which is exactly how the sibling test came to be
            // left armed for six days.
            const string selectById = """
                SELECT OwnerUserId, CurrencyCode, DistanceUnit, AltitudeUnit, WeightUnit, TimeDisplay,
                       Use24HourClock, Theme, SimBriefPilotId, VatsimCid, UpdateChannel,
                       SimEdition, CommunityFolderPath, SimAircraftScanJson, SimAircraftOverridesJson
                FROM UserSettings WHERE Id = $id;
                """;

            var populated = await PinnedSchemaRead.SingleAsync(
                connection,
                selectById,
                c => c.Parameters.AddWithValue("$id", populatedId.ToString().ToUpperInvariant()),
                ReadSettingsRow);

            Assert.Equal(populatedOwner.ToString().ToUpperInvariant(), populated.OwnerUserId);
            Assert.Equal("JPY", populated.CurrencyCode);
            Assert.Equal("Km", populated.DistanceUnit);
            Assert.Equal("Metres", populated.AltitudeUnit);
            Assert.Equal("Lb", populated.WeightUnit);
            Assert.Equal("Local", populated.TimeDisplay);
            Assert.False(populated.Use24HourClock);
            Assert.Equal("light", populated.Theme);
            Assert.Equal("884422", populated.SimBriefPilotId);
            Assert.Equal("1275309", populated.VatsimCid);
            Assert.Equal(nameof(UpdateChannel.Development), populated.UpdateChannel);

            // THE assertion. A row written before editions existed belongs to somebody who has never
            // been asked which edition they own, and Standard is the smallest aircraft set - so the
            // worst this can cost them is a tick box, never a contract they cannot fly.
            Assert.Equal(nameof(SimEdition.Standard), populated.SimEdition);

            // "not set" and "set to nothing" are different facts. A folder path of "" would have
            // FSOps scanning a folder called nothing, and a scan of "" is not the same as no scan.
            Assert.Null(populated.CommunityFolderPath);
            Assert.Null(populated.SimAircraftScanJson);
            Assert.Null(populated.SimAircraftOverridesJson);

            var sparse = await PinnedSchemaRead.SingleAsync(
                connection,
                selectById,
                c => c.Parameters.AddWithValue("$id", sparseId.ToString().ToUpperInvariant()),
                ReadSettingsRow);

            Assert.Equal(sparseOwner.ToString().ToUpperInvariant(), sparse.OwnerUserId);
            Assert.Equal("GBP", sparse.CurrencyCode);
            Assert.True(sparse.Use24HourClock);
            Assert.Equal(nameof(UpdateChannel.Stable), sparse.UpdateChannel);
            Assert.Equal(nameof(SimEdition.Standard), sparse.SimEdition);
            Assert.Null(sparse.SimBriefPilotId);
            Assert.Null(sparse.VatsimCid);
            Assert.Null(sparse.CommunityFolderPath);

            Assert.Equal(2, await PinnedSchemaRead.CountAsync(connection, "SELECT COUNT(*) FROM UserSettings;"));

            var columns = await PinnedSchemaRead.ColumnNamesAsync(connection, "UserSettings");
            Assert.Contains("SimEdition", columns);
            Assert.Contains("CommunityFolderPath", columns);
            Assert.Contains("SimAircraftScanJson", columns);
            Assert.Contains("SimAircraftOverridesJson", columns);

            // Everything that was already there is still there, by name. A rebuild that dropped one
            // would be caught by the reads above, but only for the fields those reads happen to
            // cover; this catches the whole shape.
            foreach (var existing in new[]
                     {
                         "Id", "OwnerUserId", "CurrencyCode", "DistanceUnit", "AltitudeUnit", "WeightUnit",
                         "TimeDisplay", "Use24HourClock", "Theme", "SimBriefPilotId", "VatsimCid", "UpdateChannel",
                     })
            {
                Assert.Contains(existing, columns);
            }

            // Read as raw text, not through the enum converter, so this asserts what is physically in
            // the file. An enum round-trip would happily turn a stored "" into member zero and report
            // Standard while the column held nothing a later reader could parse.
            var stored = await PinnedSchemaRead.DistinctTextAsync(connection, "SELECT DISTINCT SimEdition FROM UserSettings;");
            Assert.Equal(new[] { "Standard" }, stored);

            var paths = await PinnedSchemaRead.DistinctTextAsync(connection, "SELECT DISTINCT CommunityFolderPath FROM UserSettings;");
            Assert.Equal(new[] { "<null>" }, paths);

            // Adding columns with and without defaults is a real ALTER TABLE on SQLite - no table
            // rebuild - so the unique index is never dropped. Asserted anyway: "it should not have
            // been touched" is the claim, and an index quietly missing is how a second settings row
            // for one user gets written and every lookup expecting exactly one starts throwing.
            var indexes = await PinnedSchemaRead.IndexNamesAsync(connection, "UserSettings");
            Assert.Contains("IX_UserSettings_OwnerUserId", indexes);
        }
    }

    /// <summary>
    /// A settings row created after the migration must also be Standard. The column default covers
    /// rows that already existed; this covers the other half - that the entity's own default agrees
    /// with it, rather than the two disagreeing and the answer depending on which one wrote the row.
    /// </summary>
    [Fact]
    public async Task ASettingsRowCreatedAfterTheMigration_IsAlsoOnStandardWithNothingScanned()
    {
        var databasePath = Path.Combine(_directory, "fresh.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        var options = new DbContextOptionsBuilder<FsOpsDbContext>().UseSqlite(connectionString).Options;

        await using (var db = new FsOpsDbContext(options))
        {
            await db.Database.MigrateAsync();
            db.UserSettings.Add(new UserSettings { Id = Guid.NewGuid(), OwnerUserId = Guid.NewGuid() });
            await db.SaveChangesAsync();
        }

        await using (var verifyDb = new FsOpsDbContext(options))
        {
            var settings = await verifyDb.UserSettings.SingleAsync();
            Assert.Equal(SimEdition.Standard, settings.SimEdition);
            Assert.Null(settings.CommunityFolderPath);
            Assert.Null(settings.SimAircraftScanJson);
            Assert.Null(settings.SimAircraftOverridesJson);
        }
    }

    /// <summary>Exactly the columns this test names - see <see cref="PinnedSchemaRead"/> for why it
    /// is not the entity. The two enums come back as raw text, for the reason given at their
    /// assertions: a converter round-trip would hide a stored value nothing can parse.</summary>
    private sealed record SettingsRow(
        string OwnerUserId,
        string CurrencyCode,
        string DistanceUnit,
        string AltitudeUnit,
        string WeightUnit,
        string TimeDisplay,
        bool Use24HourClock,
        string Theme,
        string? SimBriefPilotId,
        string? VatsimCid,
        string? UpdateChannel,
        string? SimEdition,
        string? CommunityFolderPath,
        string? SimAircraftScanJson,
        string? SimAircraftOverridesJson);

    private static SettingsRow ReadSettingsRow(SqliteDataReader r) =>
        new(
            r.GetString(r.GetOrdinal("OwnerUserId")),
            r.GetString(r.GetOrdinal("CurrencyCode")),
            r.GetString(r.GetOrdinal("DistanceUnit")),
            r.GetString(r.GetOrdinal("AltitudeUnit")),
            r.GetString(r.GetOrdinal("WeightUnit")),
            r.GetString(r.GetOrdinal("TimeDisplay")),
            r.GetBoolean(r.GetOrdinal("Use24HourClock")),
            r.GetString(r.GetOrdinal("Theme")),
            r.TextOrNull("SimBriefPilotId"),
            r.TextOrNull("VatsimCid"),
            r.TextOrNull("UpdateChannel"),
            r.TextOrNull("SimEdition"),
            r.TextOrNull("CommunityFolderPath"),
            r.TextOrNull("SimAircraftScanJson"),
            r.TextOrNull("SimAircraftOverridesJson"));
}
