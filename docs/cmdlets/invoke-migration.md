# Invoke-Migration

Runs a PowerShell-defined migration graph. Use [step configuration](../configuration.md) for the graph shape and the [connector guides](../connectors/sources/README.md) for connector-specific options.

SQLite and DuckDB Sources and Targets use a `FileInfo` naming the database file or a `DirectoryInfo` plus a step `Path`; the engine creates their database connections and loads the providers automatically. SQL Server, PostgreSQL, MySQL, and Oracle Sources and Targets use caller-supplied ADO.NET connections. Object-storage Sources and Targets use caller-supplied provider clients, HTTP/OData Sources use a URI or `HttpClient`, and MongoDB uses an `IMongoClient` or `IMongoDatabase`. File and object-storage steps select their serialization with a top-level `Format` descriptor, independently of the storage-provider `Type`; format-specific settings belong under `Format.Options`. SQLite and DuckDB Transforms use separate engine-managed, run-and-step-specific databases; do not provide a `ConnectionId` for these transforms. `Transform.InitializeSql` runs before record or batch processing. Both providers support per-batch SQL and whole-dataset SQL after an explicit matching staging Target completes. File targets publish one complete formatted file only after all batches succeed. SQLite Targets validate declared schemas and keys by default; set `CreateTable = $true` only when the run should create missing target objects. See the [transform guide](../dev/transformations/transform.md#database-backed-migration-graph-transforms) and [SQLite target guide](../connectors/targets/sqlite.md).

> [!NOTE]
> Dataverse source and target steps load `Rnwood.Dataverse.Data.PowerShell` automatically. You create and own the connection object. For its complete authentication, connection, query, and write options, see the upstream [module documentation](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell), including [connections](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/core-concepts/connections.md) and [authentication](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/getting-started/authentication.md).

## Parameters

| Parameter | Required | Default / validation | Description |
|---|---:|---|---|
| `-DatabasePath` | Yes | Non-empty path | File-backed SQLite migration store. Its parent directory is created if needed. |
| `-RunName` | Yes | Non-empty string | Human-readable run name. A reused run ID must keep the same name. |
| `-RunId` | No | New GUID in `N` format | Stable run identity. Supply the same ID to resume a compatible run. |
| `-StepHash` | No | Generated SHA-256 | Resume-compatibility identity. By default, normalized step definitions, module requirements and resolved versions, helper script contents, and shared scripts are included. Supply your own only when you manage compatibility. |
| `-Steps` | Yes | One or more PowerShell objects | Migration graph. Hashtables are recommended. |
| `-Connections` | No | Empty hashtable | Map of connection IDs to live objects. The caller retains ownership of each object. |
| `-MaxDegreeOfParallelism` | No | `64`; positive integer | Global worker limit. Dataverse targets are also bounded by per-identity connection recommendations and any target-level `MaxConcurrency` cap. Higher values use more workers and can increase resource use. |
| `-MaxQueuedBatches` | No | `64`; positive | Maximum queued and in-flight work; applies backpressure to source readers. |
| `-MaxQueuedBatchesPerStep` | No | `32`; positive | Per-step bound on queued dependency work and producer reservations. |
| `-MaxAttempts` | No | `5`; positive | Maximum handler attempts before a batch fails. |
| `-DataverseNoRetryDiagnosticMode` | No | Off | Disables Dataverse connection, cmdlet, and scheduler retries for diagnostic runs. |
| `-ModulesToImport` | No | Empty string array | Module names or paths imported into pooled runspaces. Connector modules are loaded as required. |
| `-RequiredModules` | No | Empty `ModuleSpecification` array | Module requirements, optionally pinned by version or GUID. Missing or incompatible requirements fail before scheduling. |
| `-ScriptsToLoad` | No | Empty string array | Shared helper script paths. File contents participate in the generated resume hash. |
| `-SharedScripts` | No | Empty `ScriptBlock` array | Inline helper definitions loaded into each PowerShell-backed invocation; their contents participate in the generated resume hash. |
| `-BatchSize` | No | `10`; range `1..1000` | Default batch size for steps and non-Dataverse targets. Dataverse targets independently default to 10; `Target.BatchSize` can override that value. |
| `-EnableDebugCapture` | No | Off | Opt in to row snapshots, callback stages, and batch outcomes. Captured data is stored in SQLite. |
| `-DebugCaptureRetentionDays` | No | `30`; range `1..3650` | Retention for debug snapshots. Basic run, step, batch, outcome, and checkpoint metadata is not removed by this setting. |
| `-WhatIf` | No | Off | Execute readers and transforms, capture planned target rows, and skip target writes and callback batch handlers. |

The cmdlet reports live progress and returns a run-result object when the run finishes. See [`Get-MigrationRun`](./get-migration-run.md) and the [dashboard guide](./migration-dashboard.md) for persisted status and progress details.

## Example

```powershell
$steps = @(
    @{
        Id = 'CustomersSource'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'file'
            Format = @{ Type = 'jsonl' }
            ConnectionId = 'input'
            Path = 'customers.jsonl'
            Key = 'SourceId'
            Id = 'DataverseId'
        }
    }
    @{
        Id = 'CustomerFields'
        Type = 'transform'
        Inputs = @('CustomersSource')
        Script = {
            param($row)
            @{ name = [string]$row.Name }
        }
    }
    @{
        Id = 'CustomersTarget'
        Type = 'target'
        Inputs = @('CustomerFields')
        Target = @{
            Type = 'dataverse'
            ConnectionId = 'crm'
            Table = 'account'
            Operation = 'Upsert'
        }
    }
)

Invoke-Migration -DatabasePath './state/migration.sqlite3' `
    -RunName 'Customer load' -Steps $steps `
    -Connections @{ input = $inputDirectory; crm = $dataverseConnection }
```

## Resume and replay

Keep the same `RunId`, `RunName`, step definitions, and generated `StepHash` to resume. A changed definition or helper script normally creates a different hash and is not compatible with the existing run. If you supply `-StepHash`, you are responsible for changing it when behavior changes.

The scheduler persists source positions and batch payloads. After interruption, pending or in-flight work may be replayed. A target write can succeed remotely before the local completion record is committed, so use idempotent operations or another reconciliation strategy. See [recovery and replay](../concepts/recovery-and-replay.md).

> [!WARNING]
> `-WhatIf` skips engine target writes, but it still runs source readers and transforms. Caller-authored PowerShell code in those steps may have side effects.

## Debug capture

`-EnableDebugCapture` stores row snapshots in the migration database for inspection in the dashboard. `-WhatIf` enables capture for planned target inputs automatically. Captures may contain business data even though common secret fields are redacted; protect the database and choose an appropriate retention period.

## Connections and parallel processes

The first process to open a database starts its local owner implicitly. When multiple PowerShell processes need to share a database, run [`Start-MigrationDatabaseServer`](./migration-database-server.md) in a dedicated process first. The owner handles SQLite access; schedulers, workers, connections, and callbacks stay in their caller processes. Different run IDs can run concurrently through one owner, while a lease prevents duplicate schedulers for the same run.

## Dataverse throughput settings

Dataverse targets default to fixed batches of 10, independently of `-BatchSize`; an explicit `Target.BatchSize` overrides that default, up to 1,000. Connection pools enforce a separate concurrency budget per identity from each connection's recommendation. Target concurrency is also capped by `-MaxDegreeOfParallelism` (default 64); a numeric target `MaxConcurrency` can set a lower cap. Legacy tuning profiles from earlier versions are ignored; use [`Clear-MigrationTargetTuning`](./clear-migration-target-tuning.md) to remove them if desired.

Retries are enabled by default. Use `-DataverseNoRetryDiagnosticMode` only when intentionally testing the response without the normal retry layers; transient errors can then fail the run immediately. See the [Dataverse target guide](../connectors/targets/dataverse.md) for adapter-specific behavior.

## Custom callbacks

A root callback uses `ReadBatches`; a dependent callback uses `Inputs` and `ExecuteBatch`. Callback outputs follow the `MigrationWorkBatch`, `MigrationBatchResult`, and `MigrationDependencyOutput` contracts described in [callback nodes](../configuration.md#callback-nodes). Multiple callback inputs are explicit batch prerequisites, not an automatic record join.
