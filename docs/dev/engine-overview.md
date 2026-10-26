# Internal engine overview and development

This guide describes the repository's implementation and contributor workflow.

> [!IMPORTANT]
> The C# engine types shown here are internal implementation details, not a supported public API.

## Current implementation

The implementation includes an embedded PowerShell runspace executor, a SQLite-backed batch-DAG scheduler, and a binary `Invoke-Migration` cmdlet. Its migration graph supports Source, Join, Transform, Filter, Append, Target, and callback steps. Steps reference inputs by ID; connector definitions and scripts are part of their configuration. Built-in connectors cover JSON Lines, CSV, Parquet, SQLite, DuckDB, Dataverse, and custom PowerShell callbacks. File and SQLite/DuckDB database connectors accept directory-plus-path or exact-file connections; file targets atomically publish one complete output file after the run succeeds. SQLite and DuckDB transforms use engine-managed, run-and-step-specific databases and support per-row, per-batch, and staged whole-dataset SQL transforms with optional upfront initialization SQL. SQLite targets validate declared schemas and keys by default, create missing objects only when explicitly requested, and upsert on declared keys; DuckDB retains its create-missing default. Database writes are transactional and replay-safe.

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

`MaxQueuedBatches` bounds pending plus in-flight batches and applies backpressure to readers and dependency release. `MaxQueuedBatchesPerStep` bounds each step's outstanding batches and producer reservations. Workers reserve downstream capacity and acquire target execution permits before leasing batches; ready steps are scheduled fairly. The dependency pump materializes persisted fan-out work only when a child slot is available, without letting prerequisite-blocked events stall sibling paths. After work drains, the scheduler executes step completion hooks before marking the run successful; file targets use this phase for atomic finalization. `MaxDegreeOfParallelism` bounds concurrent handlers. `MaxAttempts` and `RetryDelay` control handler retries; exhausted failures and invalid result contracts are recorded and fail the run. Run and batch leases prevent concurrent schedulers from owning the same run and make expired in-flight work recoverable.

Handlers and custom targets must make external side effects replay-safe. A process may stop after Dataverse accepts a write but before SQLite commits the result; prefer upserts or another explicit idempotency/reconciliation strategy. SQLite and DuckDB targets atomically record completed batch identities with their writes.

For internal component boundaries, scheduler lifecycles, SQLite state transitions, and Mermaid diagrams, see the [developer architecture guide](./architecture.md).

## Streaming data-flow transformations

The implementation also contains a separate named data-flow DAG with streaming Append/Union All and sorted Merge nodes, stable external-memory Sort, and standalone Transform and Filter nodes. It is distinct from the `Invoke-Migration` graph and is not part of its supported step contract. See the [internal transformation reference](./transformations/README.md) and [step configuration](../configuration.md).

## Build and test

Building the engine also builds the embedded dashboard frontend, so install Node.js and npm as well as the .NET 10 SDK.

Build and run the test suite with:

```powershell
dotnet test Migration4Dataverse.slnx
```

The tests cover JSONL/CSV/Parquet/SQLite/DuckDB/custom connectors, per-row database transforms, SQLite parameterized/read-only source queries, per-batch and whole-dataset SQL, schema/index validation and keyed upserts, Dataverse source query modes through mocked PowerShell cmdlets, custom-module imports in runspaces, streaming transformation nodes and sort spill cleanup, typed PowerShell handlers and cancellation, SQLite schema migration, and scheduler concurrency, dependency priority/gating, retries, and recovery paths. They do not replace an end-to-end test against a non-production Dataverse environment.

## Project status

The implementation includes the batch-DAG scheduler, a binary `Invoke-Migration` cmdlet, built-in connectors, and the separate streaming transformation pipeline. Dataverse auto tuning measures batch-size experiments against whole-pipeline throughput and shares adaptive concurrency per connection, using available retry/throttle telemetry; it does not guarantee protection from service limits. Normal retries remain enabled, with an opt-in no-retry diagnostic mode. The runtime does not use a pool of separately authenticated Dataverse identities. SQLite supports per-row, per-batch, and staged whole-dataset SQL transforms; DuckDB and Dataverse transforms remain per-row or prepared-statement based as documented. The project also lacks end-to-end recovery reconciliation and full KingswaySoft/SSIS feature parity.

See the [feature plan](../../PLAN.md) for the feature inventory, decisions, validated behavior, and remaining work.
