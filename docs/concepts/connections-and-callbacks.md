# Connections and callbacks

Connection objects are passed to `Invoke-Migration` separately from step definitions using `-Connections`. Source and Target steps refer to them by `ConnectionId`. The caller owns their lifetime and must not place secrets or live runtime objects in step configuration.

| Connector | Expected runtime connection |
|---|---|
| JSONL, CSV, Parquet source or target | `System.IO.DirectoryInfo` directory root |
| Dataverse source or target | Connection object supported by `Rnwood.Dataverse.Data.PowerShell` |
| Custom source or target | Any non-null caller-provided object |

Each file connector `Path` is resolved below its connection directory. Custom connector callbacks execute in the embedded PowerShell runspace pool, not in the caller's interactive scope. Import modules with `ModulesToImport` or a custom connector's `Modules`; pass state through the supplied connection, callback arguments, or cursor rather than relying on interactive variables.

Dataverse source and target steps automatically load `Rnwood.Dataverse.Data.PowerShell` into the runspace pool. The resolved source connection is supplied to `Get-DataverseRecord` or `Invoke-DataverseSql`; do not set a separate connection in source `Options`.

Callback output is part of the protocol. Source callbacks emit one page object, and custom target callbacks emit exactly one Boolean `true` after a successful write. Diagnostics should use a non-output stream such as `Write-Verbose`; extra success-stream output is treated as invalid callback output.
