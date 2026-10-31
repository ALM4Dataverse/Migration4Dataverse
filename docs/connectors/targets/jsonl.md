# JSON Lines target

The `file` target with `Format.Type = 'jsonl'` writes the complete step output to one UTF-8 JSON Lines file. Batches are staged separately, ordered by sequence, and combined into the configured file during the scheduler's completion phase. Publication uses a temporary file and atomic replacement per destination; multiple file targets are not a cross-file transaction.

## Configuration

| Property | Required | Description |
|---|---:|---|
| `Type` | Yes | `file`. |
| `Format.Type` | Yes | `jsonl`. |
| `ConnectionId` | Yes | ID whose runtime value is a `System.IO.DirectoryInfo` or `System.IO.FileInfo`. |
| `Path` | Conditional | Exact output filename relative to a `DirectoryInfo`; omit when the connection is a `FileInfo`. |
| `Operation` | No | `Write`; this is the only accepted JSONL target operation. |
| `BatchSize` | No | Positive target batch size override. |
| `Format.Options` | No | `BinaryColumns` can restrict which top-level binary fields are written as sidecar references. |

## Example

```powershell
Target = @{
    Type = 'file'
    Format = @{
        Type = 'jsonl'
        Options = @{ BinaryColumns = @('Content') }
    }
    ConnectionId = 'output-files'
    Path = 'customers.jsonl'
    Operation = 'Write'
}
```

Identical batch replays are accepted; conflicting content for a batch identity fails. A new run replaces the configured output file with its complete output.

| Logical mapped value | Accepted .NET / JSON type |
|---|---|
| Null, Boolean, number, text | JSON `null`, `Boolean`, number (`Int64`/`Double`), `String` |
| Object / array | JSON object / array from PowerShell dictionaries and collections |
| GUID and date/time | `Guid` / `DateTime` or string; serialized as JSON strings |
| `MigrationBinaryValue`, `byte[]`, readable `Stream` | Top-level field is written as a relative sidecar reference; no file bytes are embedded |

Sidecar files live in `<target-file>.attachments/`; see [binary values and sidecar files](../../concepts/binary-values-and-sidecars.md).
