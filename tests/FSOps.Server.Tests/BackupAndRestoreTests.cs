using System.Data.Common;
using System.IO.Compression;
using FSOps.Core.Entities;
using FSOps.Data;
using FSOps.Server.Services.Backup;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FSOps.Server.Tests;

/// <summary>
/// The backup feature's own failure modes, each of which is the difference between a real backup
/// and a plausible one.
///
/// <para><b>The write-ahead log is the test that matters.</b> FSOps runs SQLite in WAL mode, so a
/// committed transaction can still be sitting in <c>fsops.db-wal</c> rather than in
/// <c>fsops.db</c>. A file copy therefore produces something that opens cleanly, passes every
/// check, and is missing the player's most recent flights - and nobody finds out until the day they
/// need it. The first test here proves both halves of that: it shows the naive copy losing the
/// last write, and the real backup keeping it, from the same database at the same moment while it
/// is still open.</para>
///
/// <para>Everything else here is a refusal. A backup from a newer build, a truncated file and a
/// file that was never a backup all have to be turned away <i>before</i> anything of the player's
/// has been touched, and the safety copy has to exist before a restore is allowed to displace
/// anything - because "sorry" is the whole of the answer otherwise.</para>
/// </summary>
public class BackupAndRestoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fsops-backup-{Guid.NewGuid():N}");
    private readonly string _dataDirectory;
    private readonly string _databasePath;

    public BackupAndRestoreTests()
    {
        _dataDirectory = Path.Combine(_root, "live");
        _databasePath = Path.Combine(_dataDirectory, "fsops.db");
        Directory.CreateDirectory(_dataDirectory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }

        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------------------------------
    // The WAL case
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ABackupTakenWhileTheAppHoldsTheDatabaseOpen_KeepsWritesThatAreStillOnlyInTheWal()
    {
        Migrate(_databasePath);

        // Everything the migration wrote is now in fsops.db itself, so what follows is unambiguous:
        // any row that is missing from a naive copy is missing because it is in the WAL.
        Checkpoint(_databasePath);

        // The server holds the database open for its whole life. That is the condition under which
        // a backup has to work, and the condition under which a copy silently fails - and it is
        // load-bearing for the demonstration as well as realistic, because SQLite checkpoints when
        // the last connection to a WAL database closes. Nothing below may release this.
        await using var appConnection = new SqliteConnection($"Data Source={_databasePath}");
        await appConnection.OpenAsync();
        NoAutoCheckpointInterceptor.Disable(appConnection);

        // Pins the write below inside the write-ahead log for as long as this connection lives -
        // see HoldAReadSnapshot for why this, and not autocheckpoint, is what makes the
        // demonstration deterministic.
        HoldAReadSnapshot(appConnection);

        await using (var db = new FsOpsDbContext(Options(_databasePath)))
        {
            var airline = NewAirline("Wal Air");
            db.Airlines.Add(airline);
            db.LedgerTransactions.Add(new LedgerTransaction
            {
                Id = Guid.NewGuid(),
                AirlineId = airline.Id,
                Utc = DateTimeOffset.UtcNow,
                Category = LedgerCategory.TicketRevenue,
                Amount = 4321m,
                Description = WalMarker,
            });
            await db.SaveChangesAsync();
        }

        // The premise, enforced rather than hoped for. Without this the next assertion can pass or
        // fail on whether SQLite happened to checkpoint, which says nothing about backups.
        AssertTheWriteIsOnlyInTheWal(_databasePath);

        // Half one: a file copy is NOT a backup. This is the failure the feature exists to avoid,
        // demonstrated rather than asserted about.
        var naiveCopy = Path.Combine(_root, "naive-file-copy.db");
        File.Copy(_databasePath, naiveCopy);
        Assert.Equal(0L, CountAirlines(naiveCopy));

        // Half two: SQLite's own backup, taken through a fresh connection while the app still holds
        // the file, sees the WAL contents and writes them into a self-contained database.
        var prepared = await PrepareBackupAsync();
        var insideTheBackup = ExtractDatabase(prepared.Path, Path.Combine(_root, "from-backup.db"));

        Assert.Equal(1L, CountAirlines(insideTheBackup));
        Assert.Equal("Wal Air", Scalar(insideTheBackup, "SELECT \"Name\" FROM \"Airlines\" LIMIT 1;"));
        Assert.Equal(
            WalMarker,
            Scalar(insideTheBackup, "SELECT \"Description\" FROM \"LedgerTransactions\" ORDER BY \"Utc\" DESC LIMIT 1;"));
    }

    [Fact]
    public async Task ABackupTakenFromTheWal_RestoresIntoACleanDataDirectoryWithTheLatestRowsIntact()
    {
        // The same scenario carried all the way through: back up without closing the app, restore
        // into a directory that has never seen this airline, and read the newest rows back.
        Migrate(_databasePath);
        Checkpoint(_databasePath);

        await using var appConnection = new SqliteConnection($"Data Source={_databasePath}");
        await appConnection.OpenAsync();
        NoAutoCheckpointInterceptor.Disable(appConnection);
        HoldAReadSnapshot(appConnection);

        await using (var db = new FsOpsDbContext(Options(_databasePath)))
        {
            var airline = NewAirline("Recovered Air");
            db.Airlines.Add(airline);
            db.LedgerTransactions.Add(new LedgerTransaction
            {
                Id = Guid.NewGuid(),
                AirlineId = airline.Id,
                Utc = DateTimeOffset.UtcNow,
                Category = LedgerCategory.TicketRevenue,
                Amount = 1234.56m,
                Description = WalMarker,
            });
            await db.SaveChangesAsync();
        }

        // This test's name claims the backup came from the write-ahead log, so that has to be true
        // rather than incidental - otherwise a checkpoint quietly turns it into an ordinary
        // round-trip test that would still pass with a plain file copy behind it.
        AssertTheWriteIsOnlyInTheWal(_databasePath);

        var prepared = await PrepareBackupAsync();
        var backupFile = Path.Combine(_root, prepared.FileName);
        File.Move(prepared.Path, backupFile);

        var (freshDirectory, freshDatabase) = NewInstall("clean");
        var staged = await StageAsync(freshDirectory, freshDatabase, backupFile);
        Assert.True(staged.Staged, staged.Refusal);

        Restart();
        var result = PendingRestore.ApplyIfStaged(freshDirectory, freshDatabase);
        Assert.NotNull(result);
        Assert.True(result!.Succeeded, result.Message);

        await using var restored = new FsOpsDbContext(Options(freshDatabase));
        await restored.Database.MigrateAsync();

        Assert.Equal("Recovered Air", (await restored.Airlines.SingleAsync()).Name);
        var ledger = await restored.LedgerTransactions.SingleAsync();
        Assert.Equal(WalMarker, ledger.Description);
        Assert.Equal(1234.56m, ledger.Amount);

        // And nothing of the staging is left lying around afterwards - including the staged file's
        // -wal and -shm, which the integrity check recreates and which describe a database that no
        // longer exists under that name.
        Assert.False(File.Exists(PendingRestore.StagedDatabasePath(freshDirectory)));
        Assert.False(File.Exists(PendingRestore.StagedStatePath(freshDirectory)));
        Assert.Empty(Directory.GetFiles(freshDirectory, PendingRestore.StagedDatabaseFileName + "-*"));
    }

    // ---------------------------------------------------------------------------------------
    // Refusals
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ATruncatedBackup_IsRefusedAndNothingIsStaged()
    {
        var backupFile = await SeedAndBackUpAsync("Truncated Air");

        // Half a file, as an interrupted copy to a USB stick leaves behind.
        var whole = File.ReadAllBytes(backupFile);
        File.WriteAllBytes(backupFile, whole[..(whole.Length / 2)]);

        var (directory, database) = NewInstall("target");
        var staged = await StageAsync(directory, database, backupFile);

        Assert.False(staged.Staged);

        // Told apart from "wrong file entirely", because the two need different answers: an
        // interrupted copy is something the player can do something about.
        Assert.Contains("incomplete", staged.Refusal);
        Assert.Contains("Nothing was changed", staged.Refusal);

        Assert.Null(PendingRestore.Read(directory));
        Restart();
        Assert.Null(PendingRestore.ApplyIfStaged(directory, database));
    }

    [Fact]
    public async Task ABackupWhoseDatabaseHasBeenTamperedWith_IsRefusedOnItsChecksum()
    {
        var backupFile = await SeedAndBackUpAsync("Checksum Air");

        // Rewrite the stored database with different bytes, leaving the manifest - and therefore the
        // recorded checksum - describing the file that used to be there.
        using (var archive = ZipFile.Open(backupFile, ZipArchiveMode.Update))
        {
            archive.GetEntry(BackupManifest.DatabaseEntryName)!.Delete();
            var replacement = archive.CreateEntry(BackupManifest.DatabaseEntryName);
            using var stream = replacement.Open();
            var bytes = System.Text.Encoding.ASCII.GetBytes(new string('x', 40_960));
            stream.Write(bytes, 0, bytes.Length);
        }

        var (directory, database) = NewInstall("target");
        var staged = await StageAsync(directory, database, backupFile);

        Assert.False(staged.Staged);
        Assert.Contains("damaged", staged.Refusal, StringComparison.OrdinalIgnoreCase);
        Assert.Null(PendingRestore.Read(directory));
    }

    [Fact]
    public async Task AFileThatIsNotABackupAtAll_IsRefusedByName()
    {
        var notABackup = Path.Combine(_root, "holiday-photo.jpg");
        File.WriteAllBytes(notABackup, System.Text.Encoding.ASCII.GetBytes(new string('j', 20_000)));

        var (directory, database) = NewInstall("target");
        var staged = await StageAsync(directory, database, notABackup);

        Assert.False(staged.Staged);
        Assert.Contains(".fsopsbak", staged.Refusal);
        Assert.Null(PendingRestore.Read(directory));
    }

    [Fact]
    public async Task AZipThatIsNotAnFsOpsBackup_IsRefused()
    {
        var zipPath = Path.Combine(_root, "documents.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        using (var stream = archive.CreateEntry("notes.txt").Open())
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes("nothing to do with FSOps");
            stream.Write(bytes, 0, bytes.Length);
        }

        var (directory, database) = NewInstall("target");
        var staged = await StageAsync(directory, database, zipPath);

        Assert.False(staged.Staged);
        Assert.Contains("not an FSOps backup", staged.Refusal);
    }

    [Fact]
    public async Task AnEmptyFile_IsRefusedRatherThanTreatedAsAnEmptyAirline()
    {
        var emptyPath = Path.Combine(_root, "empty.fsopsbak");
        File.WriteAllBytes(emptyPath, Array.Empty<byte>());

        var (directory, database) = NewInstall("target");
        var staged = await StageAsync(directory, database, emptyPath);

        Assert.False(staged.Staged);
        Assert.NotNull(staged.Refusal);
    }

    [Fact]
    public async Task ABackupFromANewerBuild_IsRefusedClearlyRatherThanAttempted()
    {
        // The trap this whole check exists for. A newer FSOps writes schema this build has never
        // heard of; restoring it would not fail cleanly, it would half-work.
        var backupFile = await BackUpWithAnUnknownMigrationAsync("Tomorrow Air", "9.9.9");

        var (directory, database) = NewInstall("target");
        var staged = await StageAsync(directory, database, backupFile);

        Assert.False(staged.Staged);
        Assert.Contains("newer version of FSOps", staged.Refusal);
        Assert.Contains("9.9.9", staged.Refusal);

        // And the message has to say which direction IS supported, or it reads as "backups do not
        // work" rather than "this one cannot go backwards".
        Assert.Contains("older versions restore into newer ones", staged.Refusal);

        Assert.Null(PendingRestore.Read(directory));
    }

    [Fact]
    public async Task ABackupFromAnOlderBuild_RestoresAndIsMigratedForward()
    {
        // The supported direction, proved rather than assumed: a database at an earlier migration is
        // restored into an install that is fully up to date, and the schema is brought forward with
        // the data intact.
        var oldDirectory = Path.Combine(_root, "old-install");
        Directory.CreateDirectory(oldDirectory);
        var oldDatabase = Path.Combine(oldDirectory, "fsops.db");

        string firstMigration;
        await using (var db = new FsOpsDbContext(Options(oldDatabase)))
        {
            firstMigration = db.Database.GetMigrations().First();
            db.Database.GetInfrastructure()
                .GetRequiredService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>()
                .Migrate(firstMigration);
        }

        var marker = Guid.NewGuid().ToString("N");
        Execute(oldDatabase, $"CREATE TABLE \"OldData\" (\"Marker\" TEXT NOT NULL); INSERT INTO \"OldData\" VALUES ('{marker}');");

        var oldService = new BackupService(oldDirectory, oldDatabase) { CurrentAppVersion = "0.9.0" };
        PreparedBackup prepared;
        await using (var db = new FsOpsDbContext(Options(oldDatabase)))
        {
            prepared = await oldService.PrepareAsync(db);
        }

        var backupFile = Path.Combine(_root, "old-build.fsopsbak");
        File.Move(prepared.Path, backupFile);

        var manifest = BackupArchive.TryReadManifest(backupFile);
        Assert.Equal(firstMigration, manifest!.MigrationVersion);

        var (directory, database) = NewInstall("current");
        var staged = await StageAsync(directory, database, backupFile);
        Assert.True(staged.Staged, staged.Refusal);

        Restart();
        var result = PendingRestore.ApplyIfStaged(directory, database);
        Assert.True(result!.Succeeded, result.Message);

        await using (var db = new FsOpsDbContext(Options(database)))
        {
            await db.Database.MigrateAsync();
            Assert.Empty(db.Database.GetPendingMigrations());
        }

        Assert.Equal(marker, Scalar(database, "SELECT \"Marker\" FROM \"OldData\" LIMIT 1;"));
    }

    // ---------------------------------------------------------------------------------------
    // The safety copy
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task StagingARestore_SavesTheAirlineItIsAboutToReplaceFirst_AndSaysWhereItWent()
    {
        var backupFile = await SeedAndBackUpAsync("Incoming Air");

        // The install about to be overwritten has an airline of its own, with a name that must be
        // recoverable afterwards.
        var (directory, database) = NewInstall("target");
        await SeedAsync(database, "The Airline About To Be Replaced");

        var staged = await StageAsync(directory, database, backupFile);
        Assert.True(staged.Staged, staged.Refusal);

        var safetyCopy = staged.State!.SafetyCopyPath;
        Assert.True(File.Exists(safetyCopy), "a restore must save what it is about to displace");
        Assert.EndsWith(BackupManifest.FileExtension, safetyCopy);
        Assert.Contains("Before restore", Path.GetFileName(safetyCopy));

        // And it is a real backup, not a marker: the displaced airline can be read straight out of it.
        var inside = ExtractDatabase(safetyCopy, Path.Combine(_root, "from-safety-copy.db"));
        Assert.Equal("The Airline About To Be Replaced", Scalar(inside, "SELECT \"Name\" FROM \"Airlines\" LIMIT 1;"));

        // The safety copy is taken before the swap, so it must already exist while the restore is
        // still only staged - not after it has been applied.
        Assert.NotNull(PendingRestore.Read(directory));
    }

    [Fact]
    public async Task ARefusedRestore_TakesNoSafetyCopyAndLeavesTheAirlineExactlyWhereItWas()
    {
        var (directory, database) = NewInstall("target");
        await SeedAsync(database, "Untouched Air");
        Restart();
        var before = File.ReadAllBytes(database);

        var rubbish = Path.Combine(_root, "rubbish.fsopsbak");
        File.WriteAllBytes(rubbish, System.Text.Encoding.ASCII.GetBytes(new string('z', 5_000)));

        var staged = await StageAsync(directory, database, rubbish);
        Assert.False(staged.Staged);

        Restart();
        Assert.Equal(before, File.ReadAllBytes(database));

        // Nothing was copied "just in case" either - a refusal that still wrote a file would leave
        // the player wondering what had happened.
        var backupsDirectory = PendingRestore.BackupsDirectory(directory);
        var written = Directory.Exists(backupsDirectory)
            ? Directory.GetFiles(backupsDirectory, "*" + BackupManifest.FileExtension)
            : Array.Empty<string>();
        Assert.Empty(written);
    }

    [Fact]
    public async Task ACancelledRestore_IsNotAppliedAtTheNextStart_AndTheSafetyCopyIsKept()
    {
        var backupFile = await SeedAndBackUpAsync("Cancelled Air");

        var (directory, database) = NewInstall("target");
        await SeedAsync(database, "Still Here Air");

        var service = new BackupService(directory, database);
        await using (var db = new FsOpsDbContext(Options(database)))
        {
            var staged = await service.StageRestoreAsync(backupFile, "Cancelled Air backup.fsopsbak", db);
            Assert.True(staged.Staged, staged.Refusal);
        }

        var safetyCopy = service.ReadPending()!.SafetyCopyPath;
        service.CancelPending();

        Assert.Null(service.ReadPending());
        Restart();
        Assert.Null(PendingRestore.ApplyIfStaged(directory, database));

        // The safety copy is the player's file now. Cancelling a restore must not delete a backup.
        Assert.True(File.Exists(safetyCopy));

        await using var db2 = new FsOpsDbContext(Options(database));
        Assert.Equal("Still Here Air", (await db2.Airlines.SingleAsync()).Name);
    }

    [Fact]
    public async Task AStagedRestoreThatGoesBadBeforeTheRestart_IsNotApplied_AndTheAirlineIsLeftAlone()
    {
        // Between staging and the restart the machine can lose power or a disk can go bad. The
        // startup pass re-checks rather than trusting the check done at upload time, because
        // replacing a working database with a broken one is the one outcome with no way back.
        var backupFile = await SeedAndBackUpAsync("Doomed Air");

        var (directory, database) = NewInstall("target");
        await SeedAsync(database, "Survivor Air");

        var staged = await StageAsync(directory, database, backupFile);
        Assert.True(staged.Staged, staged.Refusal);

        File.WriteAllBytes(
            PendingRestore.StagedDatabasePath(directory),
            System.Text.Encoding.ASCII.GetBytes(new string('q', 16_384)));

        Restart();
        var result = PendingRestore.ApplyIfStaged(directory, database);
        Assert.NotNull(result);
        Assert.False(result!.Succeeded);
        Assert.Contains("left", result.Message, StringComparison.OrdinalIgnoreCase);

        await using var db = new FsOpsDbContext(Options(database));
        Assert.Equal("Survivor Air", (await db.Airlines.SingleAsync()).Name);
    }

    [Fact]
    public async Task ARestoreThatCannotReplaceTheFile_LeavesTheAirlineIntactRatherThanHalfSwapped()
    {
        // The realistic version of "something else has the database open": the player has a second
        // copy of FSOps running, which this app supports on purpose. A restore that half-applied
        // here would be worse than one that refused, so it must refuse - with the file untouched.
        var backupFile = await SeedAndBackUpAsync("Blocked Air");

        var (directory, database) = NewInstall("target");
        await SeedAsync(database, "Held Open Air");

        var staged = await StageAsync(directory, database, backupFile);
        Assert.True(staged.Staged, staged.Refusal);

        Restart();
        var before = File.ReadAllBytes(database);

        RestoreResult? result;
        using (var somebodyElse = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            somebodyElse.Open();
            result = PendingRestore.ApplyIfStaged(directory, database);
        }

        Assert.NotNull(result);
        Assert.False(result!.Succeeded);
        Assert.Contains("left as it was", result.Message);
        Assert.Equal(before, File.ReadAllBytes(database));

        // The backup the player chose is untouched, so they can simply try again.
        Assert.True(File.Exists(backupFile));
    }

    [Fact]
    public async Task AnAppliedRestore_LeavesAResultTheAppCanShowAfterItRestarts()
    {
        var backupFile = await SeedAndBackUpAsync("Reported Air");

        var (directory, database) = NewInstall("target");
        await SeedAsync(database, "Old Air");

        var staged = await StageAsync(directory, database, backupFile);
        Assert.True(staged.Staged, staged.Refusal);

        Restart();
        PendingRestore.ApplyIfStaged(directory, database);

        var service = new BackupService(directory, database);
        var reported = service.ReadLastResult();

        Assert.NotNull(reported);
        Assert.True(reported!.Succeeded);
        Assert.Equal(Path.GetFileName(backupFile), reported.SourceFileName);
        Assert.True(File.Exists(reported.SafetyCopyPath));

        // And the player can dismiss it, so it is not still on screen weeks later.
        service.AcknowledgeLastResult();
        Assert.Null(service.ReadLastResult());
    }

    // ---------------------------------------------------------------------------------------
    // Naming
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ABackupIsNamedForTheAirlineAndTheDate_NeverAGuid()
    {
        var name = BackupArchive.SuggestFileName("Skyline Air", new DateTimeOffset(2026, 8, 20, 14, 32, 0, TimeSpan.Zero));

        Assert.StartsWith("Skyline Air backup ", name);
        Assert.EndsWith(BackupManifest.FileExtension, name);
        Assert.Contains("2026-08-20", name);
    }

    [Fact]
    public void AnAirlineNameThatCannotBeAFileName_StillProducesAUsableOne()
    {
        var name = BackupArchive.SuggestFileName("Air: North/South *", DateTimeOffset.UtcNow);

        Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));
        Assert.Contains("Air", name);
    }

    [Fact]
    public void BeforeAnAirlineExists_TheBackupIsStillNamedSomethingSensible()
    {
        var name = BackupArchive.SuggestFileName(null, DateTimeOffset.UtcNow);

        Assert.StartsWith("FSOps backup ", name);
        Assert.EndsWith(BackupManifest.FileExtension, name);
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The text written into the ledger row the WAL tests turn on. Looked for in the raw bytes of a
    /// database file, which is the only unambiguous way to say which of <c>fsops.db</c> and
    /// <c>fsops.db-wal</c> a write is currently in.
    /// </summary>
    private const string WalMarker = "The last sector before the backup";

    private static DbContextOptions<FsOpsDbContext> Options(string databasePath) =>
        new DbContextOptionsBuilder<FsOpsDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .AddInterceptors(new WalModeConnectionInterceptor(), new NoAutoCheckpointInterceptor())
            .Options;

    /// <summary>
    /// Disables SQLite's automatic checkpoint on every connection these tests open.
    ///
    /// <para><b>Why this exists.</b> Two tests here demonstrate that a row can be committed and
    /// still be only in the write-ahead log. Nothing in SQLite promises to leave it there: a
    /// checkpoint folds the WAL back into the database file, and if one lands between the write and
    /// the copy, the naive copy legitimately contains the row and the test fails while the feature
    /// is entirely correct. That is a test that passes and fails by luck, on the one guarantee that
    /// backups keep the player's data - which is the worst thing for it to be flaky about, because
    /// an intermittently red test gets ignored and would then mask a real regression.</para>
    ///
    /// <para>Turning autocheckpoint off makes the staging deterministic, and has a second effect
    /// worth naming: a checkpoint is also the only thing that writes to the main database file
    /// while no transaction is in flight, so with it off the <c>File.Copy</c> those tests take
    /// cannot race a writer and produce a torn file either. Both ways this test could fail without
    /// the feature being wrong are closed by the same pragma.</para>
    ///
    /// <para>It is per-connection rather than per-database, which is why it has to be an
    /// interceptor rather than a single PRAGMA somewhere: EF pools connections and opens new ones
    /// as it pleases, and a setting applied to one of them says nothing about the next.</para>
    /// </summary>
    private sealed class NoAutoCheckpointInterceptor : DbConnectionInterceptor
    {
        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            base.ConnectionOpened(connection, eventData);
            Disable(connection);
        }

        public override async Task ConnectionOpenedAsync(
            DbConnection connection,
            ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
            Disable(connection);
        }

        internal static void Disable(DbConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_autocheckpoint = 0;";
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Takes a read snapshot on <paramref name="connection"/> and holds it, which is what actually
    /// makes the WAL demonstrations below deterministic.
    ///
    /// <para><b>Why a reader, and not just autocheckpoint.</b> Turning autocheckpoint off is not
    /// enough on its own - measured, not assumed: with it off, a full-solution run under load still
    /// managed to checkpoint between the write and the assertion. SQLite starts a checkpoint from
    /// more than one place, and a test cannot enumerate them all.</para>
    ///
    /// <para>So this leans on the one rule that closes all of them at once: <b>a checkpointer never
    /// copies WAL frames past the oldest active reader's snapshot.</b> A read transaction opened
    /// <i>before</i> the write pins the snapshot at the pre-write mark, so whoever tries to
    /// checkpoint and for whatever reason, the write physically cannot reach <c>fsops.db</c> while
    /// this is held. That is a guarantee of the WAL design rather than a matter of timing, which is
    /// the difference between a test that demonstrates something and one that gets lucky.</para>
    ///
    /// <para>Issued as raw SQL rather than <c>BeginTransaction()</c> on purpose: Microsoft.Data.Sqlite
    /// defaults to serializable and issues <c>BEGIN IMMEDIATE</c>, which takes the write lock and
    /// would deadlock against the very write this is meant to observe. A plain deferred <c>BEGIN</c>
    /// followed by a read takes a read snapshot and nothing else.</para>
    /// </summary>
    private static void HoldAReadSnapshot(SqliteConnection connection)
    {
        using var begin = connection.CreateCommand();
        begin.CommandText = "BEGIN;";
        begin.ExecuteNonQuery();

        // A deferred BEGIN acquires nothing until something is actually read, so the snapshot is
        // taken here rather than above. Without this line the transaction is open and useless.
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT COUNT(*) FROM \"Airlines\";";
        read.ExecuteScalar();
    }

    /// <summary>
    /// True when <paramref name="text"/> appears in the raw bytes of the file. Opened shared,
    /// because the whole point is to read a database file the app still has open.
    /// </summary>
    private static bool FileContains(string path, string text)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[stream.Length];
        var read = 0;
        while (read < bytes.Length)
        {
            var chunk = stream.Read(bytes, read, bytes.Length - read);
            if (chunk == 0)
            {
                break;
            }

            read += chunk;
        }

        // Latin-1 maps every byte to one character, so an ASCII marker can be found in binary
        // content without any decoding failing part-way through a page of page data.
        return System.Text.Encoding.Latin1.GetString(bytes, 0, read).Contains(text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Asserts the state the WAL demonstrations depend on, rather than hoping for it: the write is
    /// in the log and has <b>not</b> been folded into the database file.
    ///
    /// <para>This deliberately replaces an earlier check that the <c>-wal</c> file was merely
    /// non-empty, which looked like the same thing and was not: a checkpoint reuses the WAL file
    /// rather than truncating it, so its length is identical whether or not the data is still only
    /// in there. Measured, not assumed - forcing a checkpoint leaves the file byte-for-byte the
    /// same size while moving the row into <c>fsops.db</c>.</para>
    ///
    /// <para>If this ever fires, the message says what it means: the test's own staging has broken,
    /// not that backups lose data.</para>
    /// </summary>
    private static void AssertTheWriteIsOnlyInTheWal(string databasePath)
    {
        var walPath = databasePath + "-wal";

        Assert.True(File.Exists(walPath), "the database must be in WAL mode for this test to mean anything");
        Assert.True(
            FileContains(walPath, WalMarker),
            "test staging: the write should be in the write-ahead log, and is not");
        Assert.False(
            FileContains(databasePath, WalMarker),
            "test staging: the write has been checkpointed into fsops.db, so this test can no longer " +
            "demonstrate anything about the write-ahead log. Autocheckpoint should be off (see " +
            "NoAutoCheckpointInterceptor) and a connection should still be open.");
    }

    private static void Migrate(string databasePath)
    {
        using var db = new FsOpsDbContext(Options(databasePath));
        db.Database.Migrate();
    }

    /// <summary>
    /// What a restart does to the database file: every connection closed and every handle released.
    /// Necessary before <see cref="PendingRestore.ApplyIfStaged"/> because that is the condition it
    /// is designed for - it runs at startup, before EF has opened anything. In-process, EF's
    /// connection pool keeps the file open after a context is disposed, so without this a test
    /// would be exercising "another copy of FSOps is running", not "FSOps has just started".
    /// </summary>
    private static void Restart() => SqliteConnection.ClearAllPools();

    /// <summary>Forces everything still in the WAL into the database file and closes every handle,
    /// so a later "this row is only in the WAL" claim is unambiguous.</summary>
    private static void Checkpoint(string databasePath)
    {
        using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
    }

    private (string Directory, string DatabasePath) NewInstall(string name)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "fsops.db");
        Migrate(databasePath);
        return (directory, databasePath);
    }

    private static Airline NewAirline(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        IcaoCode = "TST",
        HomeAirportIcao = "EGGD",
        StrategyProfile = AirlineStrategyProfile.Domestic,
        Playstyle = AirlinePlaystyle.Casual,
        OwnerUserId = Guid.NewGuid(),
        CreatedUtc = DateTimeOffset.UtcNow,
    };

    private static async Task SeedAsync(string databasePath, string airlineName)
    {
        await using var db = new FsOpsDbContext(Options(databasePath));
        db.Airlines.Add(NewAirline(airlineName));
        await db.SaveChangesAsync();
    }

    private async Task<PreparedBackup> PrepareBackupAsync()
    {
        var service = new BackupService(_dataDirectory, _databasePath);
        await using var db = new FsOpsDbContext(Options(_databasePath));
        return await service.PrepareAsync(db);
    }

    /// <summary>Seeds the live install, backs it up, and returns the finished .fsopsbak.</summary>
    private async Task<string> SeedAndBackUpAsync(string airlineName)
    {
        Migrate(_databasePath);
        await SeedAsync(_databasePath, airlineName);

        var prepared = await PrepareBackupAsync();
        var backupFile = Path.Combine(_root, prepared.FileName);
        File.Move(prepared.Path, backupFile);
        return backupFile;
    }

    /// <summary>
    /// Builds a backup whose database records a migration this build has never heard of - which is
    /// exactly what a save from a newer FSOps looks like from here.
    /// </summary>
    private async Task<string> BackUpWithAnUnknownMigrationAsync(string airlineName, string appVersion)
    {
        Migrate(_databasePath);
        await SeedAsync(_databasePath, airlineName);

        var snapshot = Path.Combine(_root, "from-the-future.db");
        DatabaseSnapshot.WriteTo(_databasePath, snapshot);
        Execute(
            snapshot,
            "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") " +
            "VALUES ('29990101000000_SomethingThisBuildHasNeverHeardOf', '8.0.10');");

        var backupFile = Path.Combine(_root, "from-the-future.fsopsbak");
        BackupArchive.Create(snapshot, new BackupManifest
        {
            AppVersion = appVersion,
            MigrationVersion = "29990101000000_SomethingThisBuildHasNeverHeardOf",
            CreatedUtc = DateTimeOffset.UtcNow,
            AirlineName = airlineName,
        }, backupFile);

        return backupFile;
    }

    private static async Task<RestoreStaging> StageAsync(string directory, string databasePath, string candidate)
    {
        var service = new BackupService(directory, databasePath) { CurrentAppVersion = "1.2.0" };
        await using var db = new FsOpsDbContext(Options(databasePath));
        return await service.StageRestoreAsync(candidate, Path.GetFileName(candidate), db);
    }

    private static string ExtractDatabase(string archivePath, string destination)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        archive.GetEntry(BackupManifest.DatabaseEntryName)!.ExtractToFile(destination, overwrite: true);
        return destination;
    }

    private static long CountAirlines(string databasePath) =>
        Convert.ToInt64(ScalarObject(databasePath, "SELECT COUNT(*) FROM \"Airlines\";") ?? 0L);

    private static string? Scalar(string databasePath, string sql) => ScalarObject(databasePath, sql) as string;

    private static object? ScalarObject(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
