# Dependencies

Migration steps form a directed acyclic graph. `Inputs` defines row flow: it says which step produces the rows a step consumes. A producer can have multiple consumers, and each consumer receives its own bounded work.

> [!NOTE]
> Listing multiple `Inputs` does not join rows by itself. Use keyed inputs for a source-to-source relationship, a `join` step to combine rows, or `DependsOn` when a step only needs to wait for another step to finish.

## Fan-out

A source can feed more than one step. Each consumer runs as its own branch:

```powershell
$steps = @(
    @{
        Id = 'AccountsSource'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'file'
            Format = @{ Type = 'jsonl' }
            ConnectionId = 'files'
            Path = 'accounts.jsonl'
            Key = 'SourceId'
        }
    }
    @{
        Id = 'NormalizeAccounts'
        Type = 'transform'
        Inputs = @('AccountsSource')
        Script = {
            param($row)
            @{ display_name = ([string]$row.Name).Trim() }
        }
    }
    @{
        Id = 'AccountAudit'
        Type = 'target'
        Inputs = @('AccountsSource')
        Target = @{
            Type = 'file'
            Format = @{ Type = 'jsonl' }
            ConnectionId = 'files'
            Path = 'audit'
        }
    }
)
```

`NormalizeAccounts` and `AccountAudit` can process batches as `AccountsSource` produces them. They do not wait for one another.

## Completion-only dependencies

Use `DependsOn` on a source or join when it must wait for another step to finish but must not consume that step's rows. For example, a Contacts source can wait until Accounts have been written:

```powershell
@{
    Id = 'ContactsSource'
    Type = 'source'
    Inputs = @()
    DependsOn = @('AccountsTarget')
    Source = @{
        Type = 'file'
        Format = @{ Type = 'csv' }
        ConnectionId = 'files'
        Path = 'contacts.csv'
        Key = 'ContactId'
    }
}
```

Multiple IDs form an all-of barrier. If a required step fails or is cancelled, the dependent reader does not start. Do not repeat an `Inputs` step in `DependsOn`; an input already declares both row flow and its dependency.

## Keyed source relationships

Use an object in a source's `Inputs` array when its rows refer to rows from parent **source** steps. `Child` names the field in the child row; `Parent` names the parent's configured `Source.Key`. `RowsPerParent` is optional and requires a fixed child count when set to a positive value.

```powershell
@{
    Id = 'OrderLinesSource'
    Type = 'source'
    Inputs = @(
        @{
            Job = 'OrdersSource'
            Keys = @{ Child = 'OrderId'; Parent = 'OrderId' }
            RowsPerParent = 2
        }
    )
    Source = @{
        Type = 'file'
        Format = @{ Type = 'jsonl' }
        ConnectionId = 'files'
        Path = 'order-lines.jsonl'
        Key = 'LineId'
    }
}
```

A source can have several keyed parent inputs:

```powershell
Inputs = @(
    @{ Job = 'OrdersSource'; Keys = @{ Child = 'order_id'; Parent = 'OrderId' } }
    @{ Job = 'ProductsSource'; Keys = @{ Child = 'product_id'; Parent = 'ProductId' } }
)
```

The engine indexes parent source keys and stages linked child rows so they can be released as their parents become available. Child rows do not need to arrive grouped or sorted by parent key. A row-linked source can itself be a parent for another step.

> [!IMPORTANT]
> Use an explicit JSON `null` to represent an absent child relationship. Missing or blank child-key fields are invalid. A non-null key with no matching parent row fails the migration rather than being silently dropped.

Null relationships are retained without waiting for that parent and do not count toward `RowsPerParent`. A positive `RowsPerParent` is checked independently for each relationship and each parent row. Omit it, or set it to `0`, for a variable number of child rows, including none. Parent source keys must be non-empty and unique.

## Joining parent records

Use a `join` step to combine a child row with a matching parent row. It has exactly two ordered inputs: the left/child stream first and the right/parent stream second. Each input names its step with `Job` and a nested output alias with `As`; the parent input also defines one or more ordered key pairs.

```powershell
@{
    Id = 'JoinOpportunities'
    Type = 'join'
    Inputs = @(
        @{ Job = 'OpportunitiesSource'; As = 'Opportunity' }
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
```

The following Transform receives one nested row; the child and parent are available as `$row.Opportunity` and `$row.Account`. A Dataverse Target parent includes its mapped columns and final Dataverse `Id`. A Source parent includes its selected columns and configured `Id`.

| Setting | Behavior |
|---|---|
| `JoinType = 'inner'` | Drop child rows that have no parent match. |
| `JoinType = 'left'` | Keep unmatched child rows and set the parent alias to `$null`. |
| `JoinOrder = 'ready'` | Default. Emit a match as soon as its parent row is available; later child rows can be emitted before an earlier unresolved row. |
| `JoinOrder = 'stable'` | Preserve left-input order; an earlier unresolved key can hold up later matches. |

Keys can contain multiple ordered pairs for composite matches. Target parent keys must match one complete ordered `Options.MatchOn` group. Source parent keys must exist and be unique. Null or blank key components never match; inputs do not need to be pre-sorted. Joins can be chained to add further lookups, including dotted paths into an earlier nested row.

## Append

An `append` step takes two or more input IDs and concatenates their rows in the declared order. It preserves duplicates and does not match records by key.

```powershell
@{
    Id = 'AllCustomers'
    Type = 'append'
    Inputs = @('DomesticCustomers', 'InternationalCustomers')
}
```

Each input should provide the row fields expected by downstream steps. For the complete node contract, see [step configuration](../configuration.md).

## Callback multi-parent prerequisites

Callback steps can declare multiple `Inputs`. Their handler returns `MigrationDependencyOutput` values, and `RequiredBatchIds` lists the parent batches that must succeed before a child batch is released. This is an explicit batch prerequisite; the scheduler does not inspect or join records in callback payloads. See [callback nodes](../configuration.md#callback-nodes).

`sort` and `merge` are not supported `Invoke-Migration` step types. The migration graph rejects them during configuration validation.
