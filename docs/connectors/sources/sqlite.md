# SQLite source

The `sqlite` source executes a read-only SQL query against a database file. Its `ConnectionId` resolves to a `System.IO.FileInfo` naming the database, or a `System.IO.DirectoryInfo` with `Path` naming the database file relative to that directory. The engine creates and owns the SQLite connection and loads the provider automatically. `Options.Query` must be one `SELECT` or `WITH` statement that returns a result set. Rows are streamed from the reader, and column names become record field names. Named `Options.Parameters` are bound as values, not interpolated into SQL.

When no positive `ExpectedRecordCount` is configured, the engine derives the progress count from `Options.Query` by wrapping it in `SELECT COUNT(*) FROM (...)`. This preserves the query's filters, limits, and other row-selection semantics without streaming source records. `Options.CountQuery` may be supplied as an explicit scalar-count override for queries that cannot be wrapped; it receives the same named parameters and its result is checked against the streamed row count.

```powershell
$sourceFile = [System.IO.FileInfo]::new((Join-Path $PWD 'source.sqlite'))

$steps = @(
    @{
        # Reads ordered records from the SQLite source database.
        Id = 'Records'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'sqlite'
            ConnectionId = 'sqlite-source'
            Key = 'id'
            Options = @{
                Query = 'SELECT id, name FROM records WHERE id >= @minimumId ORDER BY id'
                Parameters = @{
                    minimumId = 'A001'
                }
                CommandTimeoutSeconds = 30
            }
        }
    }
)

Invoke-M4dMigration -DatabasePath './migration.sqlite3' `
    -RunName 'SQLite read' -Steps $steps `
    -Connections @{ 'sqlite-source' = $sourceFile }
```

| Option | Default | Description |
|---|---:|---|
| `Query` | Required | One read-only `SELECT` or `WITH` statement that returns the source rows. Include the `Source.Key` column and order by it for deterministic resume. |
| `Parameters` | Empty | Optional named query parameters. Use `@name` placeholders and supply matching names and values in a hashtable. |
| `CountQuery` | `null` | Optional explicit scalar-count query. It uses `Parameters`; when omitted, the engine derives a count by wrapping `Query`. A positive `ExpectedRecordCount` skips count-query execution. |
| `CommandTimeoutSeconds` | `30` | ADO.NET command timeout in seconds; `0` disables the timeout. |

SQLite's `query_only` mode is enabled while the source query runs and restored to its previous setting afterward. The engine opens and closes its owned connection while reading. Because a source streams from an active reader, its `ConnectionId` must not be reused by another source, transform, or target step; use separate IDs for each database path.

| SQLite result value | Type in the PowerShell mapping |
|---|---|
| SQL `NULL`, `INTEGER`, `REAL`, `TEXT` | `$null`, `Int64`, `Double`, `String` |
| `BLOB` | Base64 `System.String` after JSON transport |
| `DATE`, timestamps, GUID-like text | `System.DateTime` for ISO date/time values; GUID-like text remains `System.String` |
| JSON stored as text | `System.String`; parse explicitly when a nested object is needed |

See [target connector](../targets/sqlite.md) and [migration step configuration](../../configuration.md).
