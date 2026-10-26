using System.Data.Common;
using DuckDB.NET.Data;
using Microsoft.Data.Sqlite;

namespace Migration4Dataverse.Engine.Connectors;

public static class MigrationDatabaseConnectionFactory
{
    public static DbConnection Create(string provider, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(connectionString);

        return provider.Trim().ToLowerInvariant() switch
        {
            "sqlite" => new SqliteConnection(connectionString),
            "duckdb" => new DuckDBConnection(connectionString),
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "The database provider must be SQLite or DuckDB.")
        };
    }
}
