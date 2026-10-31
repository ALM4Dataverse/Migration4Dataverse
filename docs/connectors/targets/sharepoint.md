# SharePoint document target

The `sharepoint` target uploads file-backed content through Microsoft Graph. Supply an authenticated `HttpClient` in `-Connections`, with its base address set to `https://graph.microsoft.com/v1.0/`. The connection must have permission to write to the configured drive and folder. The target is finalized after graph batches have been staged.

`Target.Path` must be omitted. Configure the destination drive and parent folder in `Target.Options`:

| Option | Default | Description |
|---|---:|---|
| `DriveId` | Required | Microsoft Graph drive ID. |
| `ParentItemId` | `root` | Drive item ID of the destination folder, or `root` for the drive root. |
| `FileNameColumn` | `FileName` | Record field containing a single file name (no path separators). |
| `ContentColumn` | `Content` | Record field containing a `MigrationBinaryValue`. |
| `ContentTypeColumn` | `ContentType` | Record field containing the MIME type. |
| `ContentType` | Detected or row value | Optional fixed MIME type overriding the row and binary-value metadata. |
| `TimeoutSeconds` | `100` | Overall timeout for finalizing the target. |

Each row becomes one document. Uploads stream from staged files and are limited to 250 MiB by Graph's simple-upload endpoint. An existing document is accepted only when downloading and hashing its content confirms it is identical; otherwise the target fails without overwriting it. Larger files require a custom upload-session callback.

| Logical record field | Accepted .NET type |
|---|---|
| File name, content type | `System.String` |
| Content | `MigrationBinaryValue` (streamed; no Base64 conversion) |

```powershell
$graph = [System.Net.Http.HttpClient]::new()
$graph.BaseAddress = [Uri]'https://graph.microsoft.com/v1.0/'
$graph.DefaultRequestHeaders.Authorization =
    [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $graphToken)

$steps = @(
    @{
        Id = 'Documents'
        Type = 'target'
        Inputs = @('DocumentSource')
        Target = @{
            Type = 'sharepoint'
            ConnectionId = 'graph'
            Options = @{
                DriveId = 'drive-id'
                ParentItemId = 'folder-item-id'
            }
        }
    }
)
```

See the [SharePoint binary-content guide](../binary-files.md#sharepoint-documents), the [source connector](../sources/sharepoint.md), and Microsoft's [driveItem upload documentation](https://learn.microsoft.com/en-us/graph/api/driveitem-put-content?view=graph-rest-1.0).
