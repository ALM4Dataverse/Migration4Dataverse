# Target connectors

Choose a target based on where the migrated rows should go:

| Type | Writes to | Guide |
|---|---|---|
| `dataverse` | Dataverse tables through `Rnwood.Dataverse.Data.PowerShell` | [Dataverse](./dataverse.md) |
| `csv` | One complete CSV file per target step | [CSV](./csv.md) |
| `jsonl` | One complete JSON Lines file per target step | [JSON Lines](./jsonl.md) |
| `parquet` | One complete Parquet file per target step | [Parquet](./parquet.md) |
| `sqlite` | A SQLite table | [SQLite](./sqlite.md) |
| `duckdb` | A DuckDB table | [DuckDB](./duckdb.md) |
| `custom` | A PowerShell callback | [Custom](./custom.md) |

Every target requires `ConnectionId`, except an SQLite staging target used by a whole-dataset transform, which uses its managed transform database. File targets use a `DirectoryInfo` plus `Path` or a `FileInfo` without `Path`; database and Dataverse targets require `Table`. `Operation` defaults to `Upsert` for Dataverse and `Write` for the other connectors. A target-level `BatchSize` overrides the invocation default. See [common target properties](../../configuration.md#target-properties).

Dataverse targets on one connection share an adaptive concurrency budget; custom callbacks use their configured `MaxConcurrency`. SQLite and DuckDB use a `FileInfo` or `DirectoryInfo` plus `Path`; the engine creates their database connections automatically. They can create a missing table or validate a supplied schema. Database writes commit with a batch replay marker. File target batches are staged with replay protection and atomically published as one complete output file only after all work succeeds.

For complete Dataverse write options, see upstream [`Set-DataverseRecord`](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Set-DataverseRecord.md), [`Remove-DataverseRecord`](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Remove-DataverseRecord.md), and [creating and updating](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/core-concepts/creating-updating.md).
