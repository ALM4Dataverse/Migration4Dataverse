# Dataverse target

The `dataverse` target writes mapped rows through `Rnwood.Dataverse.Data.PowerShell`. The upstream module performs the Dataverse operations; this guide covers the migration adapter's supported operations, extra options, and tuning behavior.

> [!NOTE]
> For the full set of write parameters and their semantics, see upstream [`Set-DataverseRecord`](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Set-DataverseRecord.md), [`Remove-DataverseRecord`](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Remove-DataverseRecord.md), and the [creating and updating concept](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/core-concepts/creating-updating.md).

## Configure a target

| Property | Required | Description |
|---|---:|---|
| `Type` | Yes | `dataverse`. |
| `ConnectionId` | Yes | ID in `-Connections` for a connection supported by the upstream module. |
| `Table` | Yes | Dataverse logical table name. |
| `Operation` | No | `Upsert` (default), `Create`, `Update`, `Delete`, or `CreateOrUpdateIfNeeded`. |
| `BatchSize` | No | Positive batch size or `auto` (default). Automatic tuning is capped by the invocation batch size and the connector limit of 1,000. |
| `MaxConcurrency` | No | Positive per-target limit or `auto` (default). Targets using the same connection share an adaptive concurrency budget. |
| `Options` | No | Matching keys, lookup behavior, or supported write options forwarded to the upstream module. |

```powershell
Target = @{
    Type = 'dataverse'
    ConnectionId = 'crm'
    Table = 'account'
    Operation = 'Upsert'
    BatchSize = 'auto'
    MaxConcurrency = 'auto'
    Options = @{
        MatchOn = ,@('new_salesforceid')
    }
}
```

The adapter loads `Rnwood.Dataverse.Data.PowerShell` when a typed Dataverse target is used. The caller creates, authenticates, and owns the connection. The repository's Dataverse examples require module version 3.3.0 or later. See upstream [`Get-DataverseConnection`](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Get-DataverseConnection.md), [connections](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/core-concepts/connections.md), and [authentication](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/getting-started/authentication.md).

## Operations and matching

| Operation | Adapter behavior |
|---|---|
| `Upsert` | Without `MatchOn`, uses `Set-DataverseRecord -Upsert`. With `MatchOn`, uses the module's keyed create/update path and skips unchanged updates. |
| `Create` | Uses `Set-DataverseRecord -CreateOnly`. `MatchOn` is not supported. |
| `Update` | Uses `Set-DataverseRecord -NoCreate`. |
| `Delete` | Uses `Remove-DataverseRecord`. Rows must include `Id` or the target must define `Options.MatchOn`. |
| `CreateOrUpdateIfNeeded` | Uses the module's create/update path and can skip unchanged updates. Supports `MatchOn` and `LookupColumns`. |

`MatchOn` is an ordered list of one or more alternate-key groups. Each group names fields present in the mapped rows. Upsert uses the mapped alternate-key values; joins that use a Dataverse target as their parent must match one complete ordered group. See [dependencies](../../concepts/dependencies.md#joining-parent-records) for join configuration.

## Lookup values

For new mappings, emit an explicit Dataverse reference instead of a scalar lookup value:

```powershell
parentcustomerid = @{
    TableName = 'account'
    KeyAttributes = @{ accountnumber = 'A-123' }
}
```

A known record can also be referenced by `{ TableName, Id }`. The adapter enables `Set-DataverseRecord -NoLookup` by default to avoid lookup queries; set `Options.NoLookup = $false` when query-based lookup resolution is required. `Options.LookupColumns` remains available for legacy mappings that contain scalar foreign keys.

## Business-logic bypass

`Options.BypassBusinessLogicExecution` and `Options.BypassBusinessLogicExecutionStepIds` are forwarded to `Set-DataverseRecord` when supplied. No bypass is enabled by default. These options are not accepted for `Delete`.

> [!WARNING]
> Bypassing plug-ins or other business logic can change validation and side effects in the target organization. Use it only when that change is intentional. See the upstream [`Set-DataverseRecord` reference](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Set-DataverseRecord.md) for the supported values.

## Automatic tuning and retries

With `BatchSize = 'auto'`, candidate batch sizes are tested serially across the pipeline and scored by successful Dataverse target throughput. With `MaxConcurrency = 'auto'`, targets on the same connection share one adaptive budget capped by `Invoke-Migration -MaxDegreeOfParallelism`. Observed retry or throttle signals can reduce that budget.

Tuning profiles are persisted in the migration database and reused when compatible with the current targets and limits. Use [`Clear-MigrationTargetTuning`](../../cmdlets/clear-migration-target-tuning.md) to clear a target profile or all profiles.

Retries are enabled by default. `Invoke-Migration -DataverseNoRetryDiagnosticMode` disables the Dataverse connection, cmdlet, and scheduler retry layers for a diagnostic run. It can cause transient errors to fail immediately; see [`Invoke-Migration`](../../cmdlets/invoke-migration.md).
