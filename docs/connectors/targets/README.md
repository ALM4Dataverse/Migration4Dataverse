# Target connectors

Target steps support these connectors:

| Type | Write behavior | Reference |
|---|---|---|
| `dataverse` | Upserts mapped records through `Set-DataverseRecord` | [Dataverse](./dataverse.md) |
| `jsonl` | Writes an atomic JSON Lines file per batch | [JSON Lines](./jsonl.md) |
| `csv` | Writes a CSV file per batch using configured output columns | [CSV](./csv.md) |
| `parquet` | Writes a Parquet file per batch using an explicit schema | [Parquet](./parquet.md) |
| `sqlite` | Inserts records into a SQLite table, creating or validating its schema | [SQLite](./sqlite.md) |
| `duckdb` | Inserts records into a DuckDB table, creating or validating its schema | [DuckDB](./duckdb.md) |
| `custom` | Calls a PowerShell callback once per batch | [Custom](./custom.md) |

Every target uses `ConnectionId`; file targets also require `Path`. Dataverse requires `Table`. `Operation` is optional and must be `Upsert` for Dataverse or `Write` for the other targets. Target `BatchSize` overrides the invocation default; Dataverse targets default to `auto` tuning. See [target properties](../../configuration.md#target-properties).

Dataverse and custom writes are serialized per target connection, subject to the custom target's configured concurrency. SQLite and DuckDB targets create a missing table by default, or validate a caller-supplied schema against an existing table. Database writes and batch replay markers commit transactionally. Built-in file targets use atomic per-batch files with replay checks.
