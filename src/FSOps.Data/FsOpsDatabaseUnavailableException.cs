using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace FSOps.Data;

/// <summary>
/// The database could not be opened or migrated at all - it is damaged, it is not a database, or
/// the file cannot be read. Carries a <see cref="UserMessage"/> written for the person sitting in
/// front of the app rather than for a log file.
///
/// <para>This is a fail-fast case, deliberately, and it is the opposite of the wall-clock catch-up
/// services' policy of logging and carrying on. There is no degraded mode here: every screen in
/// FSOps is the database, so starting without one would only move the failure somewhere less
/// obvious. What was wrong before was never that the app exited - it was that it exited saying
/// nothing anyone could act on.</para>
///
/// <para><b>The app must never repair, rename or delete a damaged database by itself.</b> Deleting
/// <c>fsops.db</c> is deleting the user's airline, and a WAL file that looks bad is sometimes the
/// only copy of the last session's work. The app says what has happened and what the options are;
/// the person decides. That is why <see cref="UserMessage"/> opens by telling them to copy the file
/// somewhere safe - it is the only instruction that is still useful if they get everything else
/// wrong, and anything that repaired the file automatically would destroy the evidence it needs.
/// </para>
/// </summary>
public sealed class FsOpsDatabaseUnavailableException : Exception
{
    private FsOpsDatabaseUnavailableException(string userMessage, string databasePath, Exception inner)
        : base(userMessage, inner)
    {
        UserMessage = userMessage;
        DatabasePath = databasePath;
    }

    /// <summary>Plain text meant to be shown verbatim. No stack trace, no exception type, no jargon.</summary>
    public string UserMessage { get; }

    public string DatabasePath { get; }

    /// <summary>
    /// True when <paramref name="exception"/> means the database file itself is unusable, as
    /// opposed to any of the hundred ordinary reasons a query can fail. Deliberately narrow:
    /// SQLITE_CORRUPT and SQLITE_NOTADB, plus the two ways the file system can refuse the file
    /// outright. Everything else is left to propagate as it always did.
    /// </summary>
    public static bool IsUnusableDatabase(Exception? exception) =>
        exception switch
        {
            null => false,
            SqliteException sqlite when sqlite.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase => true,
            UnauthorizedAccessException => true,
            IOException => true,
            _ => IsUnusableDatabase(exception.InnerException),
        };

    public static FsOpsDatabaseUnavailableException For(string databasePath, Exception inner) =>
        new(BuildUserMessage(databasePath, inner), databasePath, inner);

    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    private static string BuildUserMessage(string databasePath, Exception inner)
    {
        var cause = IsCorruption(inner)
            ? "The file is damaged, or it is not a database."
            : "The file could not be read. It may be open in another program, or the folder may not be writable.";

        // The "copy it somewhere safe" line comes first on purpose. If the reader takes in one
        // sentence and nothing else, it has to be that one - every other option here destroys or
        // replaces the file, and a copy is what any later attempt at recovery would work from.
        return $"""
            FSOps cannot open its database.

              {databasePath}

            {cause}

            Your airline is stored in this file, so copy it somewhere safe before doing anything
            else. If it can be repaired, that copy is what a repair would work from.

            To start over with a new, empty airline, move fsops.db - and any fsops.db-wal and
            fsops.db-shm beside it - out of that folder, then start FSOps again. This will not
            recover the old airline.
            """.ReplaceLineEndings();
    }

    private static bool IsCorruption(Exception? exception) =>
        exception switch
        {
            null => false,
            SqliteException sqlite => sqlite.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase,
            _ => IsCorruption(exception.InnerException),
        };
}

/// <summary>
/// The pre-migration backup could not be taken. Thrown <i>before</i> anything touches the schema:
/// a migration that rewrites data is the one mistake with no recovery, and the copy taken
/// beforehand is that recovery, so proceeding without it is not a trade worth making silently.
/// </summary>
public sealed class FsOpsBackupFailedException : Exception
{
    public FsOpsBackupFailedException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static FsOpsBackupFailedException For(string databasePath, string backupPath, Exception? inner = null) =>
        new($"FSOps could not back up '{databasePath}' to '{backupPath}' before applying a database update, " +
            "so the update was not applied. Your data has not been changed. Check that the folder is writable " +
            "and that there is free disk space, then start FSOps again.", inner);
}
