using Microsoft.Data.Sqlite;

namespace FSOps.Server.Tests;

/// <summary>
/// Reads rows out of a database that has been deliberately migrated to a <b>specific, older</b>
/// migration rather than to the head.
///
/// <para><b>Why this exists, and why converting it back to EF would be a mistake.</b> A migration
/// test pins the database to one migration on purpose, so that a migration written later can never
/// quietly become part of what the test claims to have proved. But <c>FsOpsDbContext</c> only ever
/// has one model - the current one - and EF builds its SELECT from that model, naming every column
/// the model knows about. Reading a pinned database through EF therefore asks for columns that do
/// not exist yet, and the test fails with <c>no such column</c> the first time anyone adds a field to
/// that table.</para>
///
/// <para>That failure is worse than an ordinary one because it misdirects: the error names the new
/// column, so it reads as a fault in the migration somebody just wrote rather than in how the test
/// reads. It happened exactly once, when <c>UserSettings.UpdateChannel</c> was added, and the person
/// who added it went looking for a bug in their own migration that was never there.</para>
///
/// <para>So these tests read with something that knows only about the columns they name. If you are
/// tempted to tidy this back into <c>db.Whatever.SingleAsync(...)</c> because it would be shorter:
/// that re-arms the trap, and the next person to add a column pays for it.</para>
/// </summary>
internal static class PinnedSchemaRead
{
    /// <summary>
    /// Runs a query expected to match exactly one row and projects it. The projection is given the
    /// live <see cref="SqliteDataReader"/> so that Microsoft.Data.Sqlite's own type conversions do
    /// the work - <c>GetFieldValue&lt;DateTimeOffset&gt;</c> and friends understand the text formats
    /// EF writes, and hand-rolled parsing here would be one more thing able to be wrong.
    /// </summary>
    public static async Task<T> SingleAsync<T>(
        SqliteConnection connection,
        string sql,
        Action<SqliteCommand> bind,
        Func<SqliteDataReader, T> project)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);

        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"No row matched: {sql}");
        var value = project(reader);
        Assert.False(await reader.ReadAsync(), $"More than one row matched: {sql}");
        return value;
    }

    /// <summary>A scalar count, for "how many rows are still there" assertions.</summary>
    public static async Task<long> CountAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>The column names a table physically has, at whatever migration it is pinned to.</summary>
    public static async Task<List<string>> ColumnNamesAsync(SqliteConnection connection, string table)
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

    /// <summary>The non-internal indexes on a table. A rebuild that forgot to recreate one is
    /// invisible in the data and only shows up much later, as a duplicate row nothing rejected.</summary>
    public static async Task<List<string>> IndexNamesAsync(SqliteConnection connection, string table)
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

    /// <summary>A nullable text column, keeping "not set" distinct from "set to nothing".</summary>
    public static string? TextOrNull(this SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public static DateTimeOffset? TimestampOrNull(this SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
    }
}
