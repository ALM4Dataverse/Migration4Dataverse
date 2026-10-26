# Source connectors

Choose a connector based on where the rows come from:

| Type | Reads from | Guide |
|---|---|---|
| `csv` | A delimited text file | [CSV](./csv.md) |
| `jsonl` | A UTF-8 JSON Lines file | [JSON Lines](./jsonl.md) |
| `parquet` | A Parquet file | [Parquet](./parquet.md) |
| `sqlite` | A SQL query over a SQLite connection | [SQLite](./sqlite.md) |
| `duckdb` | A SQL query over a DuckDB connection | [DuckDB](./duckdb.md) |
| `dataverse` | A Simple, FetchXML, or SQL4CDS query | [Dataverse](./dataverse.md) |
| `custom` | A paged PowerShell callback | [Custom](./custom.md) |

Every source requires `ConnectionId` and a stable `Key`. File sources use a `DirectoryInfo` plus `Path` or a `FileInfo` without `Path`. `Id` is optional; use it when a source field should be carried as the row's target-record ID. `ExpectedRecordCount` is also optional. See [common source properties](../../configuration.md#source-properties).

File and SQLite/DuckDB database connectors use a `System.IO.DirectoryInfo` root with a step `Path` or a `System.IO.FileInfo` naming the exact file. The engine creates database connections automatically; source rows must be JSON-compatible objects and database sources require a query in `Options.Query`.

For Dataverse cmdlet options beyond the migration adapter's requirements, see the upstream [`Get-DataverseRecord` reference](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Get-DataverseRecord.md), [`Invoke-DataverseSql` reference](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Invoke-DataverseSql.md), and [querying concept](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/core-concepts/querying.md).
