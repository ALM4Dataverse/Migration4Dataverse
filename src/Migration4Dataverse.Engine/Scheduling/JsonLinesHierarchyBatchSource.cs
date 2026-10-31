using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.Scheduling;

public interface IHierarchyRecordReader : IAsyncDisposable
{
    long RecordCount { get; }

    ValueTask<JsonElement?> PeekAsync(CancellationToken cancellationToken);

    JsonElement Take();
}

public interface IHierarchyRecordCheckpointReader
{
    string CreateCheckpointJson(string lastKey);
}

public sealed class JsonLinesHierarchyLevel
{
    public JsonLinesHierarchyLevel(
        string jobName,
        string path,
        string keyProperty,
        string? parentKeyProperty,
        int rowsPerParent,
        long expectedRecordCount,
        IEnumerable<JsonLinesHierarchyLevel>? children = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyProperty);
        ArgumentOutOfRangeException.ThrowIfNegative(rowsPerParent);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRecordCount);

        StepId = jobName;
        Path = System.IO.Path.GetFullPath(path);
        KeyProperty = keyProperty;
        ParentKeyProperty = string.IsNullOrWhiteSpace(parentKeyProperty) ? null : parentKeyProperty;
        RowsPerParent = rowsPerParent;
        ExpectedRecordCount = expectedRecordCount;
        Children = (children ?? Array.Empty<JsonLinesHierarchyLevel>()).ToArray();

        if (ParentKeyProperty is null && RowsPerParent != 0)
        {
            throw new ArgumentException("A root source must use zero rows per parent.", nameof(rowsPerParent));
        }
    }

    public string StepId { get; }

    public string Path { get; }

    public string KeyProperty { get; }

    public string? ParentKeyProperty { get; }

    public int RowsPerParent { get; }

    public long ExpectedRecordCount { get; }

    public IReadOnlyList<JsonLinesHierarchyLevel> Children { get; }
}

public sealed record JsonLinesHierarchyRecord(
    JsonElement Source,
    IReadOnlyDictionary<string, IReadOnlyList<JsonLinesHierarchyRecord>> Children);

public class HierarchyBatchSource
{
    private readonly HierarchySourceLevel _root;
    private readonly int _batchSize;
    private readonly IReadOnlyDictionary<string, HierarchySourceLevel> _levels;
    private readonly Func<HierarchySourceLevel, IHierarchyRecordReader> _readerFactory;
    private readonly Func<HierarchySourceLevel, string?, IHierarchyRecordReader>? _checkpointReaderFactory;

    protected HierarchyBatchSource(JsonLinesHierarchyLevel root, int batchSize)
        : this(ConvertLevel(root), batchSize, level => new JsonLinesReader(level))
    {
    }

    public HierarchyBatchSource(
        HierarchySourceLevel root,
        int batchSize,
        Func<HierarchySourceLevel, IHierarchyRecordReader> readerFactory,
        Func<HierarchySourceLevel, string?, IHierarchyRecordReader>? checkpointReaderFactory = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(readerFactory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        if (root.ParentKeyProperty is not null)
        {
            throw new ArgumentException("The hierarchy root must not have a parent key.", nameof(root));
        }

        _root = root;
        _batchSize = batchSize;
        _readerFactory = readerFactory;
        _checkpointReaderFactory = checkpointReaderFactory;
        _levels = ValidateHierarchy(root);
    }

    public Func<MigrationStepCheckpoint, CancellationToken, IAsyncEnumerable<MigrationWorkBatch>> CreateReader()
    {
        return ReadBatchesAsync;
    }

    public static IHierarchyRecordReader CreateJsonLinesReader(HierarchySourceLevel level)
    {
        ArgumentNullException.ThrowIfNull(level);
        if (level.FormatType != "jsonl")
        {
            throw new ArgumentException("A JSON Lines reader requires the JSON Lines format.", nameof(level));
        }

        return new JsonLinesReader(level);
    }

    public async IAsyncEnumerable<MigrationWorkBatch> ReadBatchesAsync(
        MigrationStepCheckpoint checkpoint,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (!string.Equals(checkpoint.StepId, _root.StepId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Checkpoint table '{checkpoint.StepId}' does not match root job '{_root.StepId}'.",
                nameof(checkpoint));
        }

        var cursor = ReadCursor(checkpoint);
        var readers = new Dictionary<string, IHierarchyRecordReader>(StringComparer.Ordinal);
        try
        {
            foreach (var level in _levels.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reader = level.StepId == _root.StepId && _checkpointReaderFactory is not null
                    ? _checkpointReaderFactory(level, checkpoint.ResumeCursorJson)
                    : _readerFactory(level);
                readers.Add(level.StepId, reader);
            }

            var rootReader = readers[_root.StepId];
            var resumesCompositeCursor = rootReader is IHierarchyRecordCheckpointReader;
            var previousRootKey = resumesCompositeCursor ? cursor : null;
            var cursorFound = cursor is null || resumesCompositeCursor;
            var batch = new List<JsonLinesHierarchyRecord>(_batchSize);
            var previousChildOrder = new Dictionary<string, (string ParentKey, string Key)>(
                StringComparer.Ordinal);
            string? lastKeyInBatch = null;

            while (await rootReader.PeekAsync(cancellationToken).ConfigureAwait(false) is JsonElement rootRow)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rootReader.Take();
                var rootKey = GetRequiredKey(rootRow, _root.KeyProperty, _root.StepId);
                if (previousRootKey is not null &&
                    string.Compare(previousRootKey, rootKey, _root.KeyComparison) >= 0)
                {
                    throw new InvalidDataException(
                        $"Root source for job '{_root.StepId}' must be strictly ordered by '{_root.KeyProperty}'.");
                }
                previousRootKey = rootKey;

                var includeRecord = true;
                if (!cursorFound)
                {
                    var comparison = string.Compare(rootKey, cursor, _root.KeyComparison);
                    if (comparison < 0)
                    {
                        includeRecord = false;
                    }
                    else if (comparison == 0)
                    {
                        cursorFound = true;
                        includeRecord = false;
                    }
                    else
                    {
                        throw new InvalidDataException(
                            $"Checkpoint key '{cursor}' is missing from root source for job '{_root.StepId}'.");
                    }
                }

                var children = await ReadChildrenAsync(
                        _root,
                        rootRow,
                        readers,
                        previousChildOrder,
                        includeRecord,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!includeRecord)
                {
                    continue;
                }

                batch.Add(new JsonLinesHierarchyRecord(rootRow, children));
                lastKeyInBatch = rootKey;
                if (batch.Count == _batchSize)
                {
                    yield return CreateWorkBatch(batch, lastKeyInBatch, rootReader);
                    batch.Clear();
                    lastKeyInBatch = null;
                }
            }

            if (!cursorFound)
            {
                throw new InvalidDataException(
                    $"Checkpoint key '{cursor}' is missing from root source for job '{_root.StepId}'.");
            }

            foreach (var level in _levels.Values)
            {
                var reader = readers[level.StepId];
                if (await reader.PeekAsync(cancellationToken).ConfigureAwait(false) is not null)
                {
                    throw new InvalidDataException(
                        $"Source for job '{level.StepId}' contains rows that are not linked to a parent record.");
                }
                if (level.HasExpectedRecordCount &&
                    reader.RecordCount != level.ExpectedRecordCount)
                {
                    throw new InvalidDataException(
                        $"Source for job '{level.StepId}' contains {reader.RecordCount} rows; " +
                        $"the job definition declares {level.ExpectedRecordCount}.");
                }
            }

            if (batch.Count > 0)
            {
                yield return CreateWorkBatch(
                    batch,
                    lastKeyInBatch ?? throw new InvalidOperationException(
                        "A non-empty source batch has no cursor key."),
                    rootReader);
            }
        }
        finally
        {
            foreach (var reader in readers.Values)
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static string? ReadCursor(MigrationStepCheckpoint checkpoint)
    {
        if (checkpoint.ResumeSequence < 0)
        {
            if (checkpoint.ResumeCursorJson is not null)
            {
                throw new InvalidDataException("An initial table checkpoint must not contain a cursor.");
            }

            return null;
        }

        if (string.IsNullOrWhiteSpace(checkpoint.ResumeCursorJson))
        {
            throw new InvalidDataException(
                $"Checkpoint sequence {checkpoint.ResumeSequence} has no source cursor.");
        }

        using var cursor = JsonDocument.Parse(checkpoint.ResumeCursorJson);
        if (cursor.RootElement.ValueKind != JsonValueKind.Object ||
            !cursor.RootElement.TryGetProperty("LastKey", out var keyElement) ||
            keyElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(keyElement.GetString()))
        {
            throw new InvalidDataException("The hierarchy source checkpoint has no valid LastKey value.");
        }

        return keyElement.GetString();
    }

    private static MigrationWorkBatch CreateWorkBatch(
        IReadOnlyList<JsonLinesHierarchyRecord> records,
        string lastKey,
        IHierarchyRecordReader rootReader)
    {
        return new MigrationWorkBatch(
            JsonSerializer.Serialize(new { Records = records }),
            rootReader is IHierarchyRecordCheckpointReader checkpointReader
                ? checkpointReader.CreateCheckpointJson(lastKey)
                : JsonSerializer.Serialize(new { LastKey = lastKey }));
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<JsonLinesHierarchyRecord>>> ReadChildrenAsync(
        HierarchySourceLevel parentLevel,
        JsonElement parentRecord,
        IReadOnlyDictionary<string, IHierarchyRecordReader> readers,
        IDictionary<string, (string ParentKey, string Key)> previousChildOrder,
        bool includePayload,
        CancellationToken cancellationToken)
    {
        if (parentLevel.Children.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<JsonLinesHierarchyRecord>>(StringComparer.Ordinal);
        }

        var parentKey = GetRequiredKey(parentRecord, parentLevel.KeyProperty, parentLevel.StepId);
        var children = new Dictionary<string, IReadOnlyList<JsonLinesHierarchyRecord>>(
            parentLevel.Children.Count,
            StringComparer.Ordinal);

        foreach (var childLevel in parentLevel.Children)
        {
            var childReader = readers[childLevel.StepId];
            var childRecords = includePayload
                ? new List<JsonLinesHierarchyRecord>(childLevel.RowsPerParent)
                : null;
            var childCount = 0;

            while (await childReader.PeekAsync(cancellationToken).ConfigureAwait(false) is JsonElement childRow)
            {
                var childParentKey = GetRequiredKey(
                    childRow,
                    childLevel.ParentKeyProperty
                        ?? throw new InvalidOperationException(
                            $"Dependent source '{childLevel.StepId}' has no parent-key property."),
                    childLevel.StepId);
                var comparison = string.Compare(childParentKey, parentKey, childLevel.KeyComparison);
                if (comparison < 0)
                {
                    throw new InvalidDataException(
                        $"Source '{childLevel.Path}' is not ordered by '{childLevel.ParentKeyProperty}'; " +
                        $"found parent key '{childParentKey}' before '{parentKey}'.");
                }
                if (comparison > 0)
                {
                    break;
                }
                var childKey = GetRequiredKey(childRow, childLevel.KeyProperty, childLevel.StepId);
                if (previousChildOrder.TryGetValue(childLevel.StepId, out var previous) &&
                    (string.Compare(previous.ParentKey, childParentKey, childLevel.KeyComparison) > 0 ||
                     (string.Equals(previous.ParentKey, childParentKey, childLevel.KeyComparison) &&
                      string.Compare(previous.Key, childKey, childLevel.KeyComparison) >= 0)))
                {
                    throw new InvalidDataException(
                        $"Source '{childLevel.Path}' must be ordered by ascending " +
                        $"'{childLevel.ParentKeyProperty}' and then ascending '{childLevel.KeyProperty}'.");
                }
                if (childLevel.RowsPerParent > 0 && childCount == childLevel.RowsPerParent)
                {
                    throw new InvalidDataException(
                        $"Source '{childLevel.Path}' has more than {childLevel.RowsPerParent} " +
                        $"rows for parent '{parentKey}'.");
                }

                childReader.Take();
                previousChildOrder[childLevel.StepId] = (childParentKey, childKey);
                childCount++;
                var descendants = await ReadChildrenAsync(
                        childLevel,
                        childRow,
                        readers,
                        previousChildOrder,
                        includePayload,
                        cancellationToken)
                    .ConfigureAwait(false);
                childRecords?.Add(new JsonLinesHierarchyRecord(childRow, descendants));
            }

            if (childLevel.RowsPerParent > 0 && childCount != childLevel.RowsPerParent)
            {
                throw new InvalidDataException(
                    $"Parent '{parentKey}' has {childCount} rows in '{childLevel.StepId}'; " +
                    $"expected {childLevel.RowsPerParent}.");
            }

            if (childRecords is not null)
            {
                children.Add(childLevel.StepId, childRecords);
            }
        }

        return children;
    }

    private static string GetRequiredKey(JsonElement row, string propertyName, string jobName)
    {
        if (row.ValueKind != JsonValueKind.Object ||
            !row.TryGetProperty(propertyName, out var keyElement))
        {
            throw new InvalidDataException(
                $"A source row for job '{jobName}' is missing key property '{propertyName}'.");
        }

        var key = keyElement.ValueKind switch
        {
            JsonValueKind.String => keyElement.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => keyElement.ToString(),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidDataException(
                $"A source row for job '{jobName}' has an invalid key in property '{propertyName}'.");
        }

        return key;
    }

    private static IReadOnlyDictionary<string, HierarchySourceLevel> ValidateHierarchy(
        HierarchySourceLevel root)
    {
        var levels = new Dictionary<string, HierarchySourceLevel>(StringComparer.Ordinal);
        Visit(root, isRoot: true);
        return levels;

        void Visit(HierarchySourceLevel level, bool isRoot)
        {
            if (!levels.TryAdd(level.StepId, level))
            {
                throw new ArgumentException(
                    $"Source hierarchy contains duplicate job name '{level.StepId}'.",
                    nameof(root));
            }
            if (isRoot != (level.ParentKeyProperty is null))
            {
                throw new ArgumentException(
                    $"Source '{level.StepId}' has an invalid parent-key definition.",
                    nameof(root));
            }
            if (level.Type is "file" or "jsonl" or "csv" or "parquet" or "excel" && !File.Exists(level.Path))
            {
                throw new FileNotFoundException(
                    $"{level.FormatType} source for job '{level.StepId}' was not found.",
                    level.Path);
            }

            foreach (var child in level.Children)
            {
                if (child.ParentKeyProperty is null)
                {
                    throw new ArgumentException(
                        $"Dependent source '{child.StepId}' must declare its parent-key property.",
                        nameof(root));
                }
                Visit(child, isRoot: false);
            }
        }
    }

    private static HierarchySourceLevel ConvertLevel(JsonLinesHierarchyLevel level)
    {
        ArgumentNullException.ThrowIfNull(level);
        return new HierarchySourceLevel(
            level.StepId,
            "jsonl",
            level.Path,
            level.KeyProperty,
            level.ParentKeyProperty,
            level.RowsPerParent,
            level.ExpectedRecordCount,
            level.Children.Select(ConvertLevel));
    }

    private sealed class JsonLinesReader : IHierarchyRecordReader
    {
        private readonly HierarchySourceLevel _level;
        private readonly StreamReader _reader;
        private JsonElement? _lookahead;

        public JsonLinesReader(HierarchySourceLevel level)
        {
            _level = level;
            var stream = new FileStream(
                level.Path!,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            _reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 1024 * 1024);
        }

        public long RecordCount { get; private set; }

        public async ValueTask<JsonElement?> PeekAsync(CancellationToken cancellationToken)
        {
            if (_lookahead.HasValue)
            {
                return _lookahead.Value;
            }

            var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException(
                        $"Source '{_level.Path}' contains a JSON Lines record that is not an object.");
                }

                _lookahead = document.RootElement.Clone();
                RecordCount++;
                return _lookahead.Value;
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Source '{_level.Path}' contains invalid JSON at record {RecordCount + 1}.",
                    exception);
            }
        }

        public JsonElement Take()
        {
            var result = _lookahead
                ?? throw new InvalidOperationException($"Source '{_level.Path}' has no buffered record.");
            _lookahead = null;
            return result;
        }

        public ValueTask DisposeAsync()
        {
            _reader.Dispose();
            return ValueTask.CompletedTask;
        }
    }

}

public sealed class JsonLinesHierarchyBatchSource : HierarchyBatchSource
{
    public JsonLinesHierarchyBatchSource(JsonLinesHierarchyLevel root, int batchSize)
        : base(root, batchSize)
    {
    }
}
