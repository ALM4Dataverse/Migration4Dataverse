using System.Text.Json;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.Connectors;

internal sealed class BinaryFileHierarchyRecordReader : IHierarchyRecordReader
{
    private readonly HierarchySourceLevel _level;
    private JsonElement? _record;
    private bool _consumed;
    private bool _disposed;

    public BinaryFileHierarchyRecordReader(HierarchySourceLevel level)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentException.ThrowIfNullOrWhiteSpace(level.Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(level.BinaryStagingRoot);
        if (!File.Exists(level.Path))
        {
            throw new FileNotFoundException(
                $"Binary file source '{level.StepId}' was not found.",
                level.Path);
        }
        _level = level;
    }

    public long RecordCount => _consumed ? 1 : 0;

    public async ValueTask<JsonElement?> PeekAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_consumed)
        {
            return null;
        }
        if (_record.HasValue)
        {
            return _record.Value;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var fileName = Path.GetFileName(_level.Path!);
        using var writer = MigrationBinaryValue.CreateWriter(
            _level.BinaryStagingRoot!,
            fileName,
            contentType: null);
        await using (var source = new FileStream(
                         _level.Path!,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         bufferSize: 1024 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await source.CopyToAsync(writer.Stream, cancellationToken).ConfigureAwait(false);
        }

        var value = writer.Complete();
        var contentType = value.DetectMimeType();
        _record = BinaryRecordJson.Create(
            _level.KeyProperty,
            fileName,
            fileName,
            contentType,
            value,
            _level.BinaryStagingRoot!);
        return _record.Value;
    }

    public JsonElement Take()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_consumed || !_record.HasValue)
        {
            throw new InvalidOperationException(
                $"Binary file source '{_level.StepId}' has no buffered record.");
        }
        _consumed = true;
        return _record.Value;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _record = null;
        return ValueTask.CompletedTask;
    }
}

internal sealed class SingleJsonHierarchyRecordReader(JsonElement record) : IHierarchyRecordReader
{
    private bool _consumed;
    private bool _disposed;

    public long RecordCount => _consumed ? 1 : 0;

    public ValueTask<JsonElement?> PeekAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<JsonElement?>(_consumed ? null : record);
    }

    public JsonElement Take()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_consumed)
        {
            throw new InvalidOperationException("The binary record has already been consumed.");
        }
        _consumed = true;
        return record;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}

internal static class BinaryRecordJson
{
    public static JsonElement Create(
        string keyProperty,
        string key,
        string? fileName,
        string? contentType,
        MigrationBinaryValue value,
        string stagingRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyProperty);
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [keyProperty] = key
        };
        record["Key"] = key;
        record["FileName"] = fileName;
        record["ContentType"] = contentType;
        record["Length"] = value.Length;
        record["Content"] = value;
        var transport = MigrationBinaryValue.PrepareForTransport(record, stagingRoot);
        return JsonSerializer.SerializeToElement(transport);
    }
}

public sealed class BinaryFileTargetOptions
{
    public string ContentColumn { get; init; } = "Content";

    public void Validate(string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ContentColumn);
    }
}
