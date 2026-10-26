# PowerShell reference

This page indexes the supported PowerShell migration contract. Use the linked pages for full command, connector, and callback details.

> [!NOTE]
> The C# engine is an internal implementation detail. This reference does not define a supported public C# API.

## Migration graph

[`Invoke-Migration`](./cmdlets/invoke-migration.md) runs a directed acyclic graph of PowerShell step definitions. The supported step types are:

| Step type | Purpose |
|---|---|
| `source` | Read rows from a built-in or custom connector. |
| `transform` | Map each input row with a PowerShell script or supported database-backed transform. |
| `filter` | Keep or discard each input row. |
| `append` | Concatenate two or more row streams without deduplicating. |
| `join` | Match a child stream to a keyed source or Dataverse target stream. |
| `target` | Write mapped rows to a built-in or custom connector. |
| `callback` | Read or process batches using the custom batch contract. |

Start with [step configuration](./configuration.md). For keyed dependencies, joins, and completion-only dependencies, see [dependencies](./concepts/dependencies.md).

## PowerShell commands

- [Cmdlets and functions](./cmdlets/README.md) — exported command index.
- [`Invoke-Migration`](./cmdlets/invoke-migration.md) — run options, module loading, previews, and resume behavior.
- [`Get-MigrationRun`](./cmdlets/get-migration-run.md) — list or inspect saved runs.
- [Migration dashboard](./cmdlets/migration-dashboard.md) — view run history, progress, and optional row lineage.
- [`Start-MigrationDatabaseServer`](./cmdlets/migration-database-server.md) — keep a local database owner available to multiple PowerShell processes.
- [`New-MigrationDatabaseConnection`](./cmdlets/new-migration-database-connection.md) — create SQLite or DuckDB ADO.NET connections.
- [`Clear-MigrationTargetTuning`](./cmdlets/clear-migration-target-tuning.md) — clear saved Dataverse tuning profiles.

The lower-level store functions are listed in the [cmdlet index](./cmdlets/README.md); ordinary migrations do not need to call them directly.

## Connectors and options

- [Source connectors](./connectors/sources/README.md)
- [Target connectors](./connectors/targets/README.md)
- [Custom source callbacks](./connectors/sources/custom.md)
- [Custom target callbacks](./connectors/targets/custom.md)
- [Connections and callback execution](./concepts/connections-and-callbacks.md)

For Dataverse, these pages describe the Migration4Dataverse adapter only:

- [Dataverse source](./connectors/sources/dataverse.md)
- [Dataverse target](./connectors/targets/dataverse.md)

The complete upstream cmdlet and concept references are maintained by [`Rnwood.Dataverse.Data.PowerShell`](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell):

- [Get-DataverseRecord](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Get-DataverseRecord.md) and [querying](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/core-concepts/querying.md)
- [Set-DataverseRecord](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Set-DataverseRecord.md), [Remove-DataverseRecord](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Remove-DataverseRecord.md), and [creating and updating](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/core-concepts/creating-updating.md)
- [Invoke-DataverseSql](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Invoke-DataverseSql.md)
- [`Get-DataverseConnection`](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Get-DataverseConnection.md), [connections](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/core-concepts/connections.md), and [authentication](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/getting-started/authentication.md)

## Operational behavior

- [Batching and backpressure](./concepts/batching.md) explains queue bounds and worker concurrency.
- [Recovery and replay](./concepts/recovery-and-replay.md) explains checkpoints, run compatibility, retries, and at-least-once external effects.
- [Dashboard](./cmdlets/migration-dashboard.md) explains progress estimates, debug snapshots, lineage, and local access.

`Invoke-Migration -WhatIf` reads and transforms rows but skips target writes and callback batch handlers. Caller-authored readers and transforms can still have side effects. Debug capture and previews store row data in the migration database; protect that file and review the retention settings.
