# Object storage connectors

The `azureblob`, `s3`, `adlsgen2`, and `gcs` connectors read and write object datasets. `Type` selects the storage provider and the required top-level `Format` independently selects serialization. Structured sources list objects under a prefix and feed them through a JSON Lines, CSV, Parquet, or custom reader; targets stage records and upload one complete object. With `Format.Type = 'binary'`, each source object becomes a file-backed `Content` value and each target record is uploaded as a separate object.

The caller supplies the live client in `-Connections`; the engine does not create credentials or dispose caller clients.

| Type | Connection object | Container/root option |
|---|---|---|
| `azureblob` | `Azure.Storage.Blobs.BlobContainerClient` or `BlobServiceClient` | `Options.Container` for a service client |
| `s3` | `Amazon.S3.IAmazonS3` | `Options.Bucket` |
| `adlsgen2` | `Azure.Storage.Files.DataLake.DataLakeFileSystemClient` or `DataLakeServiceClient` | `Options.FileSystem` for a service client |
| `gcs` | `Google.Cloud.Storage.V1.StorageClient` | `Options.Bucket` |

## Source

A source `Path` is the object prefix (for example `imports/accounts/`). `Options.Prefix` overrides `Path`. Built-in formats select objects by their registered extensions; use `Options.IncludeAllExtensions = $true` to include other extensions. A custom source format must declare `Format.Extensions` or set `Options.IncludeAllExtensions = $true`.

### Format descriptor

| Property | Required | Description |
|---|---:|---|
| `Type` | Yes | `jsonl`, `csv`, `parquet`, `binary`, or `custom`. |
| `Options` | No | The selected format's options, such as CSV reader options or Parquet reader options. |
| `Extensions` | Custom only | File extensions used to select objects for a custom format source. Required unless `Options.IncludeAllExtensions` is true. |
| `Identity` | Custom source only | Stable identifier for resume compatibility. |
| `Read` | Custom source only | Scriptblock that reads a stream and emits records. |
| `ContentType` | Custom format only | MIME type used for custom-format target uploads, unless overridden by storage `Options.ContentType`. |

### Storage options

| Option | Default | Description |
|---|---:|---|
| `Prefix` | Source `Path` | Object prefix to list. |
| `IncludeAllExtensions` | `$false` | Include all non-directory objects, regardless of extension. |
| `Container` | `null` | Azure Blob container for a `BlobServiceClient` connection. |
| `Bucket` | `null` | S3 or GCS bucket. |
| `FileSystem` | `null` | ADLS Gen2 file system for a `DataLakeServiceClient` connection. |

Objects are sorted by key with ordinal string order before reading. Source keys must still be unique and strictly increasing within each object and across the combined stream. The source does not scan object contents to calculate a total; set `ExpectedRecordCount` when the count is known.

For `Format = 'binary'`, each record contains `Key`, `FileName`, `ContentType`, `Length`, and `Content`. Downloads stream directly into run-scoped binary staging; MIME type is taken from object metadata when available or detected from the staged content and file name. See [binary files and documents](./binary-files.md#object-storage-source-and-target).

| Logical source field | Type in the PowerShell mapping |
|---|---|
| Key, file name, content type | `System.String` |
| Length | `System.Int64` |
| Content | `MigrationBinaryValue` |
| Structured rows | The JSONL, CSV, or Parquet source types for the selected `Format` |

```powershell
$steps = @(
    @{
        Id = 'Accounts'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 's3'
            Format = @{
                Type = 'parquet'
                Options = @{
                    Columns = @('accountid', 'name')
                }
            }
            ConnectionId = 'archive'
            Path = 'exports/accounts/'
            Key = 'accountid'
            Options = @{
                Bucket = 'migration-archive'
            }
        }
    }
)
```

## Target

A structured target `Path` is the final object key. For `Format.Type = 'binary'`, it is a prefix and each record's object key is appended. `Target.Table` is not used. The engine stages batches locally and uploads only after the graph succeeds; uploads carry a SHA-256 metadata value under `migration4dataverse-sha256`.

### Format descriptor

| Property | Required | Description |
|---|---:|---|
| `Type` | Yes | `jsonl`, `csv`, `parquet`, `binary`, or `custom`. |
| `Options` | No | The selected format's writer options. For binary targets, includes `ObjectKeyColumn` and `ContentColumn`. |
| `Extensions` | Custom only | Extension used for the staged custom-format object. |
| `Write` | Custom only | Scriptblock that writes record batches to a stream. |
| `ContentType` | Custom only | MIME type for the uploaded object; storage `Options.ContentType` overrides it. |

### Storage options

| Option | Default | Description |
|---|---:|---|
| `Container` | `null` | Azure Blob container for a `BlobServiceClient` connection. |
| `Bucket` | `null` | S3 or GCS bucket. |
| `FileSystem` | `null` | ADLS Gen2 file system for a `DataLakeServiceClient` connection. |
| `ContentType` | Format-derived | Optional upload MIME type override. |

For structured targets, an existing object is accepted only when its replay hash metadata matches the finalized output. For binary targets, each existing object is accepted only when its replay hash metadata matches that record's content. Different existing content fails rather than being overwritten.

| Logical target value | Accepted type |
|---|---|
| Structured `jsonl`, `csv`, or `parquet` | The JSON-compatible values and explicit schema types documented for the corresponding file target |
| Binary `ObjectKeyColumn` | `System.String` relative object key |
| Binary `ContentColumn` | `MigrationBinaryValue` (streamed to one object per row) |

## Custom formats

Set `Format.Type = 'custom'` to serialize records independently of the object-storage provider. On a custom source, `Format.Extensions` filters listed object keys, or `Options.IncludeAllExtensions = $true` disables extension filtering. Custom target output is staged and uploaded after graph completion. See [custom stream callbacks](./file-formats.md#custom-stream-callbacks) for the read/write scriptblock contracts, streaming context, and examples.

```powershell
@{
    Id = 'AccountsTarget'
    Type = 'target'
    Inputs = @('Accounts')
    Target = @{
        Type = 's3'
        Format = @{ Type = 'jsonl' }
        ConnectionId = 'archive'
        Path = 'exports/accounts.jsonl'
        Options = @{
            Bucket = 'migration-archive'
        }
    }
}
```

## Provider details

### Azure Blob

Use a `BlobContainerClient` directly, or a `BlobServiceClient` with `Options.Container`. Source objects are listed with `GetBlobsAsync` and downloaded to temporary files. Target uploads use `If-None-Match: *` and metadata-based replay detection.

### Amazon S3

Use an `IAmazonS3` client with `Options.Bucket`. Source listings follow `ListObjectsV2` continuation tokens. Target uploads use a `PutObjectRequest` with `If-None-Match: *` and metadata-based replay detection. Very large single-file uploads are limited by the S3 single-request size; use partitioned outputs for larger datasets.

### Azure Data Lake Storage Gen2

Use a `DataLakeFileSystemClient`, or a `DataLakeServiceClient` with `Options.FileSystem`. Source listings recurse under the prefix and skip directories. Target uploads use a conditional create and metadata-based replay detection.

### Google Cloud Storage

Use a `StorageClient` with `Options.Bucket`. Source listings follow the client's paged object enumeration. Target uploads use `IfGenerationMatch = 0` and metadata-based replay detection.

See [source connectors](./sources/README.md), [target connectors](./targets/README.md), and [migration step configuration](../configuration.md).
