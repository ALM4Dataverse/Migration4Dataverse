# Migration step configuration

`Invoke-Migration` accepts native PowerShell hashtables or other PowerShell objects in `-Steps`; native hashtables are sufficient and are used in these examples. Configuration is not a standalone JSON file. A migration is a top-level directed acyclic graph whose edges are declared with `Inputs`. Source and Target connector definitions are inline on their respective nodes. Transform and Filter script blocks are also inline. Runtime connection objects are supplied separately through `-Connections` and are never stored as part of a step definition.

```powershell
$steps = @(
    @{
        # Reads account records from the input files.
        Id = 'CustomersSource'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'csv'
            ConnectionId = 'files'
            Path = 'customers.csv'
            Key = 'SourceId'
            Id = 'DataverseId'
            Options = @{
                ColumnTypes = @{ DataverseId = 'guid' }
            }
        }
    }
    @{
        # Maps each account row to the Dataverse field shape.
        Id = 'CustomerFields'
        Type = 'transform'
        Inputs = @('CustomersSource')
        Script = {
            param($row)
            @{ name = [string]$row.Name }
        }
    }
    @{
        # Upserts the mapped account rows into Dataverse.
        Id = 'CustomersTarget'
        Type = 'target'
        Inputs = @('CustomerFields')
        Target = @{
            Type = 'dataverse'
            ConnectionId = 'crm'
            Table = 'account'
            Operation = 'Upsert'
            BatchSize = 100
        }
    }
)

$connections = @{
    files = [System.IO.DirectoryInfo]'/data/import'
    crm = $dataverseConnection
}

Invoke-Migration -DatabasePath './migration.sqlite3' `
    -RunName 'Customer load' -Steps $steps -Connections $connections
```

## Node contract

Every node has a unique, non-empty `Id`, a `Type`, and an `Inputs` array. Ordinary inputs may be step IDs; keyed source and join inputs are objects with a `Job` step ID and explicit `Keys` pairs. Multiple consumers can reference the same node. A graph must have a root reader, must be acyclic, and every node must contribute to a Target or terminal callback step.

| Type | Inputs | Additional properties |
|---|---:|---|
| `source` | None for a root source, or one or more keyed parent sources | `Source` connector definition; optional `DependsOn` completion barriers. Each keyed input names both `Child` and `Parent` columns in `Keys`. |
| `join` | Exactly two ordered inputs: left/child first, right/parent second | `JoinType` (`inner` or `left`); optional `JoinOrder` (`ready` or `stable`, default `ready`); each input declares `Job` and `As`, and the second also declares ordered `Keys` pairs of `{ Child, Parent }`. Emits one nested row for downstream steps. |
| `transform` | Exactly one | Inline `Script` that emits zero or more dictionaries or custom objects for each row; optional database `Transform` configuration supports SQLite scripts and DuckDB statements. |
| `filter` | Exactly one | Inline `Script` whose output truthiness decides whether the original row passes unchanged. |
| `append` | Two or more | Pass-through union of the input streams. |
| `target` | Exactly one | `Target` connector definition. Target rows may only feed Join relationships. |
| `callback` | Root or one or more | `ExecuteBatch`; root callbacks also require `ReadBatches`, while dependent callbacks must not declare it. |

`sort` and `merge` are available in the standalone data-flow API but are not yet supported as nodes in `Invoke-Migration`; they require stream-wide scheduling rather than per-batch execution. An unsupported node type is rejected before the run starts.

### Source dependencies

`Inputs` declares row flow and implicitly declares those input dependencies. `DependsOn` is reserved for additional completion-only barriers on source-driven source and join steps; it accepts only step IDs and does not transfer rows:

- A source input object with `Job` and `Keys` defines a row-linked source relationship. `Keys` must contain exactly one `{ Child, Parent }` pair; `Parent` must name the parent's configured `Source.Key`. `RowsPerParent` is optional and belongs to that input object.
- Multiple keyed source inputs are supported. An explicit JSON `null` child key means that relationship is absent; the row is retained and emitted before rows with all parent keys present. A non-null key with no matching parent row fails the source. The child-key field must exist; missing or blank values are invalid.
- Row-linked child rows are staged in a temporary SQLite database and released in deterministic order; child files need not be grouped or sorted. Parent `Source.Key` values must be non-empty and unique. A positive `RowsPerParent` applies independently to that relationship and requires exactly that many children for every parent row; null relationships do not count. Omit it (or set it to `0`) for variable counts, including zero.
- Do not repeat an input step ID in `DependsOn`. Use a completion barrier only for a separate step whose completion is required before the source or join starts.

See the [dependency guide](./concepts/dependencies.md) for examples and constraints.

### Transform and Filter semantics

- A **Transform** script receives one input row as `$row` and may emit zero or more dictionaries or custom objects. No output drops the row; multiple outputs create multiple records.
- A **Join** is a distinct graph step; it does not change the standalone data-flow `Merge` operator. It has exactly two inputs in order: a left/child stream, then a right/parent stream. Each input object has `Job` and `As`; the second also has one or more ordered `Keys` pairs of `{ Child, Parent }`. Keys may be dotted paths into a nested joined row. If the second input is a Dataverse Target, its `Parent` columns must match one ordered `Target.Options.MatchOn` group. A Source can also provide parent rows; its key fields must be present and unique. Joins can be chained to add further parent lookups.
- `JoinType` is required. `inner` drops a child row with no parent match; `left` preserves it and sets the parent alias to `$null`. Null or blank child key components never match; `left` preserves those rows and `inner` filters them. Parent Target records contain mapped columns and the final Dataverse `Id`; Source records contain the selected source columns and configured `Id`. A downstream Transform receives one nested row as `$row`, for example `$row.Account.Id`.
- Join rows are indexed in SQLite as input batches complete. The default `JoinOrder = 'ready'` emits available matches without waiting behind an earlier child whose parent has not arrived; later child rows can therefore be emitted first. Deferred rows are resolved when a parent arrives or the parent step completes. Set `JoinOrder = 'stable'` to preserve left-input order, which can delay later matches behind an earlier unresolved key. A null key does not wait. Inputs do not need to be sorted by join keys. Staging is retained for recovery after failure and cleared after successful completion.
- A **Filter** script uses PowerShell output truthiness. A truthy result passes the original row unchanged; no output or a falsey result drops it.
- For each Transform output, the engine preserves the input `Id` unless the script supplies an `Id` itself. Source connector property `Source.Id` initializes `Id` when present.
- Script blocks execute as source text in engine-owned PowerShell runspaces; caller closure state is not captured.
- A database Transform declares a `Transform` map with `Type` set to `sqlite` or `duckdb` and a `ConnectionId`. Its PowerShell `Script` receives `($row, $connection)` and can use the live connection. SQLite scripts run once per input row. DuckDB can instead declare `Transform.Sql` and `Transform.Parameters`; the prepared statement runs once per row, with positional `?` parameters mapped to row fields in the order of the `Parameters` hashtable. A statement that returns rows transforms to those rows; a statement without a result set preserves the input row.
- Database Transform scripts and prepared statements are per-row only. Per-batch and whole-dataset database transforms are not currently supported.

### Source properties

| Property | Required | Description |
|---|---:|---|
| `Type` | Yes | `jsonl`, `csv`, `parquet`, `sqlite`, `duckdb`, `dataverse`, or `custom`. |
| `ConnectionId` | Yes | Key in `-Connections`. File sources require a `System.IO.DirectoryInfo`; SQLite/DuckDB use `Microsoft.Data.Sqlite.SqliteConnection` or `DuckDB.NET.Data.DuckDBConnection`; Dataverse and custom sources use caller-provided connection objects. |
| `Path` | File sources | Relative path below the source directory connection. Required for JSONL, CSV, and Parquet; omitted for databases and Dataverse. Custom sources may use it as a callback argument. |
| `Key` | Yes | Source field used for deterministic batch checkpointing. Root-source values must be non-empty, unique, and strictly increasing in ordinal order. Row-linked child keys must be non-empty and unique within each non-null parent relationship; the engine stages and orders those rows, so their input need not be grouped by the parent key. A source used as a keyed parent must have non-empty, unique key values. |
| `Id` | No | Source property copied to the record's `Id` field when present. |
| `ExpectedRecordCount` | No | Optional explicit expected row count. A positive value supplies the progress total and is checked against the source rows when enumeration completes. It takes precedence over automatic providers. Otherwise, SQLite and DuckDB derive counts by wrapping `Options.Query`, Simple and FetchXML Dataverse sources use `Get-DataverseRecord -TotalRecordCount`, custom sources can use `Custom.CountCallback`, and Parquet sources use file metadata. |
| `Options` | No | Dataverse query or connector-specific options. SQLite and DuckDB require a `Query` returning rows; an optional `CountQuery` overrides the automatic count derived from `Query`. See [SQLite sources](./connectors/sources/sqlite.md) and [DuckDB sources](./connectors/sources/duckdb.md). |
| `Custom` | Custom only | Custom callback definition; see [custom sources](./connectors/sources/custom.md). |

### Target properties

| Property | Required | Description |
|---|---:|---|
| `Type` | Yes | `dataverse`, `jsonl`, `csv`, `parquet`, `sqlite`, `duckdb`, or `custom`. |
| `ConnectionId` | Yes | Key in `-Connections`; Dataverse expects a module connection, file targets a `System.IO.DirectoryInfo`, SQLite/DuckDB require their ADO.NET connection, and custom targets accept any non-null object. |
| `Path` | File targets | Relative path below the target directory connection; required for JSONL, CSV, and Parquet. |
| `Table` | Dataverse and database targets | Dataverse logical table name or SQLite/DuckDB output table (optionally schema-qualified). |
| `Operation` | No | `Upsert` for Dataverse; `Write` for file, database, and custom targets. |
| `BatchSize` | No | Positive override for the invocation's `-BatchSize`; Dataverse also accepts `auto` (the default) to tune up to the invocation batch size. |
| `MaxConcurrency` | Dataverse only | Positive per-target limit or `auto` (default) for concurrent target batches. Auto tuning is bounded by `-MaxDegreeOfParallelism`; target steps sharing a connection tune independently. |
| `Options` | No | Connector-specific options. Database targets default to creating a missing table from the first non-empty batch; `Schema` can declare column types up front and `CreateTable = $false` requires a pre-created table. Existing table columns and types are validated. See [SQLite targets](./connectors/targets/sqlite.md) and [DuckDB targets](./connectors/targets/duckdb.md). Dataverse options include `MatchOn` and `LookupColumns`; CSV or Parquet settings are described in the [CSV](./connectors/targets/csv.md) and [Parquet](./connectors/targets/parquet.md) references. |
| `Custom` | Custom only | Custom callback definition; see [custom targets](./connectors/targets/custom.md). |

For a Dataverse lookup that references a record written earlier in the run, use a Join to expose the mapped parent record, then assign its final `Id` in a `{ TableName, Id }` reference from the single Transform input row. Direct references to existing Dataverse records may also provide their GUID directly. Parent snapshots contain all mapped columns and remain in the migration database for resume, so protect that file. The engine no longer resolves alternate-key references embedded in Transform output. See [Dataverse targets](./connectors/targets/dataverse.md) for an example.

## Callback nodes

Callback nodes expose the scheduler's batch contracts for integrations that need custom batch generation or handling.

```powershell
$steps = @(
    @{
        # Reads resumable batches from the external source.
        Id = 'ReadExternalData'
        Type = 'callback'
        Inputs = @()
        SourceConnectionId = 'source'
        ReadBatches = {
            param($Checkpoint, $Connection, $Cursor)
            # Return at most one MigrationWorkBatch.
        }
        ExecuteBatch = {
            param($Batch, $SourceConnection, $TargetConnection)
            # Return exactly one MigrationBatchResult.
        }
    }
    @{
        # Processes each batch produced by the reader.
        Id = 'ProcessExternalData'
        Type = 'callback'
        Inputs = @('ReadExternalData')
        ExecuteBatch = {
            param($Batch)
            # Return exactly one MigrationBatchResult.
        }
    }
)
```

`ReadBatches` is used only on a root callback and receives a `MigrationStepCheckpoint`, resolved source connection, and cursor (`$null` for the first page). It may emit zero or one `MigrationWorkBatch`; every non-final batch must advance its non-empty `CursorAfterJson`, and the final batch sets `IsLast`.

`ExecuteBatch` receives a leased `StoredBatch` and any resolved source and target connections. It emits exactly one `MigrationBatchResult`. `MigrationDependencyOutput` can route payloads to declared child step IDs and specify `RequiredBatchIds` for explicit multi-parent batch prerequisites. These prerequisites gate release; they do not implicitly join records. See the [dependency guide](./concepts/dependencies.md).

## Connection map and history

Connection IDs resolve against the `-Connections` dictionary. Reusing an ID shares the runtime object. The caller owns connection lifetime and credential security. Source and target file paths resolve beneath their corresponding `DirectoryInfo` roots.

The engine stores a sanitized snapshot of the step graph and connection properties for run history and configuration previews. Live connection instances are recorded separately with sensitive values redacted. See [`Get-MigrationRun`](./cmdlets/get-migration-run.md) and the [dashboard guide](./cmdlets/migration-dashboard.md).
