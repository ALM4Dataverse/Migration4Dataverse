# Sort (internal pipeline)

`sort` performs a stable sort using one or more typed keys. It must consume the input before returning ordered rows, and uses bounded in-memory runs plus temporary spill files rather than retaining the entire data set in memory.

## Configuration

| Field | Requirement | Description |
|---|---|---|
| `Type` | Required | `sort`. |
| `Inputs` | Exactly one | Input node ID. |
| `Keys` | One or more | Ordered key definitions. See [sort key fields](./README.md#sort-key-definition). |
| `SortOptions` | No | Optional memory, row, record, spill, and temporary-directory limits. |
| `SourceId` | Not accepted | Use a source node for source definitions. |

## Example

```powershell
$sortTemporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) 'migration-sort'
$node = @{
    Id = 'ordered-customers'
    Type = 'sort'
    Inputs = @('customers')
    Keys = @(
        @{ Name = 'Region'; Type = 'string' }
        @{ Name = 'Balance'; Type = 'decimal'; Descending = $true }
    )
    SortOptions = @{
        MemoryBudgetBytes = 67108864
        MaxInMemoryRows = 50000
        MaxRecordBytes = 4194304
        MaxSpillBytes = 2147483648
        TemporaryDirectory = $sortTemporaryDirectory
    }
}
```

## Sort options

| Option | Default | Validation and effect |
|---|---:|---|
| `MemoryBudgetBytes` | `67108864` (64 MiB) | Must be positive. Estimated in-memory record budget for forming sorted runs; not a process-wide RSS limit. |
| `MaxInMemoryRows` | `50000` | Must be positive. Maximum rows in a run before spilling. |
| `MaxRecordBytes` | `4194304` (4 MiB) | Must be positive and no greater than `MemoryBudgetBytes`; records exceeding the limit fail. |
| `MaxSpillBytes` | `long.MaxValue` | Must be non-negative. Maximum live spill-file data; default is effectively unlimited. |
| `TemporaryDirectory` | `null` | Optional non-empty directory path. When omitted, the system temporary directory is used. |

The sort is stable: equal-key records retain input order. It flushes runs when either memory or row thresholds are reached, then merges the runs. Runtime and object overhead exceed the estimated record budget. Spill files contain source records, so choose a protected directory with sufficient space and set a practical `MaxSpillBytes`. Temporary files are removed on normal completion, failure, and iterator disposal; abrupt process termination can leave them behind.
