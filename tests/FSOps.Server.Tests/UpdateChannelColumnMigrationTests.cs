using FSOps.Core.Entities;
using FSOps.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace FSOps.Server.Tests;

/// <summary>
/// Proves AddUpdateChannel is non-destructive, and - the part that actually matters - proves what
/// value it leaves behind on rows that already existed.
///
/// <para>Adding a column is the least alarming migration there is, which is exactly why the default
/// is worth testing rather than eyeballing. This project has twice shipped a scaffolded default that
/// was wrong: <c>defaultValue: ""</c> for an enum stored as text, which parses as no member at all,
/// and <c>0.0</c> for a sim rate, which would have read as a frozen clock for every historical
/// flight. Here the failure would be quieter still - a settings row that came back on the
/// development channel would start offering untested builds to somebody who never asked for one, and
/// nothing would look broken while it happened.</para>
///
/// <para>Run against a real file on disk rather than <c>:memory:</c>, because that is what the user
/// has. An in-memory database cannot demonstrate that the file survives being opened, migrated and
/// reopened, and the whole point of this test is a claim about somebody's real save.</para>
/// </summary>
public class UpdateChannelColumnMigrationTests : IDisposable
{
    private const string PreviousMigration = "20260814101756_AddReputationSnapshots";

    private const string MigrationUnderTest = "20260814112207_AddUpdateChannel";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fsops-updatechannel-migration-" + Guid.NewGuid().ToString("N"));

    public UpdateChannelColumnMigrationTests() => Directory.CreateDirectory(_directory);

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
    public async Task AddingUpdateChannel_LeavesEveryExistingSettingIntact_AndPutsExistingRowsOnStable()
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
        // seeding the defaults would let a migration that silently reset the table still pass, because
        // the reset value and the seeded value would be the same value.
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO UserSettings (Id, OwnerUserId, CurrencyCode, DistanceUnit, AltitudeUnit, WeightUnit,
                    TimeDisplay, Use24HourClock, Theme, SimBriefPilotId, VatsimCid)
                VALUES ($populatedId, $populatedOwner, 'JPY', 'Km', 'Metres', 'Lb', 'Local', 0, 'light',
                    '884422', '1275309');

                INSERT INTO UserSettings (Id, OwnerUserId, CurrencyCode, DistanceUnit, AltitudeUnit, WeightUnit,
                    TimeDisplay, Use24HourClock, Theme, SimBriefPilotId, VatsimCid)
                VALUES ($sparseId, $sparseOwner, 'GBP', 'Nm', 'Feet', 'Kg', 'Utc', 1, 'dark', NULL, NULL);
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

        await using (var verifyDb = new FsOpsDbContext(options))
        {
            var populated = await verifyDb.UserSettings.SingleAsync(s => s.Id == populatedId);
            Assert.Equal(populatedOwner, populated.OwnerUserId);
            Assert.Equal("JPY", populated.CurrencyCode);
            Assert.Equal(DistanceUnit.Km, populated.DistanceUnit);
            Assert.Equal(AltitudeUnit.Metres, populated.AltitudeUnit);
            Assert.Equal(WeightUnit.Lb, populated.WeightUnit);
            Assert.Equal(TimeDisplay.Local, populated.TimeDisplay);
            Assert.False(populated.Use24HourClock);
            Assert.Equal("light", populated.Theme);
            Assert.Equal("884422", populated.SimBriefPilotId);
            Assert.Equal("1275309", populated.VatsimCid);

            // THE assertion. A row written before channels existed belongs to somebody who never
            // asked for anything but released builds.
            Assert.Equal(UpdateChannel.Stable, populated.UpdateChannel);

            var sparse = await verifyDb.UserSettings.SingleAsync(s => s.Id == sparseId);
            Assert.Equal(sparseOwner, sparse.OwnerUserId);
            Assert.Equal("GBP", sparse.CurrencyCode);
            Assert.True(sparse.Use24HourClock);
            Assert.Equal(UpdateChannel.Stable, sparse.UpdateChannel);

            // "not set" and "set to nothing" are different facts; a careless rebuild turns the first
            // into the second, and SimBriefPilotId = "" would make the app fetch a plan for pilot
            // number nothing.
            Assert.Null(sparse.SimBriefPilotId);
            Assert.Null(sparse.VatsimCid);

            Assert.Equal(2, await verifyDb.UserSettings.CountAsync());
        }

        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();

            var columns = await ReadColumnNamesAsync(connection, "UserSettings");
            Assert.Contains("UpdateChannel", columns);
            Assert.Contains("SimBriefPilotId", columns);
            Assert.Contains("VatsimCid", columns);

            // Read as raw text, not through the enum converter, so this asserts what is physically in
            // the file. An enum round-trip would happily turn a stored "" into member zero and report
            // Stable while the column held nothing a later reader could parse.
            var stored = await ReadDistinctTextAsync(connection, "SELECT DISTINCT UpdateChannel FROM UserSettings;");
            Assert.Equal(new[] { "Stable" }, stored);

            // Adding a column with a default is a real ALTER TABLE on SQLite - no table rebuild - so
            // the unique index is never dropped. Asserted anyway: "it should not have been touched"
            // is the claim, and an index quietly missing is how a second settings row for one user
            // gets written and every lookup expecting exactly one starts throwing.
            var indexes = await ReadIndexNamesAsync(connection, "UserSettings");
            Assert.Contains("IX_UserSettings_OwnerUserId", indexes);
        }
    }

    /// <summary>
    /// A settings row created after the migration must also be Stable. The column default covers rows
    /// that already existed; this covers the other half - that the entity's own default agrees with
    /// it, rather than the two disagreeing and the answer depending on which one wrote the row.
    /// </summary>
    [Fact]
    public async Task ASettingsRowCreatedAfterTheMigration_IsAlsoOnStable()
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
            Assert.Equal(UpdateChannel.Stable, settings.UpdateChannel);
        }
    }

    private static async Task<List<string>> ReadColumnNamesAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        return names;
    }

    private static async Task<List<string>> ReadIndexNamesAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = $table AND name NOT LIKE 'sqlite_%';";
        command.Parameters.AddWithValue("$table", table);
        return await ReadTextColumnAsync(command);
    }

    private static async Task<List<string>> ReadDistinctTextAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await ReadTextColumnAsync(command);
    }

    private static async Task<List<string>> ReadTextColumnAsync(SqliteCommand command)
    {
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.IsDBNull(0) ? "<null>" : reader.GetString(0));
        }

        return values;
    }
}
