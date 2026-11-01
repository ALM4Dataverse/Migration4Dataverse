# PowerShell reference

This page indexes the supported PowerShell migration contract. Use the linked pages for full command, connector, and callback details.

> [!NOTE]
> The C# engine is an internal implementation detail. This reference does not define a supported public C# API.

## Migration graph

[`Invoke-M4dMigration`](./cmdlets/invoke-migration.md) runs a directed acyclic graph of PowerShell step definitions. The supported step types are:

| Step type | Purpose |
|---|---|
| `source` | Read rows from a built-in or custom connector. |
| `transform` | Map input with PowerShell or an engine-managed SQLite/DuckDB transform; database transforms support per-row scripts, per-batch SQL, and whole-dataset SQL. |
| `filter` | Keep or discard each input row. |
| `append` | Concatenate two or more row streams without deduplicating. |
| `join` | Match a child stream to a keyed source or Dataverse target stream. |
| `target` | Write mapped rows to a built-in or custom connector. |
| `callback` | Read or process batches using the custom batch contract. |

Start with [step configuration](./configuration.md). For keyed dependencies, joins, and completion-only dependencies, see [dependencies](./concepts/dependencies.md).

## PowerShell commands

- [Cmdlets and functions](./cmdlets/README.md) — exported command index.
- [`Get-M4dMigrationComponent`](./cmdlets/get-migration-component.md) — list documented source and target connectors and graph actions with Markdown-based help.
- [`Invoke-M4dMigration`](./cmdlets/invoke-migration.md) — load a `.m4d` definition, run lifecycle hooks, and apply options.
- [`New-M4dMigrationFile`](./cmdlets/new-migration-file.md) — create a complete commented migration template.
- [`Get-M4dMigrationRun`](./cmdlets/get-migration-run.md) — list or inspect saved runs.
- [Run inspection and recovery](./cmdlets/migration-inspection-and-recovery.md) — inspect steps, batches, errors, captured rows, and queue recovery actions from PowerShell.
- [Migration dashboard](./cmdlets/migration-dashboard.md) — view run history, progress, and optional row lineage.
- [`Start-M4dMigrationDatabaseServer`](./cmdlets/migration-database-server.md) — keep a local database owner available to multiple PowerShell processes.
- [`Clear-M4dMigrationTargetTuning`](./cmdlets/clear-migration-target-tuning.md) — remove legacy Dataverse tuning profiles from older databases.

The lower-level store functions are listed in the [cmdlet index](./cmdlets/README.md); ordinary migrations do not need to call them directly.

## Connectors and options

- [Source connectors](./connectors/sources/README.md)
- [Target connectors](./connectors/targets/README.md)
- [File formats](./connectors/file-formats.md) — built-in and custom serialization independent of local files and object storage.
- [SQLite source SQL and parameters](./connectors/sources/sqlite.md)
- [SQLite target schema, keys, indexes, and upsert](./connectors/targets/sqlite.md)
- [SQL Server, PostgreSQL, MySQL, and Oracle](./connectors/sql-databases.md)
- [Azure Blob, Amazon S3, ADLS Gen2, and Google Cloud Storage](./connectors/object-storage.md)
- [HTTP and OData sources](./connectors/http.md)
- [Salesforce Sales Cloud source](./connectors/sources/salesforce.md)
- [Excel source](./connectors/sources/excel.md)
- [SharePoint list source](./connectors/sources/sharepoint.md)
- [HubSpot source](./connectors/sources/hubspot.md)
- [Zendesk source](./connectors/sources/zendesk.md)
- [ServiceNow source](./connectors/sources/servicenow.md)
- [MongoDB source and target](./connectors/mongodb.md)
- [Custom source callbacks](./connectors/sources/custom.md)
- [Custom target callbacks](./connectors/targets/custom.md)
- [`New-M4dMigrationBinaryValue`](./cmdlets/new-migration-binary-value.md) — file-backed binary values, streaming transforms, Base64, and MIME detection.
- [Connections and callback execution](./concepts/connections-and-callbacks.md)
- [Database-backed SQLite and DuckDB transforms](./dev/transformations/transform.md#database-backed-migration-graph-transforms)

## File content and attachments

File access and serialization are separate: local files use `Type = 'file'`, object-storage connectors use the provider type, and both select a top-level `Format` such as `csv`, `jsonl`, `parquet`, `binary`, or `custom`. Format settings belong in `Format.Options`; storage settings remain in connector `Options`. See the [file format guides](./connectors/sources/README.md) and [object-storage guide](./connectors/object-storage.md).

Mappings and custom callbacks can pass or modify file-backed `MigrationBinaryValue` objects without placing file bytes in persisted batch JSON. The Dataverse adapter supports File/Image source columns, Notes and activity attachment bodies, and File/Image target uploads. See the [Dataverse source](./connectors/sources/dataverse.md) and [Dataverse target](./connectors/targets/dataverse.md) guides for adapter limits.

The [binary files and documents guide](./connectors/binary-files.md) covers filesystem files, binary object-storage formats, and SharePoint document content. Filesystem sources and targets handle one file per step; object-storage sources emit one row per object and targets create one object per row; SharePoint sources can stream document-library content and targets can stream document uploads. Microsoft Graph simple uploads are limited to 250 MiB; use a custom target callback with an upload session for larger files.

The [binary values and sidecar files concept](./concepts/binary-values-and-sidecars.md) documents common logical and .NET value types, streaming transformations, and manifestless round-tripping of binary columns beside CSV, JSON Lines, and Parquet outputs.

For Dataverse, these pages describe the Migration4Dataverse adapter only:

- [Dataverse source](./connectors/sources/dataverse.md)
- [Dataverse target](./connectors/targets/dataverse.md)
- A Dataverse `ConnectionId` may resolve to a single connection or a per-run connection array shared by typed sources and targets; root Dataverse sources may define explicit query partitions. See the [source guide](./connectors/sources/dataverse.md#parallel-source-partitions) and [target guide](./connectors/targets/dataverse.md#connections-and-request-balancing).

The complete upstream cmdlet and concept references are maintained by [`Rnwood.Dataverse.Data.PowerShell`](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell):

- [Get-DataverseRecord](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Get-DataverseRecord.md) and [querying](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/core-concepts/querying.md)
- [Set-DataverseRecord](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Set-DataverseRecord.md), [Remove-DataverseRecord](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Remove-DataverseRecord.md), and [creating and updating](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/core-concepts/creating-updating.md)
- [Invoke-DataverseSql](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Invoke-DataverseSql.md)
- [`Get-DataverseConnection`](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Get-DataverseConnection.md), [connections](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/core-concepts/connections.md), and [authentication](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/getting-started/authentication.md)

## Operational behavior

- [Batching and backpressure](./concepts/batching.md) explains queue bounds and worker concurrency.
- [Error handling and recovery](./concepts/recovery-and-replay.md) explains source-read and batch retries, persisted failures, edited-row retry, replay, and at-least-once external effects.
- [Dashboard](./cmdlets/migration-dashboard.md) explains progress, failure inspection, exact-row editing/retry, failed-batch replay preparation, debug snapshots, lineage, and local access.

`Invoke-M4dMigration -WhatIf` reads and transforms rows but skips target writes and callback batch handlers. Caller-authored readers and transforms can still have side effects. Debug capture and previews store row data in the migration database; protect that file and review the retention settings.

### Error behavior

Source-reader failures are retried up to `-SourceReadMaxAttempts` (default `3`) from the latest durable source cursor. Exceptions from a batch handler are retried up to `-MaxAttempts` (default `5`); a result explicitly marked as containing errors is terminal and is not retried. `-MaxErrorRatePercent` and per-step `ErrorRateThresholdPercent` can accept a cumulative row-error percentage; both default to strict stop-on-error. Reader, handler, and infrastructure exceptions remain fatal.

The dashboard displays persisted run and batch failures, can export recorded batch error details, queue failed batches for replay, and edit/retry individual rows when a Transform, Filter, or callback reports an exact input index. Source-reader, batch-level, and unattributed target errors remain batch-level. Step continuation does not retry the failed step; downstream receives only outputs emitted by that step, and an approved source-reader failure skips unread rows. Recovery decisions take effect only when `Invoke-M4dMigration` is rerun with the same run ID and compatible step definition. Runs with accepted row errors or an operator-approved continuation finish as `CompletedWithErrors`. See [Invoke-M4dMigration error behavior](./cmdlets/invoke-migration.md#error-and-recovery-behavior) and [recovery and replay](./concepts/recovery-and-replay.md) for the full contract.
