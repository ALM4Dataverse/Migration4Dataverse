# Dependencies

Migration steps form a directed acyclic graph. `Inputs` declares row-flow edges: a producer can have multiple consumers, and each emitted dependency payload is routed to the declared child step. Declaring multiple inputs does not implicitly join their rows; use keyed `Inputs` for row-linked source records or a `join` step to combine rows. `DependsOn` is reserved for additional whole-step completion barriers.

```powershell
$steps = @(
    @{
        # Reads account rows once and makes them available to two independent consumers.
        Id = 'AccountsSource'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'jsonl'
            ConnectionId = 'files'
            Path = 'accounts.jsonl'
            Key = 'SourceId'
        }
    }
    @{
        # Trims account names for the main account output.
        Id = 'NormalizeAccounts'
        Type = 'transform'
        Inputs = @('AccountsSource')
        Script = {
            param($row)
            @{ display_name = ([string]$row.Name).Trim() }
        }
    }
    @{
        # Writes an audit copy directly from the source without waiting for normalization.
        Id = 'AccountAudit'
        Type = 'target'
        Inputs = @('AccountsSource')
        Target = @{
            Type = 'jsonl'
            ConnectionId = 'files'
            Path = 'audit'
        }
    }
)
```

`NormalizeAccounts` and `AccountAudit` are independent consumers of the same source. The producer's successful batch releases a bounded batch for each declared consumer.

## Whole-step completion dependencies

`Inputs` declares row-flow edges and implicitly declares those dependencies. Use `DependsOn` with one or more step IDs on a source-driven step only for an additional completion barrier that is not already an input. Point to the parent's terminal Target when the source or join must wait for all parent rows to be written:

```powershell
@{
    # Reads contacts only after the account target has completed.
    Id = 'ContactsSource'
    Type = 'source'
    Inputs = @()
    DependsOn = @('AccountsTarget')
    Source = @{
        Type = 'csv'
        ConnectionId = 'files'
        Path = 'contacts.csv'
        Key = 'ContactId'
    }
}
```

Multiple IDs are an all-of barrier. The dependent source or join begins only after each referenced step has completed; a failed or cancelled parent fails the run without starting the reader. `DependsOn` does not carry rows. Do not repeat an `Inputs` step in `DependsOn`.

## Row-linked source dependencies

Use a keyed `Inputs` entry when child source rows refer to a parent source. The entry names both sides of the relationship: `Child` is the child property and `Parent` is the parent's configured `Source.Key`. `RowsPerParent` is optional: use a positive number to require a fixed number of children for every parent, or omit it (equivalent to `0`) for variable child counts, including parents with no children:

```powershell
@{
    # Reads order lines and links each row to its parent order by key.
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
        Type = 'jsonl'
        ConnectionId = 'files'
        Path = 'order-lines.jsonl'
        Key = 'LineId'
    }
}
```

Use multiple keyed entries when a child row can refer to more than one parent source:

```powershell
Inputs = @(
    @{ Job = 'OrdersSource'; Keys = @{ Child = 'order_id'; Parent = 'OrderId' } }
    @{ Job = 'ProductsSource'; Keys = @{ Child = 'product_id'; Parent = 'ProductId' } }
)
```

Each `Job` must identify a source step. The engine indexes each parent's source keys to their batches, then releases a child batch when the batches for that row's non-null parent keys are available. A child row with a JSON `null` key has no relationship to that parent: it is retained and adds no prerequisite for that parent. If every parent key is null, the row has no row-linked batch prerequisites. Any non-null key that does not match a row in its named parent source fails the migration instead of being dropped. Missing or blank key fields are invalid; use an explicit `null` for an absent relationship. Whole-step completion dependencies remain separate and only gate release when explicitly specified.

The engine stages each row-linked source in a temporary SQLite database so it can release null-containing rows without waiting for unrelated parent rows and order linked rows deterministically. Child source files do not need to be pre-sorted or grouped, and a row-linked source can itself be a parent for further dependencies. Root source records remain strictly ordered by `Source.Key`. Parent source keys must be non-empty and unique within their source step. A positive `RowsPerParent` applies independently to each relationship and requires exactly that many child rows for every parent row; null relationships do not count toward that parent's child count. Omit it (or set it to `0`) for variable child counts, including zero. Completion dependencies gate all child rows until the referenced steps finish; use a parent Target when child lookups must not race parent writes. The staging file is bounded-memory and is removed when source enumeration ends.

## Joining written parent records

Use a `join` step to combine two row streams: the child/left input first and the parent/right input second. Each input declares its `Job` and output alias in `As`; the second declares the key mapping. The child row and parent record are emitted as properties of one nested row; a following Transform still consumes exactly one input row:

```powershell
@{
    # Joins opportunities to written account rows, retaining those without a match.
    Id = 'JoinOpportunities'
    Type = 'join'
    Inputs = @(
        @{ Job = 'OpportunitiesSource'; As = 'Opportunity' }
        @{
            Job = 'AccountsTarget'
            As = 'Account'
            Keys = @(
                @{ Child = 'account_id'; Parent = 'new_salesforceid' }
                @{ Child = 'account_region'; Parent = 'region_code' }
            )
        }
    )
    JoinType = 'left'
}

@{
    # Maps the joined opportunity and account into a target lookup reference.
    Id = 'MapOpportunities'
    Type = 'transform'
    Inputs = @('JoinOpportunities')
    Script = {
        param($row)
        @{
            account_lookup = if ($null -ne $row.Account) {
                @{
                    TableName = 'account'
                    Id = [string]$row.Account.Id
                }
            }
        }
    }
}
```

The first input is the left/child stream; the second is the right/parent stream. `Keys` contains ordered `{ Child, Parent }` pairs and supports composite keys. If the parent input is a Dataverse Target, its `Parent` columns must exactly match an ordered `MatchOn` group. Each matched `$row.<As>` record includes all mapped Target columns and the final Dataverse GUID in `Id`. Chain two-input joins to add more parent lookups; a later join may use dotted key paths such as `Opportunity.id` into a nested joined row.

`JoinType` is required. `inner` drops a child row with no parent match; `left` retains it and sets the parent alias to `$null`. Null or blank child key components never match; left joins retain those rows immediately, while inner joins filter them. Join inputs are staged and indexed in SQLite as each completed batch arrives. `JoinOrder` defaults to `ready`, which emits available matches without waiting behind an earlier child whose parent has not arrived; later child rows can therefore be emitted first. Deferred rows are resolved when a parent arrives or the parent step completes. Set `JoinOrder = 'stable'` to preserve left-input order; an earlier unresolved key can then hold up later matches. Neither side needs to be sorted by the join key. Staging is retained after failures for recovery and removed after successful completion.

## Append

`append` takes two or more input step IDs and emits their rows as a union-all stream. It does not match records by key. Each parent stream must be capable of producing the same row shape expected by downstream steps.

```powershell
@{
    # Combines domestic and international customer streams without matching rows.
    Id = 'AllCustomers'
    Type = 'append'
    Inputs = @('DomesticCustomers', 'InternationalCustomers')
}
```

## Callback multi-parent prerequisites

Callback steps can declare multiple input IDs. The producing handler returns `MigrationDependencyOutput` values, and `RequiredBatchIds` identifies any additional parent batches that must succeed before a child batch is released. The callback creates or interprets the payload; the scheduler does not infer record joins from the `Inputs` list.

Stream-wide `Merge` and `Sort` are supported by the standalone data-flow API but are not yet implemented as `Invoke-Migration` step types. The migration graph rejects them before starting a run. See [migration step configuration](../configuration.md) and the [data-flow transformation reference](../transformations/README.md).
