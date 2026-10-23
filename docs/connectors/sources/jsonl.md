# JSON Lines source

The `jsonl` source reads UTF-8 JSON Lines incrementally: each non-empty line is one JSON object. It is suitable for streaming file input and does not define connector-specific `Options`.

## Configuration

| Property | Required | Description |
|---|---:|---|
| `Type` | Yes | `jsonl`. |
| `ConnectionId` | Yes | ID whose runtime value is a `System.IO.DirectoryInfo` directory. |
| `Path` | Yes | File path relative to the connection directory. |
| `Key` | Yes | Source property used as the row key for routing and resume ordering. |
| `Id` | Yes | Source property containing the target record GUID. |
| `ExpectedRecordCount` | No | Positive expected number of records; `0` (default) disables the row-count check. |
| `Options` | No | No JSONL-specific options are currently defined. |

## Example

```powershell
Source = @{
    Type = 'jsonl'
    ConnectionId = 'input-files'
    Path = 'customers.jsonl'
    Key = 'SourceId'
    Id = 'DataverseId'
    ExpectedRecordCount = 250
}
```

The JSONL file must contain objects, one per line. Ensure the key is stable and ordered appropriately for replay and child routing. See [recovery and replay](../../concepts/recovery-and-replay.md).
