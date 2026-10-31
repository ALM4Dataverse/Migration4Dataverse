# Source connectors

Choose a connector based on where the rows come from:

| Type | Reads from | Guide |
|---|---|---|
| `file` | A local file; select CSV, JSON Lines, Parquet, Excel, binary, or a custom format with `Format` | [File formats](../file-formats.md), [CSV](./csv.md), [Parquet](./parquet.md), [Excel](./excel.md), [binary files](../binary-files.md) |
| `sqlite` | A SQL query over a SQLite connection | [SQLite](./sqlite.md) |
| `duckdb` | A SQL query over a DuckDB connection | [DuckDB](./duckdb.md) |
| `sqlserver` | A SQL query over a SQL Server connection | [SQL databases](../sql-databases.md#sql-server) |
| `postgresql` | A SQL query over a PostgreSQL connection | [SQL databases](../sql-databases.md#postgresql) |
| `mysql` | A SQL query over a MySQL connection | [SQL databases](../sql-databases.md#mysql) |
| `oracle` | A SQL query over an Oracle connection | [SQL databases](../sql-databases.md#oracle) |
| `azureblob` | Objects in an Azure Blob container | [Object storage](../object-storage.md#azure-blob) |
| `s3` | Objects in an Amazon S3 bucket | [Object storage](../object-storage.md#amazon-s3) |
| `adlsgen2` | Files in Azure Data Lake Storage Gen2 | [Object storage](../object-storage.md#azure-data-lake-storage-gen2) |
| `gcs` | Objects in a Google Cloud Storage bucket | [Object storage](../object-storage.md#google-cloud-storage) |
| `http` | Records from a JSON HTTP API | [HTTP and OData](../http.md) |
| `odata` | Records from an OData service | [HTTP and OData](../http.md) |
| `salesforce` | Salesforce Sales Cloud records | [Salesforce](./salesforce.md) |
| `sharepoint` | SharePoint list items through Microsoft Graph | [SharePoint](./sharepoint.md) |
| `hubspot` | HubSpot CRM records | [HubSpot](./hubspot.md) |
| `zendesk` | Zendesk tickets, users, organizations, or search results | [Zendesk](./zendesk.md) |
| `servicenow` | ServiceNow Table API records | [ServiceNow](./servicenow.md) |
| `mongodb` | Documents from a MongoDB collection | [MongoDB](../mongodb.md) |
| `dataverse` | A Simple, FetchXML, or Cds4Sql query | [Dataverse](./dataverse.md) |
| `custom` | A paged PowerShell callback | [Custom](./custom.md) |

Every source requires `ConnectionId` and a stable `Key`. File sources use `Type = 'file'`, a required `Format` descriptor, and a `DirectoryInfo` plus `Path` or a `FileInfo` without `Path`. Object-storage sources also declare `Format` independently from the storage provider `Type`; see [object storage](../object-storage.md) and [file formats](../file-formats.md). `Id` is optional; use it when a source field should be carried as the row's target-record ID. `ExpectedRecordCount` is also optional. See [common source properties](../../configuration.md#source-properties).

File and SQLite/DuckDB database connectors use a `System.IO.DirectoryInfo` root with a step `Path` or a `System.IO.FileInfo` naming the exact file. SQL Server, PostgreSQL, MySQL, and Oracle sources require a caller-supplied ADO.NET `DbConnection`; object-storage sources require the provider client; Salesforce, SharePoint, HubSpot, Zendesk, and ServiceNow sources require a caller-supplied authenticated `HttpClient`; MongoDB sources require an `IMongoClient` or `IMongoDatabase`. Ordinary source rows are JSON-compatible objects; the binary format exposes its file-backed `Content` value separately. SQL database sources require a query in `Options.Query`.

Each connector guide includes a table of the logical values it emits and their concrete PowerShell/.NET representations. Structured JSON connectors use JSON-native values; binary-capable sources expose file-backed content as `MigrationBinaryValue`. See [binary values and sidecar files](../../concepts/binary-values-and-sidecars.md) for the shared type contract and large-attachment round-trip format.

For Dataverse cmdlet options beyond the migration adapter's requirements, see the upstream [`Get-DataverseRecord` reference](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Get-DataverseRecord.md), [`Invoke-DataverseSql` reference](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Invoke-DataverseSql.md), and [querying concept](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/core-concepts/querying.md).
