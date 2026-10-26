using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.Scheduling;

public sealed record RowLinkedParentDependency(
    string StepId,
    string ParentKeyProperty,
    int RowsPerParent);

public sealed class RowLinkedSourceBatchSource
{
    private readonly IMigrationStore _store;
    private readonly SourceKeyCoordinator _sourceKeyCoordinator;
    private readonly HierarchySourceLevel _childLevel;
    private readonly IReadOnlyList<RowLinkedParentDependency> _parents;
    private readonly int _batchSize;
    private readonly Func<HierarchySourceLevel, IHierarchyRecordReader> _readerFactory;

    public RowLinkedSourceBatchSource(
        IMigrationStore store,
        SourceKeyCoordinator sourceKeyCoordinator,
        HierarchySourceLevel childLevel,
        IEnumerable<RowLinkedParentDependency> parents,
        int batchSize,
        Func<HierarchySourceLevel, IHierarchyRecordReader> readerFactory)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sourceKeyCoordinator);
        ArgumentNullException.ThrowIfNull(childLevel);
        ArgumentNullException.ThrowIfNull(parents);
        ArgumentNullException.ThrowIfNull(readerFactory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        _parents = parents.ToArray();
        if (_parents.Count == 0)
        {
            throw new ArgumentException("At least one row-linked parent is required.", nameof(parents));
        }

        _store = store;
        _sourceKeyCoordinator = sourceKeyCoordinator;
        _childLevel = childLevel;
        _batchSize = batchSize;
        _readerFactory = readerFactory;
    }

    public Func<MigrationStepCheckpoint, CancellationToken, IAsyncEnumerable<MigrationWorkBatch>> CreateReader()
    {
        return ReadBatchesAsync;
    }

    public async IAsyncEnumerable<MigrationWorkBatch> ReadBatchesAsync(
        MigrationStepCheckpoint checkpoint,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (!string.Equals(checkpoint.StepId, _childLevel.StepId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Checkpoint step '{checkpoint.StepId}' does not match child source '{_childLevel.StepId}'.",
                nameof(checkpoint));
        }

        var cursor = ReadCursor(checkpoint);
        var stagingPath = Path.Combine(
            Path.GetTempPath(),
            $"migration4dataverse-row-linked-{Guid.NewGuid():N}.db");
        try
        {
            await using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = stagingPath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Private
                }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await CreateStagingSchemaAsync(connection, cancellationToken).ConfigureAwait(false);

            var recordCount = await StageChildRowsAsync(connection, cancellationToken).ConfigureAwait(false);
            if (_childLevel.HasExpectedRecordCount &&
                recordCount != _childLevel.ExpectedRecordCount)
            {
                throw new InvalidDataException(
                    $"Source for job '{_childLevel.StepId}' contains {recordCount} rows; " +
                    $"the job definition declares {_childLevel.ExpectedRecordCount}.");
            }

            var cursorFound = cursor is null;
            var pendingRows = new List<JsonElement>(_batchSize);
            string[]? pendingParentBatchIds = null;
            string? lastKey = null;
            long lastOrdinal = -1;

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = BuildOrderedChildRowsQuery();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var ordinal = reader.GetInt64(0);
                    var childKey = reader.GetString(1);
                    if (!cursorFound)
                    {
                        if (ordinal == cursor!.Value.Ordinal)
                        {
                            if (!string.Equals(childKey, cursor.Value.Key, StringComparison.Ordinal))
                            {
                                throw new InvalidDataException(
                                    $"Checkpoint row for key '{cursor.Value.Key}' changed in source '{_childLevel.StepId}'.");
                            }
                            cursorFound = true;
                        }
                        continue;
                    }

                    using var childDocument = JsonDocument.Parse(reader.GetString(2));
                    var childRow = childDocument.RootElement.Clone();
                    var parentBatchIds = new List<string>(_parents.Count);
                    for (var parentIndex = 0; parentIndex < _parents.Count; parentIndex++)
                    {
                        var parentKey = GetOptionalParentKey(
                            childRow,
                            _parents[parentIndex].ParentKeyProperty,
                            _childLevel.StepId);
                        if (parentKey is null)
                        {
                            continue;
                        }

                        var parentBatch = await _sourceKeyCoordinator.WaitForSourceKeyAsync(
                                checkpoint.RunId,
                                _parents[parentIndex].StepId,
                                parentKey,
                                cancellationToken)
                            .ConfigureAwait(false);
                        parentBatchIds.Add(parentBatch.BatchId);
                    }

                    var requiredBatchIds = parentBatchIds
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(batchId => batchId, StringComparer.Ordinal)
                        .ToArray();
                    if (pendingRows.Count > 0 &&
                        (!requiredBatchIds.SequenceEqual(pendingParentBatchIds!, StringComparer.Ordinal) ||
                         pendingRows.Count == _batchSize))
                    {
                        yield return CreateWorkBatch(
                            pendingRows,
                            pendingParentBatchIds!,
                            lastKey!,
                            lastOrdinal);
                        pendingRows.Clear();
                        pendingParentBatchIds = null;
                    }

                    pendingParentBatchIds ??= requiredBatchIds;
                    pendingRows.Add(childRow);
                    lastKey = childKey;
                    lastOrdinal = ordinal;
                    if (pendingRows.Count == _batchSize)
                    {
                        yield return CreateWorkBatch(pendingRows, pendingParentBatchIds, lastKey, lastOrdinal);
                        pendingRows.Clear();
                        pendingParentBatchIds = null;
                    }
                }
            }

            if (!cursorFound)
            {
                throw new InvalidDataException(
                    $"Checkpoint key '{cursor}' is missing from source for job '{_childLevel.StepId}'.");
            }

            if (pendingRows.Count > 0)
            {
                yield return CreateWorkBatch(
                    pendingRows,
                    pendingParentBatchIds
                        ?? throw new InvalidOperationException("A non-empty row-linked batch has no prerequisites."),
                    lastKey ?? throw new InvalidOperationException("A non-empty row-linked batch has no cursor key."),
                    lastOrdinal);
            }

            await ValidateRowsPerParentAsync(connection, checkpoint.RunId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
        }
    }

    private async Task<long> StageChildRowsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        long recordCount = 0;
        await using var source = _readerFactory(_childLevel);
        while (await source.PeekAsync(cancellationToken).ConfigureAwait(false) is JsonElement childRow)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var childKey = GetRequiredKey(childRow, _childLevel.KeyProperty, _childLevel.StepId);

            var parentKeys = _parents
                .Select(parent => GetOptionalParentKey(childRow, parent.ParentKeyProperty, _childLevel.StepId))
                .ToArray();
            await InsertChildRowAsync(
                    connection,
                    recordCount,
                    childKey,
                    parentKeys,
                    childRow.GetRawText(),
                    cancellationToken)
                .ConfigureAwait(false);
            source.Take();
            recordCount++;
        }

        return recordCount;
    }

    private async Task CreateStagingSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var parentColumns = string.Join(
            ", ",
            Enumerable.Range(0, _parents.Count).Select(index => $"ParentKey{index} TEXT NULL"));
        var sql = $"""
            CREATE TABLE ChildRows (
                InputOrdinal INTEGER NOT NULL PRIMARY KEY CHECK (InputOrdinal >= 0),
                ChildKey TEXT NOT NULL COLLATE BINARY,
                HasNullParent INTEGER NOT NULL CHECK (HasNullParent IN (0, 1)),
                {parentColumns},
                RecordJson TEXT NOT NULL
            );
            CREATE TABLE ParentKeys (
                ParentIndex INTEGER NOT NULL,
                ParentKey TEXT NOT NULL COLLATE BINARY,
                PRIMARY KEY (ParentIndex, ParentKey)
            );
            """;
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        for (var index = 0; index < _parents.Count; index++)
        {
            await using var indexCommand = connection.CreateCommand();
            indexCommand.CommandText = $"""
                CREATE UNIQUE INDEX UX_ChildRows_Parent{index}Key
                ON ChildRows (ParentKey{index}, ChildKey)
                WHERE ParentKey{index} IS NOT NULL;
                """;
            await indexCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task InsertChildRowAsync(
        SqliteConnection connection,
        long inputOrdinal,
        string childKey,
        IReadOnlyList<string?> parentKeys,
        string recordJson,
        CancellationToken cancellationToken)
    {
        var columns = string.Join(", ", Enumerable.Range(0, _parents.Count).Select(index => $"ParentKey{index}"));
        var parameters = string.Join(", ", Enumerable.Range(0, _parents.Count).Select(index => $"$parentKey{index}"));
        var values = parentKeys.Any(key => key is null) ? 1 : 0;
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO ChildRows (InputOrdinal, ChildKey, HasNullParent, {columns}, RecordJson)
            VALUES ($inputOrdinal, $childKey, $hasNullParent, {parameters}, $recordJson);
            """;
        command.Parameters.AddWithValue("$inputOrdinal", inputOrdinal);
        command.Parameters.AddWithValue("$childKey", childKey);
        command.Parameters.AddWithValue("$hasNullParent", values);
        for (var index = 0; index < parentKeys.Count; index++)
        {
            command.Parameters.AddWithValue($"$parentKey{index}", parentKeys[index] ?? (object)DBNull.Value);
        }
        command.Parameters.AddWithValue("$recordJson", recordJson);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidDataException(
                $"Source for job '{_childLevel.StepId}' contains duplicate key '{childKey}' for a parent relationship.",
                exception);
        }
    }

    private string BuildOrderedChildRowsQuery()
    {
        var orderColumns = new List<string>
        {
            "HasNullParent DESC",
            "CASE WHEN HasNullParent = 1 THEN ChildKey END COLLATE BINARY"
        };
        orderColumns.AddRange(
            Enumerable.Range(0, _parents.Count).Select(index => $"ParentKey{index} COLLATE BINARY"));
        orderColumns.Add("ChildKey COLLATE BINARY");
        orderColumns.Add("InputOrdinal");
        return $"""
            SELECT InputOrdinal, ChildKey, RecordJson
            FROM ChildRows
            ORDER BY {string.Join(", ", orderColumns)};
            """;
    }

    private async Task ValidateRowsPerParentAsync(
        SqliteConnection connection,
        string runId,
        CancellationToken cancellationToken)
    {
        var fixedParents = Enumerable.Range(0, _parents.Count)
            .Where(index => _parents[index].RowsPerParent > 0)
            .ToArray();
        if (fixedParents.Length == 0)
        {
            return;
        }

        foreach (var dependency in fixedParents.Select(index => _parents[index]))
        {
            await _sourceKeyCoordinator.WaitForSourceCompletionAsync(
                    runId,
                    dependency.StepId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        for (var parentIndex = 0; parentIndex < _parents.Count; parentIndex++)
        {
            var expectedCount = _parents[parentIndex].RowsPerParent;
            if (expectedCount == 0)
            {
                continue;
            }

            foreach (var parent in _store.EnumerateSourceKeyBatchReferences(runId, _parents[parentIndex].StepId))
            {
                await InsertParentKeyAsync(connection, parentIndex, parent.SourceKey, cancellationToken)
                    .ConfigureAwait(false);
            }

            var parentKeyColumn = $"ParentKey{parentIndex}";
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT parent.ParentKey
                FROM ParentKeys AS parent
                LEFT JOIN ChildRows AS child ON child.{parentKeyColumn} = parent.ParentKey
                WHERE parent.ParentIndex = $parentIndex
                GROUP BY parent.ParentKey
                HAVING COUNT(child.ChildKey) <> $expectedCount
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$parentIndex", parentIndex);
            command.Parameters.AddWithValue("$expectedCount", expectedCount);
            var invalidParent = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (invalidParent is not null)
            {
                throw new InvalidDataException(
                    $"Parent '{invalidParent}' has an invalid number of rows in source '{_childLevel.StepId}'; " +
                    $"expected {expectedCount}.");
            }
        }
    }

    private static async Task InsertParentKeyAsync(
        SqliteConnection connection,
        int parentIndex,
        string parentKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ParentKeys (ParentIndex, ParentKey)
            VALUES ($parentIndex, $parentKey)
            ON CONFLICT (ParentIndex, ParentKey) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$parentIndex", parentIndex);
        command.Parameters.AddWithValue("$parentKey", parentKey);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private MigrationWorkBatch CreateWorkBatch(
        IReadOnlyList<JsonElement> rows,
        IReadOnlyList<string> parentBatchIds,
        string lastKey,
        long lastOrdinal)
    {
        var payload = JsonSerializer.Serialize(new
        {
            Records = rows.Select(row => new { Source = row })
        });
        return new MigrationWorkBatch(
            payload,
            JsonSerializer.Serialize(new { LastKey = lastKey, LastOrdinal = lastOrdinal }))
        {
            ParentBatchIds = parentBatchIds
        };
    }

    private static (string Key, long Ordinal)? ReadCursor(MigrationStepCheckpoint checkpoint)
    {
        if (checkpoint.ResumeSequence < 0)
        {
            if (checkpoint.ResumeCursorJson is not null)
            {
                throw new InvalidDataException("An initial source checkpoint must not contain a cursor.");
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
            string.IsNullOrWhiteSpace(keyElement.GetString()) ||
            !cursor.RootElement.TryGetProperty("LastOrdinal", out var ordinalElement) ||
            ordinalElement.ValueKind != JsonValueKind.Number ||
            !ordinalElement.TryGetInt64(out var ordinal) ||
            ordinal < 0)
        {
            throw new InvalidDataException("The row-linked source checkpoint has no valid row cursor.");
        }

        return (keyElement.GetString()!, ordinal);
    }

    private static string GetRequiredKey(JsonElement row, string propertyName, string stepId)
    {
        if (row.ValueKind != JsonValueKind.Object ||
            !row.TryGetProperty(propertyName, out var keyElement))
        {
            throw new InvalidDataException(
                $"A source row for job '{stepId}' is missing key property '{propertyName}'.");
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
                $"A source row for job '{stepId}' has an invalid key in property '{propertyName}'.");
        }

        return key;
    }

    private static string? GetOptionalParentKey(JsonElement row, string propertyName, string stepId)
    {
        if (row.ValueKind != JsonValueKind.Object ||
            !row.TryGetProperty(propertyName, out var keyElement))
        {
            throw new InvalidDataException(
                $"A source row for job '{stepId}' is missing parent-key property '{propertyName}'.");
        }
        if (keyElement.ValueKind == JsonValueKind.Null)
        {
            return null;
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
                $"A source row for job '{stepId}' has an invalid parent key in property '{propertyName}'.");
        }

        return key;
    }
}
