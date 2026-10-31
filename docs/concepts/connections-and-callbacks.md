# Connections and callbacks

Runtime connection values are passed to `Invoke-M4dMigration` separately from step definitions using `-Connections`. Source and Target steps refer to them by `ConnectionId`. For SQLite and DuckDB, use a `FileInfo` naming the database or a `DirectoryInfo` plus a relative step `Path`; the engine creates and disposes the database connection and loads the provider automatically. The caller owns the lifetime of other supplied runtime objects and must not place secrets or live runtime objects in step configuration.

For Dataverse, create and authenticate the connection with `Rnwood.Dataverse.Data.PowerShell`. Migration4Dataverse supplies that connection to the module's source and target cmdlets; it does not replace the module's authentication or connection management.

See upstream [`Get-DataverseConnection`](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Get-DataverseConnection.md), the [connections concept](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/core-concepts/connections.md), and the [authentication guide](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/getting-started/authentication.md) for the full Dataverse connection options.

| Connector | Expected runtime connection |
|---|---|
| JSONL, CSV, Parquet source or target | `System.IO.DirectoryInfo` directory root or `System.IO.FileInfo` exact file |
| SQLite source or target | `FileInfo` naming the database or `DirectoryInfo` plus `Path` |
| DuckDB source or target | `FileInfo` naming the database or `DirectoryInfo` plus `Path` |
| SQL Server source or target | `Microsoft.Data.SqlClient.SqlConnection` |
| PostgreSQL source or target | `Npgsql.NpgsqlConnection` |
| MySQL source or target | `MySqlConnector.MySqlConnection` |
| Oracle source or target | `Oracle.ManagedDataAccess.Client.OracleConnection` |
| Azure Blob source or target | `BlobContainerClient` or `BlobServiceClient` |
| Amazon S3 source or target | `IAmazonS3` |
| ADLS Gen2 source or target | `DataLakeFileSystemClient` or `DataLakeServiceClient` |
| Google Cloud Storage source or target | `StorageClient` |
| HTTP or OData source | `Uri`, absolute URL string, or `HttpClient` |
| MongoDB source or target | `IMongoClient` or `IMongoDatabase` |
| SQLite or DuckDB Transform | No caller connection; the engine creates a run-and-step-specific database beside `-DatabasePath` |
| Dataverse source or target | Connection object supported by `Rnwood.Dataverse.Data.PowerShell` |
| Custom source or target | Any non-null caller-provided object |

File and SQLite/DuckDB connectors use `DirectoryInfo` plus a relative `Path`, or `FileInfo` as the exact file and omit `Path`. SQL database connectors use caller-supplied ADO.NET connections, object-storage connectors use provider clients, HTTP sources use a URI or `HttpClient`, and MongoDB uses an `IMongoClient` or `IMongoDatabase`. The caller owns these objects; the engine opens/closes SQL connections as needed but does not dispose caller connections or clients. File and object-storage targets publish one complete output after the migration succeeds. SQLite and DuckDB transforms do not accept `ConnectionId`; their managed databases are isolated by provider, run ID, and step ID. `Transform.InitializeSql` runs before any source records or target batches and should be idempotent for resume. Whole-dataset SQLite/DuckDB staging shares the transform's managed database. Custom connector callbacks execute in the embedded PowerShell runspace pool, not in the caller's interactive scope. Import modules with `ModulesToImport` or a custom connector's `Modules`; pass state through the supplied connection, callback arguments, or cursor rather than relying on interactive variables.

Dataverse source and target steps automatically load `Rnwood.Dataverse.Data.PowerShell` into the runspace pool. The resolved source connection is supplied to `Get-DataverseRecord` or `Invoke-DataverseSql`; do not set a separate connection in source `Options`. See the [Dataverse source](../connectors/sources/dataverse.md) and [Dataverse target](../connectors/targets/dataverse.md) guides for adapter-specific constraints and upstream cmdlet links.

Callback output is part of the protocol. Source callbacks emit one page object, and custom target callbacks emit exactly one Boolean `true` after a successful write. Diagnostics should use a non-output stream such as `Write-Verbose`; extra success-stream output is treated as invalid callback output.
