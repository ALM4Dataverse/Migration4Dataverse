# Batching and backpressure

Root source readers produce durable work in batches. The scheduler persists a batch payload before dispatching it to a worker. A successful parent batch can release dependent child work as soon as the dependency event is committed.

## Limits

- `BatchSize` controls the default typed input batch size; a typed target can override it.
- `MaxDegreeOfParallelism` controls the handler worker count (supported range `1..32`).
- `MaxQueuedBatches` (default `64`) bounds pending plus in-flight batches and applies backpressure to source reading.
- `MaxQueuedBatchesPerStep` (default `32`) bounds a step's outstanding batches and producer reservations, preserving capacity for sibling paths. Persisted dependency events are materialized incrementally as child capacity becomes available.
- `MaxAttempts` bounds retries for handler failures.
- Typed Dataverse writes using the same target connection are serialized, independently of scheduler worker count.
- File target batches are written as separate files. They are not concatenated into one table file.

The scheduler prioritizes ready dependency batches over queued root batches. This lets consumer steps process batches while source readers are still producing data. Callback multi-parent prerequisites wait until each required batch has completed successfully.

These settings bound scheduler work; they do not impose a hard process-wide memory limit on PowerShell, serializers, connectors, or external libraries.
