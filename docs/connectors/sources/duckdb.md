# DuckDB source

The `duckdb` source executes a SQL query against a database file. Its `ConnectionId` resolves to a `System.IO.FileInfo` naming the database, or a `System.IO.DirectoryInfo` with `Path` naming the database file relative to that directory. The engine creates and owns the DuckDB connection and loads its provider and native library automatically. `Options.Query` must return a result set. Rows stream from the reader, and column names become record field names.

When no positive `ExpectedRecordCount` is configured, the engine derives the progress count from `Options.Query` by wrapping it in `SELECT COUNT(*) FROM (...)`. This preserves the query's filters, limits, and other row-selection semantics without streaming source records. `Options.CountQuery` may be supplied as an explicit scalar-count override for queries that cannot be wrapped; its result is checked against the streamed row count.

```powershell
$sourceFile = [System.IO.FileInfo]::new((Join-Path $PWD 'source.duckdb'))

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

Invoke-M4dMigration -DatabasePath './migration.sqlite3' `
    -RunName 'DuckDB read' -Steps $steps `
    -Connections @{ 'duckdb-source' = $sourceFile }
```

| Option | Default | Description |
|---|---:|---|
| `Query` | Required | SQL that returns the source rows. Include the `Source.Key` column and order by it for deterministic resume. |
| `CountQuery` | `null` | Optional explicit scalar-count query. When omitted, the engine derives a count by wrapping `Query`; a positive `ExpectedRecordCount` skips count-query execution. |
| `Parameters` | `{}` | Ordered parameters bound to positional `?` placeholders in `Query` and `CountQuery`. |
| `CommandTimeoutSeconds` | `30` | ADO.NET command timeout in seconds; `0` disables the timeout. |

The engine opens and closes its owned connection while reading. Because a source streams from an active reader, its `ConnectionId` must not be reused by another source, transform, or target step; use separate IDs for each database path.

| DuckDB result value | Type in the PowerShell mapping |
|---|---|
| `BOOLEAN`, integer widths, `FLOAT`/`DOUBLE`, `DECIMAL` | `Boolean`, `Int64`, `Double` |
| `VARCHAR` / `TEXT` | `System.String` |
| `BLOB` | Base64 `System.String` after JSON transport |
| `DATE`, timestamps | `System.DateTime` for ISO-formatted values after default PowerShell JSON parsing |
| `UUID` | `System.String` |
| JSON stored as text | `System.String`; parse explicitly when a nested object is needed |

See [target connector](../targets/duckdb.md) and [migration step configuration](../../configuration.md).
