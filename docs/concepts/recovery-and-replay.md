# Recovery and replay

The SQLite store persists run identity, batches, outcomes, checkpoints, leases, and dependency events. Reuse the same `RunId` to resume a run after cancellation or interruption.

## Resume compatibility

A run is bound to its `RunName` and `StepHash`. By default, the cmdlet computes the step hash from normalized step definitions and module names. Changing a script, connection ID, path, or other hashed step data can make the run incompatible. If supplying `StepHash` explicitly, the caller owns this compatibility decision.

Root sources receive their last contiguous completed sequence and cursor checkpoint. They must resume deterministically and reproduce any already-persisted batch identities with the same payload and cursor. Custom typed sources restart their page cursor at the beginning on enumeration and are replayed through the last completed root key, so their records must be stable, replayable, and ordered by the source key.

## At-least-once external effects

A worker can complete an external write and fail before its SQLite completion transaction commits. Expired leases and retries may therefore execute the same batch again. The scheduler provides durable recovery, not exactly-once external side effects.

- Use upsert or another idempotent write operation where possible.
- Custom targets must deduplicate or safely overwrite by stable batch identity.
- Keep source data and step definitions stable while resuming.
- A run with terminal row errors cannot be resumed as though it had completed successfully; inspect and resolve the failures through the store workflow.

## Cancellation and leases

Cancellation returns running work to a recoverable state when possible. A later run can reclaim expired batch leases after the current run lease is no longer active. Leases protect scheduler ownership and provide recovery; they cannot atomically coordinate a remote target write with the SQLite transaction.

See the [architecture guide](../dev/architecture.md) for transaction boundaries and persistence details.
