using System.Globalization;
using System.Text;
using System.Text.Json;
using CsvHelper;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.Connectors;

public sealed class CsvHierarchyRecordReader : IHierarchyRecordReader
{
    private readonly HierarchySourceLevel _level;
    private readonly CsvConnectorOptions _options;
    private readonly CsvReader _csv;
    private readonly HashSet<string> _nullValues;
    private readonly Dictionary<string, string> _columnTypes;
    private readonly HashSet<string> _binaryColumns;
    private string[]? _columnNames;
    private int[]? _columnIndexes;
    private JsonElement? _lookahead;
    private bool _initialized;
    private bool _disposed;

    public CsvHierarchyRecordReader(HierarchySourceLevel level, CsvConnectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(options);
        if (level.FormatType != "csv")
        {
            throw new ArgumentException("A CSV reader requires the CSV format.", nameof(level));
        }

        _level = level;
        _options = options;
        _options.Validate(requireSourceHeaders: true);
        _nullValues = new HashSet<string>(_options.NullValues, StringComparer.Ordinal);
        _columnTypes = new Dictionary<string, string>(_options.ColumnTypes, StringComparer.OrdinalIgnoreCase);
        _binaryColumns = new HashSet<string>(
            _options.BinaryColumns ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        var configuration = options.CreateConfiguration();
        configuration.NewLine = DetectNewLine(
            level.Path!,
            options.CreateEncoding(),
            options.DetectEncodingFromByteOrderMarks,
            configuration.NewLine);
        var stream = new FileStream(
            level.Path!,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var textReader = new StreamReader(
            stream,
            options.CreateEncoding(),
            detectEncodingFromByteOrderMarks: options.DetectEncodingFromByteOrderMarks,
            bufferSize: 1024 * 1024);
        _csv = new CsvReader(textReader, configuration);
    }

    public long RecordCount { get; private set; }

    public async ValueTask<JsonElement?> PeekAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lookahead.HasValue)
        {
            return _lookahead.Value;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (!await _csv.ReadAsync().ConfigureAwait(false))
        {
            return null;
        }

        var row = new Dictionary<string, object?>(_columnNames!.Length, StringComparer.Ordinal);
        var culture = CsvConnectorOptions.ResolveCulture(_options.Culture);
        for (var columnIndex = 0; columnIndex < _columnIndexes!.Length; columnIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rawValue = _csv.GetField(_columnIndexes[columnIndex]);
            row.Add(
                _columnNames[columnIndex],
                ConvertField(rawValue, _columnNames[columnIndex], culture));
        }

        _lookahead = JsonSerializer.SerializeToElement(row);
        RecordCount++;
        return _lookahead.Value;
    }

    public JsonElement Take()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = _lookahead
            ?? throw new InvalidOperationException($"CSV source '{_level.Path}' has no buffered record.");
        _lookahead = null;
        return result;
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _csv.Dispose();
            _disposed = true;
        }

        return ValueTask.CompletedTask;
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        string[] headers;
        if (_options.HasHeaderRecord)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await _csv.ReadAsync().ConfigureAwait(false))
            {
                throw new InvalidDataException($"CSV source '{_level.Path}' is empty and has no header record.");
            }
            _csv.ReadHeader();
            headers = _csv.HeaderRecord
                ?? throw new InvalidDataException($"CSV source '{_level.Path}' has an invalid header record.");
        }
        else
        {
            headers = _options.Headers!;
        }

        if (headers.Length == 0 ||
            headers.Any(string.IsNullOrWhiteSpace) ||
            headers.Distinct(StringComparer.Ordinal).Count() != headers.Length)
        {
            throw new InvalidDataException($"CSV source '{_level.Path}' has empty or duplicate column names.");
        }

        var selectedColumns = _options.Columns is { Length: > 0 }
            ? _options.Columns
            : headers;
        if (_binaryColumns.Any(column =>
                !selectedColumns.Contains(column, StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                $"CSV source '{_level.Path}' must include every Options.BinaryColumns entry in Options.Columns.");
        }
        _columnIndexes = new int[selectedColumns.Length];
        _columnNames = new string[selectedColumns.Length];
        for (var index = 0; index < selectedColumns.Length; index++)
        {
            var sourceIndex = Array.IndexOf(headers, selectedColumns[index]);
            if (sourceIndex < 0)
            {
                throw new InvalidDataException(
                    $"CSV source '{_level.Path}' does not contain configured column '{selectedColumns[index]}'.");
            }

            _columnIndexes[index] = sourceIndex;
            _columnNames[index] = selectedColumns[index];
        }

        _initialized = true;
    }

    private object? ConvertField(string? value, string columnName, CultureInfo culture)
    {
        if (value is null ||
            (_options.EmptyFieldAsNull && value.Length == 0) ||
            _nullValues.Contains(value))
        {
            return null;
        }
        if (_binaryColumns.Contains(columnName))
        {
            return value;
        }
        if (!_columnTypes.TryGetValue(columnName, out var type) ||
            string.IsNullOrWhiteSpace(type) ||
            type.Equals("string", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        try
        {
            return type.Trim().ToLowerInvariant() switch
            {
                "bool" or "boolean" => bool.Parse(value),
                "byte" => byte.Parse(value, NumberStyles.Integer, culture),
                "short" or "int16" => short.Parse(value, NumberStyles.Integer, culture),
                "int" or "int32" => int.Parse(value, NumberStyles.Integer, culture),
                "long" or "int64" => long.Parse(value, NumberStyles.Integer, culture),
                "float" or "single" => float.Parse(value, NumberStyles.Float | NumberStyles.AllowThousands, culture),
                "double" => double.Parse(value, NumberStyles.Float | NumberStyles.AllowThousands, culture),
                "decimal" => decimal.Parse(value, NumberStyles.Number, culture),
                "guid" => Guid.Parse(value),
                "date" => DateOnly.Parse(value, culture),
                "datetime" => DateTime.Parse(value, culture, DateTimeStyles.RoundtripKind),
                "datetimeoffset" => DateTimeOffset.Parse(value, culture, DateTimeStyles.RoundtripKind),
                "json" => ParseJson(value, columnName),
                "binary" => Convert.FromBase64String(value),
                _ => throw new InvalidDataException(
                    $"CSV column '{columnName}' declares unsupported type '{type}'.")
            };
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                $"CSV source '{_level.Path}' contains a value in column '{columnName}' that is not a valid {type}.",
                exception);
        }
    }

    private static string DetectNewLine(
        string path,
        Encoding encoding,
        bool detectEncodingFromByteOrderMarks,
        string fallback)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        using var reader = new StreamReader(
            stream,
            encoding,
            detectEncodingFromByteOrderMarks,
            bufferSize: 4096);

        int character;
        while ((character = reader.Read()) >= 0)
        {
            if (character == '\r')
            {
                return reader.Peek() == '\n' ? "\r\n" : "\r";
            }
            if (character == '\n')
            {
                return "\n";
            }
        }

        return fallback;
    }

    private static JsonElement ParseJson(string value, string columnName)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"CSV column '{columnName}' contains invalid JSON.",
                exception);
        }
    }
}
