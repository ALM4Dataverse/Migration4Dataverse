# Filter (internal pipeline)

The `filter` node runs a PowerShell script for each input row. It requires exactly one input. Apart from the required `Script`, it has no additional optional node settings.

```powershell
$node = @{
    Id = 'keep-active'
    Type = 'filter'
    Inputs = @('records')
    Script = {
        param($row)
        $row.Active
    }
}
```

The script receives the input row as its first argument. PowerShell output truthiness determines whether the row is retained: truthy output passes the original input row unchanged, while no output or falsey output discards it. Script blocks are executed as source text in an engine-owned runspace, so caller closure state is not captured. Script failures are reported with the node ID.

Filter is a standalone node, not a predicate property on another step. See the [pipeline reference](./README.md) for configuration and graph constraints.
