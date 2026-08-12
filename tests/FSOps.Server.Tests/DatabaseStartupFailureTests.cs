using FSOps.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FSOps.Server.Tests;

/// <summary>
/// Two startup guarantees, both of which exist because of failures with no way back.
///
/// <para><b>A copy before every migration.</b> docs/PLAN.md names a migration that quietly rewrites
/// data as the one mistake with no recovery. The copy taken beforehand IS that recovery, so it has
/// to be taken before the schema is touched, has to be verified rather than assumed, and has to
/// stop the migration outright if it cannot be taken - proceeding without the safety net is exactly
/// the trade that cannot be undone. It is taken only when something is actually pending, because
/// copying a growing database on every launch would cost real time for no benefit on the
/// overwhelming majority of starts.</para>
///
/// <para><b>A damaged database is explained, not swallowed and not silently repaired.</b> A corrupt
/// file used to terminate the process with an unhandled exception and nothing a person could act
/// on. It now becomes a <see cref="FsOpsDatabaseUnavailableException"/> carrying plain text. These
/// tests pin the two properties that matter most about that text: it names the file, and it tells
/// the reader to copy it somewhere safe FIRST - the only instruction still useful if they get
/// everything else wrong.</para>
/// </summary>
public class DatabaseStartupFailureTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"fsops-dbstartup-{Guid.NewGuid():N}");

    public DatabaseStartupFailureTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void FirstRun_CreatesTheDatabaseAndWritesNoBackup()
    {
        var path = Path.Combine(_directory, "fsops.db");

        BuildProvider(path).MigrateFsOpsDatabase();

        Assert.True(File.Exists(path));

        // Nothing existed to lose, so there was nothing worth copying.
        Assert.False(File.Exists(path + ".bak"));
    }

    [Fact]
    public void SecondRunWithNothingPending_DoesNotRewriteTheBackup()
    {
        var path = Path.Combine(_directory, "fsops.db");
        BuildProvider(path).MigrateFsOpsDatabase();

        // Stand in for a backup left by an earlier migration. If a start with nothing pending
        // took a fresh copy, this sentinel would be replaced by a real database.
        var backupPath = path + ".bak";
        File.WriteAllText(backupPath, "sentinel");

        BuildProvider(path).MigrateFsOpsDatabase();

        Assert.Equal("sentinel", File.ReadAllText(backupPath));
    }

    [Fact]
    public void AMigrationThatIsActuallyPending_LeavesAReadableBackupOfWhatWasThereBefore()
    {
        var path = Path.Combine(_directory, "fsops.db");

        // Migrate to the first migration only, then write a row, so the backup has something in it
        // whose survival can be checked rather than merely its file size.
        var provider = BuildProvider(path);
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FsOpsDbContext>();
            var first = db.Database.GetMigrations().First();
            db.Database.GetInfrastructure()
                .GetRequiredService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>()
                .Migrate(first);
        }

        var marker = Guid.NewGuid().ToString("N");
        ExecuteNonQuery(path, $"CREATE TABLE \"BackupProof\" (\"Marker\" TEXT NOT NULL); INSERT INTO \"BackupProof\" VALUES ('{marker}');");

        // Now the rest of the migrations really are pending, so a backup must be taken.
        BuildProvider(path).MigrateFsOpsDatabase();

        var backupPath = path + ".bak";
        Assert.True(File.Exists(backupPath), "a pending migration must leave a backup behind");
        Assert.True(new FileInfo(backupPath).Length > 0);

        // The point of the copy: the pre-migration content is still readable out of it.
        Assert.Equal(marker, QueryScalar(backupPath, "SELECT \"Marker\" FROM \"BackupProof\" LIMIT 1"));

        // And no temporary file is left lying around.
        Assert.False(File.Exists(backupPath + ".tmp"));
    }

    [Fact]
    public void ADamagedDatabase_IsReportedWithSomethingTheUserCanActOn()
    {
        var path = Path.Combine(_directory, "fsops.db");

        // Not a database at all, but big enough to get past the "empty file" shortcut SQLite takes.
        File.WriteAllBytes(path, System.Text.Encoding.ASCII.GetBytes(new string('x', 8192)));

        var ex = Assert.Throws<FsOpsDatabaseUnavailableException>(() => BuildProvider(path).MigrateFsOpsDatabase());

        Assert.Equal(path, ex.DatabasePath);
        Assert.Contains(path, ex.UserMessage);

        // The instruction that has to survive everything else. Asserted on position, not just
        // presence: it is only useful if it is read before the paragraph describing how to move
        // the file out of the way.
        var copyFirst = ex.UserMessage.IndexOf("copy it somewhere safe", StringComparison.OrdinalIgnoreCase);
        var startOver = ex.UserMessage.IndexOf("start over", StringComparison.OrdinalIgnoreCase);
        Assert.True(copyFirst >= 0, "the message must tell the user to copy the file somewhere safe");
        Assert.True(startOver > copyFirst, "the 'copy it first' instruction must come before the 'start over' one");

        // No stack trace, no exception type name, no SQLite error number.
        Assert.DoesNotContain("SqliteException", ex.UserMessage);
        Assert.DoesNotContain("   at ", ex.UserMessage);
    }

    [Fact]
    public void ADamagedDatabase_IsNeverRepairedDeletedOrRenamedByTheApp()
    {
        var path = Path.Combine(_directory, "fsops.db");
        var content = System.Text.Encoding.ASCII.GetBytes(new string('x', 8192));
        File.WriteAllBytes(path, content);

        Assert.Throws<FsOpsDatabaseUnavailableException>(() => BuildProvider(path).MigrateFsOpsDatabase());

        // Deleting a damaged fsops.db is deleting the user's airline, and a file that looks bad may
        // still be the only copy of their last session. The app explains; the person decides.
        Assert.True(File.Exists(path), "the damaged database must be left exactly where it was");
        Assert.Equal(content, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bak"), "a damaged database must not be copied over a good backup");
    }

    [Fact]
    public void OrdinaryFailuresAreNotDressedUpAsACorruptDatabase()
    {
        // A missing table is a bug, not a damaged file, and must keep propagating as it always did.
        Assert.False(FsOpsDatabaseUnavailableException.IsUnusableDatabase(
            new SqliteException("SQLite Error 1: 'no such table: Airlines'.", 1)));

        Assert.False(FsOpsDatabaseUnavailableException.IsUnusableDatabase(new InvalidOperationException("nope")));
        Assert.False(FsOpsDatabaseUnavailableException.IsUnusableDatabase(null));

        // SQLITE_CORRUPT (11) and SQLITE_NOTADB (26) are the two that mean the file itself is gone.
        Assert.True(FsOpsDatabaseUnavailableException.IsUnusableDatabase(new SqliteException("corrupt", 11)));
        Assert.True(FsOpsDatabaseUnavailableException.IsUnusableDatabase(new SqliteException("not a db", 26)));

        // And it has to see through the wrapping EF does on the way out.
        Assert.True(FsOpsDatabaseUnavailableException.IsUnusableDatabase(
            new InvalidOperationException("wrapped", new SqliteException("corrupt", 11))));
    }

    private static ServiceProvider BuildProvider(string databasePath)
    {
        var services = new ServiceCollection();
        // Pooling=False so a disposed provider actually releases the file, letting these tests read
        // the database back off disk afterwards. The real app keeps pooling; only the backup's own
        // two connections turn it off, and for a different reason (see BackUpBeforeMigrating).
        services.AddDbContext<FsOpsDbContext>(options => options.UseSqlite($"Data Source={databasePath};Pooling=False"));
        return services.BuildServiceProvider();
    }

    private static void ExecuteNonQuery(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string? QueryScalar(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }
}
