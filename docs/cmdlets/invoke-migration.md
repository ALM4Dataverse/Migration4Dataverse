# Invoke-Migration

Runs a durable, top-level migration-step graph through the C# dependency scheduler. Supply `Source`, `Transform`, `Filter`, `Append`, and `Target` nodes, or callback nodes for custom batch workflows. The supported PowerShell shape is documented in [migration step configuration](../configuration.md).

## Parameters

| Parameter | Required | Default / validation | Description |
|---|---:|---|---|
| `-DatabasePath` | Yes | Non-empty path | File-backed SQLite migration store. Its parent directory is created when missing. |
| `-RunName` | Yes | Non-empty string | Human-readable name associated with the run ID. Reusing a run ID with a different name is rejected. |
| `-RunId` | No | New GUID in `N` format | Stable identity used to resume an existing run. Keep the same ID to resume; use a new ID for a distinct run. |
| `-StepHash` | No | SHA-256 of normalized steps and `ModulesToImport` | Resume-compatibility identity. The generated hash also versions row-linked source behavior. Supply a stable hash only when you intentionally manage compatibility yourself. |
| `-Steps` | Yes | One or more native hashtables or PowerShell objects | Top-level migration graph. Every node declares `Id`, `Type`, and `Inputs`; source nodes may also declare `DependsOn`. |
| `-Connections` | No | Empty hashtable | Map from connection IDs to live objects, including `Microsoft.Data.Sqlite.SqliteConnection` and `DuckDB.NET.Data.DuckDBConnection` instances for database connectors. The caller retains ownership of supplied objects. |
| `-MaxDegreeOfParallelism` | No | `4`; positive; scheduler supports at most `32` | Maximum scheduler workers and shared Dataverse connection concurrency ceiling. |
| `-MaxQueuedBatches` | No | `64`; positive | Bounds queued and in-flight work and applies backpressure to readers. |
| `-MaxQueuedBatchesPerStep` | No | `32`; positive | Bounds queued dependency work for each step so one downstream path cannot consume all shared capacity. |
| `-MaxAttempts` | No | `5`; positive | Maximum handler attempts before a batch fails. `-DataverseNoRetryDiagnosticMode` forces one attempt for diagnosing throttling. |
| `-DataverseNoRetryDiagnosticMode` | No | Off | Diagnostic-only switch that disables Dataverse connection retries, `Set-DataverseRecord` retries, and scheduler retries for the run. Normal retries remain enabled by default. |
| `-ModulesToImport` | No | Empty string array | PowerShell modules or paths imported into pooled runspaces. Required connector modules are loaded automatically; custom connector modules are imported from their connector definitions. |
| `-BatchSize` | No | `100`; range `1..1000` | Default batch size for Source steps. A Target step's `BatchSize` overrides it. Callback nodes define their own batch payloads. |
| `-EnableDebugCapture` | No | Off | Opt in to per-row input, output, callback-stage, and batch-outcome snapshots. Debug capture is persisted in SQLite and can materially increase write volume or expose business data. Common secret/token fields are redacted; protect the database and review payloads. |
| `-DebugCaptureRetentionDays` | No | `30`; range `1..3650` | Retention for opt-in debug snapshots. Expired snapshots are purged as new snapshots are captured. This does not delete basic run, step, batch, outcome, or checkpoint metadata. |

The cmdlet emits live PowerShell progress while the scheduler runs, then one `DependencySchedulerResult`. Progress is based on target input rows, including rows still queued or in flight. A percentage is shown only when every target's input stream is finalized and all its batches are countable; otherwise the status says that the total is still growing or unavailable. With multiple targets, totals represent target-write work and may count the same logical source row more than once. The result includes `TargetRowsProcessed`, `TargetRowsSucceeded`, `TargetRowsFailed`, `TargetRowsExpected` (null when unknown), and `FilteredRows` (filter drops summed across steps, so a row removed at multiple steps is counted multiple times). Calling `Stop-Processing` cancels the active run; cancelled work remains durable and can be resumed with the same run identity and step definition.

Every SQLite database has one process owner. Store access starts that owner implicitly in the first caller when needed; for multiple independent PowerShell processes, start [`Start-MigrationDatabaseServer`](./migration-database-server.md) in a dedicated process first. Migration schedulers, workers, live connections, and callbacks remain in their caller processes and communicate with the owner over same-user local IPC. Multiple distinct run IDs may run concurrently through one database; a per-run scheduler lease prevents duplicate schedulers for the same run. Keep the owner alive until all clients have finished. See [Migration dashboard](./migration-dashboard.md) for using the same owner for dashboard reads.

Every run retains basic operational metadata, including the step graph, batch state/timing, attempts, outcomes, row success/failure counts where present, countable input rows, filtered rows, and run status. Standard `Records` batch payloads are counted as they are queued; custom callback payloads without a `Records` array remain explicitly uncounted. Source totals may come from an explicit `ExpectedRecordCount`, a wrapped SQLite/DuckDB `Options.Query` (or an explicit `Options.CountQuery` override), `Get-DataverseRecord -RecordCount` for Simple/FetchXML queries, a custom-source `CountCallback`, or Parquet file metadata. Supported providers run before the migration starts and do not stream source rows to calculate a total. Other step totals are finalized from observed batches after upstream work completes. It also stores a sanitized snapshot of step and connection properties and safe connection-instance snapshots at run start and completion. Credentials and common secret fields are redacted by property name, but arbitrary secrets may not be recognized. The engine-hosted React dashboard binds to loopback at `http://127.0.0.1:5180` by default. Start it with `Start-MigrationDashboard -DatabasePath ...`; see [Migration dashboard](./migration-dashboard.md). Use [`Get-MigrationRun`](./get-migration-run.md) to list or inspect this history from PowerShell.

`Inputs` routes row batches and implicitly declares row-flow dependencies. A source input object uses `Job` and `Keys = @{ Child = '...'; Parent = '...' }` for a keyed parent-source relationship; `RowsPerParent` is optional. Null child keys mean that relationship is absent and the row is retained without waiting on that parent, while unmatched non-null keys fail the source. `DependsOn` accepts only step IDs and is reserved for additional completion barriers. Row-linked source rows are staged in temporary SQLite storage; child files need not be pre-sorted. A `join` node has exactly two ordered `Inputs`: the left/child step and the right/parent step, each with an `As` alias; the parent input also declares ordered `Keys` pairs. Joins can be chained to add parent lookups, including lookups by dotted paths into an earlier joined row. `JoinType` selects `inner` or `left` behavior. `JoinOrder` defaults to `ready`, so matches stream as parent rows arrive even when an earlier child key is still pending; this can reorder output rows. Set it to `stable` to preserve left-input order, at the cost of waiting behind earlier unresolved keys. Non-null unmatched keys resolve when the parent step completes; null keys never match, and left joins retain them immediately. See the [dependency guide](../concepts/dependencies.md) for key configuration and completion semantics.

Debug-capture settings are fixed when a run is created. Resuming with a different capture setting or retention period is rejected.

For Dataverse Targets, `BatchSize` and `MaxConcurrency` default to `auto`. Batch-size experiments are serialized across the pipeline and scored against total successful Dataverse output rows per second. Auto concurrency is a shared adaptive budget for targets on the same connection, bounded by `-MaxDegreeOfParallelism`; connection retry/throttle telemetry can reduce the budget when throttling is observed. Batch sizes persist by target step ID, while shared concurrency profiles are versioned and associated with a connection ID. Compatible values are reused on later runs using the same database, subject to current invocation limits. Use [`Clear-MigrationTargetTuning`](./clear-migration-target-tuning.md) to remove a step's profile or clear all profiles from the database. Normal retry behavior is retained; use `-DataverseNoRetryDiagnosticMode` only when intentionally testing the un-retried service response.

## Step graph example

```powershell
$steps = @(
    @{
        # Reads customer rows from the input JSON Lines file.
        Id = 'CustomersSource'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'jsonl'
            ConnectionId = 'input'
            Path = 'customers.jsonl'
            Key = 'SourceId'
            Id = 'DataverseId'
        }
    }
    @{
        # Maps source fields into the target account schema.
        Id = 'CustomerFields'
        Type = 'transform'
        Inputs = @('CustomersSource')
        Script = {
            param($row)
            @{ name = [string]$row.Name }
        }
    }
    @{
        # Upserts mapped customer rows into Dataverse.
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

## Callback nodes

A root callback node declares `ReadBatches`; a dependent callback node declares one or more `Inputs` and only `ExecuteBatch`. The reader receives `($checkpoint, $sourceConnection, $cursor)` and emits at most one `MigrationWorkBatch` per call. A non-final batch must advance its non-empty `CursorAfterJson`; the final batch sets `IsLast`. The handler receives `($batch, $sourceConnection, $targetConnection)` and emits exactly one `MigrationBatchResult`.

Callback dependency outputs are explicit prerequisites, not implicit data joins. A handler can return `MigrationDependencyOutput` items whose `RequiredBatchIds` identify all required parent batches. See [callback nodes](../configuration.md#callback-nodes).

When debug capture is enabled, callback-supplied `MigrationDebugStage.ParentRecordReferences` can map output rows to indexed input rows in distinct parent batches. This records lineage but does not combine parent payloads.

## Resume and side effects

The default step hash includes normalized step definitions and imported module names. For row-linked sources it also includes a reader-contract version, because older runs do not have the durable source-key index needed to resume safely. Such runs must use a new run ID; callers supplying `-StepHash` are responsible for changing their hash when the row-linked reader contract changes. A run ID can only be resumed with the same run name and step hash. Handlers can be retried after an external side effect has succeeded but before SQLite completion is committed. Built-in SQLite and DuckDB targets use transactional batch markers for replay safety; other custom target callbacks must implement their own replay-safe writes. See [recovery and replay](../concepts/recovery-and-replay.md).
