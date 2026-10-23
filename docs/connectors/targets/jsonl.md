# JSON Lines target

The `jsonl` target writes one UTF-8 JSON Lines file for each batch. The target writer uses a temporary file and atomic move. If a batch is replayed with identical content it is accepted; conflicting content for the same batch identity fails.

## Configuration

| Property | Required | Description |
|---|---:|---|
| `Type` | Yes | `jsonl`. |
| `ConnectionId` | Yes | ID whose runtime value is a `System.IO.DirectoryInfo` directory. |
| `Path` | Yes | Output subdirectory relative to the connection root. |
| `Operation` | No | `Write`; this is the only accepted JSONL target operation. |
| `BatchSize` | No | Positive target batch size override. |
| `Options` | No | No JSONL-specific options are currently defined. |

## Example

```powershell
Target = @{
    Type = 'jsonl'
    ConnectionId = 'output-files'
    Path = 'customers'
    Operation = 'Write'
}
```

Output paths are partitioned by step, run, and batch identity. JSONL files are not automatically combined into a single output file.
