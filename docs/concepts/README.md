# Migration concepts

Start with [Migration basics](./migration-basics.md) for a plain-language explanation of sources, transforms, filters, targets, and connections. Use the [step configuration reference](../configuration.md) for exact properties, then use these guides for specific operational questions:

- [Dependencies](./dependencies.md) — when rows flow, when steps wait, and how keyed source relationships and joins behave.
- [Batching and backpressure](./batching.md) — batch sizes, queue limits, and concurrency.
- [Binary values and sidecar files](./binary-values-and-sidecars.md) — common connector value types, file-backed binary transformations, and CSV/JSONL/Parquet attachment round-tripping.
- [Recovery and replay](./recovery-and-replay.md) — checkpoints, resume compatibility, leases, and idempotent writes.
- [Connections and callbacks](./connections-and-callbacks.md) — runtime connection ownership and embedded PowerShell callback rules.

For Dataverse-specific command options, these guides link to the upstream `Rnwood.Dataverse.Data.PowerShell` documentation.
