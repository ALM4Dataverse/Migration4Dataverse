using System.Runtime.CompilerServices;
using System.Text.Json;
using Migration4Dataverse.Engine.Scheduling;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.PowerShell;

internal sealed class IndexedStreamingJoinBatchSource
{
    private readonly SqliteMigrationStore _store;
    private readonly StepCompletionCoordinator _completionCoordinator;
    private readonly string _joinStepId;
    private readonly string _childInputStepId;
    private readonly string _parentInputStepId;
    private readonly string _childAlias;
    private readonly string _parentAlias;
    private readonly string _joinType;
    private readonly int _batchSize;
    private readonly bool _preserveInputOrder;

    public IndexedStreamingJoinBatchSource(
        SqliteMigrationStore store,
        StepCompletionCoordinator completionCoordinator,
        string joinStepId,
        string childInputStepId,
        string parentInputStepId,
        string childAlias,
        string parentAlias,
        string joinType,
        int batchSize,
        bool preserveInputOrder = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(completionCoordinator);
        ArgumentException.ThrowIfNullOrWhiteSpace(joinStepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(childInputStepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentInputStepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(childAlias);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentAlias);
        ArgumentException.ThrowIfNullOrWhiteSpace(joinType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        _store = store;
        _completionCoordinator = completionCoordinator;
        _joinStepId = joinStepId;
        _childInputStepId = childInputStepId;
        _parentInputStepId = parentInputStepId;
        _childAlias = childAlias;
        _parentAlias = parentAlias;
        _joinType = joinType;
        _batchSize = batchSize;
        _preserveInputOrder = preserveInputOrder;
    }

    public Func<MigrationStepCheckpoint, CancellationToken, IAsyncEnumerable<MigrationWorkBatch>> CreateReader()
    {
        return ReadBatchesAsync;
    }

    private async IAsyncEnumerable<MigrationWorkBatch> ReadBatchesAsync(
        MigrationStepCheckpoint checkpoint,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cursor = checkpoint.LastCompletedSequence >= 0
            ? JsonSerializer.Deserialize<JoinCursor>(checkpoint.CursorJson
                ?? throw new InvalidDataException(
                    $"Join step '{_joinStepId}' checkpoint is missing its input cursor."))
                ?? throw new InvalidDataException($"Join step '{_joinStepId}' has an invalid input cursor.")
            : new JoinCursor(0, 0);

        var batches = _preserveInputOrder
            ? ReadStableBatchesAsync(checkpoint, cursor, cancellationToken)
            : ReadReadyMatchesAsync(checkpoint, cursor, cancellationToken);
        await foreach (var batch in batches.ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    private async IAsyncEnumerable<MigrationWorkBatch> ReadStableBatchesAsync(
        MigrationStepCheckpoint checkpoint,
        JoinCursor cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long afterSequence = 0;
        var afterRecordIndex = 0;
        var consumedRows = new List<JoinInputRecord>(_batchSize);
        var outputRecords = new List<Dictionary<string, JsonElement?>>(_batchSize);
        var lineageByRecord = new List<MigrationDebugParentRecordReference[]>(_batchSize);
        var prerequisiteBatchIds = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var childThroughSequence = _store.GetStepCheckpoint(
                checkpoint.RunId,
                _childInputStepId).LastCompletedSequence;
            var rows = _store.ReadJoinInputRecordPage(
                checkpoint.RunId,
                _joinStepId,
                _childInputStepId,
                afterSequence,
                afterRecordIndex,
                _batchSize,
                childThroughSequence);
            if (rows.Count == 0)
            {
                if (_completionCoordinator.IsStepCompleted(checkpoint.RunId, _childInputStepId))
                {
                    break;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                continue;
            }

            foreach (var childRecord in rows)
            {
                var childCursor = new JoinCursor(childRecord.Sequence, childRecord.RecordIndex);
                if (CompareCursor(childCursor, cursor) <= 0)
                {
                    continue;
                }

                var parentRecord = childRecord.KeyJson is null
                    ? null
                    : await FindParentRecordAsync(
                        checkpoint.RunId,
                        childRecord.KeyJson,
                        cancellationToken).ConfigureAwait(false);
                consumedRows.Add(childRecord);
                prerequisiteBatchIds.Add(childRecord.OriginBatchId);

                if (parentRecord is not null)
                {
                    AddMatchedRecord(childRecord, parentRecord, outputRecords, lineageByRecord);
                    prerequisiteBatchIds.Add(parentRecord.OriginBatchId);
                }
                else if (_joinType == "left")
                {
                    AddUnmatchedRecord(childRecord, outputRecords, lineageByRecord);
                }

                cursor = childCursor;
                if (consumedRows.Count == _batchSize)
                {
                    yield return CreateBatch(
                        consumedRows,
                        outputRecords,
                        lineageByRecord,
                        prerequisiteBatchIds);
                    consumedRows = new List<JoinInputRecord>(_batchSize);
                    outputRecords = new List<Dictionary<string, JsonElement?>>(_batchSize);
                    lineageByRecord = new List<MigrationDebugParentRecordReference[]>(_batchSize);
                    prerequisiteBatchIds = new HashSet<string>(StringComparer.Ordinal);
                }
            }

            var last = rows[^1];
            afterSequence = last.Sequence;
            afterRecordIndex = last.RecordIndex;
        }

        if (consumedRows.Count > 0)
        {
            yield return CreateBatch(
                consumedRows,
                outputRecords,
                lineageByRecord,
                prerequisiteBatchIds);
        }
    }

    private async IAsyncEnumerable<MigrationWorkBatch> ReadReadyMatchesAsync(
        MigrationStepCheckpoint checkpoint,
        JoinCursor cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var scanCursor = new JoinInputPosition(cursor.Sequence, cursor.RecordIndex);
        var pendingRows = (cursor.Pending ?? Array.Empty<JoinInputPosition>())
            .Select(position => _store.GetJoinInputRecord(
                    checkpoint.RunId,
                    _joinStepId,
                    _childInputStepId,
                    position.Sequence,
                    position.RecordIndex)
                ?? throw new InvalidDataException(
                    $"Join step '{_joinStepId}' checkpoint references a missing pending input row."))
            .ToList();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var consumedRows = new List<JoinInputRecord>(_batchSize);
            var outputRecords = new List<Dictionary<string, JsonElement?>>(_batchSize);
            var lineageByRecord = new List<MigrationDebugParentRecordReference[]>(_batchSize);
            var prerequisiteBatchIds = new HashSet<string>(StringComparer.Ordinal);
            var parentCompleted = _completionCoordinator.IsStepCompleted(checkpoint.RunId, _parentInputStepId);

            for (var pendingIndex = 0;
                 pendingIndex < pendingRows.Count && consumedRows.Count < _batchSize;)
            {
                var childRecord = pendingRows[pendingIndex];
                var parentRecord = childRecord.KeyJson is null
                    ? null
                    : _store.FindJoinInputRecordByKey(
                        checkpoint.RunId,
                        _joinStepId,
                        _parentInputStepId,
                        childRecord.KeyJson,
                        long.MaxValue);
                if (parentRecord is null && !parentCompleted)
                {
                    pendingIndex++;
                    continue;
                }

                consumedRows.Add(childRecord);
                prerequisiteBatchIds.Add(childRecord.OriginBatchId);
                if (parentRecord is not null)
                {
                    AddMatchedRecord(childRecord, parentRecord, outputRecords, lineageByRecord);
                    prerequisiteBatchIds.Add(parentRecord.OriginBatchId);
                }
                else if (_joinType == "left")
                {
                    AddUnmatchedRecord(childRecord, outputRecords, lineageByRecord);
                }

                pendingRows.RemoveAt(pendingIndex);
            }

            var childThroughSequence = _store.GetStepCheckpoint(
                checkpoint.RunId,
                _childInputStepId).LastCompletedSequence;
            IReadOnlyList<JoinInputRecord> rows = Array.Empty<JoinInputRecord>();
            if (consumedRows.Count < _batchSize)
            {
                rows = _store.ReadJoinInputRecordPage(
                    checkpoint.RunId,
                    _joinStepId,
                    _childInputStepId,
                    scanCursor.Sequence,
                    scanCursor.RecordIndex,
                    _batchSize,
                    childThroughSequence);
            }

            foreach (var childRecord in rows)
            {
                if (consumedRows.Count >= _batchSize)
                {
                    break;
                }

                scanCursor = new JoinInputPosition(childRecord.Sequence, childRecord.RecordIndex);
                var parentRecord = childRecord.KeyJson is null
                    ? null
                    : _store.FindJoinInputRecordByKey(
                        checkpoint.RunId,
                        _joinStepId,
                        _parentInputStepId,
                        childRecord.KeyJson,
                        long.MaxValue);
                if (parentRecord is not null)
                {
                    consumedRows.Add(childRecord);
                    prerequisiteBatchIds.Add(childRecord.OriginBatchId);
                    AddMatchedRecord(childRecord, parentRecord, outputRecords, lineageByRecord);
                    prerequisiteBatchIds.Add(parentRecord.OriginBatchId);
                }
                else if (childRecord.KeyJson is null || parentCompleted)
                {
                    consumedRows.Add(childRecord);
                    prerequisiteBatchIds.Add(childRecord.OriginBatchId);
                    if (_joinType == "left")
                    {
                        AddUnmatchedRecord(childRecord, outputRecords, lineageByRecord);
                    }
                }
                else
                {
                    pendingRows.Add(childRecord);
                }
            }

            if (consumedRows.Count > 0)
            {
                yield return CreateBatch(
                    consumedRows,
                    outputRecords,
                    lineageByRecord,
                    prerequisiteBatchIds,
                    new JoinCursor(
                        scanCursor.Sequence,
                        scanCursor.RecordIndex,
                        pendingRows.Select(row => new JoinInputPosition(row.Sequence, row.RecordIndex)).ToArray()));
                continue;
            }

            if (rows.Count == 0)
            {
                var childCompleted = _completionCoordinator.IsStepCompleted(checkpoint.RunId, _childInputStepId);
                if (childCompleted && parentCompleted && pendingRows.Count == 0)
                {
                    yield break;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<JoinInputRecord?> FindParentRecordAsync(
        string runId,
        string keyJson,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parent = _store.FindJoinInputRecordByKey(
                runId,
                _joinStepId,
                _parentInputStepId,
                keyJson,
                long.MaxValue);
            if (parent is not null || _completionCoordinator.IsStepCompleted(runId, _parentInputStepId))
            {
                return parent;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }

    private MigrationWorkBatch CreateBatch(
        IReadOnlyList<JoinInputRecord> consumedRows,
        IReadOnlyList<Dictionary<string, JsonElement?>> outputRecords,
        IReadOnlyList<MigrationDebugParentRecordReference[]> lineageByRecord,
        IEnumerable<string> prerequisiteBatchIds,
        JoinCursor? cursor = null)
    {
        var lastRow = consumedRows[^1];
        var payload = JsonSerializer.Serialize(new
        {
            Records = outputRecords,
            __LineageReferences = lineageByRecord
        });
        return new MigrationWorkBatch(
            payload,
            JsonSerializer.Serialize(cursor ?? new JoinCursor(lastRow.Sequence, lastRow.RecordIndex)))
        {
            ParentBatchIds = prerequisiteBatchIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            FilteredRows = consumedRows.Count - outputRecords.Count
        };
    }

    private void AddMatchedRecord(
        JoinInputRecord childRecord,
        JoinInputRecord parentRecord,
        ICollection<Dictionary<string, JsonElement?>> outputRecords,
        ICollection<MigrationDebugParentRecordReference[]> lineageByRecord)
    {
        using var childDocument = JsonDocument.Parse(childRecord.RecordJson);
        using var parentDocument = JsonDocument.Parse(parentRecord.RecordJson);
        outputRecords.Add(new Dictionary<string, JsonElement?>(StringComparer.OrdinalIgnoreCase)
        {
            [_childAlias] = childDocument.RootElement.Clone(),
            [_parentAlias] = parentDocument.RootElement.Clone()
        });
        lineageByRecord.Add(
            [new MigrationDebugParentRecordReference(
                childRecord.OriginBatchId,
                childRecord.OriginRecordIndex)]);
    }

    private void AddUnmatchedRecord(
        JoinInputRecord childRecord,
        ICollection<Dictionary<string, JsonElement?>> outputRecords,
        ICollection<MigrationDebugParentRecordReference[]> lineageByRecord)
    {
        using var childDocument = JsonDocument.Parse(childRecord.RecordJson);
        outputRecords.Add(new Dictionary<string, JsonElement?>(StringComparer.OrdinalIgnoreCase)
        {
            [_childAlias] = childDocument.RootElement.Clone(),
            [_parentAlias] = null
        });
        lineageByRecord.Add(
            [new MigrationDebugParentRecordReference(
                childRecord.OriginBatchId,
                childRecord.OriginRecordIndex)]);
    }

    private static int CompareCursor(JoinCursor left, JoinCursor right)
    {
        var sequenceComparison = left.Sequence.CompareTo(right.Sequence);
        return sequenceComparison != 0
            ? sequenceComparison
            : left.RecordIndex.CompareTo(right.RecordIndex);
    }

    private sealed record JoinCursor(
        long Sequence,
        int RecordIndex,
        JoinInputPosition[]? Pending = null);

    private sealed record JoinInputPosition(long Sequence, int RecordIndex);
}
