# Object storage connectors

The `azureblob`, `s3`, `adlsgen2`, and `gcs` connectors read and write partitioned object datasets. Each source lists objects under a prefix, downloads them in key order, and feeds their records through the normal file readers. Each target stages batches locally and uploads one complete object after the graph succeeds.

The caller supplies the live client in `-Connections`; the engine does not create credentials or dispose caller clients.

| Type | Connection object | Container/root option |
|---|---|---|
| `azureblob` | `Azure.Storage.Blobs.BlobContainerClient` or `BlobServiceClient` | `Options.Container` for a service client |
| `s3` | `Amazon.S3.IAmazonS3` | `Options.Bucket` |
| `adlsgen2` | `Azure.Storage.Files.DataLake.DataLakeFileSystemClient` or `DataLakeServiceClient` | `Options.FileSystem` for a service client |
| `gcs` | `Google.Cloud.Storage.V1.StorageClient` | `Options.Bucket` |

## Source

A source `Path` is the object prefix (for example `imports/accounts/`). `Options.Prefix` overrides `Path`. Only files matching `Options.Format` are read unless `IncludeAllExtensions = $true`.

| Option | Default | Description |
|---|---:|---|
| `Format` | `jsonl` | `jsonl`, `csv`, or `parquet`. |
| `Prefix` | Source `Path` | Object prefix to list. |
| `IncludeAllExtensions` | `$false` | Include objects whose extension does not match `Format`. |
| `Container` | `null` | Azure Blob container for a `BlobServiceClient` connection. |
| `Bucket` | `null` | S3 or GCS bucket. |
| `FileSystem` | `null` | ADLS Gen2 file system for a `DataLakeServiceClient` connection. |
| `Csv` | Empty | Nested CSV reader options when `Format = 'csv'`. |
| `Parquet` | Empty | Nested Parquet reader options when `Format = 'parquet'`. |

Objects are sorted by key with ordinal string order before reading. Source keys must still be unique and strictly increasing within each object and across the combined stream. The source does not scan object contents to calculate a total; set `ExpectedRecordCount` when the count is known.

```powershell
$steps = @(
    @{
        Id = 'Accounts'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 's3'
            ConnectionId = 'archive'
            Path = 'exports/accounts/'
            Key = 'accountid'
            Options = @{
                Format = 'parquet'
                Bucket = 'migration-archive'
            }
        }
    }
)
```

## Target

A target `Path` is the final object key. `Target.Table` is not used. The engine writes each batch to a local staging file, finalizes the complete file, and uploads it with a SHA-256 metadata value under `migration4dataverse-sha256`.

| Option | Default | Description |
|---|---:|---|
| `Format` | `jsonl` | `jsonl`, `csv`, or `parquet`. |
| `Container` | `null` | Azure Blob container for a `BlobServiceClient` connection. |
| `Bucket` | `null` | S3 or GCS bucket. |
| `FileSystem` | `null` | ADLS Gen2 file system for a `DataLakeServiceClient` connection. |
| `Csv` | Empty | Nested CSV writer options. |
| `Parquet` | Empty | Nested Parquet writer options. |
| `ContentType` | Derived from `Format` | Upload content type. |

An existing object is accepted only when its replay hash metadata matches the newly finalized content. A different existing object fails the target rather than being overwritten.

```powershell
@{
    Id = 'AccountsTarget'
    Type = 'target'
    Inputs = @('Accounts')
    Target = @{
        Type = 's3'
        ConnectionId = 'archive'
        Path = 'exports/accounts.jsonl'
        Options = @{
            Format = 'jsonl'
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
