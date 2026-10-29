using System.Globalization;
using System.Text.Json;
using ExcelDataReader;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.Connectors;

internal sealed class ExcelSourceOptions
{
    public string? Sheet { get; init; }

    public int HeaderRow { get; init; } = 1;

    public bool HasHeaderRecord { get; init; } = true;

    public string[]? Columns { get; init; }

    public Dictionary<string, string> ColumnTypes { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    public bool SkipEmptyRows { get; init; } = true;

    public bool EmptyValueAsNull { get; init; } = true;

    public static ExcelSourceOptions FromJson(string optionsJson, string stepId)
    {
        return RestJsonConnectorSupport.DeserializeOptions<ExcelSourceOptions>(optionsJson, stepId);
    }

    public void Validate(string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(HeaderRow);
        if (!HasHeaderRecord && (Columns is null || Columns.Length == 0))
        {
            throw new ArgumentException(
                $"Excel source '{stepId}' without a header row must declare Options.Columns.");
        }
        if (Columns is not null)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var column in Columns)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(column);
                if (!names.Add(column))
                {
                    throw new ArgumentException(
                        $"Excel source '{stepId}' contains duplicate column '{column}'.");
                }
            }
        }
        if (ColumnTypes is null)
        {
            throw new ArgumentException($"Excel source '{stepId}' ColumnTypes cannot be null.");
        }
    }
}

internal sealed class ExcelHierarchyRecordReader : BufferedJsonHierarchyRecordReader
{
    public ExcelHierarchyRecordReader(HierarchySourceLevel level, ExcelSourceOptions options)
        : base(level, "excel", cancellationToken => FetchRecordsAsync(level, options, cancellationToken))
    {
    }

    public static async Task<long> CountRecordsAsync(
        HierarchySourceLevel level,
        ExcelSourceOptions options,
        CancellationToken cancellationToken)
    {
        var records = await FetchRecordsAsync(level, options, cancellationToken).ConfigureAwait(false);
        return records.Count;
    }

    private static Task<IReadOnlyList<JsonElement>> FetchRecordsAsync(
        HierarchySourceLevel level,
        ExcelSourceOptions options,
        CancellationToken cancellationToken)
    {
        options.Validate(level.StepId);
        var path = level.Path
            ?? throw new ArgumentException($"Excel source '{level.StepId}' has no file path.");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Excel source for job '{level.StepId}' was not found.",
                path);
        }
        cancellationToken.ThrowIfCancellationRequested();
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var records = new List<JsonElement>();
        var sheetFound = string.IsNullOrWhiteSpace(options.Sheet);
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sheetFound &&
                !string.Equals(reader.Name, options.Sheet, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            sheetFound = true;
            ReadSheet(reader, level, options, records, cancellationToken);
            break;
        }
        while (reader.NextResult());

        if (!sheetFound)
        {
            throw new InvalidDataException(
                $"Excel source '{level.StepId}' does not contain sheet '{options.Sheet}'.");
        }
        return Task.FromResult<IReadOnlyList<JsonElement>>(records);
    }

    private static void ReadSheet(
        IExcelDataReader reader,
        HierarchySourceLevel level,
        ExcelSourceOptions options,
        ICollection<JsonElement> records,
        CancellationToken cancellationToken)
    {
        HeaderInfo? headerInfo = null;
        var rowNumber = 0;
        var sawHeader = false;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowNumber++;
            if (rowNumber < options.HeaderRow)
            {
                continue;
            }
            if (!sawHeader)
            {
                if (options.HasHeaderRecord)
                {
                    headerInfo = ReadHeaders(reader, level, options);
                    sawHeader = true;
                    continue;
                }
                headerInfo = new HeaderInfo(
                    options.Columns!,
                    Enumerable.Range(0, options.Columns!.Length).ToArray());
                sawHeader = true;
            }
            if (rowNumber == options.HeaderRow && options.HasHeaderRecord)
            {
                continue;
            }
            var row = new Dictionary<string, object?>(headerInfo!.Columns.Length, StringComparer.Ordinal);
            var hasValue = false;
            for (var index = 0; index < headerInfo.Columns.Length; index++)
            {
                var sourceIndex = headerInfo.Indexes[index];
                var value = sourceIndex < reader.FieldCount
                    ? reader.GetValue(sourceIndex)
                    : null;
                if (!IsEmpty(value))
                {
                    hasValue = true;
                }
                row.Add(
                    headerInfo.Columns[index],
                    ConvertValue(value, headerInfo.Columns[index], level, options));
            }
            if (!hasValue && options.SkipEmptyRows)
            {
                continue;
            }
            records.Add(JsonSerializer.SerializeToElement(row));
        }
        if (options.HasHeaderRecord && !sawHeader)
        {
            throw new InvalidDataException(
                $"Excel source '{level.StepId}' sheet '{reader.Name}' has no header row.");
        }
    }

    private static HeaderInfo ReadHeaders(
        IExcelDataReader reader,
        HierarchySourceLevel level,
        ExcelSourceOptions options)
    {
        var raw = new List<string>();
        for (var index = 0; index < reader.FieldCount; index++)
        {
            var value = reader.GetValue(index);
            raw.Add(IsEmpty(value) ? string.Empty : RestJsonConnectorSupport.FormatInvariant(value).Trim());
        }
        while (raw.Count > 0 && raw[^1].Length == 0)
        {
            raw.RemoveAt(raw.Count - 1);
        }
        if (raw.Count == 0 ||
            raw.Any(string.IsNullOrWhiteSpace) ||
            raw.Distinct(StringComparer.Ordinal).Count() != raw.Count)
        {
            throw new InvalidDataException(
                $"Excel source '{level.StepId}' sheet '{reader.Name}' has empty or duplicate column names.");
        }
        if (options.Columns is null || options.Columns.Length == 0)
        {
            return new HeaderInfo(raw.ToArray(), Enumerable.Range(0, raw.Count).ToArray());
        }
        var indexes = new int[options.Columns.Length];
        for (var index = 0; index < options.Columns.Length; index++)
        {
            var sourceIndex = raw.IndexOf(options.Columns[index]);
            if (sourceIndex < 0)
            {
                throw new InvalidDataException(
                    $"Excel source '{level.StepId}' sheet '{reader.Name}' does not contain configured column '{options.Columns[index]}'.");
            }
            indexes[index] = sourceIndex;
        }
        return new HeaderInfo(options.Columns, indexes);
    }

    private sealed record HeaderInfo(string[] Columns, int[] Indexes);

    private static object? ConvertValue(
        object? value,
        string columnName,
        HierarchySourceLevel level,
        ExcelSourceOptions options)
    {
        if (value is null ||
            (options.EmptyValueAsNull && value is string text && text.Length == 0))
        {
            return null;
        }
        if (!options.ColumnTypes.TryGetValue(columnName, out var type) ||
            string.IsNullOrWhiteSpace(type) ||
            type.Equals("string", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }
        var textValue = RestJsonConnectorSupport.FormatInvariant(value);
        try
        {
            return type.Trim().ToLowerInvariant() switch
            {
                "bool" or "boolean" => value is bool boolean
                    ? boolean
                    : bool.Parse(textValue),
                "byte" => byte.Parse(textValue, NumberStyles.Integer, CultureInfo.InvariantCulture),
                "short" or "int16" => short.Parse(textValue, NumberStyles.Integer, CultureInfo.InvariantCulture),
                "int" or "int32" => int.Parse(textValue, NumberStyles.Integer, CultureInfo.InvariantCulture),
                "long" or "int64" => long.Parse(textValue, NumberStyles.Integer, CultureInfo.InvariantCulture),
                "float" or "single" => float.Parse(textValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture),
                "double" => double.Parse(textValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture),
                "decimal" => decimal.Parse(textValue, NumberStyles.Number, CultureInfo.InvariantCulture),
                "guid" => Guid.Parse(textValue),
                "date" => value is DateTime date
                    ? DateOnly.FromDateTime(date)
                    : DateOnly.Parse(textValue, CultureInfo.InvariantCulture),
                "datetime" => value is DateTime dateTime
                    ? dateTime
                    : DateTime.Parse(textValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                "datetimeoffset" => value is DateTimeOffset dateTimeOffset
                    ? dateTimeOffset
                    : DateTimeOffset.Parse(textValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                "json" => ParseJson(textValue, columnName, level),
                "binary" => Convert.FromBase64String(textValue),
                _ => throw new InvalidDataException(
                    $"Excel column '{columnName}' declares unsupported type '{type}'.")
            };
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                $"Excel source '{level.StepId}' contains a value in column '{columnName}' that is not a valid {type}.",
                exception);
        }
    }

    private static JsonElement ParseJson(
        string value,
        string columnName,
        HierarchySourceLevel level)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Excel source '{level.StepId}' column '{columnName}' contains invalid JSON.",
                exception);
        }
    }

    private static bool IsEmpty(object? value)
    {
        return value is null || (value is string text && text.Length == 0);
    }
}
