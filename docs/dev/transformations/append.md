# Append / Union All (internal pipeline)

`append` concatenates two or more input streams in their declared order. The aliases `union-all`, `union_all`, and `union all` are also accepted. This is Union All behavior: it preserves duplicate rows and does not sort or deduplicate.

## Configuration

| Field | Requirement | Description |
|---|---|---|
| `Type` | Required | `append` or an accepted alias. |
| `Inputs` | At least two | Ordered node IDs to concatenate. |
| `SourceId` | Not accepted | Source IDs belong on a separate `source` node. |
| `Keys` | Not accepted | Append has no ordering options. |
| `SortOptions` | Not accepted | Append has no buffering options. |

## Example

```powershell
$node = @{
    Id = 'all-customers'
    Type = 'append'
    Inputs = @('north-region', 'south-region')
}
```

Inputs are consumed sequentially, one at a time, so Append does not buffer the complete input. Rows can be emitted before all later inputs have been read. It preserves input record values and order within each input. This node has no node-specific options; use [pipeline configuration](./README.md) for source and key semantics.
