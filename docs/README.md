# Documentation

Choose the section that matches what you are doing. The PowerShell guides describe the supported migration workflow; the developer guides describe repository internals.

## PowerShell users

Start with the [repository README](../README.md) to build the module and run an example.

### Define a migration

- [Migration basics](./concepts/migration-basics.md) — what sources, transforms, filters, targets, and connections mean.
- [Step configuration](./configuration.md) — supported step types, common properties, connections, and callback shapes.
- [Dependencies](./concepts/dependencies.md) — row flow, keyed source relationships, joins, and completion barriers.
- [Recovery and replay](./concepts/recovery-and-replay.md) — run IDs, checkpoints, retries, and side-effect safety.
- [Connections and callbacks](./concepts/connections-and-callbacks.md) — connection ownership and PowerShell callback rules.

### Choose connectors

- [Source connectors](./connectors/sources/README.md) — CSV, JSON Lines, Parquet, SQLite, DuckDB, Dataverse, and custom readers.
- [Target connectors](./connectors/targets/README.md) — file, database, Dataverse, and custom writers.
- [Dataverse source](./connectors/sources/dataverse.md) and [Dataverse target](./connectors/targets/dataverse.md) — Migration4Dataverse-specific behavior and links to the upstream command references.

### Run and monitor

- [Cmdlets and functions](./cmdlets/README.md) — command index.
- [Invoke-Migration](./cmdlets/invoke-migration.md) — run, resume, preview, and debug capture.
- [Migration dashboard](./cmdlets/migration-dashboard.md) — local monitoring and row inspection.
- [Batching and backpressure](./concepts/batching.md) — queue limits and concurrency behavior.
- [PowerShell reference](./reference.md) — supported migration contract, organized by topic.

> [!NOTE]
> Dataverse query, write, authentication, and connection options belong to `Rnwood.Dataverse.Data.PowerShell`. The connector guides link to its cmdlet references and concepts instead of duplicating their full option lists.

## Developers and contributors

The C# engine is an internal implementation, not a supported public C# API. These guides are for people changing or testing this repository:

- [Developer documentation](./dev/README.md) — entry point for implementation material.
- [Engine overview](./dev/engine-overview.md) — internal scheduler, development, and test workflow.
- [Architecture](./dev/architecture.md) — persistence, scheduling, recovery, and component boundaries.
- [Internal data-flow transformations](./dev/transformations/README.md) — the separate engine transformation pipeline, not `Invoke-Migration` step types.
- [Feature plan](../PLAN.md) — implementation status and remaining work.
