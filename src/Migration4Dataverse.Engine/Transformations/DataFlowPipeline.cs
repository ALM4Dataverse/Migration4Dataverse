using System.Collections;
using System.Runtime.CompilerServices;
using System.Management.Automation;
using System.Text;
using System.Text.Json;

namespace Migration4Dataverse.Engine.Transformations;

public sealed class DataFlowPipeline
{
    private readonly DataFlowPipelineDefinition _definition;
    private readonly IReadOnlyDictionary<string, DataFlowNodeDefinition> _nodesById;
    private readonly IReadOnlyDictionary<string, DataFlowSourceDefinition> _sourcesById;

    public DataFlowPipeline(DataFlowPipelineDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        _definition = definition;
        _nodesById = definition.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        _sourcesById = definition.Sources.ToDictionary(source => source.Id, StringComparer.Ordinal);
    }

    public IAsyncEnumerable<JsonElement> ReadAsync(
        DataFlowSourceReader sourceReader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceReader);
        var scripts = PrepareNodeScripts(null);
        return ReadPipelineAsync(sourceReader, scripts, cancellationToken);
    }

    public IAsyncEnumerable<JsonElement> ReadAsync(
        DataFlowSourceReader sourceReader,
        IReadOnlyDictionary<string, ScriptBlock> nodeScripts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceReader);
        var scripts = PrepareNodeScripts(nodeScripts);
        return ReadPipelineAsync(sourceReader, scripts, cancellationToken);
    }

    public static Task WriteJsonLinesAsync(
        string definitionJson,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionJson);
        return WriteJsonLinesAsync(
            DataFlowPipelineDefinition.FromJson(definitionJson),
            outputPath,
            cancellationToken);
    }

    public static Task WriteJsonLinesAsync(
        string definitionJson,
        string outputPath,
        IReadOnlyDictionary<string, ScriptBlock> nodeScripts,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionJson);
        return WriteJsonLinesAsync(
            DataFlowPipelineDefinition.FromJson(definitionJson),
            outputPath,
            nodeScripts,
            cancellationToken);
    }

    public static Task WriteJsonLinesAsync(
        IDictionary definition,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var nodeScripts = new Dictionary<string, ScriptBlock>(StringComparer.Ordinal);
        var serializableDefinition = PreparePowerShellDefinition(definition, nodeScripts);
        var definitionJson = JsonSerializer.Serialize(serializableDefinition);
        return WriteJsonLinesAsync(definitionJson, outputPath, nodeScripts, cancellationToken);
    }

    public static async Task WriteJsonLinesAsync(
        DataFlowPipelineDefinition definition,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var pipeline = new DataFlowPipeline(definition);
        var scripts = pipeline.PrepareNodeScripts(null);
        await WriteJsonLinesAsync(
                pipeline,
                scripts,
                outputPath,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task WriteJsonLinesAsync(
        DataFlowPipelineDefinition definition,
        string outputPath,
        IReadOnlyDictionary<string, ScriptBlock> nodeScripts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(nodeScripts);
        var pipeline = new DataFlowPipeline(definition);
        var scripts = pipeline.PrepareNodeScripts(nodeScripts);
        await WriteJsonLinesAsync(
                pipeline,
                scripts,
                outputPath,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteJsonLinesAsync(
        DataFlowPipeline pipeline,
        IReadOnlyDictionary<string, string> scripts,
        string outputPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(fullOutputPath)
            ?? throw new ArgumentException("The JSON Lines output path has no parent directory.", nameof(outputPath));
        Directory.CreateDirectory(outputDirectory);

        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(fullOutputPath)}.{Guid.NewGuid():N}.tmp");
        var completed = false;
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 1024 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var writer = new StreamWriter(
                             stream,
                             new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                             bufferSize: 1024 * 1024,
                             leaveOpen: true))
            {
                await foreach (var record in pipeline.ReadPipelineAsync(
                                   ReadJsonLinesSourceAsync,
                                   scripts,
                                   cancellationToken)
                                   .WithCancellation(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var json = JsonSerializer.Serialize(record);
                    await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                }

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullOutputPath, overwrite: true);
            completed = true;
        }
        finally
        {
            if (!completed && File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static Dictionary<string, object?> PreparePowerShellDefinition(
        IDictionary definition,
        IDictionary<string, ScriptBlock> nodeScripts)
    {
        var serializableDefinition = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in definition)
        {
            if (entry.Key is not string propertyName)
            {
                throw new ArgumentException(
                    "PowerShell data-flow configuration keys must be strings.",
                    nameof(definition));
            }

            if (string.Equals(propertyName, "Nodes", StringComparison.OrdinalIgnoreCase) &&
                entry.Value is IEnumerable nodes and not string)
            {
                serializableDefinition.Add(
                    propertyName,
                    nodes.Cast<object?>()
                        .Select(node => PreparePowerShellNode(node, nodeScripts, definition))
                        .ToArray());
            }
            else
            {
                serializableDefinition.Add(propertyName, PreparePowerShellValue(entry.Value, definition));
            }
        }

        return serializableDefinition;
    }

    private static object? PreparePowerShellNode(
        object? node,
        IDictionary<string, ScriptBlock> nodeScripts,
        IDictionary definition)
    {
        if (node is not IDictionary nodeDefinition)
        {
            return PreparePowerShellValue(node, definition);
        }

        string? nodeId = null;
        foreach (DictionaryEntry entry in nodeDefinition)
        {
            if (entry.Key is string key &&
                string.Equals(key, "Id", StringComparison.OrdinalIgnoreCase))
            {
                nodeId = entry.Value as string;
                break;
            }
        }

        var serializableNode = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in nodeDefinition)
        {
            if (entry.Key is not string propertyName)
            {
                throw new ArgumentException(
                    "PowerShell data-flow node configuration keys must be strings.",
                    nameof(definition));
            }

            if (string.Equals(propertyName, "Script", StringComparison.OrdinalIgnoreCase))
            {
                var scriptValue = entry.Value is PSObject scriptObject
                    ? scriptObject.BaseObject
                    : entry.Value;
                if (scriptValue is not ScriptBlock scriptBlock)
                {
                    throw new ArgumentException(
                        $"The inline Script property on data-flow node '{nodeId ?? "<unknown>"}' must be a PowerShell script block.",
                        nameof(definition));
                }
                if (string.IsNullOrWhiteSpace(nodeId))
                {
                    throw new ArgumentException(
                        "A data-flow node with an inline Script property must have a non-empty string Id.",
                        nameof(definition));
                }

                nodeScripts.Add(nodeId, scriptBlock);
            }
            else
            {
                serializableNode.Add(propertyName, PreparePowerShellValue(entry.Value, definition));
            }
        }

        return serializableNode;
    }

    private static object? PreparePowerShellValue(object? value, IDictionary definition)
    {
        if (value is null || value is string || value is byte[])
        {
            return value;
        }
        if (value is ScriptBlock)
        {
            throw new ArgumentException(
                "PowerShell script blocks must be placed in a transform or filter node's inline Script property.",
                nameof(definition));
        }
        if (value is IDictionary dictionary)
        {
            var serializableDictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is not string key)
                {
                    throw new ArgumentException(
                        "PowerShell data-flow configuration keys must be strings.",
                        nameof(definition));
                }

                serializableDictionary.Add(key, PreparePowerShellValue(entry.Value, definition));
            }

            return serializableDictionary;
        }
        if (value is PSObject psObject)
        {
            if (psObject.BaseObject is not PSCustomObject)
            {
                return PreparePowerShellValue(psObject.BaseObject, definition);
            }

            var serializableProperties = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in psObject.Properties)
            {
                if (property.MemberType is PSMemberTypes.NoteProperty or PSMemberTypes.Property or PSMemberTypes.AliasProperty)
                {
                    serializableProperties.Add(
                        property.Name,
                        PreparePowerShellValue(property.Value, definition));
                }
            }

            return serializableProperties;
        }
        if (value is IEnumerable enumerable)
        {
            return enumerable.Cast<object?>()
                .Select(item => PreparePowerShellValue(item, definition))
                .ToArray();
        }

        return value;
    }

    private async IAsyncEnumerable<JsonElement> ReadPipelineAsync(
        DataFlowSourceReader sourceReader,
        IReadOnlyDictionary<string, string> scripts,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var scriptRunner = scripts.Count == 0
            ? null
            : new DataFlowPowerShellScriptRunner(scripts);
        await foreach (var record in ReadNodeAsync(
                           _definition.OutputNodeId,
                           sourceReader,
                           scripts,
                           scriptRunner,
                           cancellationToken)
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return record;
        }
    }

    private async IAsyncEnumerable<JsonElement> ReadNodeAsync(
        string nodeId,
        DataFlowSourceReader sourceReader,
        IReadOnlyDictionary<string, string> scripts,
        DataFlowPowerShellScriptRunner? scriptRunner,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var node = _nodesById[nodeId];
        switch (DataFlowPipelineDefinition.NormalizeNodeType(node.Type))
        {
            case "source":
            {
                var source = _sourcesById[node.SourceId!];
                var records = sourceReader(source, cancellationToken)
                    ?? throw new InvalidOperationException(
                        $"The source reader returned null for data-flow source '{source.Id}'.");
                await foreach (var record in records.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (record.ValueKind != JsonValueKind.Object)
                    {
                        throw new InvalidDataException(
                            $"Data-flow source '{source.Id}' returned a record that is not a JSON object.");
                    }
                    yield return record;
                }
                break;
            }

            case "append":
            case "unionall":
                foreach (var input in node.Inputs)
                {
                    await foreach (var record in ReadNodeAsync(
                                       input,
                                       sourceReader,
                                       scripts,
                                       scriptRunner,
                                       cancellationToken)
                                       .WithCancellation(cancellationToken)
                                       .ConfigureAwait(false))
                    {
                        yield return record;
                    }
                }
                break;

            case "merge":
                await foreach (var record in MergeAsync(
                                   node,
                                   sourceReader,
                                   scripts,
                                   scriptRunner,
                                   cancellationToken)
                                   .WithCancellation(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    yield return record;
                }
                break;

            case "sort":
                var inputRecords = ReadNodeAsync(
                    node.Inputs[0],
                    sourceReader,
                    scripts,
                    scriptRunner,
                    cancellationToken);
                await foreach (var record in ExternalMergeSorter.SortAsync(
                                   inputRecords,
                                   node.Id,
                                   node.Keys,
                                   node.SortOptions ?? new DataFlowSortOptions(),
                                   cancellationToken)
                                   .WithCancellation(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    yield return record;
                }
                break;

            case "transform":
                await foreach (var record in TransformAsync(
                                   node,
                                   sourceReader,
                                   scripts,
                                   scriptRunner,
                                   cancellationToken)
                                   .WithCancellation(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    yield return record;
                }
                break;

            case "filter":
                await foreach (var record in FilterAsync(
                                   node,
                                   sourceReader,
                                   scripts,
                                   scriptRunner,
                                   cancellationToken)
                                   .WithCancellation(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    yield return record;
                }
                break;

            default:
                throw new InvalidOperationException(
                    $"Data-flow node '{node.Id}' uses unsupported type '{node.Type}'.");
        }
    }

    private async IAsyncEnumerable<JsonElement> MergeAsync(
        DataFlowNodeDefinition node,
        DataFlowSourceReader sourceReader,
        IReadOnlyDictionary<string, string> scripts,
        DataFlowPowerShellScriptRunner? scriptRunner,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var left = new SortedInput(
            ReadNodeAsync(
                node.Inputs[0],
                sourceReader,
                scripts,
                scriptRunner,
                cancellationToken).GetAsyncEnumerator(cancellationToken),
            node.Inputs[0],
            node.Id,
            node.Keys);
        var right = new SortedInput(
            ReadNodeAsync(
                node.Inputs[1],
                sourceReader,
                scripts,
                scriptRunner,
                cancellationToken).GetAsyncEnumerator(cancellationToken),
            node.Inputs[1],
            node.Id,
            node.Keys);
        await using (left.ConfigureAwait(false))
        await using (right.ConfigureAwait(false))
        {
            var leftRecord = await left.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            var rightRecord = await right.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            while (leftRecord is not null && rightRecord is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DataFlowValueComparer.Compare(leftRecord.Keys, rightRecord.Keys, node.Keys) <= 0)
                {
                    yield return leftRecord.Row;
                    leftRecord = await left.ReadNextAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    yield return rightRecord.Row;
                    rightRecord = await right.ReadNextAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            while (leftRecord is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return leftRecord.Row;
                leftRecord = await left.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            }

            while (rightRecord is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return rightRecord.Row;
                rightRecord = await right.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async IAsyncEnumerable<JsonElement> TransformAsync(
        DataFlowNodeDefinition node,
        DataFlowSourceReader sourceReader,
        IReadOnlyDictionary<string, string> scripts,
        DataFlowPowerShellScriptRunner? scriptRunner,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var input in ReadNodeAsync(
                           node.Inputs[0],
                           sourceReader,
                           scripts,
                           scriptRunner,
                           cancellationToken)
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = await (scriptRunner
                    ?? throw new InvalidOperationException(
                        $"Transform node '{node.Id}' has no runtime script executor."))
                .TransformAsync(node.Id, input, cancellationToken)
                .ConfigureAwait(false);
            foreach (var record in output)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return record;
            }
        }
    }

    private async IAsyncEnumerable<JsonElement> FilterAsync(
        DataFlowNodeDefinition node,
        DataFlowSourceReader sourceReader,
        IReadOnlyDictionary<string, string> scripts,
        DataFlowPowerShellScriptRunner? scriptRunner,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var input in ReadNodeAsync(
                           node.Inputs[0],
                           sourceReader,
                           scripts,
                           scriptRunner,
                           cancellationToken)
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await (scriptRunner
                    ?? throw new InvalidOperationException(
                        $"Filter node '{node.Id}' has no runtime script executor."))
                .FilterAsync(node.Id, input, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return input;
            }
        }
    }

    private IReadOnlyDictionary<string, string> PrepareNodeScripts(
        IReadOnlyDictionary<string, ScriptBlock>? nodeScripts)
    {
        var scriptNodes = _definition.Nodes
            .Where(node => DataFlowPipelineDefinition.NormalizeNodeType(node.Type) is "transform" or "filter")
            .ToDictionary(node => node.Id, node => node.Type.Trim().ToLowerInvariant(), StringComparer.Ordinal);
        var scripts = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var nodeId in scriptNodes.Keys)
        {
            if (nodeScripts is null || !nodeScripts.TryGetValue(nodeId, out var scriptBlock))
            {
                throw new ArgumentException(
                    $"Data-flow node '{nodeId}' requires a runtime PowerShell script block keyed by its node ID.",
                    nameof(nodeScripts));
            }
            if (scriptBlock is null)
            {
                throw new ArgumentException(
                    $"The runtime PowerShell script block for data-flow node '{nodeId}' must not be null.",
                    nameof(nodeScripts));
            }

            var script = scriptBlock.ToString().Trim();
            if (script.StartsWith('{') && script.EndsWith('}'))
            {
                script = script[1..^1].Trim();
            }
            if (string.IsNullOrWhiteSpace(script))
            {
                throw new ArgumentException(
                    $"The runtime PowerShell script block for data-flow node '{nodeId}' must not be empty.",
                    nameof(nodeScripts));
            }

            scripts.Add(nodeId, script);
        }

        if (nodeScripts is not null)
        {
            foreach (var nodeId in nodeScripts.Keys)
            {
                if (!scriptNodes.ContainsKey(nodeId))
                {
                    throw new ArgumentException(
                        $"Runtime script '{nodeId}' does not reference a transform or filter node.",
                        nameof(nodeScripts));
                }
            }
        }

        return scripts;
    }

    private static async IAsyncEnumerable<JsonElement> ReadJsonLinesSourceAsync(
        DataFlowSourceDefinition source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!string.Equals(source.Type.Trim(), "jsonl", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"The JSON Lines pipeline helper cannot read source type '{source.Type}'.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Path);

        await using var stream = new FileStream(
            source.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024 * 1024,
            leaveOpen: true);

        long recordNumber = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is string line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            recordNumber++;
            JsonElement record;
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException(
                        $"JSON Lines source '{source.Id}' contains a record that is not a JSON object.");
                }
                record = document.RootElement.Clone();
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"JSON Lines source '{source.Id}' contains invalid JSON at record {recordNumber}.",
                    exception);
            }

            yield return record;
        }
    }

    private sealed class SortedInput(
        IAsyncEnumerator<JsonElement> enumerator,
        string inputNodeId,
        string mergeNodeId,
        IReadOnlyList<DataFlowSortKey> keys) : IAsyncDisposable
    {
        private object?[]? _previousKeys;
        private long _recordNumber;

        public async ValueTask<MergeRecord?> ReadNextAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                return null;
            }

            var row = enumerator.Current;
            var rowNumber = checked(++_recordNumber);
            var keyValues = DataFlowValueComparer.ReadKeys(
                row,
                keys,
                $"merge node '{mergeNodeId}' input '{inputNodeId}'");
            if (_previousKeys is not null &&
                DataFlowValueComparer.Compare(_previousKeys, keyValues, keys) > 0)
            {
                throw new InvalidDataException(
                    $"Merge node '{mergeNodeId}' requires input '{inputNodeId}' to be sorted by its configured keys; " +
                    $"record {rowNumber} is out of order.");
            }

            _previousKeys = keyValues;
            return new MergeRecord(row.Clone(), keyValues);
        }

        public ValueTask DisposeAsync()
        {
            return enumerator.DisposeAsync();
        }
    }

    private sealed record MergeRecord(JsonElement Row, object?[] Keys);
}
