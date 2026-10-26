# Recovery and replay

The SQLite store persists run identity, batches, outcomes, checkpoints, leases, and dependency events. Reuse the same `RunId` to resume a run after cancellation or interruption.

Only one active scheduler run may own a SQLite database at a time, including when separate processes attempt to start different run IDs. The database-wide lease expires after its owner stops renewing it; expired batch leases remain independently protected until they expire. Historical and completed runs can share the database. Use separate database files for parallel runs.

SQLite writes from concurrent engine components are serialized per database within the process. WAL allows readers to continue alongside a writer; writes from other processes still use SQLite's normal locking and timeout behavior. Host the dashboard in the run-owning process when monitoring an active run.

## Resume compatibility

A run is bound to its `RunName` and `StepHash`. By default, the cmdlet computes the step hash from normalized step definitions and module names. Changing a script, connection ID, path, or other hashed step data can make the run incompatible. If supplying `StepHash` explicitly, the caller owns this compatibility decision.

Root readers receive the last contiguous completed checkpoint (`LastCompletedSequence` and `CursorJson`) and the latest durable source position (`ResumeSequence` and `ResumeCursorJson`). They continue after that latest enqueued batch; pending and in-flight batches are replayed from their stored SQLite payloads rather than reconstructed. Custom typed sources may restart their page cursor at the beginning on enumeration and replay records through the last durable source key, so their records must be stable, replayable, and ordered by the source key.

## At-least-once external effects

A worker can complete an external write and fail before its SQLite completion transaction commits. Expired leases and retries may therefore execute the same batch again. The scheduler provides durable recovery, not exactly-once external side effects.

- Use upsert or another idempotent write operation where possible.
- Custom targets must deduplicate or safely overwrite by stable batch identity.
- Keep source data and step definitions stable while resuming.
- A run with terminal row errors cannot be resumed as though it had completed successfully; inspect and resolve the failures through the store workflow.

## Cancellation and leases

Cancellation returns running work to a recoverable state when possible. A later run can reclaim expired batch leases after the current run lease is no longer active. If a lease is reclaimed while its former handler is still running, that stale worker cannot complete or abandon the batch; only the current lease owner can change its durable state. Losing a lease does not by itself fail the run. Leases protect scheduler ownership and provide recovery; they cannot atomically coordinate a remote target write with the SQLite transaction.

See the [architecture guide](../dev/architecture.md) for transaction boundaries and persistence details.
