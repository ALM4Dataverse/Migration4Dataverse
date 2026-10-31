# Binary files and documents

Binary content is passed through the migration graph as a `MigrationBinaryValue`. Its bytes are staged in a run-scoped directory; queued batches store relative references and metadata rather than Base64 payloads. Mappings may pass a value unchanged, replace it, or use the streaming helpers documented in [`New-M4dMigrationBinaryValue`](../cmdlets/new-migration-binary-value.md).

For the shared type contract and manifestless round-trip behavior with CSV, JSON Lines, and Parquet, see [binary values and sidecar files](../concepts/binary-values-and-sidecars.md).

## Filesystem source and target

Use `Type = 'file'` with `Format.Type = 'binary'` and a `FileInfo` connection naming one exact file, or a `DirectoryInfo` connection plus a relative `Path`.

A binary source emits one record with the configured source key property and `Key`, `FileName`, `ContentType`, `Length`, and `Content`. `Content` is a `MigrationBinaryValue`; MIME type is detected from the content signature and file extension.

| Logical field | Emitted .NET type |
|---|---|
| Key / file name / content type | `System.String` |
| Length | `System.Int64` |
| Content | `MigrationBinaryValue` |

A binary target writes one record's `Content` value to the configured target file. Set `Format.Options.ContentColumn` to change the field name. The target rejects zero or multiple records and publishes the output atomically only after the graph succeeds.

| Logical field | Accepted .NET type |
|---|---|
| Content | `MigrationBinaryValue` |

```powershell
@{
    Id = 'Document'
    Type = 'source'
    Inputs = @()
    Source = @{
        Type = 'file'
        Format = @{ Type = 'binary' }
        ConnectionId = 'files'
        Path = 'report.pdf'
        Key = 'FileName'
    }
}

@{
    Id = 'DocumentCopy'
    Type = 'target'
    Inputs = @('Document')
    Target = @{
        Type = 'file'
        Format = @{
            Type = 'binary'
            Options = @{ ContentColumn = 'Content' }
        }
        ConnectionId = 'output'
        Path = 'report-copy.pdf'
    }
}
```

For `FileInfo` connections, omit `Path` and use the exact file supplied as the connection. For `DirectoryInfo` connections, `Path` is required and must be relative to that directory.

## Object-storage source and target

The `azureblob`, `s3`, `adlsgen2`, and `gcs` connectors accept `Format.Type = 'binary'`. A source emits one record per non-directory object, including its original `Key`, `FileName`, `ContentType`, `Length`, and file-backed `Content`. Downloads stream directly into run-scoped staging.

A binary object-storage target writes one object per input record. `Target.Path` is an output prefix; the target appends the value in `Format.Options.ObjectKeyColumn` (default `Key`). The `Content` field defaults to `Format.Options.ContentColumn`. Mappings can change either field to rename or relocate an object. Existing objects are accepted only when their SHA-256 replay metadata matches.

```powershell
@{
    Id = 'Documents'
    Type = 'source'
    Inputs = @()
    Source = @{
        Type = 'azureblob'
        Format = @{ Type = 'binary' }
        ConnectionId = 'source-storage'
        Path = 'incoming/'
        Key = 'Key'
        Options = @{ Container = 'source' }
    }
}

@{
    Id = 'DocumentArchive'
    Type = 'target'
    Inputs = @('Documents')
    Target = @{
        Type = 's3'
        Format = @{
            Type = 'binary'
            Options = @{
                ObjectKeyColumn = 'Key'
                ContentColumn = 'Content'
            }
        }
        ConnectionId = 'archive'
        Path = 'backup/'
        Options = @{ Bucket = 'migration-archive' }
    }
}
```

The destination key is `Target.Path` joined with the mapped key. For example, `backup/` and `folder/report.pdf` produce `backup/folder/report.pdf`. The binary format includes all objects except directory markers; `IncludeAllExtensions` does not change this.

## SharePoint documents

Set `Options.IncludeDocumentContent = $true` on a SharePoint list source to expand each list item to its `driveItem` and stream its document content. The resulting row has the additional `FileName`, `ContentType`, `Length`, and `Content` fields.

The SharePoint target writes each record's file content to the configured `Options.DriveId` and `ParentItemId` (default `root`). `FileNameColumn`, `ContentColumn`, and `ContentTypeColumn` default to `FileName`, `Content`, and `ContentType`. It checks an existing file's streamed SHA-256 before treating a retry as successful; different existing content fails rather than being overwritten.

Microsoft Graph simple uploads are limited to 250 MiB. Larger files require an upload-session workflow; use a custom target callback with the provider's streaming upload-session API for those files. See [SharePoint source](./sources/sharepoint.md) and [SharePoint target](./targets/sharepoint.md).
