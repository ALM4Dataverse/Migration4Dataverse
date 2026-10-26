using System.Data.Common;

namespace Migration4Dataverse.Engine.Connectors;

internal sealed class DatabaseReadOnlyScope : IDisposable
{
    private readonly DbConnection _connection;
    private readonly bool _restoreQueryOnly;
    private bool _disposed;

    private DatabaseReadOnlyScope(DbConnection connection, bool restoreQueryOnly)
    {
        _connection = connection;
        _restoreQueryOnly = restoreQueryOnly;
    }

    public static async Task<DatabaseReadOnlyScope> EnterAsync(
        DbConnection connection,
        string connectorType,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(connectorType, "sqlite", StringComparison.Ordinal))
        {
            return new DatabaseReadOnlyScope(connection, restoreQueryOnly: false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA query_only";
        var previousValue = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (Convert.ToInt32(previousValue, System.Globalization.CultureInfo.InvariantCulture) != 0)
        {
            return new DatabaseReadOnlyScope(connection, restoreQueryOnly: false);
        }

        command.CommandText = "PRAGMA query_only = ON";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return new DatabaseReadOnlyScope(connection, restoreQueryOnly: true);
    }

    public static DatabaseReadOnlyScope Enter(DbConnection connection, string connectorType)
    {
        if (!string.Equals(connectorType, "sqlite", StringComparison.Ordinal))
        {
            return new DatabaseReadOnlyScope(connection, restoreQueryOnly: false);
        }

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA query_only";
        var previousValue = command.ExecuteScalar();
        if (Convert.ToInt32(previousValue, System.Globalization.CultureInfo.InvariantCulture) != 0)
        {
            return new DatabaseReadOnlyScope(connection, restoreQueryOnly: false);
        }

        command.CommandText = "PRAGMA query_only = ON";
        command.ExecuteNonQuery();
        return new DatabaseReadOnlyScope(connection, restoreQueryOnly: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (!_restoreQueryOnly)
        {
            _disposed = true;
            return;
        }
        if (_connection.State != System.Data.ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "The SQLite connection closed before its read-only query scope was restored.");
        }

        using var command = _connection.CreateCommand();
        command.CommandText = "PRAGMA query_only = OFF";
        command.ExecuteNonQuery();
        _disposed = true;
    }
}
