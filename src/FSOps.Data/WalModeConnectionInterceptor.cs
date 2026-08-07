using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FSOps.Data;

/// <summary>
/// SQLite defaults to its rollback-journal mode, which locks the whole database file for
/// writers. WAL mode lets readers and a writer work concurrently, which matters once the
/// server is reading flight state while a hosted service is posting ledger rows. This runs
/// the PRAGMA every time a physical connection opens, since WAL is a per-connection setting
/// until it has been persisted to the file (harmless to repeat).
/// </summary>
public class WalModeConnectionInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        base.ConnectionOpened(connection, eventData);
        SetWalMode(connection);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void SetWalMode(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        command.ExecuteNonQuery();
    }
}
