# Target connectors

Choose a target based on where the migrated rows should go:

| Type | Writes to | Guide |
|---|---|---|
| `dataverse` | Dataverse tables through `Rnwood.Dataverse.Data.PowerShell` | [Dataverse](./dataverse.md) |
| `file` | One complete file per target step; select CSV, JSON Lines, Parquet, binary, or a custom format with `Format` | [File formats](../file-formats.md), [JSON Lines](./jsonl.md), [CSV](./csv.md), [Parquet](./parquet.md), [binary files](../binary-files.md) |
| `sqlite` | A SQLite table | [SQLite](./sqlite.md) |
| `duckdb` | A DuckDB table | [DuckDB](./duckdb.md) |
| `sqlserver` | A SQL Server table | [SQL databases](../sql-databases.md#sql-server) |
| `postgresql` | A PostgreSQL table | [SQL databases](../sql-databases.md#postgresql) |
| `mysql` | A MySQL table | [SQL databases](../sql-databases.md#mysql) |
| `oracle` | An Oracle table | [SQL databases](../sql-databases.md#oracle) |
| `azureblob` | An Azure Blob object | [Object storage](../object-storage.md#azure-blob) |
| `s3` | An Amazon S3 object | [Object storage](../object-storage.md#amazon-s3) |
| `adlsgen2` | An Azure Data Lake Storage Gen2 file | [Object storage](../object-storage.md#azure-data-lake-storage-gen2) |
| `gcs` | A Google Cloud Storage object | [Object storage](../object-storage.md#google-cloud-storage) |
| `sharepoint` | SharePoint documents through Microsoft Graph | [SharePoint documents](./sharepoint.md) |
| `mongodb` | A MongoDB collection | [MongoDB](../mongodb.md) |
| `custom` | A PowerShell callback | [Custom](./custom.md) |

Every target requires `ConnectionId`, except SQLite or DuckDB staging targets used by a whole-dataset transform, which use their managed transform database. File targets use `Type = 'file'`, a required `Format` descriptor, and a `DirectoryInfo` plus `Path` or a `FileInfo` without `Path`. Object-storage targets independently configure their storage provider `Type` and `Format`; structured outputs use `Path` as the final object key, while binary outputs use it as an output prefix. SQL database, MongoDB, and Dataverse targets require `Table`; SharePoint targets use Graph options rather than `Path`. `Operation` defaults to `Upsert` for Dataverse and `Write` for the other connectors. Non-Dataverse targets inherit the invocation batch-size default; Dataverse targets default independently to 10. All targets can override their batch size with `Target.BatchSize`. See [common target properties](../../configuration.md#target-properties).

Dataverse connection pools enforce a separate concurrency budget per identity, based on each connection's recommendation, and route calls among identities with capacity. A target can set `MaxConcurrency` as an additional fixed cap. Custom callbacks use their configured `MaxConcurrency`. SQLite and DuckDB use a `FileInfo` naming the database file or a `DirectoryInfo` plus `Path`; the engine creates their database connections automatically. SQL Server, PostgreSQL, MySQL, and Oracle use caller-supplied ADO.NET connections. Object-storage targets stage locally and upload one complete object; MongoDB targets write documents and a batch replay marker. Database batch writes commit with a batch replay marker once target objects exist; MongoDB records its marker after the documents are written, so keyed upserts are recommended for replay safety. File target batches are staged with replay protection and atomically published as one complete output file only after all work succeeds.

Each target guide includes a table of logical values and accepted PowerShell/.NET representations. Structured file targets can keep top-level binary columns out of the data file by writing sidecars; see [binary values and sidecar files](../../concepts/binary-values-and-sidecars.md).

For complete Dataverse write options, see upstream [`Set-DataverseRecord`](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Set-DataverseRecord.md), [`Remove-DataverseRecord`](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Remove-DataverseRecord.md), and [creating and updating](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/core-concepts/creating-updating.md).
