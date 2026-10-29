# Batching and backpressure

Root source readers produce durable work in batches. The scheduler persists a batch payload before dispatching it to a worker. A successful parent batch can release dependent child work as soon as the dependency event is committed.

## Limits

- `Invoke-Migration -BatchSize` controls the default batch size for steps that do not specify one (10 by default). Non-Dataverse targets inherit it unless they set `Target.BatchSize`.
- `MaxDegreeOfParallelism` controls the handler worker count (64 by default) and must be positive; it has no fixed engine ceiling. Higher values use more workers and can increase load on connected services.
- `MaxQueuedBatches` (default `64`) bounds pending plus in-flight batches and applies backpressure to source reading.
- `MaxQueuedBatchesPerStep` (default `32`) bounds a step's outstanding batches and producer reservations, preserving capacity for sibling paths. Persisted dependency events are materialized incrementally as child capacity becomes available.
- `MaxAttempts` bounds retries for handler failures.
- Dataverse targets default to fixed batches of 10, independent of `Invoke-Migration -BatchSize`; an explicit `Target.BatchSize` overrides that default. They run within connection budgets. A multi-identity connection pool caps each identity independently using its connection recommendation, then balances requests across identities with capacity. Target `MaxConcurrency` can impose a smaller per-target cap; `MaxDegreeOfParallelism` remains the global ceiling.
- Custom target callbacks use their configured `MaxConcurrency`; callbacks sharing a target connection must use the same value.
- Built-in file targets stage batches and publish one complete output file per target during the scheduler's completion phase. Replacement is atomic per destination; multiple target files are not committed as a cross-file transaction.

The scheduler prioritizes ready dependency batches over queued root batches. This lets consumer steps process batches while source readers are still producing data. Callback multi-parent prerequisites wait until each required batch has completed successfully.

> [!NOTE]
> Queue limits bound scheduler work, not total process memory. PowerShell, serializers, connectors, and external libraries can allocate additional memory.
