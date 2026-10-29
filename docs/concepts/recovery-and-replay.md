# Recovery and replay

The SQLite store persists run identity, batches, outcomes, checkpoints, leases, and dependency events. Reuse the same `RunId` to resume a run after cancellation or interruption.

Each database has one local owner process for Migration4Dataverse store access. Multiple distinct run IDs can execute concurrently through that owner; a per-run lease prevents two schedulers from owning the same run ID. If separate PowerShell processes share a database, start [`Start-MigrationDatabaseServer`](../cmdlets/migration-database-server.md) in a dedicated process first.

SQLite writes are serialized through the owner, while readers can continue alongside a writer using WAL. A run lease and its batch leases are independent: a scheduler must renew its run lease, and expired batch work can be reclaimed only after its prior batch lease is no longer valid.

## Resume compatibility

A run is bound to its `RunName` and `StepHash`. By default, the cmdlet computes the step hash from normalized step definitions, module requirements and resolved versions, helper script contents, and shared scripts. Changing a script, connection ID, path, or other hashed step data can make the run incompatible. If supplying `StepHash` explicitly, the caller owns this compatibility decision.

Root readers receive the last contiguous completed checkpoint (`LastCompletedSequence` and `CursorJson`) and the latest durable source position (`ResumeSequence` and `ResumeCursorJson`). They continue after that latest enqueued batch; pending and in-flight batches are replayed from their stored SQLite payloads rather than reconstructed. Custom typed sources may restart their page cursor at the beginning on enumeration and replay records through the last durable source key, so their records must be stable, replayable, and ordered by the source key.

Partitioned Dataverse root sources store the last emitted key for each stable partition ID alongside the overall source key. On resume, each partition query is rerun and skips through its own saved key before the readers merge again. Keep partition IDs and query behavior unchanged when resuming; changing the partition set is not compatible with the saved cursor.

## At-least-once external effects

A worker can complete an external write and fail before its SQLite completion transaction commits. Expired leases and retries may therefore execute the same batch again. The scheduler provides durable recovery, not exactly-once external side effects.

> [!WARNING]
> Design external writes to be idempotent or provide a reconciliation strategy. SQLite cannot atomically commit a transaction with a remote Dataverse write or custom callback side effect.

- Use upsert or another idempotent write operation where possible.
- Custom targets must deduplicate or safely overwrite by stable batch identity.
- Keep source data and step definitions stable while resuming.
- A run with terminal row errors cannot be resumed as though it had completed successfully; inspect and resolve the failures through the store workflow.

## Cancellation and leases

Cancellation returns running work to a recoverable state when possible. A later run can reclaim expired batch leases after the current run lease is no longer active. If a lease is reclaimed while its former handler is still running, that stale worker cannot complete or abandon the batch; only the current lease owner can change its durable state. Losing a lease does not by itself fail the run. Leases protect scheduler ownership and provide recovery; they cannot atomically coordinate a remote target write with the SQLite transaction.

See the [architecture guide](../dev/architecture.md) for transaction boundaries and persistence details.
