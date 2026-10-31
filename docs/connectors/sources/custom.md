# Custom PowerShell source

The `custom` source calls a paged PowerShell callback. It may use any caller-provided non-null runtime connection object and does not require a file path. An optional `Path` is resolved against a `DirectoryInfo` connection and passed to the callback; when omitted, the callback receives an empty string.

## Configuration and callback example

```powershell
Source = @{
    Type = 'custom'
    ConnectionId = 'service'
    Key = 'SourceId'
    Id = 'DataverseId'
    Custom = @{
        Identity = 'customers-source-v1'
        PageSize = 500
        Modules = @('My.Source.Module')
        Callback = {
            param($Connection, $Cursor, $PageSize, $StepId, $Path, $BinaryContext)
            @{
                Records = @()
                NextCursor = $null
                Done = $true
            }
        }
        CountCallback = {
            param($Connection, $StepId, $Path, $Options)
            [long]$Connection.RecordCount
        }
    }
}
```

`Callback` receives the runtime connection, prior cursor (`$null` on first call), configured page size, step ID, path argument, and a binary context with `StagingRoot`. The path is the configured `Path`, or an empty string when `Path` is omitted. The sixth argument is optional for existing callbacks. It must emit exactly one page object:

- `Records`: required array of record objects.
- `Done`: required Boolean.
- `NextCursor`: optional cursor; when `Done` is false it must be non-empty and different from the previous cursor.

Every page must contain no more than `PageSize` records. Diagnostics must go to a non-output stream such as `Write-Verbose`; additional success-stream output invalidates the page.

Each record may contain a `MigrationBinaryValue`, `byte[]`, or readable `Stream`. The engine stages binary content separately before it enters the persisted migration queue. Prefer a stream or `New-M4dMigrationBinaryValue -FilePath` for large files; avoid converting a file to a byte array or Base64 string unless the source API requires it.

| Callback value | Type in the migration mapping |
|---|---|
| Null, Boolean, integer, fractional number, text | `$null`, `Boolean`, `Int64`, `Double`, `String` |
| GUID / ISO date-time strings | `String` / usually `DateTime` after default PowerShell JSON parsing |
| Nested object / array | PowerShell dictionary / object and array |
| `MigrationBinaryValue` | `MigrationBinaryValue` (file-backed transport) |
| `byte[]` / `Stream` | Staged as a `MigrationBinaryValue` before transport |

When no positive `ExpectedRecordCount` is configured, the optional `CountCallback` runs once before the migration starts and receives the runtime connection, step ID, path, and source `Options` hashtable. It must return exactly one non-negative integer, using a provider-side count operation rather than reading records. Its result supplies the source's progress total and is checked against the streamed row count; without it, the count remains unknown until the stream is consumed.

## Options

| Property | Required | Default | Description |
|---|---:|---:|---|
| `Identity` | Yes | — | Stable implementation identity included in resume compatibility. Change it when callback behavior changes. |
| `Callback` | Yes | — | Scriptblock implementing the page protocol. |
| `CountCallback` | No | — | Optional lightweight count query that returns one non-negative integer. |
| `PageSize` | No | `100` | Positive maximum number of records returned by a page. |
| `Modules` | No | `[]` | Module names or paths imported in the embedded runspace pool. |
| `MaxConcurrency` | No | `1` | Accepted positive integer. This field does not configure a source-specific callback semaphore; each source enumeration fetches its pages sequentially. |

The page cursor is local to one enumeration, not the scheduler checkpoint. On resume, the callback may restart at the beginning, but records are replayed only through the latest durably enqueued source key; pending and in-flight batches are read from their stored payloads. Return records in a stable order by `Source.Key` and make the callback deterministic and replayable. A source capable of seeking directly to the checkpoint key may avoid rereading earlier pages.
