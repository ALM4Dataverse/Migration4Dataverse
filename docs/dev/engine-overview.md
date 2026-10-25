# Engine overview and development

This guide describes the reusable C# engine and development workflow.

## Current implementation

The engine includes an embedded PowerShell runspace executor, a SQLite-backed batch-DAG scheduler, and a generic binary `Invoke-Migration` cmdlet. A migration is a DAG of top-level steps: Source, Transform, Filter, Append, Target, and callback steps. Steps reference their input IDs using `Inputs`; connector definitions and scripts are part of their step configuration. The cmdlet supports JSONL, CSV, Parquet, SQLite, DuckDB, custom, and Dataverse source and target connectors. SQLite and DuckDB targets create missing tables by default or validate caller-supplied schemas; their batch writes are transactional and replay-safe.

The SQLite store retains run/step/batch monitoring metadata, sanitized configuration properties, and start/completion connection snapshots for compatible runs. An engine-hosted, loopback-only React dashboard uses Fluent UI 9 theme tokens, native controls, and React Flow to visualize the dependency graph, preview supplied configuration without execution, and inspect progress, outcomes, batch timings, and failures. `Get-MigrationRun` lists or retrieves persisted run history. `Invoke-Migration -EnableDebugCapture` additionally records row snapshots; the dashboard lets users search each step's inputs and outputs and highlight selected rows' bidirectional lineage with configurable retention. See the [dashboard guide](../cmdlets/migration-dashboard.md) and run the synthetic demo in `examples/dashboard`.

`Inputs` expresses batch-flow dependencies, not an implicit record join. Append unions incoming streams; callback steps can declare explicit batch prerequisites. Stream-wide Merge and Sort remain available through the separate data-flow API, but are not yet supported as `Invoke-Migration` step types. See the [feature plan](../../PLAN.md) for current scope and remaining work.

## C# batch-DAG scheduler

The `DependencyScheduler` API accepts `MigrationStepDefinition` instances. Each definition has a step ID, input IDs, an optional source-batch reader, and a batch handler. Source steps read from their checkpoint; consumers receive dependency batches emitted for their IDs. A handler returns `MigrationBatchResult`, which can include outcomes, debug stages, and `MigrationDependencyOutput` values. Set `RequiredBatchIds` when a callback child also requires other parent batches to succeed; the scheduler does not infer a row-level join.

```csharp
var steps = new[]
{
    new MigrationStepDefinition(
        "AccountsSource",
        inputs: null,
        readSourceBatches: ReadAccountBatchesAsync,
        executeBatchAsync: CaptureAccountBatchAsync,
        type: "source"),
    new MigrationStepDefinition(
        "AccountsTarget",
        inputs: new[] { "AccountsSource" },
        readSourceBatches: null,
        executeBatchAsync: UpsertAccountsAsync,
        type: "target")
};

var scheduler = new DependencyScheduler(new SqliteJobStore(databasePath));
var result = await scheduler.ExecuteAsync(
    runId,
    "account-load",
    stepHash,
    steps,
    new DependencySchedulerOptions
    {
        MaxDegreeOfParallelism = 4,
        MaxQueuedBatches = 64,
        MaxQueuedBatchesPerStep = 32
    });
```

`MaxQueuedBatches` bounds pending plus in-flight batches and applies backpressure to readers and dependency release. `MaxQueuedBatchesPerStep` bounds each step's outstanding batches and producer reservations. Workers reserve downstream capacity and acquire target execution permits before leasing batches; ready steps are scheduled fairly. The dependency pump materializes persisted fan-out work only when a child slot is available, without letting prerequisite-blocked events stall sibling paths. `MaxDegreeOfParallelism` bounds concurrent handlers. `MaxAttempts` and `RetryDelay` control handler retries; exhausted failures and invalid result contracts are recorded and fail the run. Run and batch leases prevent concurrent schedulers from owning the same run and make expired in-flight work recoverable.

Handlers and custom targets must make external side effects replay-safe. A process may stop after Dataverse accepts a write but before SQLite commits the result; prefer upserts or another explicit idempotency/reconciliation strategy. SQLite and DuckDB targets atomically record completed batch identities with their writes.

For internal component boundaries, scheduler lifecycles, SQLite state transitions, and Mermaid diagrams, see the [developer architecture guide](./architecture.md).

## Streaming data-flow transformations

The engine also exposes a separate named data-flow DAG with streaming Append/Union All and sorted Merge nodes, stable external-memory Sort, and standalone Transform and Filter nodes. This data-flow API is distinct from the top-level migration step graph; only supported migration graph types may be used with `Invoke-Migration`. Contracts and limits are documented in the [transformation reference](../transformations/README.md) and [step configuration](../configuration.md).

## Build and test

Build and run the test suite with:

```powershell
dotnet test Migration4Dataverse.slnx
```

The tests cover JSONL/CSV/Parquet/SQLite/DuckDB/custom connectors, per-row database transforms, Dataverse source query modes through mocked PowerShell cmdlets, custom-module imports in runspaces, streaming transformation nodes and sort spill cleanup, typed PowerShell handlers and cancellation, SQLite schema migration, and scheduler concurrency, dependency priority/gating, retries, and recovery paths. They do not replace an end-to-end test against a non-production Dataverse environment.

## Project status

The C# engine includes the batch-DAG scheduler, a binary `Invoke-Migration` cmdlet for typed and callback-based migration steps, built-in file, SQLite, DuckDB, Dataverse, and custom connectors, and a separate streaming transformation DAG. Dataverse auto tuning measures batch-size experiments against whole-pipeline throughput and shares adaptive concurrency per connection, using available retry/throttle telemetry; this is not a guarantee against service-protection limits. Normal retries remain enabled, with an opt-in no-retry diagnostic mode. It is not yet connected to a pool of separately authenticated Dataverse identities. The transformation API supports Append/Union All, sorted Merge, external Sort, and script-backed Transform and Filter nodes; the migration graph currently supports Source, Transform, Filter, Append, Target, and callback steps. Database-backed migration transforms are per-row only; per-batch and whole-dataset modes remain deferred. The project also lacks end-to-end recovery reconciliation and full KingswaySoft/SSIS feature parity.

See the [feature plan](../../PLAN.md) for the feature inventory, decisions, validated behavior, and remaining work.
