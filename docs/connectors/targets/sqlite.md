# SQLite target

The `sqlite` target writes each non-empty input batch to a table in a database file. Its `ConnectionId` resolves to a `System.IO.FileInfo` naming the database, or a `System.IO.DirectoryInfo` with `Path` naming the database file relative to that directory. The engine creates and owns the SQLite connection and loads the provider automatically. Declare `Options.Schema` and `Options.KeyColumns`; existing tables and indexes are validated before writing, and rows are upserted on the declared key. The default is validate-only: missing tables or indexes fail rather than being created. Set `CreateTable = $true` to explicitly permit creation of a missing table and declared indexes. Existing objects are never altered or dropped.

```powershell
$targetFile = [System.IO.FileInfo]::new((Join-Path $PWD 'output.sqlite'))

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
                Schema = [ordered]@{
                    Id = 'TEXT'
                    Name = 'TEXT'
                }
                KeyColumns = @('Id')
                Indexes = @(
                    @{
                        Name = 'IX_records_name'
                        Columns = @('Name')
                    }
                )
            }
        }
    }
)

Invoke-Migration -DatabasePath './migration.sqlite3' `
    -RunName 'SQLite write' -Steps $steps `
    -Connections @{ 'sqlite-target' = $targetFile }
```

| Option | Default | Description |
|---|---:|---|
| `Schema` | Required | Ordered column-to-SQL-type map. The table must have the same columns and declared types. Supported types are `INTEGER`, `REAL`, `TEXT`, `BLOB`, and `NUMERIC`. |
| `KeyColumns` | Required | Ordered, non-empty columns used for upsert conflict resolution. The table must have a matching unique key/index. |
| `Indexes` | Empty | Optional index declarations with `Name`, ordered `Columns`, and optional `Unique` (`$false` by default). Existing indexes are validated by name, uniqueness, and ordered columns. |
| `CreateTable` | `$false` | When `$true`, create a missing table, key index, and declared indexes. When omitted or `$false`, missing objects fail validation. No existing object is modified or dropped. |
| `CommandTimeoutSeconds` | `30` | ADO.NET command timeout in seconds; `0` disables the timeout. |

`KeyColumns` must be backed by a full, non-partial unique index with the same ordered columns. Every batch must match the declared schema. Writes use SQLite upsert semantics on the declared key. A write and its per-batch replay marker are committed in one transaction; an identical retry is ignored, while retrying the same batch identity with different records fails. The engine reserves the internal table name `__migration4dataverse_batches`.

Database connections are serialized while in use. Each database connection is opened and disposed by the engine.

| SQLite column type | Accepted PowerShell/JSON value |
|---|---|
| `INTEGER`, `REAL`, `NUMERIC` | Boolean / JSON number convertible to the declared type |
| `TEXT` | String; object/array values become JSON text |
| `BLOB` | Base64 `System.String`, decoded to bytes |
| `DATE`, timestamp, GUID stored in text/numeric columns | ISO text or supported JSON scalar representation |

See [SQLite source](../sources/sqlite.md) and [migration step configuration](../../configuration.md).
