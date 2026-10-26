# Dataverse target

The `dataverse` target writes mapped records through `Rnwood.Dataverse.Data.PowerShell`. There is no built-in Dataverse source connector.

## Configuration

| Property | Required | Description |
|---|---:|---|
| `Type` | Yes | `dataverse`. |
| `ConnectionId` | Yes | Key in `-Connections` whose value is a connection supported by `Rnwood.Dataverse.Data.PowerShell`. |
| `Table` | Yes | Dataverse logical table name. |
| `Operation` | No | `Upsert` (default), `Create`, `Update`, `Delete`, or `CreateOrUpdateIfNeeded`. |
| `BatchSize` | No | Positive batch size or `auto` (default). Auto tuning is capped at the invocation batch size and the connector limit of 1,000. |
| `MaxConcurrency` | No | Positive per-target cap or `auto` (default). Auto mode shares an adaptive concurrency budget across all Dataverse targets using the same connection and is capped by `Invoke-Migration -MaxDegreeOfParallelism`. |
| `Options` | No | `MatchOn` selects matching key groups for update, create/update-if-needed, and delete operations; it also identifies target parent keys for Joins. Upsert uses the alternate-key values in the mapped record. `LookupColumns` maps lookup columns to the key field used to resolve scalar lookup values. |

## Example

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

Upstream Transform steps produce target fields; the engine initializes each record's `Id` from the GUID-valued source field named by `Source.Id`, unless a Transform output explicitly supplies `Id`. For scalar lookups configured with `LookupColumns`, provide the foreign-key value; the connector resolves it using the referenced table's configured key field.

`Upsert` with `Options.MatchOn` uses the module's keyed create/update path; it queries existing values and skips unchanged updates. Without `MatchOn`, it uses `Set-DataverseRecord -Upsert`. `Create` uses `Set-DataverseRecord -CreateOnly`; `Update` uses `-NoCreate`; `Delete` uses `Remove-DataverseRecord`. Delete inputs must include `Id` or configure `Options.MatchOn`. Create does not accept `MatchOn`.

`CreateOrUpdateIfNeeded` uses `Set-DataverseRecord`'s create/update path. With `MatchOn`, the module queries existing values and skips updates when the mapped values are unchanged. This is useful for tables where a direct upsert is slower. It supports `MatchOn` and `LookupColumns`.

Use `LookupColumns` to resolve scalar foreign keys without a separate migration Source/Join that rereads the parent table:

```powershell
Target = @{
    Type = 'dataverse'
    ConnectionId = 'crm'
    Table = 'contact'
    Operation = 'Upsert'
    Options = @{
        MatchOn = ,@('new_salesforceid')
        LookupColumns = @{
            parentcustomerid = 'new_salesforceid'
        }
    }
}
```

The Transform output should set `parentcustomerid` to the source parent's `new_salesforceid` value. The Dataverse module uses the configured lookup key to resolve those scalar values. Direct references to known records can continue to use `{ TableName, Id }`.

To make a written parent record available to a mapping Transform, add a Join before the Transform. The Join retains the child row under its `As` alias and exposes each matched parent under the relationship alias:

```powershell
@(
    @{
        # Joins contacts to the written account rows while retaining unmatched contacts.
        Id = 'JoinContacts'
        Type = 'join'
        Inputs = @(
            @{ Job = 'ContactsSource'; As = 'Contact' }
            @{
                Job = 'AccountsTarget'
                As = 'Account'
                Keys = @(
                    @{ Child = 'account_id'; Parent = 'new_salesforceid' }
                )
            }
        )
        JoinType = 'left'
    }
    @{
        # Maps each contact and assigns its matched Dataverse account reference.
        Id = 'MapContacts'
        Type = 'transform'
        Inputs = @('JoinContacts')
        Script = {
            param($row)
            @{
                parentcustomerid = if ($null -ne $row.Account) {
                    @{
                        TableName = 'account'
                        Id = [string]$row.Account.Id
                    }
                }
            }
        }
    }
)
```

Each Join has exactly two ordered inputs: the left/child row stream and the right/parent stream. Each input declares its step ID in `Job` and a nested-row alias in `As`; the parent input also declares `Keys`, pairing child fields with parent fields. `Keys` may contain multiple ordered pairs for a composite key. For a Target parent, the keys must match one ordered `Options.MatchOn` group; a Source parent must provide the key fields with unique values. Chain two-input joins to add further parent lookups; later keys can use dotted paths into the left input's nested row. `JoinType` is required: `inner` drops rows with no match; `left` keeps them and exposes the unmatched alias as `$null`. `JoinOrder` defaults to `ready`: matches stream as soon as their parent rows become available, even if an earlier child is waiting, so output rows may be reordered. Set `JoinOrder = 'stable'` to preserve left-input order; an earlier unresolved key can delay later rows. Null or blank keys never match. Inputs need not be pre-sorted, and temporary staging is cleaned after success but retained for recovery after failure. A following Transform accepts only `$row`, with the nested parent accessible as `$row.Account`; Target parents include mapped fields and the final GUID in `Id`, while Source parents include selected columns and their configured `Id`. Direct references to existing Dataverse records use a `{ TableName, Id }` object with a GUID. `MatchOn` selects alternate-key upserts and is not combined with `Set-DataverseRecord -Upsert`.

Batch size and concurrency default to `auto`; setting either to a positive integer fixes that setting. Auto batch-size tuning remains per target, but the engine serializes candidate experiments across the pipeline and compares each candidate using total successful Dataverse target rows per second, not that target's isolated rate. Each candidate is measured over completed batches; batch-size candidates are capped by the invocation's `BatchSize` and the connector limit of 1,000. Auto concurrency instead uses one adaptive budget per Dataverse connection, shared by all auto-concurrency targets on that connection and capped by `-MaxDegreeOfParallelism` (maximum 32). It starts from `RecommendedDegreesOfParallelism` when the connection exposes it, or a conservative default, and increases only when the connection is saturated and whole-pipeline throughput improves. Retry and throttle signals are counted from connection logs; observed throttling reduces the shared budget. If retry telemetry is unavailable, the controller does not increase concurrency automatically.

When an auto-tuned value is available, it is persisted in the migration SQLite database and reused by later runs. Batch sizes are keyed by target step `Id`; shared concurrency profiles are versioned and tied to the connection ID, and are reused only when the auto-concurrency targets on that connection have compatible profiles. Current invocation limits cap reused values. Use `Clear-MigrationTargetTuning -DatabasePath ... -StepId ...` to clear one profile, or `-All` to clear every profile in the database. Each `Set-DataverseRecord` invocation uses internal parallelism one. The normal retry policy remains enabled and retries are visible in dashboard telemetry when connection logs expose them. For diagnosis only, `Invoke-Migration -DataverseNoRetryDiagnosticMode` disables Dataverse connection retries, cmdlet retries, and scheduler retries for the run; this can cause transient failures to fail immediately. The connection's retry and log-collection settings are restored when the run ends. The caller owns connection creation, authentication, lifetime, and disposal.

When downstream Joins require parent records, target outputs (including final IDs) are staged in the migration database until the Join completes. A completion-gated Dataverse Source can instead reread a completed Target from Dataverse and provide the parent rows, avoiding streamed Target outputs at the cost of additional reads. Protect the database file accordingly. Review alternate-key and target-side behavior for the Dataverse table being migrated. Custom APIs can be called from a custom Target callback using its supplied connection; SQL4CDS is also available from a Dataverse-connected Transform script through `Invoke-DataverseSql`.
