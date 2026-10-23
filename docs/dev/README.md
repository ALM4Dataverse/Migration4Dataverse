# Developer documentation

This section explains the implementation boundaries and durable execution model for contributors.

- [Engine overview and development](./engine-overview.md) — C# scheduler usage, transformations, build/test instructions, and current limitations.
- [Architecture and internal flows](./architecture.md) — component boundaries, scheduler lifecycle, SQLite state/recovery, and Mermaid diagrams.
- [Public reference](../reference.md) — index for cmdlets, configuration, concepts, connectors, and transformations.
- [Feature plan](../../PLAN.md) — implementation status and remaining work.

When changing engine behavior, persistence, retry or recovery semantics, step definitions, connectors, connector options, or transformations, update the relevant architecture and public reference documentation in the same change. Repository-level maintenance guidance is in [AGENTS.md](../../AGENTS.md).
