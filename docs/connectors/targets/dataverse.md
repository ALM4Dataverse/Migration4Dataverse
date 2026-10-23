# Dataverse target

The `dataverse` target upserts mapped records through `Set-DataverseRecord` from `Rnwood.Dataverse.Data.PowerShell`. There is no built-in Dataverse source connector.

## Configuration

| Property | Required | Description |
|---|---:|---|
| `Type` | Yes | `dataverse`. |
| `ConnectionId` | Yes | Key in `-Connections` whose value is a connection supported by `Rnwood.Dataverse.Data.PowerShell`. |
| `Table` | Yes | Dataverse logical table name. |
| `Operation` | No | `Upsert`; no other operation is supported by this connector. |
| `BatchSize` | No | Positive batch size or `auto` (default). Auto tuning is capped at the invocation batch size and the connector limit of 1,000. |
| `MaxConcurrency` | No | Positive per-target limit for concurrent batches or `auto` (default). Auto tuning is capped at `Invoke-Migration -MaxDegreeOfParallelism`. |
| `Options` | No | `MatchOn` selects alternate-key matching; `LookupColumns` maps scalar lookup values to referenced-table key fields. A Join can use a Target's `MatchOn` key group to retrieve the mapped record after it is written. |

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

Batch size and concurrency default to `auto`; setting either to a positive integer fixes that setting. Auto tuning begins at batch size one and concurrency one, tests three completed batches per candidate, increases batch size before concurrency, and stops an axis after two consecutive candidates fail to improve measured throughput by at least five percent. Batch-size candidates are capped by the invocation's `BatchSize`; concurrency candidates are capped by `-MaxDegreeOfParallelism` (maximum 32). Tuning is independent per target, including targets that share a connection. Each `Set-DataverseRecord` invocation uses internal parallelism one. Fixed or tuned concurrency can increase load on the shared connection and Dataverse service, so use conservative limits where connection safety or throttling requires them. The caller owns connection creation, authentication, lifetime, and disposal.

When downstream Joins require parent records, target outputs (including final IDs) are staged in the migration database until the Join completes. A completion-gated Dataverse Source can instead reread a completed Target from Dataverse and provide the parent rows, avoiding streamed Target outputs at the cost of additional reads; the sequential Dataverse example demonstrates this option. Protect the database file accordingly. Upsert supports replaying a batch after uncertain completion, but review alternate-key and target-side behavior for the Dataverse table being migrated. This proof-of-concept does not implement create/update/delete operation selection or advanced Dataverse connector options.
