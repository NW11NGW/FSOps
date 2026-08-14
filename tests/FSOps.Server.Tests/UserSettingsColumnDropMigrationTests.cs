using FSOps.Core.Entities;
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

        await using var verifyDb = new FsOpsDbContext(options);

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

        // The nullable columns must still read back as null, not as the empty string a careless
        // rebuild would leave behind - "not set" and "set to nothing" are different facts, and
        // SimBriefPilotId = "" would make the app try to fetch a plan for pilot number nothing.
        var sparse = await verifyDb.UserSettings.SingleAsync(s => s.Id == sparseId);
        Assert.Equal(sparseOwner, sparse.OwnerUserId);
        Assert.Null(sparse.SimBriefPilotId);
        Assert.Null(sparse.VatsimCid);
        Assert.Equal("GBP", sparse.CurrencyCode);
        Assert.True(sparse.Use24HourClock);

        Assert.Equal(2, await verifyDb.UserSettings.CountAsync());

        var columns = await ReadColumnNamesAsync(connection, "UserSettings");
        Assert.DoesNotContain("CommunityFolderPath", columns);
        Assert.Contains("SimBriefPilotId", columns);
        Assert.Contains("VatsimCid", columns);

        // The rebuild drops and recreates the table, so the unique index has to come back with it -
        // without it a second settings row for the same user could be written and every lookup that
        // expects exactly one would start failing.
        var indexes = await ReadIndexNamesAsync(connection, "UserSettings");
        Assert.Contains("IX_UserSettings_OwnerUserId", indexes);
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
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
