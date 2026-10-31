# Dataverse target

The `dataverse` target writes mapped rows through `Rnwood.Dataverse.Data.PowerShell`. The upstream module performs the Dataverse operations; this guide covers the migration adapter's supported operations, extra options, and concurrency limits.

> [!NOTE]
> For the full set of write parameters and their semantics, see upstream [`Set-DataverseRecord`](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Set-DataverseRecord.md), [`Remove-DataverseRecord`](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Remove-DataverseRecord.md), and the [creating and updating concept](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/core-concepts/creating-updating.md).

## Configure a target

| Property | Required | Description |
|---|---:|---|
| `Type` | Yes | `dataverse`. |
| `ConnectionId` | Yes | ID in `-Connections` for a supported connection, or an array of connections to balance operations across identities. |
| `Table` | Yes | Dataverse logical table name. |
| `Operation` | No | `Upsert` (default), `Create`, `Update`, `Delete`, or `CreateOrUpdateIfNeeded`. |
| `BatchSize` | No | Positive fixed batch size, default 10 independently of `Invoke-M4dMigration -BatchSize`; maximum 1,000. An explicit value overrides the default. |
| `MaxConcurrency` | No | Optional positive per-target concurrency cap. By default, the target uses its connection's recommended concurrency budget, bounded by the invocation limit. |
| `Options` | No | Matching keys, lookup behavior, or supported write options forwarded to the upstream module. |

```powershell
Target = @{
    Type = 'dataverse'
    ConnectionId = 'crm'
    Table = 'account'
    Operation = 'Upsert'
    BatchSize = 10
    Options = @{
        MatchOn = ,@('new_salesforceid')
    }
}
```

The adapter loads `Rnwood.Dataverse.Data.PowerShell` when a typed Dataverse target is used. The caller creates, authenticates, and owns the connection. The repository's Dataverse examples require module version 3.3.0 or later. See upstream [`Get-DataverseConnection`](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Get-DataverseConnection.md), [connections](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/core-concepts/connections.md), and [authentication](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/getting-started/authentication.md).

## File columns and attachments

Map a Dataverse File/Image column to a `MigrationBinaryValue`. The target first writes the ordinary row, obtains its primary-key ID, then streams the value to Dataverse in file blocks. A Base64-representation value is decoded as a stream before upload. File names and content types are preserved when supplied; `DetectMimeType()` can infer a type from a small content prefix or the file extension.

```powershell
Script = {
    param($row, $BinaryContext)
    @{
        Id = $row.Id
        new_contractfile = $row.contract
    }
}
```

Notes (`annotation`) and activity attachment (`activitymimeattachment`) targets accept binary values mapped to `documentbody` and `body`, respectively. The adapter converts these values to Base64 for the upstream record cmdlet because those Dataverse columns use Base64 text in the record-write contract. This API boundary materializes the encoded string; keep the surrounding migration path file-backed and use a suitably small Dataverse target `BatchSize` for large attachments.

> [!WARNING]
> A File/Image upload occurs after the row write. If the upload fails, the batch fails and may be retried; use a stable mapping and an idempotent operation so the target row and file can be safely written again. Empty File/Image content is rejected rather than silently treated as a clear operation.

| Logical mapped value | Accepted value at the migration adapter |
|---|---|
| Text, Boolean, number | JSON-compatible `System.String`, `Boolean`, or numeric value |
| GUID and date/time | GUID string and ISO date/time string |
| Lookup | The identifier/reference form supported by the upstream `Set-DataverseRecord` contract |
| File/Image or attachment content | `MigrationBinaryValue`; Notes/activity bodies are encoded to Base64 at the upstream cmdlet boundary |

This is the adapter's value contract, not a catalog of Dataverse attribute types. For complete lookup and attribute rules, use the linked upstream write references.

## Connections and request balancing

Use one authenticated connection or an array under the named `-Connections` slot:

```powershell
$connections = @{
    crm = @($crmAsUserA, $crmAsUserB)
}
```

Pool members must target the same Dataverse organization and have the same `MaxRetryCount`. Sources and targets that use the same `ConnectionId` share one in-memory pool; all caller-owned connection objects remain caller-owned. The adapter identifies each member with `WhoAmI` and accounts connections for the same user together.

The pool groups connections by their `WhoAmI` organization/user identity. Connections with the same identity share one budget. Each identity's limit comes from its connection's `RecommendedDegreesOfParallelism` (falling back to 4 when unavailable); the pool's total budget is the sum of unique identity budgets. Individual service calls are routed to an identity below its limit, preferring the lowest rolling five-minute request-unit use and then the fewest active requests. If all identity budgets are full, the caller waits for capacity. The pool expands nested `ExecuteMultiple` and `ExecuteTransaction` requests, charges recognized `*Multiple` messages by target count, and counts retries as distinct attempts. It does not impose a universal service-protection quota or adapt concurrency based on throughput. This balancing also applies to Dataverse source requests that share the slot.

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

## Batch size, concurrency, and retries

Batch size is fixed for each run. Dataverse targets default to 10-row batches regardless of `Invoke-M4dMigration -BatchSize`; set the target's `BatchSize` to choose another positive value up to 1,000. `MaxConcurrency` is an optional fixed per-target cap. When omitted, the target uses the connection's recommended concurrency budget. The pool enforces separate budgets for each identity, while `Invoke-M4dMigration -MaxDegreeOfParallelism` (default 64) remains the overall worker ceiling. A target-level cap can lower concurrency but cannot raise either limit.

Retry and throttle telemetry remains visible in migration diagnostics, but it does not automatically change batch size or concurrency. Tuning profiles saved by older versions are ignored; [`Clear-M4dMigrationTargetTuning`](../../cmdlets/clear-migration-target-tuning.md) remains available to remove those legacy values.

Retries are enabled by default. `Invoke-M4dMigration -DataverseNoRetryDiagnosticMode` disables the Dataverse connection, cmdlet, and scheduler retry layers for a diagnostic run. It can cause transient errors to fail immediately; see [`Invoke-M4dMigration`](../../cmdlets/invoke-migration.md).
