# Migration step configuration

`Invoke-M4dMigration -Steps` accepts PowerShell objects; native hashtables are the simplest way to define a migration. The definitions are not a standalone JSON or SSIS package. A migration is a directed acyclic graph whose nodes read, transform, route, or write rows.

> [!NOTE]
> Put live connection objects in `-Connections`, not in the step definitions. This keeps credentials and runtime objects separate from the configuration used for run history and resume checks.

## A small migration

```powershell
$steps = @(
    @{
        Id = 'AccountsSource'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'file'
            Format = @{ Type = 'csv' }
            ConnectionId = 'files'
            Path = 'accounts.csv'
            Key = 'SourceId'
        }
    }
    @{
        Id = 'MapAccounts'
        Type = 'transform'
        Inputs = @('AccountsSource')
        Script = {
            param($row)
            @{
                new_sourceid = [string]$row.SourceId
                name = ([string]$row.Name).Trim()
            }
        }
    }
    @{
        Id = 'AccountsTarget'
        Type = 'target'
        Inputs = @('MapAccounts')
        Target = @{
            Type = 'dataverse'
            ConnectionId = 'crm'
            Table = 'account'
            Operation = 'Upsert'
            Options = @{ MatchOn = ,@('new_sourceid') }
        }
    }
)

$connections = @{
    files = [System.IO.DirectoryInfo]'/data/import'
    crm = $dataverseConnection
}

Invoke-M4dMigration -DatabasePath './migration.sqlite3' `
    -RunName 'Account import' -Steps $steps -Connections $connections
```

## Node contract

Each node has a unique `Id`, a `Type`, and an `Inputs` array. `Inputs` names the node or nodes that provide rows; it does not automatically join multiple inputs. A graph must be acyclic, contain a root source or root callback, and route its work to a target or terminal callback.

| Type | Inputs | Additional configuration |
|---|---|---|
| `source` | None for a root reader, or keyed source inputs | `Source`; optionally `DependsOn` for completion-only barriers. |
| `transform` | Exactly one | Inline `Script`, or a supported database-backed `Transform`. |
| `filter` | Exactly one | Inline `Script`; truthy output retains the row. |
| `append` | Two or more | Concatenates the input streams without matching or deduplicating rows. |
| `join` | Exactly two, ordered child/left then parent/right | `JoinType`, aliases, and ordered key pairs. |
| `target` | Exactly one | `Target`. A target can feed a `join`, or a matching SQLite/DuckDB `transform` in `whole_dataset` mode as a completion barrier. |
| `callback` | Root or one or more | Root callbacks also define `ReadBatches`; every callback defines `ExecuteBatch`. |

Every referenced step ID must exist. A node may have multiple consumers. `sort` and `merge` are not supported `Invoke-M4dMigration` step types and are rejected during configuration validation.

## Shared step properties

| Property | Required | Description |
|---|---:|---|
| `ErrorRateThresholdPercent` | No | Inclusive maximum cumulative failed-row percentage for this step, from `0` to `100`. It overrides `Invoke-M4dMigration -MaxErrorRatePercent`; both default to `0`, which preserves stop-on-row-error behavior. |

The error rate is calculated from failed rows divided by succeeded plus failed rows across completed batches in that step. A threshold accepts row-level errors only; exhausted source-reader, handler, and infrastructure exceptions remain fatal. Downstream receives only dependency payloads explicitly emitted by the step; a handler that reports partial success must emit only the successful rows it intends downstream to process. Accepted errors remain visible and the run finishes as `CompletedWithErrors`.

## Source dependencies

`Inputs` declares row flow. Use a keyed source input when a source row refers to one or more parent **source** rows. Use a `join` node to combine a child stream with a source or Dataverse target stream. Use `DependsOn` only when a source or join must wait for another step to finish without receiving its rows.

```powershell
Inputs = @(
    @{
        Job = 'OrdersSource'
        Keys = @{ Child = 'OrderId'; Parent = 'OrderId' }
        RowsPerParent = 2
    }
)
```

The full rules for null keys, unmatched parents, fixed child counts, join ordering, and completion barriers are in the [dependency guide](./concepts/dependencies.md).

## Transform and Filter semantics

- A Transform script receives one input row as `$row` and may emit zero, one, or multiple record objects. No output drops the row; multiple outputs expand it.
- Unless the script supplies an `Id`, each Transform output keeps the input `Id`. A source `Id` property initializes that value when present.
- A Filter script uses PowerShell output truthiness. A truthy result passes the original row unchanged; no output or a falsey result drops it.
- Scripts run as source text in engine-owned runspaces. They do not capture variables from the caller's interactive scope.

A database-backed Transform can use a `Transform` map whose `Type` is `sqlite` or `duckdb`; do not set `ConnectionId`. The engine creates a database file beside `-DatabasePath` for each provider, run ID, and transform step ID. `Transform.InitializeSql` runs on that database before any source records or target batches are processed; make it safe to rerun when resuming a run. A PowerShell `Script` receives `($row, $connection)` and runs once per row. DuckDB can instead use a prepared per-row `Transform.Sql` statement with ordered `Transform.Parameters`; result rows become Transform output, while a statement without a result set passes through the input row. Dataverse transforms remain connection-backed and require `ConnectionId`. SQLite and DuckDB transforms accept `Transform.CommandTimeoutSeconds` (default `30`; `0` disables the timeout) for engine-run initialization and SQL commands.

SQLite and DuckDB both support set-based SQL transforms. `Mode = 'per_batch'` stages the current bounded input batch in a temporary table named `__migration4dataverse_input`, executes `Transform.Sql`, and requires the result to include either `__migration_parent_index` or `__migration_parent_indices` (a JSON array) so output lineage is explicit. These lineage columns are removed from emitted rows. SQLite uses named `Transform.Parameters`; DuckDB uses an ordered `Transform.Parameters` map bound to positional `?` placeholders. `Mode = 'whole_dataset'` requires one `Inputs` step that is a matching SQLite or DuckDB Target; omit `ConnectionId` and `Path` from the staging Target and `ConnectionId` from the Transform so the engine can share their managed database. The target must finish staging the rows before the transform's query is read. Configure `Transform.Key` as the stable result key. The query is a single read-only `SELECT` or `WITH` statement. Provide `Transform.ExpectedRecordCount` when the exact result count is known; `Transform.CountQuery` is not supported in this mode. Whole-dataset mode reads the staged table(s) as a source after the barrier; it does not consume the target's batch payloads directly. SQLite uses named `Transform.Parameters` for whole-dataset queries; DuckDB uses an ordered parameter map and positional `?` placeholders. See the [database SQL transform guide](./dev/transformations/transform.md#database-backed-migration-graph-transforms).

## Source properties

Every source has a `Source` hashtable. See the [source connector index](./connectors/sources/README.md) for connector-specific settings.

| Property | Required | Description |
|---|---:|---|
| `Type` | Yes | `file`, `sqlite`, `duckdb`, `sqlserver`, `postgresql`, `mysql`, `oracle`, `azureblob`, `s3`, `adlsgen2`, `gcs`, `http`, `odata`, `salesforce`, `sharepoint`, `hubspot`, `zendesk`, `servicenow`, `mongodb`, `dataverse`, or `custom`. |
| `Format` | File and object-storage sources | Required descriptor with `Type` (`jsonl`, `csv`, `parquet`, `excel`, `binary`, or `custom`) and optional format `Options`. Custom formats can also declare `Extensions`, source `Identity`, and a `Read` scriptblock; see [object storage and custom formats](./connectors/object-storage.md#custom-formats). |
| `ConnectionId` | Yes | Key in `-Connections`. File and SQLite/DuckDB sources expect a `DirectoryInfo` or `FileInfo`; SQL Server, PostgreSQL, MySQL, and Oracle expect a matching ADO.NET `DbConnection`; object-storage sources expect the provider client; HTTP sources expect a `Uri`, absolute URL string, or `HttpClient`; Salesforce, SharePoint, HubSpot, Zendesk, and ServiceNow sources expect an authenticated `HttpClient`; MongoDB sources expect an `IMongoClient` or `IMongoDatabase`. |
| `Path` | File, object-storage, and HTTP sources | File sources use an exact filename relative to the source directory; object-storage sources use an object prefix; HTTP sources use a URL or relative path. Omit it for a `FileInfo`, SQL database, API source, MongoDB, or Dataverse source. An optional custom-source path is passed to the callback; omitted means an empty string. |
| `Key` | Yes | Stable field used to order and checkpoint source rows. Root source keys must be non-empty, unique, and strictly increasing. Dataverse uses case-insensitive ordinal order. |
| `Id` | No | Source field copied to the row's `Id`, commonly the target record GUID. |
| `ExpectedRecordCount` | No | Positive expected row count. It takes precedence over a connector's count provider and is checked when reading completes. |
| `Options` | No | Connector/provider-specific options. Format settings belong in `Format.Options`; file sources do not accept storage `Options`. See the linked source guide. |
| `ResolveBinaryReference` | CSV, JSON Lines, or Parquet | Optional scriptblock to resolve a field declared in `Format.Options.BinaryColumns`; see [binary values and sidecar files](./concepts/binary-values-and-sidecars.md). |
| `Partitions` | Dataverse root source | Explicit parallel query partitions; each partition has a stable `Id` and optional `Options` overrides. See the [Dataverse source guide](./connectors/sources/dataverse.md#parallel-source-partitions). |
| `Custom` | Custom source | Callback definition; see [custom sources](./connectors/sources/custom.md). |

For row-linked child sources, rows are staged and ordered by their parent relationships; the source file itself need not be sorted by those parent keys. Parent source keys must be unique. See [recovery and replay](./concepts/recovery-and-replay.md) for stable-source requirements.

## Target properties

Every target has a `Target` hashtable. See the [target connector index](./connectors/targets/README.md) for connector-specific settings.

| Property | Required | Description |
|---|---:|---|
| `Type` | Yes | `dataverse`, `file`, `sqlite`, `duckdb`, `sqlserver`, `postgresql`, `mysql`, `oracle`, `azureblob`, `s3`, `adlsgen2`, `gcs`, `sharepoint`, `mongodb`, or `custom`. |
| `Format` | File and object-storage targets | Required descriptor with `Type` (`jsonl`, `csv`, `parquet`, `binary`, or `custom`) and optional format `Options`. Custom formats can also declare `Extensions`, `ContentType`, and a `Write` scriptblock; see [object storage and custom formats](./connectors/object-storage.md#custom-formats). |
| `ConnectionId` | Yes, except managed staging targets | Key in `-Connections`. File and SQLite/DuckDB targets expect a `DirectoryInfo` or `FileInfo`; SQL Server, PostgreSQL, MySQL, and Oracle expect a matching ADO.NET `DbConnection`; object-storage targets expect the provider client; SharePoint targets expect an authenticated Graph `HttpClient`; MongoDB targets expect an `IMongoClient` or `IMongoDatabase`. SQLite or DuckDB staging targets used by a whole-dataset transform omit `ConnectionId` because the engine manages their database. |
| `Path` | File and object-storage targets | Exact filename relative to the target directory, or the object key/prefix for object storage. Binary filesystem targets publish one file; binary object-storage targets append each record's key to the prefix. Omit it for a `FileInfo`, SQL database, MongoDB, SharePoint, or Dataverse target. Whole-dataset database staging targets omit both `ConnectionId` and `Path`. An optional custom-target path is exposed as `Context.TargetPath`; omitted means an empty string. |
| `Table` | Dataverse, SQL database, and MongoDB targets | Dataverse logical table, SQL table, or MongoDB collection name. |
| `Operation` | No | Defaults to `Upsert` for Dataverse and `Write` for other targets. |
| `BatchSize` | No | Positive fixed target override. Non-Dataverse targets otherwise inherit `Invoke-M4dMigration -BatchSize`; Dataverse targets default independently to 10 and are limited to 1,000 rows. |
| `MaxConcurrency` | Dataverse only | Optional positive per-target concurrency cap. When omitted, the target uses its connection's recommended identity budget. |
| `Options` | No | Connector/provider-specific settings, such as database schema/key definitions, object-storage prefix/bucket, HTTP pagination, MongoDB filter/key columns, or Dataverse matching keys. Format settings belong in `Format.Options`; file targets do not accept storage `Options`. See the linked connector guides. |
| `CreateBinaryReference` | CSV, JSON Lines, or Parquet | Optional scriptblock to choose a relative sidecar path for top-level binary fields; see [binary values and sidecar files](./concepts/binary-values-and-sidecars.md). |
| `Custom` | Custom target | Callback definition; see [custom targets](./connectors/targets/custom.md). |

## Callback nodes

Callbacks expose the batch protocol for integrations that need a custom source reader or batch handler. A root callback defines `ReadBatches`; a dependent callback defines one or more `Inputs` and only `ExecuteBatch`.

`ReadBatches` receives a `MigrationStepCheckpoint`, the resolved source connection, and a cursor. It returns at most one `MigrationWorkBatch` per call; no output ends the reader. `MigrationWorkBatch.PayloadJson` carries the batch records as JSON; non-final batches must advance `CursorAfterJson`, and the final batch sets `IsLast`. `ExecuteBatch` receives a leased `StoredBatch` and emits exactly one `MigrationBatchResult`.

To make a callback failure eligible for row-only retry, return `MigrationBatchResult.RowErrors` with one `MigrationRowError` per failed input record. Each `RecordIndex` is zero-based in the input batch's `Records` array; set `HasErrors` to `$true`, include matching failed-row outcomes, and do not emit outputs for failed rows. The dashboard then allows the operator to edit that input row's JSON and retry only it. If the callback cannot identify the exact input row, leave `RowErrors` empty and use the batch-level recovery options instead.

Set optional `SourceConnectionId` and `TargetConnectionId` on a callback step to resolve entries from `-Connections`. `ReadBatches` receives the source connection, while `ExecuteBatch` receives both connections; an unset connection is `$null`.

A callback can return `MigrationDependencyOutput` values with `RequiredBatchIds` to wait for additional parent batches. This creates an explicit batch prerequisite; it does not join records. See the [dependency guide](./concepts/dependencies.md#callback-multi-parent-prerequisites).

## Connection map and history

Connection IDs resolve against the `-Connections` map. Reusing an ID shares the same runtime object; database connection IDs must resolve consistently to the same provider and database path. A typed Dataverse source or target may use one connection object or an array of `ServiceClient` objects:

```powershell
$connections = @{
    crm = @($crmAsUserA, $crmAsUserB)
}
```

The array is a per-run pool for typed Dataverse Sources and Targets; the same `ConnectionId` shares the pool across both. It is not supported for other connector types, callbacks, or Dataverse transforms. Pool members must target the same organization and use the same `MaxRetryCount`. The caller owns SQL database, object-storage, HTTP, MongoDB, custom, and Dataverse connection creation, credentials, lifetime, and disposal; the engine disposes only its temporary pool wrapper and any client it creates from a URI. File targets stage batches and atomically publish output before the run is marked successful; object-storage and SharePoint binary targets stage records locally and upload files during finalization. SQLite and DuckDB transform connections are engine-owned and are stored under `.migration-transforms` beside the migration database.

The store records a sanitized snapshot of the graph and safe connection properties for run history and configuration previews. Redaction is best-effort; do not put credentials or secrets in step definitions. See [`Get-M4dMigrationRun`](./cmdlets/get-migration-run.md), the [dashboard guide](./cmdlets/migration-dashboard.md), and [connections and callbacks](./concepts/connections-and-callbacks.md).
