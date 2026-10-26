# Target connectors

Choose a target based on where the migrated rows should go:

| Type | Writes to | Guide |
|---|---|---|
| `dataverse` | Dataverse tables through `Rnwood.Dataverse.Data.PowerShell` | [Dataverse](./dataverse.md) |
| `csv` | A CSV file for each batch | [CSV](./csv.md) |
| `jsonl` | An atomic JSON Lines file for each batch | [JSON Lines](./jsonl.md) |
| `parquet` | A Parquet file for each batch | [Parquet](./parquet.md) |
| `sqlite` | A SQLite table | [SQLite](./sqlite.md) |
| `duckdb` | A DuckDB table | [DuckDB](./duckdb.md) |
| `custom` | A PowerShell callback | [Custom](./custom.md) |

Every target requires `ConnectionId`. File targets also require `Path`; database and Dataverse targets require `Table`. `Operation` defaults to `Upsert` for Dataverse and `Write` for the other connectors. A target-level `BatchSize` overrides the invocation default. See [common target properties](../../configuration.md#target-properties).

Dataverse targets on one connection share an adaptive concurrency budget; custom callbacks use their configured `MaxConcurrency`. SQLite and DuckDB can create a missing table or validate a supplied schema. Database writes commit with a batch replay marker, while file writers use atomic per-batch output.

For complete Dataverse write options, see upstream [`Set-DataverseRecord`](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Set-DataverseRecord.md), [`Remove-DataverseRecord`](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Remove-DataverseRecord.md), and [creating and updating](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/core-concepts/creating-updating.md).
