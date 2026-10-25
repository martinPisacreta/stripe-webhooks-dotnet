using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace StripeWebhooks.Api.Data;

/// <summary>
/// SQLite has one writer at a time; other connections get SQLITE_BUSY until the lock is free.
/// Microsoft.Data.Sqlite retries that with a fixed 150 ms sleep, which makes a burst of concurrent
/// webhook deliveries queue up in 150 ms steps. busy_timeout lets SQLite wait itself, in 1-100 ms
/// steps, so a writer gets in shortly after the lock is released. It is a per-connection setting.
/// </summary>
internal sealed class SqliteBusyTimeout : DbConnectionInterceptor
{
    private const string Pragma = "PRAGMA busy_timeout = 5000;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Pragma;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Pragma;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
