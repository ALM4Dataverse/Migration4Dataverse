using System.Security.Cryptography;
using System.Text.Json;
using System.Collections;
using Amazon.S3;
using Amazon.S3.Model;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Files.DataLake.Models;
using Google;
using Google.Cloud.Storage.V1;
using GoogleObject = Google.Apis.Storage.v1.Data.Object;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.Connectors;

public sealed class ObjectStorageSourceOptions
{
    public string? Prefix { get; init; }

    public string? Container { get; init; }

    public string? Bucket { get; init; }

    public string? FileSystem { get; init; }

    public bool IncludeAllExtensions { get; init; }

    public void Validate(string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        if (Prefix is not null && string.IsNullOrWhiteSpace(Prefix))
        {
            throw new ArgumentException(
                $"Object storage source '{stepId}' Prefix cannot be empty when specified.");
        }
    }

}

public sealed class ObjectStorageTargetOptions
{
    public string ObjectKeyColumn { get; init; } = "Key";

    public string? Container { get; init; }

    public string? Bucket { get; init; }

    public string? FileSystem { get; init; }

    public string? ContentType { get; init; }

    public void Validate(string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ObjectKeyColumn);
    }
}

internal static class ObjectStorageConnectionSupport
{
    public static BlobContainerClient GetAzureBlobContainer(
        object connection,
        string? containerName,
        string description)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection is BlobContainerClient container)
        {
            return container;
        }
        if (connection is BlobServiceClient service)
        {
            if (string.IsNullOrWhiteSpace(containerName))
            {
                throw new ArgumentException(
                    $"Azure Blob {description} requires Options.Container when its connection is a BlobServiceClient.");
            }
            return service.GetBlobContainerClient(containerName);
        }

        throw new ArgumentException(
            $"Azure Blob {description} requires a BlobContainerClient or BlobServiceClient connection; " +
            $"received '{connection.GetType().FullName}'.");
    }

    public static StorageClient GetGcsClient(object connection, string description)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection is not StorageClient client)
        {
            throw new ArgumentException(
                $"Google Cloud Storage {description} requires a StorageClient connection; " +
                $"received '{connection.GetType().FullName}'.");
        }
        return client;
    }

    public static string GetGcsBucket(string? bucket, string description)
    {
        if (string.IsNullOrWhiteSpace(bucket))
        {
            throw new ArgumentException($"Google Cloud Storage {description} requires Options.Bucket.");
        }
        return bucket;
    }

    public static IAmazonS3 GetS3Client(object connection, string description)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection is not IAmazonS3 client)
        {
            throw new ArgumentException(
                $"S3 {description} requires an IAmazonS3 connection; received '{connection.GetType().FullName}'.");
        }
        return client;
    }

    public static string GetS3Bucket(string? bucket, string description)
    {
        if (string.IsNullOrWhiteSpace(bucket))
        {
            throw new ArgumentException($"S3 {description} requires Options.Bucket.");
        }
        return bucket;
    }

    public static void ValidateConnection(string connectorType, object connection, string description)
    {
        if (connectorType == "azureblob")
        {
            if (connection is not (BlobContainerClient or BlobServiceClient))
            {
                throw new ArgumentException(
                    $"Azure Blob {description} requires a BlobContainerClient or BlobServiceClient connection.");
            }
            return;
        }
        if (connectorType == "s3")
        {
            GetS3Client(connection, description);
            return;
        }
        if (connectorType == "adlsgen2")
        {
            DataLakeConnectionSupport.ValidateConnection(connection, description);
            return;
        }
        if (connectorType == "gcs")
        {
            GetGcsClient(connection, description);
            return;
        }
        throw new ArgumentOutOfRangeException(nameof(connectorType));
    }
}

public sealed class ObjectStorageHierarchyRecordReader : IHierarchyRecordReader
{
    private readonly HierarchySourceLevel _level;
    private readonly IReadOnlyList<ObjectStorageItem> _items;
    private readonly Func<HierarchySourceLevel, IHierarchyRecordReader> _fileReaderFactory;
    private readonly string _format;
    private readonly string _temporaryDirectory;
    private int _index;
    private IHierarchyRecordReader? _current;
    private long _completedRecordCount;
    private bool _disposed;

    public ObjectStorageHierarchyRecordReader(
        HierarchySourceLevel level,
        IReadOnlyList<ObjectStorageItem> items,
        ObjectStorageSourceOptions options,
        Func<HierarchySourceLevel, IHierarchyRecordReader>? fileReaderFactory = null)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(level.StepId);
        _level = level;
        _items = items;
        _fileReaderFactory = fileReaderFactory ?? HierarchyRecordReaderFactory.CreateFileReader;
        _format = level.FormatType
            ?? throw new ArgumentException(
                $"Object storage source '{level.StepId}' requires a format.",
                nameof(level));
        if (_format == "custom" &&
            level.FormatExtensions.Count == 0 &&
            !options.IncludeAllExtensions)
        {
            throw new ArgumentException(
                $"Custom object-storage source '{level.StepId}' must declare Format.Extensions or set Options.IncludeAllExtensions.");
        }
        if (_format == "binary" && string.IsNullOrWhiteSpace(level.BinaryStagingRoot))
        {
            throw new ArgumentException(
                $"Binary object source '{level.StepId}' requires a binary staging directory.",
                nameof(level));
        }
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "migration4dataverse-objects",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporaryDirectory);
    }

    public long RecordCount => _completedRecordCount + (_current?.RecordCount ?? 0);

    public async ValueTask<JsonElement?> PeekAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (true)
        {
            if (_current is not null)
            {
                var value = await _current.PeekAsync(cancellationToken).ConfigureAwait(false);
                if (value.HasValue)
                {
                    return value;
                }
                _completedRecordCount += _current.RecordCount;
                await _current.DisposeAsync().ConfigureAwait(false);
                _current = null;
                _index++;
                continue;
            }

            if (_index >= _items.Count)
            {
                return null;
            }

            var item = _items[_index];
            if (_format == "binary")
            {
                var stagingRoot = _level.BinaryStagingRoot
                    ?? throw new InvalidOperationException(
                        $"Binary object source '{_level.StepId}' has no binary staging directory.");
                var fileName = ObjectStorageConnectorSupport.GetObjectFileName(item.Key);
                using var writer = MigrationBinaryValue.CreateWriter(
                    stagingRoot,
                    fileName,
                    item.ContentType);
                if (item.DownloadToStreamAsync is not null)
                {
                    await item.DownloadToStreamAsync(writer.Stream, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var temporaryPath = Path.Combine(_temporaryDirectory, $"{_index:D8}.bin");
                    await item.DownloadAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
                    await using var input = File.OpenRead(temporaryPath);
                    await input.CopyToAsync(writer.Stream, cancellationToken).ConfigureAwait(false);
                }

                var binaryValue = writer.Complete();
                var contentType = item.ContentType ?? binaryValue.DetectMimeType();
                _current = new SingleJsonHierarchyRecordReader(BinaryRecordJson.Create(
                    _level.KeyProperty,
                    item.Key,
                    fileName,
                    contentType,
                    binaryValue,
                    stagingRoot));
                continue;
            }
            var extension = ObjectStorageConnectorSupport.GetFileExtension(
                _format,
                _level.FormatExtensions);
            var path = Path.Combine(_temporaryDirectory, $"{_index:D8}{extension}");
            await item.DownloadAsync(path, cancellationToken).ConfigureAwait(false);
            var fileLevel = new HierarchySourceLevel(
                _level.StepId,
                "file",
                path,
                _level.KeyProperty,
                _level.ParentKeyProperty,
                _level.RowsPerParent,
                expectedRecordCount: 0,
                optionsJson: "{}",
                connectionId: _level.ConnectionId,
                identity: _level.Identity,
                formatType: _format,
                formatOptionsJson: _level.FormatOptionsJson,
                formatExtensions: _level.FormatExtensions,
                formatObjectKey: item.Key);
            _current = _fileReaderFactory(fileLevel);
        }
    }

    public JsonElement Take()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _current?.Take()
            ?? throw new InvalidOperationException(
                $"Object storage source '{_level.StepId}' has no buffered record.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_current is not null)
        {
            await _current.DisposeAsync().ConfigureAwait(false);
        }
        try
        {
            if (Directory.Exists(_temporaryDirectory))
            {
                Directory.Delete(_temporaryDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static async Task<IReadOnlyList<ObjectStorageItem>> ListAzureBlobItemsAsync(
        BlobContainerClient container,
        ObjectStorageSourceOptions options,
        string formatType,
        IReadOnlyList<string> formatExtensions,
        string? prefix,
        CancellationToken cancellationToken)
    {
        var items = new List<ObjectStorageItem>();
        await foreach (var blob in container.GetBlobsAsync(
                           BlobTraits.None,
                           BlobStates.None,
                           prefix,
                           cancellationToken).ConfigureAwait(false))
        {
            if (!ObjectStorageConnectorSupport.MatchesFormat(
                    blob.Name,
                    formatType,
                    options.IncludeAllExtensions,
                    formatExtensions))
            {
                continue;
            }
            var blobClient = container.GetBlobClient(blob.Name);
            items.Add(new ObjectStorageItem(
                blob.Name,
                (path, token) => blobClient.DownloadToAsync(path, token))
            {
                ContentType = blob.Properties.ContentType,
                DownloadToStreamAsync = (stream, token) => blobClient.DownloadToAsync(stream, token)
            });
        }
        items.Sort((left, right) => string.Compare(left.Key, right.Key, StringComparison.Ordinal));
        return items;
    }

    public static async Task<IReadOnlyList<ObjectStorageItem>> ListS3ItemsAsync(
        IAmazonS3 client,
        string bucket,
        ObjectStorageSourceOptions options,
        string formatType,
        IReadOnlyList<string> formatExtensions,
        string? prefix,
        CancellationToken cancellationToken)
    {
        var items = new List<ObjectStorageItem>();
        string? continuationToken = null;
        do
        {
            var response = await client.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = bucket,
                    Prefix = prefix,
                    ContinuationToken = continuationToken
                },
                cancellationToken).ConfigureAwait(false);
            foreach (var item in response.S3Objects)
            {
                if (!ObjectStorageConnectorSupport.MatchesFormat(
                        item.Key,
                        formatType,
                        options.IncludeAllExtensions,
                        formatExtensions))
                {
                    continue;
                }
                var key = item.Key;
                items.Add(new ObjectStorageItem(
                    key,
                    async (path, token) =>
                    {
                        using var objectResponse = await client.GetObjectAsync(
                            new GetObjectRequest
                            {
                                BucketName = bucket,
                                Key = key
                            },
                            token).ConfigureAwait(false);
                        await using var file = File.Create(path);
                        await objectResponse.ResponseStream.CopyToAsync(file, token).ConfigureAwait(false);
                    })
                {
                    DownloadToStreamAsync = async (stream, token) =>
                    {
                        using var objectResponse = await client.GetObjectAsync(
                            new GetObjectRequest
                            {
                                BucketName = bucket,
                                Key = key
                            },
                            token).ConfigureAwait(false);
                        await objectResponse.ResponseStream.CopyToAsync(stream, token).ConfigureAwait(false);
                    }
                });
            }
            continuationToken = response.IsTruncated == true
                ? response.NextContinuationToken
                : null;
        }
        while (!string.IsNullOrWhiteSpace(continuationToken));
        items.Sort((left, right) => string.Compare(left.Key, right.Key, StringComparison.Ordinal));
        return items;
    }
}

public sealed record ObjectStorageItem(
    string Key,
    Func<string, CancellationToken, Task> DownloadAsync)
{
    public string? ContentType { get; init; }

    public Func<Stream, CancellationToken, Task>? DownloadToStreamAsync { get; init; }
}

public static class ObjectStorageTargetWriter
{
    public static void WriteBatch(
        object connection,
        string connectorType,
        string objectKey,
        string formatType,
        IReadOnlyList<string> formatExtensions,
        string stepId,
        string runId,
        string batchId,
        long sequence,
        string recordsJson,
        string optionsJson,
        string formatOptionsJson,
        string stagingRoot)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordsJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ObjectStorageConnectionSupport.ValidateConnection(
            connectorType,
            connection,
            $"target '{stepId}'");
        var options = DeserializeTargetOptions(optionsJson, stepId);
        options.Validate(stepId);
        FileBatchTargetWriter.WriteSingleFileTargetBatch(
            GetStagingPath(stagingRoot, runId, stepId, formatType, formatExtensions),
            runId,
            stepId,
            batchId,
            sequence,
            recordsJson);
    }

    public static async Task FinalizeAsync(
        object connection,
        string connectorType,
        string objectKey,
        string stepId,
        string runId,
        string optionsJson,
        string formatType,
        IReadOnlyList<string> formatExtensions,
        string formatOptionsJson,
        string? formatContentType,
        string stagingRoot,
        string binaryStagingRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryStagingRoot);
        var options = DeserializeTargetOptions(optionsJson, stepId);
        options.Validate(stepId);
        var stagingPath = GetStagingPath(stagingRoot, runId, stepId, formatType, formatExtensions);
        if (string.Equals(formatType, "binary", StringComparison.OrdinalIgnoreCase))
        {
            var binaryOptions = DeserializeFormatOptions<BinaryFileTargetOptions>(formatOptionsJson, stepId);
            binaryOptions.Validate(stepId);
            await FinalizeBinaryAsync(
                connection,
                connectorType,
                objectKey,
                stepId,
                runId,
                options,
                binaryOptions,
                stagingPath,
                binaryStagingRoot,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        FileBatchTargetWriter.FinalizeSingleFileTarget(
            stagingPath,
            runId,
            stepId,
            formatType.Trim().ToLowerInvariant(),
            formatOptionsJson);

        var hash = await ComputeHashAsync(stagingPath, cancellationToken).ConfigureAwait(false);
        await UploadStagedObjectAsync(
            connection,
            connectorType,
            objectKey,
            stepId,
            stagingPath,
            hash,
            options.ContentType
                ?? formatContentType
                ?? RecordFileFormatRegistry.GetContentType(formatType),
            options,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task UploadFinalizedFileAsync(
        object connection,
        string connectorType,
        string objectKey,
        string stepId,
        string stagingPath,
        string optionsJson,
        string? formatContentType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingPath);
        var options = DeserializeTargetOptions(optionsJson, stepId);
        options.Validate(stepId);
        var hash = await ComputeHashAsync(stagingPath, cancellationToken).ConfigureAwait(false);
        await UploadStagedObjectAsync(
            connection,
            connectorType,
            objectKey,
            stepId,
            stagingPath,
            hash,
            options.ContentType ?? formatContentType ?? "application/octet-stream",
            options,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task FinalizeBinaryAsync(
        object connection,
        string connectorType,
        string prefix,
        string stepId,
        string runId,
        ObjectStorageTargetOptions options,
        BinaryFileTargetOptions formatOptions,
        string stagingPath,
        string binaryStagingRoot,
        CancellationToken cancellationToken)
    {
        var records = FileBatchTargetWriter.ReadSingleFileTargetRecords(
            stagingPath,
            runId,
            stepId);
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var restored = MigrationBinaryValue.RestoreFromTransport(record, binaryStagingRoot);
            if (restored is not IDictionary fields ||
                fields[formatOptions.ContentColumn] is not MigrationBinaryValue value)
            {
                throw new InvalidDataException(
                    $"Binary object-storage target '{stepId}' requires a MigrationBinaryValue in field '{formatOptions.ContentColumn}'.");
            }
            if (fields[options.ObjectKeyColumn] is not string key ||
                string.IsNullOrWhiteSpace(key) ||
                key.StartsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Binary object-storage target '{stepId}' requires a relative object key in field '{options.ObjectKeyColumn}'.");
            }

            var destinationKey = string.IsNullOrWhiteSpace(prefix)
                ? key
                : $"{prefix.TrimEnd('/')}/{key.TrimStart('/')}";
            var rowContentType = fields["ContentType"] as string;
            var contentType = !string.IsNullOrWhiteSpace(options.ContentType)
                ? options.ContentType
                : !string.IsNullOrWhiteSpace(rowContentType)
                    ? rowContentType
                    : !string.IsNullOrWhiteSpace(value.ContentType)
                        ? value.ContentType
                        : value.DetectMimeType();
            var stagingFile = Path.Combine(
                Path.GetDirectoryName(stagingPath)!,
                $"binary-{Guid.NewGuid():N}.bin");
            try
            {
                await using (var input = value.OpenRead())
                await using (var output = new FileStream(
                                 stagingFile,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 1024 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }

                await UploadStagedObjectAsync(
                    connection,
                    connectorType,
                    destinationKey,
                    stepId,
                    stagingFile,
                    value.Sha256,
                    contentType,
                    options,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (File.Exists(stagingFile))
                {
                    File.Delete(stagingFile);
                }
            }
        }
    }

    private static async Task UploadStagedObjectAsync(
        object connection,
        string connectorType,
        string objectKey,
        string stepId,
        string stagingPath,
        string hash,
        string contentType,
        ObjectStorageTargetOptions options,
        CancellationToken cancellationToken)
    {
        if (connectorType == "azureblob")
        {
            var container = ObjectStorageConnectionSupport.GetAzureBlobContainer(
                connection,
                options.Container,
                $"target '{stepId}'");
            var blob = container.GetBlobClient(objectKey);
            if (await blob.ExistsAsync(cancellationToken).ConfigureAwait(false))
            {
                var properties = await blob.GetPropertiesAsync(
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                properties.Value.Metadata.TryGetValue(
                    ObjectStorageConnectorSupport.HashMetadataKey,
                    out var existingHash);
                if (string.Equals(existingHash, hash, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                throw new InvalidDataException(
                    $"Azure Blob target '{objectKey}' already exists with different content.");
            }

            await using var stream = File.OpenRead(stagingPath);
            try
            {
                await blob.UploadAsync(
                    stream,
                    new BlobUploadOptions
                    {
                        HttpHeaders = new BlobHttpHeaders { ContentType = contentType },
                        Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            [ObjectStorageConnectorSupport.HashMetadataKey] = hash
                        },
                        Conditions = new BlobRequestConditions
                        {
                            IfNoneMatch = new Azure.ETag("*")
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Azure.RequestFailedException exception) when (
                exception.Status is 409 or 412)
            {
                var properties = await blob.GetPropertiesAsync(
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                properties.Value.Metadata.TryGetValue(
                    ObjectStorageConnectorSupport.HashMetadataKey,
                    out var existingHash);
                if (!string.Equals(existingHash, hash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Azure Blob target '{objectKey}' was created concurrently with different content.",
                        exception);
                }
            }
            return;
        }

        if (connectorType == "gcs")
        {
            var gcsClient = ObjectStorageConnectionSupport.GetGcsClient(connection, $"target '{stepId}'");
            var gcsBucket = ObjectStorageConnectionSupport.GetGcsBucket(options.Bucket, $"target '{stepId}'");
            GoogleObject? existing = null;
            try
            {
                existing = await gcsClient.GetObjectAsync(
                    gcsBucket,
                    objectKey,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Google.GoogleApiException exception) when (
                exception.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
            }
            if (existing is not null)
            {
                string? existingHash = null;
                existing.Metadata?.TryGetValue(
                    ObjectStorageConnectorSupport.HashMetadataKey,
                    out existingHash);
                if (string.Equals(existingHash, hash, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                throw new InvalidDataException(
                    $"Google Cloud Storage target '{objectKey}' already exists with different content.");
            }

            await using var stream = File.OpenRead(stagingPath);
            var upload = new GoogleObject
            {
                Bucket = gcsBucket,
                Name = objectKey,
                ContentType = contentType,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [ObjectStorageConnectorSupport.HashMetadataKey] = hash
                }
            };
            try
            {
                await gcsClient.UploadObjectAsync(
                    upload,
                    stream,
                    new UploadObjectOptions { IfGenerationMatch = 0 },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Google.GoogleApiException exception) when (
                exception.HttpStatusCode is System.Net.HttpStatusCode.Conflict or
                    System.Net.HttpStatusCode.PreconditionFailed)
            {
                var concurrent = await gcsClient.GetObjectAsync(
                    gcsBucket,
                    objectKey,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                string? concurrentHash = null;
                concurrent.Metadata?.TryGetValue(
                    ObjectStorageConnectorSupport.HashMetadataKey,
                    out concurrentHash);
                if (!string.Equals(concurrentHash, hash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Google Cloud Storage target '{objectKey}' was created concurrently with different content.",
                        exception);
                }
            }
            return;
        }

        if (connectorType == "adlsgen2")
        {
            var fileSystem = DataLakeConnectionSupport.GetFileSystemClient(
                connection,
                options.FileSystem ?? options.Container,
                $"target '{stepId}'");
            var file = fileSystem.GetFileClient(objectKey);
            if (await file.ExistsAsync(cancellationToken).ConfigureAwait(false))
            {
                var properties = await file.GetPropertiesAsync(
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                properties.Value.Metadata.TryGetValue(
                    ObjectStorageConnectorSupport.HashMetadataKey,
                    out var existingHash);
                if (string.Equals(existingHash, hash, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                throw new InvalidDataException(
                    $"Azure Data Lake Storage Gen2 target '{objectKey}' already exists with different content.");
            }

            await using var stream = File.OpenRead(stagingPath);
            try
            {
                await file.UploadAsync(
                    stream,
                    new DataLakeFileUploadOptions
                    {
                        HttpHeaders = new PathHttpHeaders { ContentType = contentType },
                        Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            [ObjectStorageConnectorSupport.HashMetadataKey] = hash
                        },
                        Conditions = new DataLakeRequestConditions
                        {
                            IfNoneMatch = new Azure.ETag("*")
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Azure.RequestFailedException exception) when (
                exception.Status is 409 or 412)
            {
                var properties = await file.GetPropertiesAsync(
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                properties.Value.Metadata.TryGetValue(
                    ObjectStorageConnectorSupport.HashMetadataKey,
                    out var existingHash);
                if (!string.Equals(existingHash, hash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Azure Data Lake Storage Gen2 target '{objectKey}' was created concurrently with different content.",
                        exception);
                }
            }
            return;
        }

        var client = ObjectStorageConnectionSupport.GetS3Client(connection, $"target '{stepId}'");
        var bucket = ObjectStorageConnectionSupport.GetS3Bucket(options.Bucket, $"target '{stepId}'");
        try
        {
            var metadata = await client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest
                {
                    BucketName = bucket,
                    Key = objectKey
                },
                cancellationToken).ConfigureAwait(false);
            var existingHash = metadata.Metadata.Keys
                .Where(key => string.Equals(
                    key,
                    ObjectStorageConnectorSupport.HashMetadataKey,
                    StringComparison.OrdinalIgnoreCase))
                .Select(key => metadata.Metadata[key])
                .FirstOrDefault();
            if (string.Equals(existingHash, hash, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            throw new InvalidDataException(
                $"S3 target '{objectKey}' already exists with different content.");
        }
        catch (AmazonS3Exception exception) when (
            exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
        }

        var request = new PutObjectRequest
        {
            BucketName = bucket,
            Key = objectKey,
            FilePath = stagingPath,
            ContentType = contentType,
            IfNoneMatch = "*"
        };
        request.Metadata.Add(ObjectStorageConnectorSupport.HashMetadataKey, hash);
        try
        {
            await client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception exception) when (
            exception.StatusCode is System.Net.HttpStatusCode.Conflict or
                System.Net.HttpStatusCode.PreconditionFailed)
        {
            var concurrent = await client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest
                {
                    BucketName = bucket,
                    Key = objectKey
                },
                cancellationToken).ConfigureAwait(false);
            var concurrentHash = concurrent.Metadata.Keys
                .Where(key => string.Equals(
                    key,
                    ObjectStorageConnectorSupport.HashMetadataKey,
                    StringComparison.OrdinalIgnoreCase))
                .Select(key => concurrent.Metadata[key])
                .FirstOrDefault();
            if (!string.Equals(concurrentHash, hash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"S3 target '{objectKey}' was created concurrently with different content.",
                    exception);
            }
        }
    }

    public static string GetStagingPath(
        string stagingRoot,
        string runId,
        string stepId,
        string formatType,
        IReadOnlyList<string>? formatExtensions = null)
    {
        var safeStepId = new string(stepId
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-')
            .ToArray());
        if (string.IsNullOrWhiteSpace(safeStepId))
        {
            safeStepId = "step";
        }
        var extension = ObjectStorageConnectorSupport.GetFileExtension(formatType, formatExtensions);
        return Path.Combine(
            Path.GetFullPath(stagingRoot),
            "object-targets",
            runId,
            $"{safeStepId}{extension}");
    }

    private static ObjectStorageTargetOptions DeserializeTargetOptions(
        string optionsJson,
        string stepId)
    {
        try
        {
            return JsonSerializer.Deserialize<ObjectStorageTargetOptions>(
                       optionsJson,
                       ObjectStorageConnectorSupport.JsonOptions)
                ?? throw new InvalidDataException(
                    $"Object storage target options for '{stepId}' are empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Object storage target options for '{stepId}' are invalid JSON.",
                exception);
        }
    }

    private static T DeserializeFormatOptions<T>(string optionsJson, string stepId)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(
                       optionsJson,
                       ObjectStorageConnectorSupport.JsonOptions)
                ?? throw new InvalidDataException(
                    $"File format options for target '{stepId}' are empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"File format options for target '{stepId}' are invalid JSON.",
                exception);
        }
    }

    private static async Task<string> ComputeHashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}

internal static class ObjectStorageConnectorSupport
{
    public const string HashMetadataKey = "migration4dataverse-sha256";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool MatchesFormat(
        string key,
        string formatType,
        bool includeAllExtensions,
        IReadOnlyList<string>? customExtensions = null)
    {
        var normalizedFormat = formatType.Trim().ToLowerInvariant();
        if (normalizedFormat == "binary")
        {
            return !key.EndsWith("/", StringComparison.Ordinal);
        }
        if (includeAllExtensions)
        {
            return true;
        }
        var extensions = string.Equals(normalizedFormat, "custom", StringComparison.Ordinal)
            ? customExtensions ?? Array.Empty<string>()
            : RecordFileFormatRegistry.GetRequired(normalizedFormat).Extensions;
        return extensions.Any(extension =>
            key.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    public static string GetFileExtension(
        string formatType,
        IReadOnlyList<string>? customExtensions = null)
    {
        return RecordFileFormatRegistry.GetFileExtension(formatType, customExtensions);
    }

    public static string GetObjectFileName(string key)
    {
        var separator = key.LastIndexOf('/');
        return separator >= 0 ? key[(separator + 1)..] : key;
    }
}
