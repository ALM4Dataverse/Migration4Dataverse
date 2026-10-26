# Developer documentation

This section is for contributors working on the repository implementation.

> [!IMPORTANT]
> The C# engine and its scheduler and transformation types are internal implementation details. They are not a supported public C# API, and their contracts may change.

- [Engine overview and development](./engine-overview.md) — internal scheduler behavior, build/test instructions, and project limits.
- [Architecture and internal flows](./architecture.md) — component boundaries, SQLite ownership, scheduling, persistence, and recovery.
- [Internal data-flow transformations](./transformations/README.md) — the separate engine pipeline and its operators.
- [PowerShell reference](../reference.md) — supported cmdlets, step configuration, concepts, and connectors.
- [Feature plan](../../PLAN.md) — implementation status and remaining work.

When engine behavior, persistence, retries, recovery, step definitions, connectors, connector options, or transformations change, update the related developer guide and PowerShell reference. Repository maintenance guidance is in [AGENTS.md](../../AGENTS.md).
