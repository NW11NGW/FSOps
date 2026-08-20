using Microsoft.Data.Sqlite;

namespace FSOps.Data;

/// <summary>
/// Takes a consistent copy of a live SQLite database, and answers the two questions worth asking
/// about a copy afterwards: is it intact, and which schema is it.
///
/// <para><b>Why this is not File.Copy.</b> FSOps runs the database in WAL mode (see
/// <see cref="WalModeConnectionInterceptor"/>), which means a committed transaction may still be
/// sitting in <c>fsops.db-wal</c> rather than in <c>fsops.db</c> itself. Copying the main file on
/// its own therefore produces a file that opens cleanly, passes every check, and is silently
/// missing the player's most recent flights - the single worst failure this feature could have,
/// because it is invisible until the day it is needed. SQLite's own backup API reads through the
/// engine, so it sees the WAL contents too and writes a fully checkpointed, self-contained
/// database. It is also safe to run while the app is open and writing, which is the only way this
/// can work at all: the server holds the file for as long as it is running.</para>
///
/// <para><c>VACUUM INTO</c> would give the same guarantee. The backup API is used instead because
/// it is already the mechanism the pre-migration copy uses (see
/// <c>ServiceCollectionExtensions.BackUpBeforeMigrating</c>), and one proven way of copying this
/// database is better than two.</para>
/// </summary>
public static class DatabaseSnapshot
{
    /// <summary>The result of <c>PRAGMA integrity_check</c> when nothing is wrong.</summary>
    public const string IntegrityOk = "ok";

    /// <summary>
    /// Writes a checkpointed copy of <paramref name="sourceDatabasePath"/> to
    /// <paramref name="destinationPath"/>, overwriting whatever was there.
    ///
    /// <para>Pooling=False on both connections is not a detail. Microsoft.Data.Sqlite returns a
    /// closed connection to its pool rather than releasing the file handle, so with pooling left on
    /// the caller cannot move, zip or delete the file it just wrote - a copy that appears to work
    /// and then cannot be used.</para>
    /// </summary>
    public static void WriteTo(string sourceDatabasePath, string destinationPath)
    {
        if (!File.Exists(sourceDatabasePath))
        {
            throw new FileNotFoundException("There is no database file to copy.", sourceDatabasePath);
        }

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        using var source = new SqliteConnection($"Data Source={sourceDatabasePath};Pooling=False");
        using var destination = new SqliteConnection($"Data Source={destinationPath};Pooling=False");
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }

    /// <summary>
    /// Runs <c>PRAGMA integrity_check</c> and returns <see cref="IntegrityOk"/> or SQLite's own
    /// description of what is wrong. A file that is not a database at all reports that rather than
    /// throwing, because every caller here is deciding whether to refuse - not whether to crash.
    /// </summary>
    public static string IntegrityCheck(string databasePath)
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False;Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            using var reader = command.ExecuteReader();

            var lines = new List<string>();
            while (reader.Read())
            {
                lines.Add(reader.GetString(0));
            }

            return lines.Count == 0 ? "the check returned nothing" : string.Join("; ", lines);
        }
        catch (SqliteException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Deletes a database file <b>and its <c>-wal</c> and <c>-shm</c> companions</b>.
    ///
    /// <para>Not a tidiness helper. A WAL-mode database always travels as up to three files, and
    /// merely opening one - even read-only, as the integrity check does - creates the companions
    /// again. Deleting the <c>.db</c> on its own therefore leaves a write-ahead log behind with
    /// nothing to belong to, and the next file to take that name inherits it. That is one of the
    /// few ways to turn a good database into a corrupt one, so every deletion of a database in this
    /// app goes through here.</para>
    /// </summary>
    public static void Delete(string databasePath)
    {
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: every caller has a working answer without the delete succeeding.
            }
        }
    }

    /// <summary>
    /// The newest migration recorded in the database's own <c>__EFMigrationsHistory</c> table, or
    /// null when the table is absent or unreadable.
    ///
    /// <para>Read from the database rather than taken from a backup's manifest on purpose: the
    /// manifest is a description of the file, and the compatibility decision has to be made on the
    /// file itself. Migration ids are timestamp-prefixed, so ordering them as text orders them by
    /// date.</para>
    /// </summary>
    public static string? ReadLatestMigration(string databasePath)
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False;Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\" DESC LIMIT 1;";
            return command.ExecuteScalar() as string;
        }
        catch (SqliteException)
        {
            return null;
        }
    }
}
