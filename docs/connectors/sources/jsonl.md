# JSON Lines source

The `file` source with `Format.Type = 'jsonl'` reads UTF-8 JSON Lines incrementally: each non-empty line is one JSON object. The format is independent of the file access connector.

## Configuration

| Property | Required | Description |
|---|---:|---|
| `Type` | Yes | `file`. |
| `Format.Type` | Yes | `jsonl`. |
| `ConnectionId` | Yes | ID whose runtime value is a `System.IO.DirectoryInfo` or `System.IO.FileInfo`. |
| `Path` | Conditional | Exact input filename relative to a `DirectoryInfo`; omit when the connection is a `FileInfo`. |
| `Key` | Yes | Source property used as the row key for routing and resume ordering. |
| `Id` | No | Optional source property copied to the row's `Id`, commonly the target record GUID. |
| `ExpectedRecordCount` | No | Positive expected number of records; `0` (default) disables the row-count check. |
| `Format.Options` | No | `BinaryColumns` can identify relative sidecar file references to resolve into file-backed values. |

## Example

```powershell
Source = @{
    Type = 'file'
    Format = @{
        Type = 'jsonl'
        Options = @{ BinaryColumns = @('Content') }
    }
    ConnectionId = 'input-files'
    Path = 'customers.jsonl'
    Key = 'SourceId'
    Id = 'DataverseId'
    ExpectedRecordCount = 250
}
```

The JSONL file must contain objects, one per line. Ensure the key is stable and ordered appropriately for replay and child routing. See [recovery and replay](../../concepts/recovery-and-replay.md).

| Logical JSON value | Emitted .NET / PowerShell type |
|---|---|
| `null`, Boolean, number, string | `$null`, `Boolean`, `Int64`/`Double`, `String` (ISO date/time strings may become `DateTime`) |
| Object / array | `Hashtable`/object dictionary and array |
| Binary sidecar reference in `Format.Options.BinaryColumns` | `String` in the file, then `MigrationBinaryValue` in the source mapping |

JSON has no native GUID, date, decimal, or binary type. Such values are read as JSON strings or numbers unless a mapping converts them. By default, binary references are resolved relative to the source file's parent; see [binary values and sidecar files](../../concepts/binary-values-and-sidecars.md).
