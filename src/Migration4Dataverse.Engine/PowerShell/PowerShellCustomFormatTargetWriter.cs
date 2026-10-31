using System.Collections;
using System.Text.Json;
using Migration4Dataverse.Engine.Connectors;

namespace Migration4Dataverse.Engine.PowerShell;

internal static class PowerShellCustomFormatTargetWriter
{
    private const string WriteRecordsScript = """
        param($ScriptText, $Records, $OutputStream, $StepId, $Path, $ObjectKey, $OptionsJson, $FirstBatch, $LastBatch, $State, $BinaryStagingRoot)
        $ErrorActionPreference = 'Stop'
        $callback = [scriptblock]::Create([string]$ScriptText)
        $options = ConvertFrom-Json -InputObject $OptionsJson -AsHashtable -Depth 100 -ErrorAction Stop
        $context = [pscustomobject]@{
            StepId = $StepId
            Path = $Path
            ObjectKey = $ObjectKey
            Options = $options
            FirstBatch = $FirstBatch
            LastBatch = $LastBatch
            State = $State
            BinaryStagingRoot = $BinaryStagingRoot
        }
        & $callback $Records $OutputStream $context
        """;

    public static void FinalizeSingleFileTarget(
        PowerShellScriptExecutor executor,
        string targetPath,
        string runId,
        string stepId,
        string scriptText,
        string formatOptionsJson,
        string binaryStagingRoot,
        string? objectKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptText);
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryStagingRoot);

        var state = new Hashtable(StringComparer.Ordinal);
        StreamingSourceFileConverter.WriteAtomically(targetPath, output =>
        {
            using var batches = FileBatchTargetWriter
                .ReadSingleFileTargetBatches(targetPath, runId, stepId)
                .GetEnumerator();
            if (!batches.MoveNext())
            {
                InvokeWriter(
                    executor,
                    scriptText,
                    Array.Empty<JsonElement>(),
                    output,
                    targetPath,
                    objectKey,
                    stepId,
                    formatOptionsJson,
                    firstBatch: true,
                    lastBatch: true,
                    state,
                    binaryStagingRoot,
                    cancellationToken);
                return;
            }

            var current = batches.Current;
            var firstBatch = true;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hasNext = batches.MoveNext();
                InvokeWriter(
                    executor,
                    scriptText,
                    current,
                    output,
                    targetPath,
                    objectKey,
                    stepId,
                    formatOptionsJson,
                    firstBatch,
                    lastBatch: !hasNext,
                    state,
                    binaryStagingRoot,
                    cancellationToken);
                if (!hasNext)
                {
                    break;
                }

                current = batches.Current;
                firstBatch = false;
            }
        });
    }

    private static void InvokeWriter(
        PowerShellScriptExecutor executor,
        string scriptText,
        IReadOnlyList<JsonElement> batch,
        Stream output,
        string path,
        string? objectKey,
        string stepId,
        string optionsJson,
        bool firstBatch,
        bool lastBatch,
        IDictionary state,
        string binaryStagingRoot,
        CancellationToken cancellationToken)
    {
        var records = batch
            .Select(record => MigrationBinaryValue.RestoreFromTransport(record, binaryStagingRoot))
            .ToArray();
        var outputs = executor.InvokeStreamingAsync(
            WriteRecordsScript,
            [
                scriptText,
                records,
                output,
                stepId,
                path,
                objectKey,
                optionsJson,
                firstBatch,
                lastBatch,
                state,
                binaryStagingRoot
            ],
            cancellationToken);
        var enumerator = outputs.GetAsyncEnumerator(cancellationToken);
        try
        {
            if (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                throw new InvalidDataException(
                    $"Custom format writer for target '{stepId}' must write to the provided stream and must not emit pipeline output.");
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
