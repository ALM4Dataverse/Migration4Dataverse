# DuckDB source

The `duckdb` source executes a SQL query through a named `DuckDB.NET.Data.DuckDBConnection`. The engine includes the DuckDB.NET provider. `Path` is omitted; the connection object identifies the database. `Options.Query` must return a result set. Rows stream from the reader, and column names become record field names.

When no positive `ExpectedRecordCount` is configured, the engine derives the progress count from `Options.Query` by wrapping it in `SELECT COUNT(*) FROM (...)`. This preserves the query's filters, limits, and other row-selection semantics without streaming source records. `Options.CountQuery` may be supplied as an explicit scalar-count override for queries that cannot be wrapped; its result is checked against the streamed row count.

```powershell
$sourceConnection = [DuckDB.NET.Data.DuckDBConnection]::new(
    "Data Source=$PWD/source.duckdb")

$steps = @(
    @{
        # Reads ordered records from the DuckDB source database.
        Id = 'Records'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'duckdb'
            ConnectionId = 'duckdb-source'
            Key = 'id'
            Options = @{
                Query = 'SELECT id, name FROM records ORDER BY id'
                CommandTimeoutSeconds = 30
            }
        }
    }
)

Invoke-Migration -DatabasePath './migration.sqlite3' `
    -RunName 'DuckDB read' -Steps $steps `
    -Connections @{ 'duckdb-source' = $sourceConnection }
```

| Option | Default | Description |
|---|---:|---|
| `Query` | Required | SQL that returns the source rows. Include the `Source.Key` column and order by it for deterministic resume. |
| `CountQuery` | `null` | Optional explicit scalar-count query. When omitted, the engine derives a count by wrapping `Query`; a positive `ExpectedRecordCount` skips count-query execution. |
| `CommandTimeoutSeconds` | `30` | ADO.NET command timeout in seconds; `0` disables the timeout. |

The engine opens a closed connection when it starts reading and closes it when reading completes. A connection that was already open is left open. Because the source streams from an active reader, its connection instance must not be reused by another source, transform, or target step; use separate named connection instances.

See [target connector](../targets/duckdb.md) and [migration step configuration](../../configuration.md).
