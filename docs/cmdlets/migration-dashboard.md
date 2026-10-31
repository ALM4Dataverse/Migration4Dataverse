# Migration dashboard

`Start-MigrationDashboard` starts the engine-hosted React single-page app and read-only HTTP API. The SPA uses React Flow for pipeline graphs. `Stop-MigrationDashboard` stops and disposes the returned server handle.

## Parameters

### Start-MigrationDashboard

| Parameter | Required | Default / validation | Description |
|---|---:|---|---|
| `-DatabasePath` | One of `-DatabasePath` or `-Steps` | Non-empty path | File-backed SQLite migration database whose stored runs are displayed. |
| `-Steps` | One of `-DatabasePath` or `-Steps` | Empty array | Step definitions to preview; accepts the same objects as `Invoke-Migration`. |
| `-Connections` | No | `$null` | Connection map used with `-Steps`. Cannot be supplied without steps. |
| `-Url` | No | `http://127.0.0.1:5180` | HTTP loopback address and port for the dashboard. Non-loopback URLs are rejected. |

`Start-MigrationDashboard` returns a dashboard handle whose `Url` property reports the listening address. Provide at least one of `-DatabasePath` or `-Steps`; supply both to show a configuration preview alongside persisted run history.

### Stop-MigrationDashboard

| Parameter | Required | Default / validation | Description |
|---|---:|---|---|
| `-Dashboard` | Yes | Pipeline input | Dashboard handle returned by `Start-MigrationDashboard`. |

The interface uses the project logo and Fluent UI 9 theme tokens with native HTML controls. The dependency graph fills the workspace below the header; the run overview and combined step inspector are collapsible panels. The inspector has separate tabs for captured rows and configuration. Row inspection is available per step in debug-captured runs: each captured stage has its own search and tab, system metadata appears before individual payload properties, and a toggle switches the payload to one JSON column. Rows load as the table is scrolled; search, column filters, and sorting are applied across all matching snapshots by the server. Selecting a row highlights its lineage and shows captured values at each traced step directly in the graph.

```powershell
$dashboard = Start-MigrationDashboard -DatabasePath './state/migration.sqlite3'
Write-Host "Open $($dashboard.Url)"
try {
    # Run Invoke-Migration in this process, or leave the dashboard open to inspect stored runs.
    $dashboard | Out-Null
    Read-Host 'Press Enter to stop the dashboard'
}
finally {
    Stop-MigrationDashboard -Dashboard $dashboard
}
```

The dashboard can show stored run history, an in-memory configuration preview, or both:

```powershell
# Preview a configuration without executing it or creating a run database.
$dashboard = Start-MigrationDashboard -Steps $steps -Connections $connections

# Show the supplied configuration alongside persisted run history.
$dashboard = Start-MigrationDashboard -DatabasePath './state/migration.sqlite3' `
    -Steps $steps -Connections $connections
```

`-Steps` and `-Connections` accept the same objects as `Invoke-Migration`. Configuration preview does not execute scripts or connect to endpoints. The preview exists only for the dashboard process; when `Invoke-Migration` runs, a safe configuration snapshot including the scheduler limits is persisted with the run so completed runs can still show their original step properties and configured capacity.

The dashboard opens the database through its single-process owner. It may run in a separate PowerShell process from migration clients; when no owner is already running, the dashboard process becomes the implicit owner. To keep the owner independent of a dashboard or migration client, start [`Start-MigrationDatabaseServer`](./migration-database-server.md) in a dedicated process. Multiple distinct run IDs can share the database through that owner. Keep the owner alive until the dashboard and all migration clients have finished.

The default URL is `http://127.0.0.1:5180`. `-Url` may select another HTTP loopback address/port; non-loopback URLs are rejected. Remote access, HTTPS termination, authentication, and multi-user authorization are not implemented.

> [!WARNING]
> The dashboard has no authentication. Run it only in a trusted local session, and protect the SQLite file with operating-system access controls.

## Dashboard views

- Run selector and compact overview with a target-output rows-per-minute chart, current and run-average rates, elapsed time, target rows, succeeded/error counts, and a progress bar. The chart scale reflects target output only, not source input. When target totals are not finalized, the overview recursively projects them from upstream outputs and observed row ratios. Hover chart points to see their time interval and rate. The ETA uses the projected remaining target rows and observed run-average target output rate when both are available; completed runs show their completion time. If a run is still marked `Running` after its persisted scheduler lease expires, the selector and overview identify it as stalled, show the last persisted update and outstanding work, and explain how to resume it with the same run ID and step definition.
- Interactive React Flow dependency graph with a separate node for every step, labeled states, filtering, zoom/pan, a minimap, and a longest-observed-dependency-chain highlight. Zoom and pan persist across periodic status refreshes; auto-fit is applied when the selected run or graph topology changes, while selecting a step still centers it. Graph nodes and table rows show a mini rows-per-minute chart from their first working period through completion (or the current time while running); hovering a sample shows its interval and rate in rows per minute. Nodes also show connector- or step-specific details. Dataverse targets display configured or effective batch size and DOP, connection activity, observed retries/throttles, and retry-telemetry availability when runtime state has been recorded. Older runs saved with automatic tuning are labeled as legacy tuning and show the recorded runtime state when available. Clicking a failed status badge or failed-batch count opens a dialog with full recorded error text, attempts, and failed-row counts; errors are paged for steps with many failed batches.
- The expanded run overview contains the execution timeline, upstream-blocking hotspots, a scheduler-capacity graph, and CPU/memory charts. Process CPU utilization is computed from process CPU-time samples; memory charts show process working set and managed heap. When available, the runtime-reported process memory budget is plotted and labeled as a host/container budget, not free memory. These measurements include migration and dashboard overhead in the hosting process.
- For Dataverse connection pools, the expanded overview charts the trailing 30-second average of routed request units per second for each connection in each pool. The average warms up as samples accumulate. Bulk writes count once per target record. Samples are collected every three seconds while the dashboard is open and retained only in browser memory; they are not historical run data.
- Configuration preview for supplied steps and connections; historical runs display the sanitized configuration captured at invocation plus connection snapshots captured at run start and completion.
- Optional row snapshot and lineage inspection when that run was created with `Invoke-Migration -EnableDebugCapture` or `Invoke-Migration -WhatIf`. Select a graph step to browse its captured inputs, transform outputs, and downstream rows by stage; search within each stage and select a row to show its captured values at each stage on the graph. Selected Dataverse source or target rows with a captured primary ID can be opened in the environment's default model-driven app. Snapshot and lineage grids use Fluent UI DataGrid with sorting, server-side column filters for all matching snapshots, drag or keyboard column reordering, resizing, content-based widths capped at 360 pixels, and a searchable column-visibility checklist. Payload cells show a copy-value action on hover or keyboard focus.

Totals are based on persisted data. Standard `Records` batches are counted when queued; arbitrary callback payloads without a `Records` array are marked unknown. Source totals can be configured explicitly or resolved before the run using a wrapped SQLite/DuckDB query (or explicit `CountQuery`), Dataverse `-RecordCount` for Simple/FetchXML, a custom-source `CountCallback`, or Parquet file metadata. Other source totals become known as rows flow. Intermediate and target totals are finalized only after their inputs complete; before then, per-step progress recursively projects input rows from upstream projected outputs and observed row ratios, or falls back to the count of currently materialized batches when a branch cannot be projected. The overview recursively projects target-write totals from upstream outputs and observed row ratios, and uses the projected remaining target rows divided by observed run-average target output rate for its ETA. These estimates can change as more rows flow and are not guarantees. With multiple targets, the overview counts target-write work, not unique source rows. The orange path is the longest observed activity chain, not a guaranteed critical path or completion estimate.

Runtime hotspots rank active downstream steps by the number of upstream steps currently waiting behind them and the accumulated blocked-step-seconds over the last 60 seconds. When a chain has multiple active descendants, the furthest active downstream step receives the blocked ancestor, so the upstream wait is not also credited to every intermediate node. This dampens rank changes caused by short-lived throughput fluctuations; it remains a graph-based diagnostic, not a causal profiler.

The scheduler-capacity graph compares persisted outstanding batches, in-flight batches, and the most impactful/busiest step's outstanding batches with the run's configured global queue, worker, and per-step queue limits. Values are sampled every three seconds while the dashboard is open and retained in browser memory for the current page session. The counts include persisted pending and in-flight batches but not producer reservations, so they may temporarily be lower than the scheduler's exact reserved capacity.

The dashboard polls the API every three seconds. It is read-only and does not expose controls to pause, retry, or mutate a migration.

## Debug snapshots and retention

Basic run/step/batch metadata is always retained, including the latest fixed Dataverse target batch size, DOP, and retry/throttle telemetry recorded with its batch outcomes. Debug snapshots are separately opt-in per run and include step inputs and outputs, callback-provided `MigrationDebugStage` records, dependency child rows, and outcomes/errors. Callback nodes can add named intermediate stages by returning them in `MigrationBatchResult.DebugStages`.

For one-to-many or merged transformations within the current batch, `MigrationDebugStage.ParentRecordIndices` may specify, for each output record, the zero-based input record indices that contributed to it. For example, `new IReadOnlyList<int>[] { new[] { 0, 1 } }` says one output row came from input rows 0 and 1.

For a row that combines records from distinct dependency batches, the callback can use `StoredBatch.ParentBatchIds` and `MigrationDebugStage.ParentRecordReferences`. Each output row receives an array of `MigrationDebugParentRecordReference` values identifying a parent batch ID and an input record index. References may target the current batch or a direct parent batch, including required parent batches; a reference must point to a valid input row from that batch. `ParentRecordIndices` and `ParentRecordReferences` are mutually exclusive. When neither mapping is supplied, the engine retains its legacy positional lineage behavior. A compact input-lineage index remains available while a run is active and for the configured retention period after completion; row payload snapshots still expire based on their capture time.

The engine redacts JSON properties whose names include common credential, password, secret, token, authorization, API-key, private-key, connection-string, or SAS markers. This is a defense in depth, not a guarantee that arbitrary sensitive values are recognized. Do not capture data unless its storage is approved. SQLite is not encrypted by this feature.

Configuration snapshots use the same name-based redaction policy. Script blocks are represented by a marker rather than evaluated or shown. Connection instance details are safe property snapshots, not retained live .NET objects; older runs created before this metadata was added remain viewable but cannot reconstruct configuration or connection details.

The default snapshot retention is 30 days; set `-DebugCaptureRetentionDays` from 1 to 3650 days. Expired snapshots are removed when the dashboard starts and when new snapshots are captured. Run graph, batch outcomes, checkpoints, and other basic metadata are not removed by this policy. Resume a run with the same debug-capture and retention settings.

Snapshots carry deterministic lineage IDs, stage names, attempt numbers, batch/step references, and parent IDs for generated dependency children and explicitly mapped merged rows. Selecting a row follows every recorded parent-child link in both directions so the dashboard can show multiple contributing rows across the pipeline. Transform and Filter steps record their outputs with input-row lineage, including expanded records. For callback-provided intermediate stages, handlers should provide `ParentRecordIndices` for reordered, merged, split, or otherwise non-positional output, and `ParentRecordReferences` when a row depends on records from multiple stored parent batches.

## Dataverse example

The [Dataverse example setup](../../examples/dataverse/Setup-Example.ps1) prepares the dataset and connection profile; then run the [Dataverse migration](../../examples/dataverse/example.ps1). It starts the dashboard while the example imports the dataset and streams its sidecar-backed file attachment to Dataverse.

Open the printed `http://127.0.0.1:5180` URL while the sample runs. The dashboard remains available after completion until the example exits.

## HTTP API

| Method and path | Description |
| --- | --- |
| `GET /api/runs` | Recent run summaries and pipeline metadata, including the scheduler lease expiry when one is held. |
| `GET /api/runs/{runId}` | One run summary, per-step status/count/timing, scheduler lease expiry, sanitized configuration, and connection instance snapshots. |
| `GET /api/configuration` | In-memory configuration preview supplied when the dashboard started; returns 404 if none was supplied. |
| `GET /api/process-metrics` | Current process CPU time, logical processor count, working set, managed heap, and optional runtime-reported memory budget. |
| `GET /api/runs/{runId}/dataverse-connection-throughput` | Current pooled Dataverse member counters used by the dashboard to sample per-connection throughput. Returns an empty array when the run has no active connection pools. |
| `GET /api/runs/{runId}/batches/{stepId}?limit=100` | Recent batch states, attempts, outcomes, and errors. Limit is 1–500. |
| `GET /api/runs/{runId}/batch-errors/{stepId}?limit=100&offset=0` | Failed batches for one step, including full recorded error text, ordered newest first. Limit is 1–500; offset is non-negative. |
| `GET /api/runs/{runId}/snapshots?batchId=...&lineageId=...&limit=500` | Captured rows for a run. Returns 404 if debug capture was disabled. |
| `GET /api/runs/{runId}/rows?stepId=...&stageName=...&search=...&sortColumn=record&sortDirection=descending&filters=...&limit=100&offset=0` | Searchable, sorted, filtered pages of input/output snapshots for one pipeline step, with counts and payload-column names for each captured stage. `stageName` is optional and filters to an exact stage. `filters` is a JSON object mapping column IDs (`record`, `attempt`, `captured`, `batch`, `lineage`, `stage`, `payload-json`, or `payload:<property>`) to substring matches. Limit is 1–500; offset is non-negative. |
| `GET /api/runs/{runId}/trace/{lineageId}` | Captured lineage history, including recorded child relationships. |
| `GET /health` | Local host readiness check. |
