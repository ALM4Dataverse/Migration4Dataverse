using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Migration4Dataverse.Engine.Transformations;

internal static class ExternalMergeSorter
{
    private const int RunHeaderLength = sizeof(long) + sizeof(int);

    public static async IAsyncEnumerable<JsonElement> SortAsync(
        IAsyncEnumerable<JsonElement> input,
        string nodeId,
        IReadOnlyList<DataFlowSortKey> keys,
        DataFlowSortOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(nodeId);

        var comparer = new SortRecordComparer(keys);
        var state = new SpillState(options);
        var records = new List<SortRecord>();
        var runs = new List<string>();
        var bufferedBytes = 0L;
        var sequence = 0L;
        string? workingDirectory = null;

        try
        {
            await foreach (var row in input.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = CreateRecord(row, sequence, nodeId, keys, options);
                sequence = checked(sequence + 1);
                if (record.EstimatedMemoryBytes > options.MemoryBudgetBytes)
                {
                    throw new InvalidDataException(
                        $"Sort node '{nodeId}' cannot buffer a record using {record.EstimatedMemoryBytes} bytes; " +
                        $"MemoryBudgetBytes is {options.MemoryBudgetBytes}.");
                }

                if (records.Count > 0 &&
                    (records.Count >= options.MaxInMemoryRows ||
                     bufferedBytes > options.MemoryBudgetBytes - record.EstimatedMemoryBytes))
                {
                    workingDirectory ??= CreateWorkingDirectory(options);
                    runs.Add(await WriteRunAsync(
                            records,
                            comparer,
                            state,
                            workingDirectory,
                            cancellationToken)
                        .ConfigureAwait(false));
                    records.Clear();
                    bufferedBytes = 0;
                }

                records.Add(record);
                bufferedBytes += record.EstimatedMemoryBytes;
                if (records.Count >= options.MaxInMemoryRows ||
                    bufferedBytes >= options.MemoryBudgetBytes)
                {
                    workingDirectory ??= CreateWorkingDirectory(options);
                    runs.Add(await WriteRunAsync(
                            records,
                            comparer,
                            state,
                            workingDirectory,
                            cancellationToken)
                        .ConfigureAwait(false));
                    records.Clear();
                    bufferedBytes = 0;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (runs.Count == 0)
            {
                records.Sort(comparer);
                foreach (var record in records)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return ParseRecord(record.Json);
                }
                yield break;
            }

            if (records.Count > 0)
            {
                workingDirectory ??= CreateWorkingDirectory(options);
                runs.Add(await WriteRunAsync(
                        records,
                        comparer,
                        state,
                        workingDirectory,
                        cancellationToken)
                    .ConfigureAwait(false));
                records.Clear();
            }

            while (runs.Count > 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mergedRuns = new List<string>((runs.Count + 1) / 2);
                for (var index = 0; index < runs.Count; index += 2)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (index + 1 == runs.Count)
                    {
                        mergedRuns.Add(runs[index]);
                        continue;
                    }

                    var leftPath = runs[index];
                    var rightPath = runs[index + 1];
                    var mergedPath = await MergeRunsAsync(
                            leftPath,
                            rightPath,
                            comparer,
                            state,
                            workingDirectory!,
                            nodeId,
                            cancellationToken)
                        .ConfigureAwait(false);
                    state.RemoveRun(leftPath);
                    state.RemoveRun(rightPath);
                    mergedRuns.Add(mergedPath);
                }
                runs = mergedRuns;
            }

            await using var finalRun = OpenRun(runs[0], FileAccess.Read);
            while (await ReadRunRecordAsync(finalRun, nodeId, keys, options, cancellationToken)
                       .ConfigureAwait(false) is SortRecord record)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ParseRecord(record.Json);
            }
        }
        finally
        {
            if (workingDirectory is not null && Directory.Exists(workingDirectory))
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
        }
    }

    private static SortRecord CreateRecord(
        JsonElement row,
        long sequence,
        string nodeId,
        IReadOnlyList<DataFlowSortKey> keys,
        DataFlowSortOptions options)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Sort node '{nodeId}' received a record that is not a JSON object.");
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(row);
        if (json.LongLength > options.MaxRecordBytes)
        {
            throw new InvalidDataException(
                $"Sort node '{nodeId}' received a {json.LongLength}-byte record, exceeding MaxRecordBytes " +
                $"({options.MaxRecordBytes}).");
        }

        var keyValues = DataFlowValueComparer.ReadKeys(row, keys, nodeId);
        return new SortRecord(
            sequence,
            json,
            keyValues,
            DataFlowValueComparer.EstimateRecordMemory(json, keyValues));
    }

    private static string CreateWorkingDirectory(DataFlowSortOptions options)
    {
        var parentDirectory = options.TemporaryDirectory is null
            ? Path.GetTempPath()
            : Path.GetFullPath(options.TemporaryDirectory);
        Directory.CreateDirectory(parentDirectory);
        var directory = Path.Combine(parentDirectory, $"migration4dataverse-sort-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task<string> WriteRunAsync(
        List<SortRecord> records,
        SortRecordComparer comparer,
        SpillState state,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        records.Sort(comparer);
        var path = state.CreateRunPath(workingDirectory);
        var completed = false;
        var outputBytes = 0L;
        try
        {
            await using (var stream = OpenRun(path, FileAccess.Write))
            {
                foreach (var record in records)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    outputBytes += await WriteRunRecordAsync(
                            stream,
                            record,
                            state,
                            outputBytes,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                await FlushRunAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            state.AddRun(outputBytes);
            completed = true;
            return path;
        }
        finally
        {
            if (!completed && File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task<string> MergeRunsAsync(
        string leftPath,
        string rightPath,
        SortRecordComparer comparer,
        SpillState state,
        string workingDirectory,
        string nodeId,
        CancellationToken cancellationToken)
    {
        var outputPath = state.CreateRunPath(workingDirectory);
        var completed = false;
        var outputBytes = 0L;
        try
        {
            await using (var leftStream = OpenRun(leftPath, FileAccess.Read))
            await using (var rightStream = OpenRun(rightPath, FileAccess.Read))
            await using (var outputStream = OpenRun(outputPath, FileAccess.Write))
            {
                var left = await ReadRunRecordAsync(
                        leftStream,
                        nodeId,
                        comparer.Keys,
                        state.Options,
                        cancellationToken)
                    .ConfigureAwait(false);
                var right = await ReadRunRecordAsync(
                        rightStream,
                        nodeId,
                        comparer.Keys,
                        state.Options,
                        cancellationToken)
                    .ConfigureAwait(false);
                EnsureMergeMemoryBudget(left, right, state.Options, nodeId);

                while (left is not null && right is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (comparer.Compare(left, right) <= 0)
                    {
                        outputBytes += await WriteRunRecordAsync(
                                outputStream,
                                left,
                                state,
                                outputBytes,
                                cancellationToken)
                            .ConfigureAwait(false);
                        left = await ReadRunRecordAsync(
                                leftStream,
                                nodeId,
                                comparer.Keys,
                                state.Options,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        outputBytes += await WriteRunRecordAsync(
                                outputStream,
                                right,
                                state,
                                outputBytes,
                                cancellationToken)
                            .ConfigureAwait(false);
                        right = await ReadRunRecordAsync(
                                rightStream,
                                nodeId,
                                comparer.Keys,
                                state.Options,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    EnsureMergeMemoryBudget(left, right, state.Options, nodeId);
                }

                while (left is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    outputBytes += await WriteRunRecordAsync(
                            outputStream,
                            left,
                            state,
                            outputBytes,
                            cancellationToken)
                        .ConfigureAwait(false);
                    left = await ReadRunRecordAsync(
                            leftStream,
                            nodeId,
                            comparer.Keys,
                            state.Options,
                            cancellationToken)
                        .ConfigureAwait(false);
                    EnsureMergeMemoryBudget(left, null, state.Options, nodeId);
                }

                while (right is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    outputBytes += await WriteRunRecordAsync(
                            outputStream,
                            right,
                            state,
                            outputBytes,
                            cancellationToken)
                        .ConfigureAwait(false);
                    right = await ReadRunRecordAsync(
                            rightStream,
                            nodeId,
                            comparer.Keys,
                            state.Options,
                            cancellationToken)
                        .ConfigureAwait(false);
                    EnsureMergeMemoryBudget(null, right, state.Options, nodeId);
                }

                await FlushRunAsync(outputStream, cancellationToken).ConfigureAwait(false);
            }

            state.AddRun(outputBytes);
            completed = true;
            return outputPath;
        }
        finally
        {
            if (!completed && File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    private static void EnsureMergeMemoryBudget(
        SortRecord? left,
        SortRecord? right,
        DataFlowSortOptions options,
        string nodeId)
    {
        var estimatedBytes = (left?.EstimatedMemoryBytes ?? 0) + (right?.EstimatedMemoryBytes ?? 0);
        if (estimatedBytes > options.MemoryBudgetBytes)
        {
            throw new InvalidDataException(
                $"Sort node '{nodeId}' needs {estimatedBytes} bytes for two merge heads, exceeding " +
                $"MemoryBudgetBytes ({options.MemoryBudgetBytes}). Increase the budget or lower MaxRecordBytes.");
        }
    }

    private static async Task<long> WriteRunRecordAsync(
        FileStream stream,
        SortRecord record,
        SpillState state,
        long pendingBytes,
        CancellationToken cancellationToken)
    {
        var recordBytes = checked((long)RunHeaderLength + record.Json.LongLength);
        state.EnsureSpillCapacity(pendingBytes, recordBytes);

        var header = new byte[RunHeaderLength];
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0, sizeof(long)), record.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(
            header.AsSpan(sizeof(long), sizeof(int)),
            record.Json.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(record.Json, cancellationToken).ConfigureAwait(false);
        return recordBytes;
    }

    private static async Task<SortRecord?> ReadRunRecordAsync(
        FileStream stream,
        string nodeId,
        IReadOnlyList<DataFlowSortKey> keys,
        DataFlowSortOptions options,
        CancellationToken cancellationToken)
    {
        var header = new byte[RunHeaderLength];
        var firstByteLength = await stream.ReadAsync(
                header.AsMemory(0, 1),
                cancellationToken)
            .ConfigureAwait(false);
        if (firstByteLength == 0)
        {
            return null;
        }

        await stream.ReadExactlyAsync(
                header.AsMemory(1),
                cancellationToken)
            .ConfigureAwait(false);
        var sequence = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0, sizeof(long)));
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(
            header.AsSpan(sizeof(long), sizeof(int)));
        if (payloadLength <= 0 || payloadLength > options.MaxRecordBytes)
        {
            throw new InvalidDataException(
                $"Sort node '{nodeId}' encountered a temporary run record with invalid length {payloadLength}.");
        }

        var json = GC.AllocateUninitializedArray<byte>(payloadLength);
        await stream.ReadExactlyAsync(json, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var row = document.RootElement;
        var keyValues = DataFlowValueComparer.ReadKeys(row, keys, nodeId);
        var estimatedBytes = DataFlowValueComparer.EstimateRecordMemory(json, keyValues);
        if (estimatedBytes > options.MemoryBudgetBytes)
        {
            throw new InvalidDataException(
                $"Sort node '{nodeId}' needs {estimatedBytes} bytes for a temporary run record, exceeding " +
                $"MemoryBudgetBytes ({options.MemoryBudgetBytes}).");
        }

        return new SortRecord(sequence, json, keyValues, estimatedBytes);
    }

    private static async Task FlushRunAsync(FileStream stream, CancellationToken cancellationToken)
    {
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static FileStream OpenRun(string path, FileAccess access)
    {
        return new FileStream(
            path,
            access == FileAccess.Read ? FileMode.Open : FileMode.CreateNew,
            access,
            FileShare.None,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static JsonElement ParseRecord(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed record SortRecord(
        long Sequence,
        byte[] Json,
        object?[] Keys,
        long EstimatedMemoryBytes);

    private sealed class SortRecordComparer(IReadOnlyList<DataFlowSortKey> keys) : IComparer<SortRecord>
    {
        public IReadOnlyList<DataFlowSortKey> Keys { get; } = keys;

        public int Compare(SortRecord? left, SortRecord? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }
            if (left is null)
            {
                return -1;
            }
            if (right is null)
            {
                return 1;
            }

            var keyComparison = DataFlowValueComparer.Compare(left.Keys, right.Keys, Keys);
            return keyComparison != 0 ? keyComparison : left.Sequence.CompareTo(right.Sequence);
        }
    }

    private sealed class SpillState(DataFlowSortOptions options)
    {
        public DataFlowSortOptions Options { get; } = options;

        public long LiveSpillBytes { get; private set; }

        private int NextRunNumber { get; set; }

        public string CreateRunPath(string workingDirectory)
        {
            var runNumber = checked(NextRunNumber++);
            return Path.Combine(workingDirectory, $"run-{runNumber:D8}.bin");
        }

        public void EnsureSpillCapacity(long pendingBytes, long recordBytes)
        {
            if (pendingBytes < 0 ||
                LiveSpillBytes > Options.MaxSpillBytes - pendingBytes ||
                recordBytes > Options.MaxSpillBytes - LiveSpillBytes - pendingBytes)
            {
                throw new InvalidDataException(
                    $"Sort node exceeded MaxSpillBytes ({Options.MaxSpillBytes}).");
            }
        }

        public void AddRun(long bytes)
        {
            LiveSpillBytes = checked(LiveSpillBytes + bytes);
        }

        public void RemoveRun(string path)
        {
            var bytes = new FileInfo(path).Length;
            File.Delete(path);
            LiveSpillBytes = checked(LiveSpillBytes - bytes);
        }
    }
}
