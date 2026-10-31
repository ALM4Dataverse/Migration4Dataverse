using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Management.Automation;
using Migration4Dataverse.Engine.Connectors;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.PowerShell;

internal sealed class PowerShellCustomFormatHierarchyRecordReader : IHierarchyRecordReader
{
    private const string ReadRecordsScript = """
        param($ScriptText, $InputStream, $StepId, $Path, $ObjectKey, $OptionsJson, $BinaryStagingRoot, $Emit)
        $ErrorActionPreference = 'Stop'
        $callback = [scriptblock]::Create([string]$ScriptText)
        $options = ConvertFrom-Json -InputObject $OptionsJson -AsHashtable -Depth 100 -ErrorAction Stop
        $context = [pscustomobject]@{
            StepId = $StepId
            Path = $Path
            ObjectKey = $ObjectKey
            Options = $options
            BinaryStagingRoot = $BinaryStagingRoot
        }
        & $callback $InputStream $context | ForEach-Object {
            $record = $_
            if ([Migration4Dataverse.Engine.Connectors.MigrationBinaryValue]::ContainsBinaryValues($record)) {
                $record = [Migration4Dataverse.Engine.Connectors.MigrationBinaryValue]::PrepareForTransport(
                    $record,
                    [string]$BinaryStagingRoot)
            }
            $json = ConvertTo-Json -InputObject $record -Depth 100 -Compress -ErrorAction Stop
            $Emit.Invoke([System.Management.Automation.PSObject]::AsPSObject($json))
        }
        """;

    private readonly Stream _stream;
    private readonly IAsyncEnumerator<PSObject> _records;
    private readonly string _stepId;
    private JsonElement? _lookahead;
    private bool _disposed;

    public PowerShellCustomFormatHierarchyRecordReader(
        PowerShellScriptExecutor executor,
        HierarchySourceLevel level,
        string scriptText)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptText);
        ArgumentException.ThrowIfNullOrWhiteSpace(level.Path);
        ArgumentException.ThrowIfNullOrWhiteSpace(level.BinaryStagingRoot);
        _stepId = level.StepId;
        _stream = new FileStream(
            level.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        _records = executor.InvokeStreamingAsync(
                ReadRecordsScript,
                [
                    scriptText,
                    _stream,
                    level.StepId,
                    level.Path,
                    level.FormatObjectKey,
                    level.FormatOptionsJson,
                    level.BinaryStagingRoot
                ])
            .GetAsyncEnumerator();
    }

    public long RecordCount { get; private set; }

    public async ValueTask<JsonElement?> PeekAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lookahead.HasValue)
        {
            return _lookahead.Value;
        }

        if (!await _records.MoveNextAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        if (_records.Current.BaseObject is not string json)
        {
            throw new InvalidDataException(
                $"Custom format reader for source '{_stepId}' emitted a non-string transport record.");
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Custom format reader for source '{_stepId}' must emit record objects.");
        }

        _lookahead = document.RootElement.Clone();
        RecordCount++;
        return _lookahead.Value;
    }

    public JsonElement Take()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = _lookahead
            ?? throw new InvalidOperationException(
                $"Custom format source '{_stepId}' has no buffered record.");
        _lookahead = null;
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await _records.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
