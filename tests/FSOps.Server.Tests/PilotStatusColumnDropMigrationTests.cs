using FSOps.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace FSOps.Server.Tests;

/// <summary>
/// Proves DropPilotStatus is non-destructive. Like every other column drop on SQLite this is a
/// whole-table rebuild, and Pilots is the table that carries career state: hours flown, the
/// last-flew timestamp idle decay is measured against, salary, and the soft-delete tombstone that
/// decides whether a released pilot stays released. Losing any of those would be unrecoverable in a
/// way "the status column went missing" would not be.
/// <para>
/// Three rows on purpose: a veteran with every field set to a deliberately non-default value (so a
/// silent reset cannot hide behind a coincidence), a fresh hire exercising the nullable
/// <c>LastFlewUtc</c>, and a soft-deleted pilot - because a rebuild that dropped or cleared
/// <c>DeletedUtc</c> would either resurrect a released pilot onto the roster or make a live one
/// disappear, and neither would be obvious from a row count.
/// </para>
/// <para>
/// Read back with raw SQL rather than through <c>FsOpsDbContext</c>, and deliberately so - see
/// <see cref="PinnedSchemaRead"/>. The database here is pinned to one migration on purpose, while EF
/// only ever has the current model and builds its SELECT from it; reading through EF therefore asks
/// for columns that do not exist yet and fails the first time anyone adds a field to Pilots. The
/// sibling UserSettings test broke exactly that way when a settings column was added, and the error
/// (<c>no such column</c>) points at the new migration rather than at the read, so it costs whoever
/// hits it a search for a bug that is not there. Converting this back to <c>db.Pilots</c> because it
/// would read more neatly re-arms that.
/// </para>
/// </summary>
public class PilotStatusColumnDropMigrationTests
{
    private const string PreviousMigration = "20260814100030_DropCommunityFolderPath";

    private const string MigrationUnderTest = "20260814100936_DropPilotStatus";

    [Fact]
    public async Task DroppingPilotStatus_KeepsEveryCareerFieldIncludingTheSoftDeleteTombstone()
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
        var veteranId = Guid.NewGuid();
        var freshId = Guid.NewGuid();
        var releasedId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 13, 9, 30, 0, TimeSpan.Zero);
        var veteranLastFlew = now.AddDays(-2);
        var releasedLastFlew = now.AddDays(-40);
        var releasedDeleted = now.AddDays(-35);

        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO Airlines (Id, Name, IcaoCode, HomeAirportIcao, StrategyProfile, Playstyle, AccentColour, ReputationScore, OwnerUserId, CreatedUtc, DeletedUtc)
                VALUES ($airlineId, 'Severn Air', 'SVN', 'EGGD', 'Domestic', 'TrueLife', '#f59e0b', 63.5, $airlineId, $created, NULL);

                INSERT INTO Pilots (Id, AirlineId, Name, IsPlayer, MonthlySalary, HoursFlown, SkillRating, LastFlewUtc, Status, CreatedUtc, DeletedUtc)
                VALUES ($veteranId, $airlineId, 'R. Whittle', 1, 4250.55, 1873.25, 71.75, $veteranLastFlew, 'Available', $created, NULL);

                INSERT INTO Pilots (Id, AirlineId, Name, IsPlayer, MonthlySalary, HoursFlown, SkillRating, LastFlewUtc, Status, CreatedUtc, DeletedUtc)
                VALUES ($freshId, $airlineId, 'A. Newbold', 0, 9000.00, 0, 50, NULL, 'Available', $created, NULL);

                INSERT INTO Pilots (Id, AirlineId, Name, IsPlayer, MonthlySalary, HoursFlown, SkillRating, LastFlewUtc, Status, CreatedUtc, DeletedUtc)
                VALUES ($releasedId, $airlineId, 'D. Gorse', 0, 8750.25, 412.5, 63.25, $releasedLastFlew, 'Available', $created, $releasedDeleted);
                """;
            seed.Parameters.AddWithValue("$airlineId", airlineId.ToString().ToUpperInvariant());
            seed.Parameters.AddWithValue("$veteranId", veteranId.ToString().ToUpperInvariant());
            seed.Parameters.AddWithValue("$freshId", freshId.ToString().ToUpperInvariant());
            seed.Parameters.AddWithValue("$releasedId", releasedId.ToString().ToUpperInvariant());
            seed.Parameters.AddWithValue("$created", now.AddDays(-90));
            seed.Parameters.AddWithValue("$veteranLastFlew", veteranLastFlew);
            seed.Parameters.AddWithValue("$releasedLastFlew", releasedLastFlew);
            seed.Parameters.AddWithValue("$releasedDeleted", releasedDeleted);
            await seed.ExecuteNonQueryAsync();
        }

        await using (var migrateDb = new FsOpsDbContext(options))
        {
            Assert.Contains(MigrationUnderTest, await migrateDb.Database.GetPendingMigrationsAsync());
            var migrator = migrateDb.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrator.MigrateAsync(MigrationUnderTest);
        }

        const string selectById = """
            SELECT Name, IsPlayer, MonthlySalary, HoursFlown, SkillRating, LastFlewUtc, CreatedUtc, DeletedUtc
            FROM Pilots WHERE Id = $id;
            """;

        Task<PilotRow> ReadPilotAsync(Guid id) => PinnedSchemaRead.SingleAsync(
            connection,
            selectById,
            c => c.Parameters.AddWithValue("$id", id.ToString().ToUpperInvariant()),
            r => new PilotRow(
                r.GetString(r.GetOrdinal("Name")),
                r.GetBoolean(r.GetOrdinal("IsPlayer")),
                r.GetDecimal(r.GetOrdinal("MonthlySalary")),
                r.GetDouble(r.GetOrdinal("HoursFlown")),
                r.GetDouble(r.GetOrdinal("SkillRating")),
                r.TimestampOrNull("LastFlewUtc"),
                r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("CreatedUtc")),
                r.TimestampOrNull("DeletedUtc")));

        var veteran = await ReadPilotAsync(veteranId);
        Assert.Equal("R. Whittle", veteran.Name);
        Assert.True(veteran.IsPlayer);
        Assert.Equal(4250.55m, veteran.MonthlySalary);
        Assert.Equal(1873.25, veteran.HoursFlown);
        Assert.Equal(71.75, veteran.SkillRating);
        Assert.Equal(veteranLastFlew, veteran.LastFlewUtc);
        Assert.Equal(now.AddDays(-90), veteran.CreatedUtc);
        Assert.Null(veteran.DeletedUtc);

        var fresh = await ReadPilotAsync(freshId);
        Assert.Equal("A. Newbold", fresh.Name);
        Assert.False(fresh.IsPlayer);
        Assert.Equal(0, fresh.HoursFlown);
        // Still null, not the epoch a careless rebuild would leave behind. "Never flown" is what
        // stops idle decay reaching a brand-new pilot at all - see PilotSkillCalculator.
        Assert.Null(fresh.LastFlewUtc);

        // The soft-deleted pilot must still be soft-deleted: present in the table, with its tombstone
        // intact. Clearing DeletedUtc would put a released pilot back on the roster AND back on the
        // monthly salary run; dropping the row would lose their history outright. The tombstone is
        // asserted directly rather than through EF's query filter, because what this migration is
        // responsible for is the stored value - the filter is model behaviour and is tested elsewhere.
        var released = await ReadPilotAsync(releasedId);
        Assert.Equal("D. Gorse", released.Name);
        Assert.Equal(releasedDeleted, released.DeletedUtc);
        Assert.Equal(releasedLastFlew, released.LastFlewUtc);
        Assert.Equal(412.5, released.HoursFlown);
        Assert.Equal(8750.25m, released.MonthlySalary);

        Assert.Equal(3, await PinnedSchemaRead.CountAsync(connection, "SELECT COUNT(*) FROM Pilots;"));
        Assert.Equal(1, await PinnedSchemaRead.CountAsync(
            connection, "SELECT COUNT(*) FROM Pilots WHERE DeletedUtc IS NOT NULL;"));

        var columns = await PinnedSchemaRead.ColumnNamesAsync(connection, "Pilots");
        Assert.DoesNotContain("Status", columns);
        Assert.Contains("LastFlewUtc", columns);
        Assert.Contains("HoursFlown", columns);
        Assert.Contains("DeletedUtc", columns);

        var indexes = await PinnedSchemaRead.IndexNamesAsync(connection, "Pilots");
        Assert.Contains("IX_Pilots_AirlineId", indexes);
    }

    /// <summary>Exactly the columns this test names - see <see cref="PinnedSchemaRead"/> for why it
    /// is not the entity.</summary>
    private sealed record PilotRow(
        string Name,
        bool IsPlayer,
        decimal MonthlySalary,
        double HoursFlown,
        double SkillRating,
        DateTimeOffset? LastFlewUtc,
        DateTimeOffset CreatedUtc,
        DateTimeOffset? DeletedUtc);
}
