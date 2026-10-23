# Merge

`merge` streams exactly two inputs into one ordered output. Each input must already be sorted by the same configured keys and directions. Merge compares one record from each side at a time; when keys are equal, the left input is emitted first.

## Configuration

| Field | Requirement | Description |
|---|---|---|
| `Type` | Required | `merge`. |
| `Inputs` | Exactly two | IDs of the left and right input nodes. |
| `Keys` | One or more | Ordered key definitions. See [sort key fields](./README.md#sort-key-definition). |
| `SourceId` | Not accepted | Use source nodes for source definitions. |
| `SortOptions` | Not accepted | Merge is streaming and has no external sort limits. |

## Example

```powershell
$node = @{
    Id = 'merged'
    Type = 'merge'
    Inputs = @('left-sorted', 'right-sorted')
    Keys = @(
        @{ Name = 'AccountNumber'; Type = 'string' }
        @{ Name = 'CreatedOn'; Type = 'datetime'; Descending = $true }
    )
}
```

Each input is checked while being read. If a row is out of order under the configured comparison, enumeration fails and identifies the offending record. Merge does not reorder an invalid input, buffer all rows, or perform a relational join; it emits the lower current key and holds at most one row of lookahead per side. Null ordering follows each key's `NullsFirst` setting.
