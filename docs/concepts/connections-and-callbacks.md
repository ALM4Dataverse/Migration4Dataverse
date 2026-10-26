# Connections and callbacks

Connection objects are passed to `Invoke-Migration` separately from step definitions using `-Connections`. Source and Target steps refer to them by `ConnectionId`. The caller owns their lifetime and must not place secrets or live runtime objects in step configuration.

For Dataverse, create and authenticate the connection with `Rnwood.Dataverse.Data.PowerShell`. Migration4Dataverse supplies that connection to the module's source and target cmdlets; it does not replace the module's authentication or connection management.

See upstream [`Get-DataverseConnection`](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Get-DataverseConnection.md), the [connections concept](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/core-concepts/connections.md), and the [authentication guide](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/getting-started/authentication.md) for the full Dataverse connection options.

| Connector | Expected runtime connection |
|---|---|
| JSONL, CSV, Parquet source or target | `System.IO.DirectoryInfo` directory root |
| SQLite source, Transform, or target | `Microsoft.Data.Sqlite.SqliteConnection` |
| DuckDB source, Transform, or target | `DuckDB.NET.Data.DuckDBConnection` |
| Dataverse source or target | Connection object supported by `Rnwood.Dataverse.Data.PowerShell` |
| Custom source or target | Any non-null caller-provided object |

Each file connector `Path` is resolved below its connection directory. Custom connector callbacks execute in the embedded PowerShell runspace pool, not in the caller's interactive scope. Import modules with `ModulesToImport` or a custom connector's `Modules`; pass state through the supplied connection, callback arguments, or cursor rather than relying on interactive variables.

Dataverse source and target steps automatically load `Rnwood.Dataverse.Data.PowerShell` into the runspace pool. The resolved source connection is supplied to `Get-DataverseRecord` or `Invoke-DataverseSql`; do not set a separate connection in source `Options`. See the [Dataverse source](../connectors/sources/dataverse.md) and [Dataverse target](../connectors/targets/dataverse.md) guides for adapter-specific constraints and upstream cmdlet links.

Callback output is part of the protocol. Source callbacks emit one page object, and custom target callbacks emit exactly one Boolean `true` after a successful write. Diagnostics should use a non-output stream such as `Write-Verbose`; extra success-stream output is treated as invalid callback output.
