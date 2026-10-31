# Recovery and replay

The SQLite store persists run identity, run-level failure details, batches, outcomes, checkpoints, leases, dependency events, failed-batch retry history, and attributable row errors. Reuse the same `RunId` to resume compatible work after cancellation or interruption.

Each database has one local owner process for Migration4Dataverse store access. Multiple distinct run IDs can execute concurrently through that owner; a per-run lease prevents two schedulers from owning the same run ID. If separate PowerShell processes share a database, start [`Start-MigrationDatabaseServer`](../cmdlets/migration-database-server.md) in a dedicated process first.

## Row-error thresholds and step continuation

`Invoke-Migration -MaxErrorRatePercent` sets a cumulative per-step failed-row percentage threshold (default `0`, range `0..100`). A step's `ErrorRateThresholdPercent` overrides the global value. The rate is failed rows divided by succeeded plus failed rows across completed batches for that step. A batch that is accepted remains `CompletedWithErrors`; downstream only receives dependency outputs emitted by its handler, and the final run status is `CompletedWithErrors`. Source-reader, exhausted handler, and infrastructure exceptions are not counted in the row-error rate.

## Editing and retrying an attributable row

The failed-task dialog shows editable input-row JSON only when the step reports an exact input record for an error. Per-record PowerShell `transform` and `filter` exceptions are captured with their zero-based input index. A `callback` can report the same mapping through `MigrationBatchResult.RowErrors`; see [callback nodes](../configuration.md#callback-nodes). The row payload must be a JSON object.

> [!WARNING]
> The dashboard displays persisted migration payloads, which may contain sensitive data. Limit access to the local dashboard and treat row details and exported error data according to the source data's handling requirements.

Queueing a row retry creates a one-record batch at the same failed step with the edited payload. It does not rerun upstream steps or successful sibling rows. Dependency outputs already emitted for successful siblings remain held until the attributable errors are repaired or explicitly accepted; after a successful row retry, those outputs and the retried row's outputs can proceed downstream. The retry preserves the row's stored lineage references and does not advance a source-reader checkpoint.

Row-only retry is unavailable when an error cannot be mapped safely to one input row. This includes source-reader failures, whole-batch handler or infrastructure failures, and target connector failures without per-record attribution (including bulk target errors). Use failed-batch replay or step continuation for those errors. Replaying a row can repeat side effects performed by that step, so reconcile external effects and use idempotent handlers where possible.

Queueing the edited row does not execute it. The action requires a Failed or Cancelled run with no active scheduler lease; rerun `Invoke-Migration` with the same run ID and compatible step definition to execute the retry.

For an unresolved fatal error, an operator may use the dashboard to approve continuation past one selected step. This does not retry the step: the scheduler drains persisted pending work, skips further enumeration for an approved source-reader failure, and releases only already-emitted dependency outputs. Unread source rows and failed records are not reconstructed. The decision is stored per run and step, requires a Failed or Cancelled run with no active scheduler lease, and takes effect only after rerunning `Invoke-Migration` with the same run ID and compatible step definition. The dashboard preserves the original run failure details when a continued run finishes with errors.

SQLite writes are serialized through the owner, while readers can continue alongside a writer using WAL. A run lease and its batch leases are independent: a scheduler must renew its run lease, and expired batch work can be reclaimed only after its prior batch lease is no longer valid.

## Resume compatibility

A run is bound to its `RunName` and `StepHash`. By default, the cmdlet computes the step hash from normalized step definitions, module requirements and resolved versions, helper script contents, and shared scripts. Changing a script, connection ID, path, or other hashed step data can make the run incompatible. If supplying `StepHash` explicitly, the caller owns this compatibility decision.

Root readers receive the last contiguous completed checkpoint (`LastCompletedSequence` and `CursorJson`) and the latest durable source position (`ResumeSequence` and `ResumeCursorJson`). They continue after that latest enqueued batch; pending and in-flight batches are replayed from their stored SQLite payloads rather than reconstructed. Custom typed sources may restart their page cursor at the beginning on enumeration and replay records through the last durable source key, so their records must be stable, replayable, and ordered by the source key.

Source readers are retried up to `-SourceReadMaxAttempts` (default `3`), with an initial `-SourceReadRetryDelaySeconds` delay (default `2`, exponential backoff capped at one minute). A retry begins at the latest durable source position; any batches already enqueued remain authoritative. Cancellation is not retried. Source data and cursor behavior must remain stable across attempts.

Partitioned Dataverse root sources store the last emitted key for each stable partition ID alongside the overall source key. On resume, each partition query is rerun and skips through its own saved key before the readers merge again. Keep partition IDs and query behavior unchanged when resuming; changing the partition set is not compatible with the saved cursor.

## At-least-once external effects

A worker can complete an external write and fail before its SQLite completion transaction commits. Expired leases and retries may therefore execute the same batch again. The scheduler provides durable recovery, not exactly-once external side effects.

> [!WARNING]
> Design external writes to be idempotent or provide a reconciliation strategy. SQLite cannot atomically commit a transaction with a remote Dataverse write or custom callback side effect.

- Use upsert or another idempotent write operation where possible.
- Custom targets must deduplicate or safely overwrite by stable batch identity.
- Keep source data and step definitions stable while resuming.
- A run with unaccepted terminal failed batches cannot be resumed as though those batches succeeded. The dashboard can queue all recorded failed batches for replay, queue an edited exact row for retry, or approve continuing past one failed step on a failed or cancelled run with no active scheduler lease. Batch replay preserves prior attempts, errors, and outcomes in retry history, resets current attempts, and leaves batches pending. Continuation skips the selected step's remaining failed/unread work and uses only persisted or explicitly emitted outputs.
- None of these actions executes work. After correcting a row, fixing the cause, or accepting the partial result and reconciling external effects, rerun `Invoke-Migration` with the same run ID and compatible step definition. A changed generated step hash requires a new run ID. The dashboard does not have the live connections needed to start a scheduler.

Run-level failures record their phase and step when known, exception type, message, details, and attempts. Errors that occur before the run is created (for example, graph validation or source-count resolution) remain invocation errors and cannot appear in run history. The dashboard shows persisted run failures, failed-batch details, and can export batch outcomes as JSON; exports may contain business data.

## Cancellation and leases

Cancellation returns running work to a recoverable state when possible. A later run can reclaim expired batch leases after the current run lease is no longer active. If a lease is reclaimed while its former handler is still running, that stale worker cannot complete or abandon the batch; only the current lease owner can change its durable state. Losing a lease does not by itself fail the run. Leases protect scheduler ownership and provide recovery; they cannot atomically coordinate a remote target write with the SQLite transaction.

See the [architecture guide](../dev/architecture.md) for transaction boundaries and persistence details.
