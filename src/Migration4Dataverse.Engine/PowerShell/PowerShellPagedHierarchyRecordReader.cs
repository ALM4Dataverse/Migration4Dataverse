using System.Text.Json;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.PowerShell;

public sealed class PowerShellPagedHierarchyRecordReader : IHierarchyRecordReader
{
    private const string ReadPageScript = """
        param($ScriptText, $Connection, $Cursor, $PageSize, $StepId, $Path)
        $ErrorActionPreference = 'Stop'
        $callback = [scriptblock]::Create([string]$ScriptText)
        $callbackOutput = @(& $callback $Connection $Cursor $PageSize $StepId $Path)
        if ($callbackOutput.Count -ne 1) {
            throw "A custom source callback must return exactly one page object; it returned $($callbackOutput.Count) output object(s)."
        }

        $page = $callbackOutput[0]
        if ($page -is [System.Collections.IDictionary]) {
            if (-not $page.Contains('Records') -or -not $page.Contains('Done')) {
                throw 'A custom source page must contain Records and Done properties.'
            }
            $records = $page['Records']
            $nextCursor = $page['NextCursor']
            $done = $page['Done']
        }
        else {
            $recordsProperty = $page.PSObject.Properties['Records']
            $nextCursorProperty = $page.PSObject.Properties['NextCursor']
            $doneProperty = $page.PSObject.Properties['Done']
            if ($null -eq $recordsProperty -or $null -eq $doneProperty) {
                $pageType = if ($null -eq $page) { '<null>' } else { $page.GetType().FullName }
                throw "A custom source page must contain Records and Done properties; received '$pageType'."
            }
            $records = $recordsProperty.Value
            $nextCursor = if ($null -eq $nextCursorProperty) { $null } else { $nextCursorProperty.Value }
            $done = $doneProperty.Value
        }

        if ($done -isnot [bool]) {
            throw 'A custom source page Done property must be a Boolean.'
        }
        $json = ConvertTo-Json -InputObject ([ordered]@{
            Records = @($records)
            NextCursor = if ($null -eq $nextCursor) { $null } else { [string]$nextCursor }
            Done = $done
        }) -Depth 100 -Compress -ErrorAction Stop
        $json
        """;

    private readonly PowerShellScriptExecutor _executor;
    private readonly HierarchySourceLevel _level;
    private readonly object _connection;
    private readonly string _scriptText;
    private readonly int _pageSize;
    private JsonElement[] _records = Array.Empty<JsonElement>();
    private JsonElement? _lookahead;
    private string? _cursor;
    private int _recordIndex;
    private bool _done;
    private bool _disposed;

    public PowerShellPagedHierarchyRecordReader(
        PowerShellScriptExecutor executor,
        HierarchySourceLevel level,
        object connection,
        string scriptText,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptText);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        if (level.Type != "custom")
        {
            throw new ArgumentException("A PowerShell paged reader requires a source level with type 'custom'.", nameof(level));
        }

        _executor = executor;
        _level = level;
        _connection = connection;
        _scriptText = scriptText;
        _pageSize = pageSize;
    }

    public long RecordCount { get; private set; }

    public async ValueTask<JsonElement?> PeekAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lookahead.HasValue)
        {
            return _lookahead.Value;
        }

        while (_recordIndex >= _records.Length)
        {
            if (_done)
            {
                return null;
            }
            await ReadNextPageAsync(cancellationToken).ConfigureAwait(false);
        }

        _lookahead = _records[_recordIndex];
        RecordCount++;
        return _lookahead.Value;
    }

    public JsonElement Take()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = _lookahead
            ?? throw new InvalidOperationException($"Custom source '{_level.Identity}' has no buffered record.");
        _lookahead = null;
        _recordIndex++;
        return result;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private async Task ReadNextPageAsync(CancellationToken cancellationToken)
    {
        var output = await _executor.InvokeAsync(
                ReadPageScript,
                new object?[]
                {
                    _scriptText,
                    _connection,
                    _cursor,
                    _pageSize,
                    _level.StepId,
                    _level.Path ?? string.Empty
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (output.Count != 1 || output[0].BaseObject is not string pageJson)
        {
            throw new InvalidOperationException(
                $"Custom source callback for job '{_level.StepId}' must return one page object.");
        }

        using var document = JsonDocument.Parse(pageJson);
        var page = document.RootElement;
        if (page.ValueKind != JsonValueKind.Object ||
            !page.TryGetProperty("Records", out var recordsElement) ||
            recordsElement.ValueKind != JsonValueKind.Array ||
            !page.TryGetProperty("Done", out var doneElement) ||
            doneElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException(
                $"Custom source callback for job '{_level.StepId}' returned a page with an invalid shape.");
        }

        var nextCursor = page.TryGetProperty("NextCursor", out var cursorElement) &&
                         cursorElement.ValueKind == JsonValueKind.String
            ? cursorElement.GetString()
            : null;
        var done = doneElement.GetBoolean();
        var newRecords = new JsonElement[recordsElement.GetArrayLength()];
        var recordIndex = 0;
        foreach (var record in recordsElement.EnumerateArray())
        {
            if (record.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"Custom source callback for job '{_level.StepId}' returned a non-object record.");
            }
            newRecords[recordIndex++] = record.Clone();
        }

        if (!done && string.IsNullOrWhiteSpace(nextCursor))
        {
            throw new InvalidDataException(
                $"Custom source callback for job '{_level.StepId}' must return NextCursor unless Done is true.");
        }
        if (!done && string.Equals(nextCursor, _cursor, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Custom source callback for job '{_level.StepId}' did not advance its cursor.");
        }
        if (newRecords.Length > _pageSize)
        {
            throw new InvalidDataException(
                $"Custom source callback for job '{_level.StepId}' returned {newRecords.Length} records, " +
                $"exceeding its page size of {_pageSize}.");
        }

        _records = newRecords;
        _recordIndex = 0;
        _cursor = nextCursor;
        _done = done;
    }
}
