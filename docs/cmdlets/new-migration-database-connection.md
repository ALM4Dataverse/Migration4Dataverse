# New-MigrationDatabaseConnection

Creates a live ADO.NET connection for one of the built-in SQLite or DuckDB connectors. The engine loads the provider assemblies, so scripts can create connections without adding provider assemblies themselves. The returned connection is not opened; the caller owns and should dispose it.

## Syntax

```powershell
New-MigrationDatabaseConnection -Provider SQLite -ConnectionString 'Data Source=source.sqlite'
New-MigrationDatabaseConnection -Provider DuckDB -ConnectionString 'Data Source=source.duckdb'
```

## Parameters

| Parameter | Required | Description |
|---|---:|---|
| `-Provider` | Yes | `SQLite` or `DuckDB`, matching the connector type used by migration steps. |
| `-ConnectionString` | Yes | Provider-specific ADO.NET connection string. |

The returned `DbConnection` can be added to the `-Connections` map and reused by database Source, Transform, and Target steps. Call `Dispose()` when the migration and any other consumers have finished with it.
