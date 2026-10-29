# JSON Lines target

The `jsonl` target writes the complete step output to one UTF-8 JSON Lines file. Batches are staged separately, ordered by sequence, and combined into the configured file during the scheduler's completion phase. Publication uses a temporary file and atomic replacement per destination; multiple file targets are not a cross-file transaction.

## Configuration

| Property | Required | Description |
|---|---:|---|
| `Type` | Yes | `jsonl`. |
| `ConnectionId` | Yes | ID whose runtime value is a `System.IO.DirectoryInfo` or `System.IO.FileInfo`. |
| `Path` | Conditional | Exact output filename relative to a `DirectoryInfo`; omit when the connection is a `FileInfo`. |
| `Operation` | No | `Write`; this is the only accepted JSONL target operation. |
| `BatchSize` | No | Positive target batch size override. |
| `Options` | No | No JSONL-specific options are currently defined. |

## Example

```powershell
Target = @{
    Type = 'jsonl'
    ConnectionId = 'output-files'
    Path = 'customers.jsonl'
    Operation = 'Write'
}
```

Identical batch replays are accepted; conflicting content for a batch identity fails. A new run replaces the configured output file with its complete output.
