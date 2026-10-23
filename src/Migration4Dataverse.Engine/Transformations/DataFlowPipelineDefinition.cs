using System.Text.Json;
using System.Text.Json.Serialization;

namespace Migration4Dataverse.Engine.Transformations;

public sealed class DataFlowPipelineDefinition
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public DataFlowSourceDefinition[] Sources { get; init; } = Array.Empty<DataFlowSourceDefinition>();

    public DataFlowNodeDefinition[] Nodes { get; init; } = Array.Empty<DataFlowNodeDefinition>();

    public string OutputNodeId { get; init; } = string.Empty;

    public static DataFlowPipelineDefinition FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        DataFlowPipelineDefinition definition;
        try
        {
            definition = JsonSerializer.Deserialize<DataFlowPipelineDefinition>(json, JsonOptions)
                ?? throw new InvalidDataException("The data-flow pipeline definition is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The data-flow pipeline definition is invalid JSON.", exception);
        }

        definition.Validate();
        return definition;
    }

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(OutputNodeId);
        ArgumentNullException.ThrowIfNull(Sources);
        ArgumentNullException.ThrowIfNull(Nodes);
        if (Nodes.Length == 0)
        {
            throw new ArgumentException("A data-flow pipeline must declare at least one node.", nameof(Nodes));
        }

        var sourcesById = new Dictionary<string, DataFlowSourceDefinition>(StringComparer.Ordinal);
        foreach (var source in Sources)
        {
            if (source is null)
            {
                throw new ArgumentException("A data-flow pipeline must not contain null sources.", nameof(Sources));
            }
            ArgumentException.ThrowIfNullOrWhiteSpace(source.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(source.Type);
            ArgumentException.ThrowIfNullOrWhiteSpace(source.ConnectionId);
            if (source.Options is { } options && options.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException(
                    $"Data-flow source '{source.Id}' Options must be a JSON object.",
                    nameof(Sources));
            }

            var sourceType = source.Type.Trim().ToLowerInvariant();
            if (sourceType is "jsonl" or "csv" or "parquet" &&
                string.IsNullOrWhiteSpace(source.Path))
            {
                throw new ArgumentException(
                    $"File source '{source.Id}' of type '{sourceType}' must declare a path.",
                    nameof(Sources));
            }
            if (sourceType == "custom" && string.IsNullOrWhiteSpace(source.Identity))
            {
                throw new ArgumentException(
                    $"Custom source '{source.Id}' must declare a stable identity.",
                    nameof(Sources));
            }
            if (!sourcesById.TryAdd(source.Id, source))
            {
                throw new ArgumentException(
                    $"Data-flow source ID '{source.Id}' is declared more than once.",
                    nameof(Sources));
            }
        }

        var nodesById = new Dictionary<string, DataFlowNodeDefinition>(StringComparer.Ordinal);
        foreach (var node in Nodes)
        {
            if (node is null)
            {
                throw new ArgumentException("A data-flow pipeline must not contain null nodes.", nameof(Nodes));
            }
            ArgumentException.ThrowIfNullOrWhiteSpace(node.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(node.Type);
            ArgumentNullException.ThrowIfNull(node.Inputs);
            ArgumentNullException.ThrowIfNull(node.Keys);
            if (!nodesById.TryAdd(node.Id, node))
            {
                throw new ArgumentException(
                    $"Data-flow node ID '{node.Id}' is declared more than once.",
                    nameof(Nodes));
            }
        }

        if (!nodesById.ContainsKey(OutputNodeId))
        {
            throw new ArgumentException(
                $"Data-flow output node '{OutputNodeId}' is not declared.",
                nameof(OutputNodeId));
        }

        var sourceUseCounts = sourcesById.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var consumerCounts = nodesById.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        foreach (var node in Nodes)
        {
            var nodeType = NormalizeNodeType(node.Type);
            switch (nodeType)
            {
                case "source":
                    if (node.Inputs.Length != 0 ||
                        node.Keys.Length != 0 ||
                        node.SortOptions is not null ||
                        string.IsNullOrWhiteSpace(node.SourceId))
                    {
                        throw new ArgumentException(
                            $"Source node '{node.Id}' must declare only a SourceId and no inputs or transform options.",
                            nameof(Nodes));
                    }
                    if (!sourceUseCounts.TryGetValue(node.SourceId, out var sourceUseCount))
                    {
                        throw new ArgumentException(
                            $"Source node '{node.Id}' references unknown source '{node.SourceId}'.",
                            nameof(Nodes));
                    }
                    sourceUseCounts[node.SourceId] = sourceUseCount + 1;
                    break;

                case "transform":
                case "filter":
                    ValidateInputs(node, nodesById, consumerCounts, minimumCount: 1, maximumCount: 1);
                    if (!string.IsNullOrWhiteSpace(node.SourceId) ||
                            node.Keys.Length != 0 ||
                            node.SortOptions is not null)
                    {
                            throw new ArgumentException(
                                $"Data-flow node '{node.Id}' of type '{nodeType}' accepts only one input.",
                                nameof(Nodes));
                    }
                    break;

                case "append":
                case "unionall":
                    ValidateInputs(node, nodesById, consumerCounts, minimumCount: 2, maximumCount: null);
                    if (!string.IsNullOrWhiteSpace(node.SourceId) ||
                        node.Keys.Length != 0 ||
                        node.SortOptions is not null)
                    {
                        throw new ArgumentException(
                            $"Append node '{node.Id}' does not accept source, key, or sort options.",
                            nameof(Nodes));
                    }
                    break;

                case "merge":
                    ValidateInputs(node, nodesById, consumerCounts, minimumCount: 2, maximumCount: 2);
                    if (!string.IsNullOrWhiteSpace(node.SourceId) || node.SortOptions is not null)
                    {
                        throw new ArgumentException(
                            $"Merge node '{node.Id}' accepts only two inputs and sort keys.",
                            nameof(Nodes));
                    }
                    ValidateKeys(node.Id, node.Keys);
                    break;

                case "sort":
                    ValidateInputs(node, nodesById, consumerCounts, minimumCount: 1, maximumCount: 1);
                    if (!string.IsNullOrWhiteSpace(node.SourceId))
                    {
                        throw new ArgumentException(
                            $"Sort node '{node.Id}' does not accept a source definition.",
                            nameof(Nodes));
                    }
                    ValidateKeys(node.Id, node.Keys);
                    (node.SortOptions ?? new DataFlowSortOptions()).Validate(node.Id);
                    break;

                default:
                    throw new ArgumentException(
                        $"Data-flow node '{node.Id}' uses unsupported type '{node.Type}'.",
                        nameof(Nodes));
            }
        }

        foreach (var (sourceId, useCount) in sourceUseCounts)
        {
            if (useCount != 1)
            {
                throw new ArgumentException(
                    $"Data-flow source '{sourceId}' must be referenced by exactly one source node; found {useCount}.",
                    nameof(Sources));
            }
        }

        foreach (var (nodeId, consumerCount) in consumerCounts)
        {
            if (consumerCount > 1)
            {
                throw new ArgumentException(
                    $"Data-flow node '{nodeId}' has {consumerCount} consumers. Fan-out is not supported; use a future Multicast node when available.",
                    nameof(Nodes));
            }
        }

        var visitStates = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (var node in Nodes)
        {
            Visit(node.Id);
        }

        var reachable = new HashSet<string>(StringComparer.Ordinal);
        VisitInputs(OutputNodeId);
        if (reachable.Count != Nodes.Length)
        {
            var unusedNode = Nodes.First(node => !reachable.Contains(node.Id));
            throw new ArgumentException(
                $"Data-flow node '{unusedNode.Id}' is not connected to output node '{OutputNodeId}'.",
                nameof(Nodes));
        }

        void Visit(string nodeId)
        {
            if (visitStates.TryGetValue(nodeId, out var state))
            {
                if (state == 1)
                {
                    throw new ArgumentException(
                        $"The data-flow pipeline contains a cycle involving node '{nodeId}'.",
                        nameof(Nodes));
                }
                if (state == 2)
                {
                    return;
                }
            }

            visitStates[nodeId] = 1;
            foreach (var input in nodesById[nodeId].Inputs)
            {
                Visit(input);
            }
            visitStates[nodeId] = 2;
        }

        void VisitInputs(string nodeId)
        {
            if (!reachable.Add(nodeId))
            {
                return;
            }
            foreach (var input in nodesById[nodeId].Inputs)
            {
                VisitInputs(input);
            }
        }
    }

    internal static string NormalizeNodeType(string type)
    {
        var normalized = type.Trim().ToLowerInvariant();
        return normalized switch
        {
            "union-all" or "union_all" or "union all" => "unionall",
            "append" => "append",
            _ => normalized
        };
    }

    private static void ValidateInputs(
        DataFlowNodeDefinition node,
        IReadOnlyDictionary<string, DataFlowNodeDefinition> nodesById,
        IDictionary<string, int> consumerCounts,
        int minimumCount,
        int? maximumCount)
    {
        if (node.Inputs.Length < minimumCount ||
            (maximumCount.HasValue && node.Inputs.Length > maximumCount.Value))
        {
            var expected = maximumCount == minimumCount
                ? maximumCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : maximumCount is null
                    ? $"at least {minimumCount}"
                    : $"between {minimumCount} and {maximumCount.Value}";
            throw new ArgumentException(
                $"Data-flow node '{node.Id}' requires {expected} input(s); received {node.Inputs.Length}.",
                nameof(Nodes));
        }

        var distinctInputs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in node.Inputs)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(input);
            if (!distinctInputs.Add(input))
            {
                throw new ArgumentException(
                    $"Data-flow node '{node.Id}' references input '{input}' more than once.",
                    nameof(Nodes));
            }
            if (!nodesById.ContainsKey(input))
            {
                throw new ArgumentException(
                    $"Data-flow node '{node.Id}' references unknown input node '{input}'.",
                    nameof(Nodes));
            }

            consumerCounts[input]++;
        }
    }

    private static void ValidateKeys(string nodeId, IReadOnlyList<DataFlowSortKey> keys)
    {
        if (keys.Count == 0)
        {
            throw new ArgumentException(
                $"Data-flow node '{nodeId}' must declare at least one sort key.",
                nameof(Nodes));
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (key is null)
            {
                throw new ArgumentException(
                    $"Data-flow node '{nodeId}' contains a null sort key.",
                    nameof(Nodes));
            }
            ArgumentException.ThrowIfNullOrWhiteSpace(key.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(key.Type);
            if (!names.Add(key.Name))
            {
                throw new ArgumentException(
                    $"Data-flow node '{nodeId}' declares sort key '{key.Name}' more than once.",
                    nameof(Nodes));
            }

            var type = key.Type.Trim().ToLowerInvariant();
            if (type is not (
                "string" or
                "bool" or "boolean" or
                "byte" or
                "short" or "int16" or
                "int" or "int32" or
                "long" or "int64" or
                "float" or "single" or
                "double" or
                "decimal" or "number" or
                "guid" or
                "date" or
                "datetime" or "datetimeoffset"))
            {
                throw new ArgumentException(
                    $"Data-flow sort key '{key.Name}' on node '{nodeId}' uses unsupported type '{key.Type}'.",
                    nameof(Nodes));
            }
        }
    }
}

public sealed class DataFlowSourceDefinition
{
    public string Id { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string ConnectionId { get; init; } = string.Empty;

    public string? Path { get; init; }

    public JsonElement? Options { get; init; }

    public string? Identity { get; init; }
}

public sealed class DataFlowNodeDefinition
{
    public string Id { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string? SourceId { get; init; }

    public string[] Inputs { get; init; } = Array.Empty<string>();

    public DataFlowSortKey[] Keys { get; init; } = Array.Empty<DataFlowSortKey>();

    public DataFlowSortOptions? SortOptions { get; init; }
}

public sealed class DataFlowSortKey
{
    public string Name { get; init; } = string.Empty;

    public string Type { get; init; } = "string";

    public bool Descending { get; init; }

    public bool NullsFirst { get; init; } = true;
}

public sealed class DataFlowSortOptions
{
    public long MemoryBudgetBytes { get; init; } = 64L * 1024 * 1024;

    public int MaxInMemoryRows { get; init; } = 50_000;

    public long MaxRecordBytes { get; init; } = 4L * 1024 * 1024;

    public long MaxSpillBytes { get; init; } = long.MaxValue;

    public string? TemporaryDirectory { get; init; }

    internal void Validate(string nodeId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MemoryBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxInMemoryRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxRecordBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxRecordBytes, MemoryBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxSpillBytes);
        if (TemporaryDirectory is not null && string.IsNullOrWhiteSpace(TemporaryDirectory))
        {
            throw new ArgumentException(
                $"Sort node '{nodeId}' TemporaryDirectory must be omitted or contain a path.",
                nameof(TemporaryDirectory));
        }
    }
}

public delegate IAsyncEnumerable<JsonElement> DataFlowSourceReader(
    DataFlowSourceDefinition source,
    CancellationToken cancellationToken);
