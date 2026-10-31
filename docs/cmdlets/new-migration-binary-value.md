# New-M4dMigrationBinaryValue

Creates a file-backed binary value for use in a migration source callback or mapping. Binary values are staged outside the migration database; only a small, versioned reference is stored in migration batches.

## Syntax

```powershell
New-M4dMigrationBinaryValue -FilePath <string> -StagingRoot <string> [-FileName <string>] [-ContentType <string>]
New-M4dMigrationBinaryValue -InputStream <Stream> -StagingRoot <string> [-FileName <string>] [-ContentType <string>]
New-M4dMigrationBinaryValue -Bytes <byte[]> -StagingRoot <string> [-FileName <string>] [-ContentType <string>]
New-M4dMigrationBinaryValue -Base64 <string> -StagingRoot <string> [-FileName <string>] [-ContentType <string>]
```

In source callbacks, use `$BinaryContext.StagingRoot`. A regular PowerShell Transform receives `$BinaryContext` as its second argument. A Transform that also receives a connection gets the binary context after that connection. A custom target receives the root as `$Context.BinaryStagingRoot`.

## Streaming and transformations

`-FilePath` and `-InputStream` copy content to staging with a stream; they do not hold the complete file in memory. `-Bytes` and `-Base64` accept already-materialized values.

The returned `MigrationBinaryValue` supports:

| Member | Description |
|---|---|
| `OpenRead()` | Open a readable stream over the staged bytes. |
| `CreateWriter(FileName, ContentType)` | Create a staged output stream for a mapped or modified value; call `Complete()` to produce a new value. |
| `EncodeBase64()` / `DecodeBase64()` | Encode or decode to another staged value without keeping the complete value in memory. Decoding preserves the file name and content type metadata. |
| `OpenBase64Read()` | Read the Base64 representation as a stream. |
| `ToBase64String()` | Return a Base64 string. This intentionally materializes a string and is best reserved for APIs that require it. |
| `DetectMimeType()` | Inspect a small prefix, then use the file extension when available. |
| `[MigrationBinaryValue]::SerializeJson(value, stagingRoot)` / `DeserializeJson(type)` | Serialize JSON directly to a staged stream or deserialize from the staged stream. |

Returning a binary value unchanged from a mapping passes the same content and metadata along. To replace it, create a writer, stream the new bytes into it, and return the value from `Complete()`. Staged values remain available while a run may be resumed after failure or cancellation; successful non-preview runs remove their run-specific staging directory.

> [!IMPORTANT]
> A failed or cancelled run's binary staging files are local to the machine and filesystem used by `Invoke-M4dMigration`. Resume the run with access to the same staging directory; do not remove it before the run is complete.
