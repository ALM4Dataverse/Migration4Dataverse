# Inspecting migration runs and recovering from errors

The PowerShell commands expose the run, step, batch, error, row, and recovery data available in the dashboard. Use the same `DatabasePath` and `RunId` that were supplied to `Invoke-M4dMigration`.

## Run and step state

`Get-M4dMigrationRun` lists recent runs or returns one run. Its `Steps` collection contains the state and progress shown in the dashboard graph, including pending and in-flight work, row counts, throughput samples, continuation status, and step-specific details. `Get-M4dMigrationStep` selects one step from a run.

```powershell
$runs = Get-M4dMigrationRun -DatabasePath $databasePath -Limit 100
$run = Get-M4dMigrationRun -DatabasePath $databasePath -RunId $runId
$run.Steps | Format-Table Id, Type, IsComplete, PendingBatches, InFlightBatches,
    SucceededRows, FailedRows, UnacceptedErrorBatches
Get-M4dMigrationStep -DatabasePath $databasePath -RunId $runId -StepId 'Normalize'
```

`Get-M4dMigrationBatch` lists recent batches for one step. `Get-M4dMigrationError` lists run-level failures (such as source-reader errors) and failed batches; batch error records include outcomes and any exactly attributed input-row errors and payloads. `Limit` and `Offset` page the batch errors for each selected step.

```powershell
Get-M4dMigrationBatch -DatabasePath $databasePath -RunId $runId -StepId 'WriteAccounts'
Get-M4dMigrationError -DatabasePath $databasePath -RunId $runId
Get-M4dMigrationError -DatabasePath $databasePath -RunId $runId -StepId 'Normalize'
Get-M4dMigrationError -DatabasePath $databasePath -RunId $runId -StepId 'Normalize' `
    -Limit 100 -Offset 100
```

`Get-M4dMigrationRetryHistory` lists prior batch attempts preserved when failed batches were queued for replay.

## Captured rows and configuration

Row inspection requires debug capture to have been enabled for the run. `Get-M4dMigrationDebugRow -StepId` returns a searchable, sortable page with optional stage and column filters. `Get-M4dMigrationDebugRow -Trace -LineageId` follows captured lineage across steps; `-BatchId` retrieves snapshots for a batch. Captured snapshots expire according to the run's retention setting.

```powershell
$page = Get-M4dMigrationDebugRow -DatabasePath $databasePath -RunId $runId `
    -StepId 'WriteAccounts' -StageName 'TargetWriteInput' -Search 'ACME' `
    -Limit 100 -Offset 0 -ColumnFilters @{ Name = 'ACME' }
$page.Items
Get-M4dMigrationDebugRow -DatabasePath $databasePath -RunId $runId `
    -Trace -LineageId $page.Items[0].LineageId
```

`Get-M4dMigrationDebugRowBinary` resolves a captured binary property to a `MigrationBinaryValue`, which can be read with its `OpenRead()` stream. `Get-M4dMigrationConfigurationPreview` returns the same sanitized step and connection preview shown when `Start-M4dMigrationDashboard` is given `-Steps` and `-Connections`. `Get-M4dMigrationProcessMetric` samples the current PowerShell process. `Get-M4dMigrationDataverseConnectionThroughput` samples live Dataverse pool counters for a run; these counters are available only in the process that owns the active pools and are not retained as run history.

The dashboard visually arranges these objects as a graph and charts; the cmdlets return the underlying state and captured data for filtering, scripting, and export.

## Queueing recovery actions

`Invoke-M4dMigrationRecovery` persists one operator decision. It does not execute a migration. It supports `-WhatIf` and `-Confirm`; after queueing an action, rerun `Invoke-M4dMigration` with the same run ID and compatible step definitions.

Queue all failed batches for replay:

```powershell
Invoke-M4dMigrationRecovery -DatabasePath $databasePath -RunId $runId `
    -RetryFailedBatches -WhatIf
Invoke-M4dMigrationRecovery -DatabasePath $databasePath -RunId $runId `
    -RetryFailedBatches
Invoke-M4dMigration -DatabasePath $databasePath -RunName $runName -RunId $runId `
    -Steps $steps -Connections $connections
```

Edit and queue one exactly attributed row. This is available for failed Transform, Filter, and callback rows when their handlers reported an exact input index. The edited JSON must be an object; successful sibling rows and upstream work are not replayed.

```powershell
$error = Get-M4dMigrationError -DatabasePath $databasePath -RunId $runId `
    -StepId 'Normalize' | Where-Object Scope -eq 'Batch' | Select-Object -First 1
$rowError = $error.RowErrors | Where-Object CanRetry | Select-Object -First 1
$payload = $rowError.PayloadJson | ConvertFrom-Json -AsHashtable
$payload.Name = 'Corrected name'
$payloadJson = ConvertTo-Json -InputObject $payload -Depth 100 -Compress
Invoke-M4dMigrationRecovery -DatabasePath $databasePath -RunId $runId `
    -StepId $error.StepId -BatchId $error.BatchId `
    -RecordIndex $rowError.RecordIndex -PayloadJson $payloadJson
Invoke-M4dMigration -DatabasePath $databasePath -RunName $runName -RunId $runId `
    -Steps $steps -Connections $connections
```

Approve continuation past a failed step when the remaining unread or failed work should be skipped:

```powershell
Invoke-M4dMigrationRecovery -DatabasePath $databasePath -RunId $runId `
    -StepId 'Source' -ContinueAfterFailure
Invoke-M4dMigration -DatabasePath $databasePath -RunName $runName -RunId $runId `
    -Steps $steps -Connections $connections
```

Source-reader failures are shown as run-level errors. Batch-level failures, including unattributed target errors, can be replayed or continued past, but cannot use row-only retry. A row retry, replay, or continuation may repeat external side effects; reconcile them and use idempotent handlers where possible. See [error handling and recovery](../concepts/recovery-and-replay.md) for the full execution and downstream semantics.

## Error behavior by step type

| Step type | How failures are reported | Recovery options |
|---|---|---|
| Source | Reader exceptions appear as run-level errors; source batches that fail in a handler appear as batch errors. | Retry failed batches when a batch exists; otherwise continue past the source step, which skips unread rows. |
| Transform | Per-record script exceptions include the failed input row and can coexist with successful sibling rows. | Edit and retry an exactly attributed row, replay failed batches, or continue past the step. |
| Filter | Per-record predicate exceptions are row-attributed; a normal false result is a filtered row, not an error. | Edit and retry an exactly attributed row, replay failed batches, or continue past the step. |
| Append | The engine concatenates input streams and has no user callback of its own. Errors from an upstream step remain attached to that upstream step; an append batch-processing failure is batch-level. | Replay failed batches or continue past the append step. |
| Join | The engine stages/indexes inputs and evaluates configured keys; unmatched keys are not errors. Source-reader or batch-processing failures are reported against the failing step. | Retry failed batches where available or continue past the failed step. |
| Target | Connector write failures are batch-level unless a handler explicitly reports safe per-row attribution. | Replay failed batches or continue past the target step; row-only retry is unavailable for unattributed bulk failures. |
| Callback | A callback can report exact input indices in `MigrationBatchResult.RowErrors`, or fail as a whole batch/source reader. | Edit and retry attributed rows; replay batch failures; continue past source-reader or step failures. |

Run the offline [error-recovery example](../../examples/error-recovery/example.ps1) with its [setup script](../../examples/error-recovery/Setup-Example.ps1) to inspect source-reader errors, edit and retry Transform/Filter/callback rows, and replay a failed Target batch in the dashboard and cmdlets. Append and Join are built-in structural operators without user callbacks; their normal input data does not generate an operator error. If a batch-processing error occurs there, the same batch replay or step-continuation options apply, while errors from upstream steps remain attributed to those steps.
