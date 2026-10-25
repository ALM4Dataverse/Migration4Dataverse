# Documentation

This documentation describes the supported public cmdlets, step configuration, engine concepts, transformations, and connectors.

## Public API

- [Cmdlets and functions](./cmdlets/README.md) — reference for every exported PowerShell command, including [`Get-MigrationRun`](./cmdlets/get-migration-run.md), [`Clear-MigrationTargetTuning`](./cmdlets/clear-migration-target-tuning.md), and the [migration dashboard](./cmdlets/migration-dashboard.md).
- [Migration step configuration](./configuration.md) — source, transform, filter, append, target, and callback step shapes.
- [Concepts](./concepts/README.md) — dependencies, batching, recovery, replay safety, and runtime connections.

## Transformations

- [Transformation API](./transformations/README.md) — pipeline definition and common behavior.
- [Append / Union All](./transformations/append.md)
- [Merge](./transformations/merge.md)
- [Sort](./transformations/sort.md)
- [Transform](./transformations/transform.md)
- [Filter](./transformations/filter.md)

## Connectors

- [Source connectors](./connectors/sources/README.md) — [Dataverse](./connectors/sources/dataverse.md), [JSON Lines](./connectors/sources/jsonl.md), [CSV](./connectors/sources/csv.md), [Parquet](./connectors/sources/parquet.md), [SQLite](./connectors/sources/sqlite.md), [DuckDB](./connectors/sources/duckdb.md), and [custom PowerShell](./connectors/sources/custom.md).
- [Target connectors](./connectors/targets/README.md) — [Dataverse](./connectors/targets/dataverse.md), [JSON Lines](./connectors/targets/jsonl.md), [CSV](./connectors/targets/csv.md), [Parquet](./connectors/targets/parquet.md), [SQLite](./connectors/targets/sqlite.md), [DuckDB](./connectors/targets/duckdb.md), and [custom PowerShell](./connectors/targets/custom.md).

## Developer documentation

- [Developer documentation index](./dev/README.md) — engine internals and contributor-oriented guides.
- [Engine overview and development](./dev/engine-overview.md) — C# scheduler usage, transformations, build/test instructions, and current limitations.
- [Architecture and internal flows](./dev/architecture.md) — scheduler lifecycle, SQLite persistence/recovery, and internal component boundaries.
- [Feature plan](../PLAN.md) — current implementation status and remaining work.
