using System.Text.Json;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.PowerShell;

internal sealed class PowerShellDataversePartitionedHierarchyRecordReader :
    IHierarchyRecordReader,
    IHierarchyRecordCheckpointReader
{
    private readonly HierarchySourceLevel _level;
    private readonly PartitionReader[] _partitions;
    private readonly Dictionary<string, string?> _lastKeysByPartition;
    private PartitionReader? _lookaheadPartition;
    private bool _disposed;

    public PowerShellDataversePartitionedHierarchyRecordReader(
        PowerShellScriptExecutor executor,
        HierarchySourceLevel level,
        PowerShellDataverseSourceDefinition definition,
        string? checkpointJson)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(definition);
        if (level.Type != "dataverse" || level.ParentKeyProperty is not null)
        {
            throw new ArgumentException(
                "Partitioned Dataverse readers require a root Dataverse source.",
                nameof(level));
        }
        if (definition.Partitions.Count < 2)
        {
            throw new ArgumentException("A partitioned Dataverse source requires at least two partitions.", nameof(definition));
        }

        _level = level;
        _lastKeysByPartition = definition.Partitions.ToDictionary(
            partition => partition.Id,
            _ => (string?)null,
            StringComparer.Ordinal);
        if (checkpointJson is not null)
        {
            ReadCheckpoint(checkpointJson, definition.Partitions, _lastKeysByPartition);
        }

        _partitions = definition.Partitions
            .Select(partition =>
            {
                var partitionDefinition = new PowerShellDataverseSourceDefinition(
                    definition.Connection,
                    partition.Options,
                    definition.QueryType);
                return new PartitionReader(
                    partition.Id,
                    new PowerShellDataverseHierarchyRecordReader(
                        executor,
                        level,
                        partitionDefinition,
                        _lastKeysByPartition[partition.Id]));
            })
            .ToArray();
    }

    public long RecordCount => _partitions.Sum(partition => partition.Reader.RecordCount);

    public async ValueTask<JsonElement?> PeekAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lookaheadPartition is not null)
        {
            return _lookaheadPartition.Record;
        }

        var heads = await Task.WhenAll(_partitions.Select(async partition =>
            (Partition: partition, Record: await partition.Reader.PeekAsync(cancellationToken).ConfigureAwait(false))))
            .ConfigureAwait(false);
        foreach (var head in heads)
        {
            head.Partition.Record = head.Record;
            if (!head.Record.HasValue)
            {
                continue;
            }

            if (_lookaheadPartition is null ||
                CompareKeys(GetKey(head.Record.Value), GetKey(_lookaheadPartition.Record!.Value)) < 0)
            {
                _lookaheadPartition = head.Partition;
            }
        }

        return _lookaheadPartition?.Record;
    }

    public JsonElement Take()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var partition = _lookaheadPartition
            ?? throw new InvalidOperationException(
                $"Partitioned Dataverse source '{_level.StepId}' has no buffered record.");
        var record = partition.Reader.Take();
        _lastKeysByPartition[partition.Id] = GetKey(record);
        partition.Record = null;
        _lookaheadPartition = null;
        return record;
    }

    public string CreateCheckpointJson(string lastKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lastKey);
        return JsonSerializer.Serialize(new
        {
            LastKey = lastKey,
            Partitions = _lastKeysByPartition
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await Task.WhenAll(_partitions.Select(partition => partition.Reader.DisposeAsync().AsTask()))
            .ConfigureAwait(false);
        _disposed = true;
    }

    private string GetKey(JsonElement record)
    {
        if (!record.TryGetProperty(_level.KeyProperty, out var keyElement))
        {
            throw new InvalidDataException(
                $"A source row for job '{_level.StepId}' is missing key property '{_level.KeyProperty}'.");
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
                $"A source row for job '{_level.StepId}' has an invalid key in property '{_level.KeyProperty}'.");
        }

        return key;
    }

    private static int CompareKeys(string left, string right)
    {
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static void ReadCheckpoint(
        string checkpointJson,
        IReadOnlyList<DataverseSourcePartition> partitions,
        IDictionary<string, string?> lastKeysByPartition)
    {
        using var document = JsonDocument.Parse(checkpointJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("Partitions", out var partitionCursor) ||
            partitionCursor.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "A partitioned Dataverse source checkpoint does not contain per-partition cursors.");
        }

        var checkpointPartitionIds = partitionCursor.EnumerateObject()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (checkpointPartitionIds.Count != partitions.Count ||
            partitions.Any(partition => !checkpointPartitionIds.Contains(partition.Id)))
        {
            throw new InvalidDataException(
                "The partition IDs in the Dataverse source checkpoint do not match the current source definition.");
        }

        foreach (var partition in partitions)
        {
            var value = partitionCursor.GetProperty(partition.Id);
            lastKeysByPartition[partition.Id] = value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String when !string.IsNullOrWhiteSpace(value.GetString()) => value.GetString(),
                _ => throw new InvalidDataException(
                    $"The Dataverse source checkpoint has an invalid cursor for partition '{partition.Id}'.")
            };
        }
    }

    private sealed class PartitionReader(string id, PowerShellDataverseHierarchyRecordReader reader)
    {
        public string Id { get; } = id;

        public PowerShellDataverseHierarchyRecordReader Reader { get; } = reader;

        public JsonElement? Record { get; set; }
    }
}
