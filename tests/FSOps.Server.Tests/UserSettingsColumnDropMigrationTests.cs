using FSOps.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace FSOps.Server.Tests;

/// <summary>
/// Proves DropCommunityFolderPath is non-destructive. Dropping a column on SQLite is not an
/// <c>ALTER TABLE</c>: EF rebuilds the whole table, copies every row across and recreates the
/// indexes, so "we only removed one column" is a claim that has to be demonstrated rather than
/// assumed - the user's settings row is the one that survives a reinstall.
/// <para>
/// Every field is seeded to a NON-DEFAULT value on purpose. Seeding the defaults would let a
/// migration that silently reset the table still pass, because the reset value and the seeded value
/// would coincide; "GBP survived" proves nothing when GBP is also what a fresh row would hold.
/// </para>
/// <para>
/// The rows are read back with raw SQL rather than through <c>FsOpsDbContext</c>, and that is not a
/// style choice - see <see cref="PinnedSchemaRead"/>. This test pins the database to one migration
/// deliberately, and EF only has the current model, so an EF read asks for every column the model
/// knows about and breaks the moment anyone adds a field to UserSettings. It did exactly that when
/// UpdateChannel was added. Reading the columns this test actually names keeps the pin honest.
/// </para>
/// <para>
/// A happy side effect: asserting on the stored text asserts what is physically in the file. An enum
/// round-trip through EF would turn a stored "" into member zero and report a cheerful default while
/// the column held something no later reader could parse - which is the exact shape of a bug this
/// project has shipped before.
/// </para>
/// </summary>
public class UserSettingsColumnDropMigrationTests
{
    private const string PreviousMigration = "20260812130846_AddVatsimOnlineCorroboration";

    private const string MigrationUnderTest = "20260814100030_DropCommunityFolderPath";

    [Fact]
    public async Task DroppingCommunityFolderPath_KeepsEveryOtherSettingAndTheUniqueIndex()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<FsOpsDbContext>().UseSqlite(connection).Options;
        await using (var bootstrapDb = new FsOpsDbContext(options))
        {
            var migrator = bootstrapDb.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
        }

        var populatedId = Guid.NewGuid();
        var sparseId = Guid.NewGuid();
        var populatedOwner = Guid.NewGuid();
        var sparseOwner = Guid.NewGuid();
        const string communityFolder = @"C:\MSFS\Packages\Community";

        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO UserSettings (Id, OwnerUserId, CurrencyCode, DistanceUnit, AltitudeUnit, WeightUnit,
                    TimeDisplay, Use24HourClock, Theme, CommunityFolderPath, SimBriefPilotId, VatsimCid)
                VALUES ($populatedId, $populatedOwner, 'JPY', 'Km', 'Metres', 'Lb', 'Local', 0, 'light',
                    $communityFolder, '884422', '1275309');

                INSERT INTO UserSettings (Id, OwnerUserId, CurrencyCode, DistanceUnit, AltitudeUnit, WeightUnit,
                    TimeDisplay, Use24HourClock, Theme, CommunityFolderPath, SimBriefPilotId, VatsimCid)
                VALUES ($sparseId, $sparseOwner, 'GBP', 'Nm', 'Feet', 'Kg', 'Utc', 1, 'dark',
                    NULL, NULL, NULL);
                """;
            seed.Parameters.AddWithValue("$populatedId", populatedId.ToString().ToUpperInvariant());
            seed.Parameters.AddWithValue("$sparseId", sparseId.ToString().ToUpperInvariant());
            seed.Parameters.AddWithValue("$populatedOwner", populatedOwner.ToString().ToUpperInvariant());
            seed.Parameters.AddWithValue("$sparseOwner", sparseOwner.ToString().ToUpperInvariant());
            seed.Parameters.AddWithValue("$communityFolder", communityFolder);
            await seed.ExecuteNonQueryAsync();
        }

        // Migrated to exactly this migration rather than all the way to the head, so a migration
        // added later can never quietly become part of what this test claims to have proved.
        await using (var migrateDb = new FsOpsDbContext(options))
        {
            Assert.Contains(MigrationUnderTest, await migrateDb.Database.GetPendingMigrationsAsync());
            var migrator = migrateDb.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync(MigrationUnderTest);
        }

        const string selectById = """
            SELECT OwnerUserId, CurrencyCode, DistanceUnit, AltitudeUnit, WeightUnit, TimeDisplay,
                   Use24HourClock, Theme, SimBriefPilotId, VatsimCid
            FROM UserSettings WHERE Id = $id;
            """;

        var populated = await PinnedSchemaRead.SingleAsync(
            connection,
            selectById,
            c => c.Parameters.AddWithValue("$id", populatedId.ToString().ToUpperInvariant()),
            r => new SettingsRow(
                r.GetString(r.GetOrdinal("OwnerUserId")),
                r.GetString(r.GetOrdinal("CurrencyCode")),
                r.GetString(r.GetOrdinal("DistanceUnit")),
                r.GetString(r.GetOrdinal("AltitudeUnit")),
                r.GetString(r.GetOrdinal("WeightUnit")),
                r.GetString(r.GetOrdinal("TimeDisplay")),
                r.GetBoolean(r.GetOrdinal("Use24HourClock")),
                r.GetString(r.GetOrdinal("Theme")),
                r.TextOrNull("SimBriefPilotId"),
                r.TextOrNull("VatsimCid")));

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

        // The nullable columns must still read back as null, not as the empty string a careless
        // rebuild would leave behind - "not set" and "set to nothing" are different facts, and
        // SimBriefPilotId = "" would make the app try to fetch a plan for pilot number nothing.
        var sparse = await PinnedSchemaRead.SingleAsync(
            connection,
            selectById,
            c => c.Parameters.AddWithValue("$id", sparseId.ToString().ToUpperInvariant()),
            r => new SettingsRow(
                r.GetString(r.GetOrdinal("OwnerUserId")),
                r.GetString(r.GetOrdinal("CurrencyCode")),
                r.GetString(r.GetOrdinal("DistanceUnit")),
                r.GetString(r.GetOrdinal("AltitudeUnit")),
                r.GetString(r.GetOrdinal("WeightUnit")),
                r.GetString(r.GetOrdinal("TimeDisplay")),
                r.GetBoolean(r.GetOrdinal("Use24HourClock")),
                r.GetString(r.GetOrdinal("Theme")),
                r.TextOrNull("SimBriefPilotId"),
                r.TextOrNull("VatsimCid")));

        Assert.Equal(sparseOwner.ToString().ToUpperInvariant(), sparse.OwnerUserId);
        Assert.Null(sparse.SimBriefPilotId);
        Assert.Null(sparse.VatsimCid);
        Assert.Equal("GBP", sparse.CurrencyCode);
        Assert.True(sparse.Use24HourClock);

        Assert.Equal(2, await PinnedSchemaRead.CountAsync(connection, "SELECT COUNT(*) FROM UserSettings;"));

        var columns = await PinnedSchemaRead.ColumnNamesAsync(connection, "UserSettings");
        Assert.DoesNotContain("CommunityFolderPath", columns);
        Assert.Contains("SimBriefPilotId", columns);
        Assert.Contains("VatsimCid", columns);

        // The rebuild drops and recreates the table, so the unique index has to come back with it -
        // without it a second settings row for the same user could be written and every lookup that
        // expects exactly one would start failing.
        var indexes = await PinnedSchemaRead.IndexNamesAsync(connection, "UserSettings");
        Assert.Contains("IX_UserSettings_OwnerUserId", indexes);
    }

    /// <summary>Exactly the columns this test names - see <see cref="PinnedSchemaRead"/> for why it
    /// is not the entity.</summary>
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
        string? VatsimCid);
}
