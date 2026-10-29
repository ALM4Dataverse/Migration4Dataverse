using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using DuckDB.NET.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.Connectors;

public static class MigrationDatabaseConnectionFactory
{
    public static DbConnection CreateManagedTransformConnection(
        string provider,
        string migrationDatabasePath,
        string runId,
        string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationDatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);

        var normalizedProvider = provider.Trim().ToLowerInvariant();
        if (normalizedProvider is not ("sqlite" or "duckdb"))
        {
            throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "The database provider must be SQLite or DuckDB.");
        }

        var databasePath = Path.GetFullPath(migrationDatabasePath);
        var databaseDirectory = Path.GetDirectoryName(databasePath)
            ?? throw new ArgumentException(
                "The migration database path must have a parent directory.",
                nameof(migrationDatabasePath));
        var transformDirectory = Path.Combine(databaseDirectory, ".migration-transforms");
        Directory.CreateDirectory(transformDirectory);

        var identity = $"{normalizedProvider}\0{runId}\0{stepId}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var filename = $"{SafeFilenamePart(stepId)}-{SafeFilenamePart(runId)}-{hash}.{normalizedProvider}";
        var transformDatabasePath = Path.Combine(transformDirectory, filename);
        var connectionString = new DbConnectionStringBuilder
        {
            ["Data Source"] = transformDatabasePath
        };
        return Create(normalizedProvider, connectionString.ConnectionString);
    }

    public static DbConnection Create(string provider, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(connectionString);

        return provider.Trim().ToLowerInvariant() switch
        {
            "sqlite" => CreateSqliteConnection(connectionString),
            "duckdb" => CreateDuckDbConnection(connectionString),
            "sqlserver" => new SqlConnection(connectionString),
            "postgresql" => new NpgsqlConnection(connectionString),
            "mysql" => new MySqlConnection(connectionString),
            "oracle" => new OracleConnection(connectionString),
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "The database provider must be SQLite, DuckDB, SQL Server, PostgreSQL, MySQL, or Oracle.")
        };
    }

    internal static DbConnection CreateForPath(string provider, string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        var databaseDirectory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The database path must have a parent directory.", nameof(databasePath));
        Directory.CreateDirectory(databaseDirectory);
        var connectionString = new DbConnectionStringBuilder
        {
            ["Data Source"] = fullPath
        };
        return Create(provider, connectionString.ConnectionString);
    }

    private static SqliteConnection CreateSqliteConnection(string connectionString)
    {
        SqliteNativeLibraryResolver.Register();
        return new SqliteConnection(connectionString);
    }

    private static DuckDBConnection CreateDuckDbConnection(string connectionString)
    {
        DuckDbNativeLibraryResolver.Register();
        return new DuckDBConnection(connectionString);
    }

    private static string SafeFilenamePart(string value)
    {
        var safe = new string(value
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-')
            .Take(64)
            .ToArray())
            .Trim('-', '_');
        return string.IsNullOrEmpty(safe) ? "step" : safe;
    }
}
