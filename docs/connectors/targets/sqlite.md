# SQLite target

The `sqlite` target writes each non-empty input batch to a table through a named `Microsoft.Data.Sqlite.SqliteConnection`. `Path` is omitted. By default, it creates a missing table from the first batch, so rows can be sent directly to a table without pre-creating it. Supply `Options.Schema` to create and validate the table before source readers start.

```powershell
$targetConnection = [Microsoft.Data.Sqlite.SqliteConnection]::new(
    "Data Source=$PWD/output.sqlite")

$steps = @(
    @{
        # Writes the transformed records to the SQLite target table.
        Id = 'Records'
        Type = 'target'
        Inputs = @('TransformedRecords')
        Target = @{
            Type = 'sqlite'
            ConnectionId = 'sqlite-target'
            Table = 'records'
            Options = @{
                CreateTable = $true
            }
        }
    }
)

Invoke-Migration -DatabasePath './migration.sqlite3' `
    -RunName 'SQLite write' -Steps $steps `
    -Connections @{ 'sqlite-target' = $targetConnection }
```

| Option | Default | Description |
|---|---:|---|
| `CreateTable` | `$true` | Create the target table on the first non-empty batch if it is missing. Set `$false` to require a pre-existing table. |
| `Schema` | `null` | Optional explicit column-to-SQL-type map. When provided, a missing table is created with these columns and an existing table must match exactly. Supported types are `INTEGER`, `REAL`, `TEXT`, `BLOB`, and `NUMERIC`. |
| `CommandTimeoutSeconds` | `30` | ADO.NET command timeout in seconds; `0` disables the timeout. |

Without `Schema`, SQLite column types are inferred from the first batch (`INTEGER`, `REAL`, or `TEXT`). Every batch must have the same fields and compatible column types. If the table already exists, both its column names and declared types are checked; a mismatch fails instead of silently writing to the wrong layout. You can create a table yourself before the run and set `CreateTable = $false` to use an established schema.

Writes and a per-batch replay marker are committed in one transaction. An identical retry is ignored; retrying the same batch identity with different records fails. The engine reserves the internal table name `__migration4dataverse_batches`.

Database connections are serialized while in use. A connection that was closed before the write is opened and closed by the engine; an already-open connection stays open.

See [SQLite source](../sources/sqlite.md) and [migration step configuration](../../configuration.md).
