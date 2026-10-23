# DuckDB target

The `duckdb` target writes each non-empty input batch to a table through a named `DuckDB.NET.Data.DuckDBConnection`. The engine includes the DuckDB.NET provider. `Path` is omitted. A missing output table is created automatically from the first batch, allowing a direct source-transform-target flow without a separate table-creation step. Supply `Options.Schema` to create and validate the table before source readers start.

```powershell
$targetConnection = [DuckDB.NET.Data.DuckDBConnection]::new(
    "Data Source=$PWD/output.duckdb")

$steps = @(
    @{
        # Writes the transformed records to the DuckDB target table.
        Id = 'Records'
        Type = 'target'
        Inputs = @('TransformedRecords')
        Target = @{
            Type = 'duckdb'
            ConnectionId = 'duckdb-target'
            Table = 'records'
            Options = @{
                CreateTable = $true
            }
        }
    }
)

Invoke-Migration -DatabasePath './migration.sqlite3' `
    -RunName 'DuckDB write' -Steps $steps `
    -Connections @{ 'duckdb-target' = $targetConnection }
```

| Option | Default | Description |
|---|---:|---|
| `CreateTable` | `$true` | Create the target table on the first non-empty batch if it is missing. Set `$false` to require a pre-existing table. |
| `Schema` | `null` | Optional explicit column-to-SQL-type map. A missing table is created with these columns; an existing table must match exactly. Supported types include `BOOLEAN`, integer widths, `FLOAT`, `REAL`, `DOUBLE`, `DECIMAL`, `DECIMAL(38,10)`, `VARCHAR`, `TEXT`, `BLOB`, `DATE`, `TIMESTAMP`, `TIMESTAMPTZ`, and `UUID`. |
| `CommandTimeoutSeconds` | `30` | ADO.NET command timeout in seconds; `0` disables the timeout. |

Without `Schema`, DuckDB types are inferred from the first batch. Every batch must provide the same fields and compatible types. Existing tables are checked against the inferred or explicit names and types before writing. `CreateTable = $false` is useful when a setup script has already created the table.

Writes and a per-batch replay marker are committed in one transaction. An identical retry is ignored; retrying the same batch identity with different records fails. The engine reserves the internal table name `__migration4dataverse_batches`.

Database connections are serialized while in use. A connection that was closed before the write is opened and closed by the engine; an already-open connection stays open.

See [DuckDB source](../sources/duckdb.md) and [migration step configuration](../../configuration.md).
