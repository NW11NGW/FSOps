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
///
/// <para><b>The rule, stated rather than left to be inferred: EVERY test that migrates a database to
/// a pinned migration and then reads it back must read through this class, and no other. Not just
/// the ones that are currently failing.</b> That distinction is the whole lesson. When this class
/// was written, two tests were converted and a third - <c>UpdateChannelColumnMigrationTests</c> - was
/// left alone because it happened to be green: at the time it was pinned to the newest migration, so
/// the current model and the pinned schema agreed by coincidence. The coincidence expired the day
/// somebody added four columns to UserSettings, and they walked into precisely the trap documented
/// two paragraphs above, which by then had been written down for six days. A fix applied only to the
/// instances that are failing is a fix that schedules its own recurrence.</para>
///
/// <para>A test that pins only to <i>seed</i> and then migrates forward to the head before reading -
/// <c>MigrationNonDestructiveTests</c>, <c>DatabaseStartupFailureTests</c> - is not covered by this
/// rule and may read through EF, because by the time it reads, the schema really is the current one.
/// The rule is about reading a database that is still pinned.</para>
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

    /// <summary>
    /// The distinct values of a text column, with NULL reported as the literal <c>&lt;null&gt;</c>
    /// rather than dropped. For asserting what is <b>physically</b> in the file: an enum read back
    /// through EF's converter would happily turn a stored <c>""</c> into member zero and report a
    /// perfectly sensible value while the column held something no later reader could parse.
    /// </summary>
    public static async Task<List<string>> DistinctTextAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.IsDBNull(0) ? "<null>" : reader.GetString(0));
        }

        return values;
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
