using System.Reflection;
using System.Text.Json;
using Migration4Dataverse.Engine.Scheduling;
using Parquet;
using Parquet.Schema;

namespace Migration4Dataverse.Engine.Connectors;

public sealed class ParquetHierarchyRecordReader : IHierarchyRecordReader
{
    private static readonly MethodInfo ReadNonNullableValuesMethod = typeof(ParquetHierarchyRecordReader)
        .GetMethod(nameof(ReadNonNullableValuesAsync), BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find the Parquet non-nullable column reader.");
    private static readonly MethodInfo ReadNullableValuesMethod = typeof(ParquetHierarchyRecordReader)
        .GetMethod(nameof(ReadNullableValuesAsync), BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find the Parquet nullable column reader.");

    private readonly HierarchySourceLevel _level;
    private readonly ParquetConnectorOptions _options;
    private readonly ParquetOptions _parquetOptions;
    private ParquetReader? _reader;
    private DataField[]? _fields;
    private JsonElement[] _rowGroupRows = Array.Empty<JsonElement>();
    private JsonElement? _lookahead;
    private int _rowGroupIndex;
    private int _rowIndex;
    private bool _disposed;

    public ParquetHierarchyRecordReader(HierarchySourceLevel level, ParquetConnectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(options);
        if (level.FormatType != "parquet")
        {
            throw new ArgumentException("A Parquet reader requires the Parquet format.", nameof(level));
        }

        _level = level;
        _options = options;
        _options.Validate();
        _parquetOptions = options.CreateParquetOptions(forWriting: false);
    }

    public long RecordCount { get; private set; }

    public static async Task<long> GetMetadataRecordCountAsync(
        string path,
        ParquetOptions parquetOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(parquetOptions);

        await using var reader = await ParquetReader.CreateAsync(
            path,
            parquetOptions,
            cancellationToken).ConfigureAwait(false);
        long recordCount = 0;
        for (var rowGroupIndex = 0; rowGroupIndex < reader.RowGroupCount; rowGroupIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var rowGroup = reader.OpenRowGroupReader(rowGroupIndex);
            recordCount = checked(recordCount + rowGroup.RowCount);
        }

        return recordCount;
    }

    public async ValueTask<JsonElement?> PeekAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lookahead.HasValue)
        {
            return _lookahead.Value;
        }

        cancellationToken.ThrowIfCancellationRequested();
        while (_rowIndex >= _rowGroupRows.Length)
        {
            if (!await LoadNextRowGroupAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
        }

        _lookahead = _rowGroupRows[_rowIndex];
        RecordCount++;
        return _lookahead.Value;
    }

    public JsonElement Take()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = _lookahead
            ?? throw new InvalidOperationException($"Parquet source '{_level.Path}' has no buffered record.");
        _lookahead = null;
        _rowIndex++;
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_reader is not null)
        {
            await _reader.DisposeAsync().ConfigureAwait(false);
        }
        _disposed = true;
    }

    private async Task<bool> LoadNextRowGroupAsync(CancellationToken cancellationToken)
    {
        _reader ??= await ParquetReader.CreateAsync(
            _level.Path!,
            _parquetOptions,
            cancellationToken).ConfigureAwait(false);
        _fields ??= SelectFields(_reader.Schema.GetDataFields());
        while (_rowGroupIndex < _reader.RowGroupCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var rowGroup = _reader.OpenRowGroupReader(_rowGroupIndex++);
            if (rowGroup.RowCount > _options.MaxSourceRowGroupRows)
            {
                throw new InvalidDataException(
                    $"Parquet source '{_level.Path}' has a row group with {rowGroup.RowCount} rows, exceeding " +
                    $"MaxSourceRowGroupRows ({_options.MaxSourceRowGroupRows}).");
            }

            var rowCount = checked((int)rowGroup.RowCount);
            if (rowCount == 0)
            {
                _rowGroupRows = Array.Empty<JsonElement>();
                _rowIndex = 0;
                continue;
            }

            var columns = new object?[_fields.Length][];
            for (var fieldIndex = 0; fieldIndex < _fields.Length; fieldIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var field = _fields[fieldIndex];
                if (field.IsArray)
                {
                    throw new InvalidDataException(
                        $"Parquet source '{_level.Path}' contains array field '{field.Name}', which is not supported by the hierarchy record contract.");
                }

                var values = await ReadColumnValuesAsync(
                        rowGroup,
                        field,
                        rowCount,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (values.Length != rowCount)
                {
                    throw new InvalidDataException(
                        $"Parquet source field '{field.Name}' contains {values.Length} values in a {rowCount}-row group.");
                }
                columns[fieldIndex] = values;
            }

            _rowGroupRows = new JsonElement[rowCount];
            for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = new Dictionary<string, object?>(_fields.Length, StringComparer.Ordinal);
                for (var fieldIndex = 0; fieldIndex < _fields.Length; fieldIndex++)
                {
                    record.Add(_fields[fieldIndex].Name, columns[fieldIndex][rowIndex]);
                }
                _rowGroupRows[rowIndex] = JsonSerializer.SerializeToElement(record);
            }

            _rowIndex = 0;
            return true;
        }

        _rowGroupRows = Array.Empty<JsonElement>();
        _rowIndex = 0;
        return false;
    }

    private static async Task<object?[]> ReadColumnValuesAsync(
        ParquetRowGroupReader rowGroup,
        DataField field,
        int rowCount,
        CancellationToken cancellationToken)
    {
        if (field.ClrType == typeof(string))
        {
            var values = new string?[rowCount];
            await rowGroup.ReadAsync(field, values.AsMemory(), null, cancellationToken).ConfigureAwait(false);
            return values.Cast<object?>().ToArray();
        }

        if (field.ClrType == typeof(byte[]))
        {
            byte[]?[] values = new byte[]?[rowCount];
            await rowGroup.ReadAsync(field, values.AsMemory(), null, cancellationToken).ConfigureAwait(false);
            return values.Cast<object?>().ToArray();
        }

        if (field.ClrType == typeof(ReadOnlyMemory<char>))
        {
            if (field.IsNullable)
            {
                var values = new ReadOnlyMemory<char>?[rowCount];
                await rowGroup.ReadAsync(field, values.AsMemory(), null, cancellationToken).ConfigureAwait(false);
                return values.Select(value => value?.ToString()).Cast<object?>().ToArray();
            }

            var nonNullableValues = new ReadOnlyMemory<char>[rowCount];
            await rowGroup.ReadAsync(
                    field,
                    nonNullableValues.AsMemory(),
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
            return nonNullableValues.Select(value => value.ToString()).Cast<object?>().ToArray();
        }

        if (!field.ClrType.IsValueType)
        {
            throw new InvalidDataException(
                $"Parquet source field '{field.Name}' uses unsupported CLR type '{field.ClrType}'.");
        }

        var method = field.IsNullable ? ReadNullableValuesMethod : ReadNonNullableValuesMethod;
        var reader = (Task<object?[]>)method.MakeGenericMethod(field.ClrType)
            .Invoke(null, new object[] { rowGroup, field, rowCount, cancellationToken })!;
        var columnValues = await reader.ConfigureAwait(false);
        return field.ClrType == typeof(ReadOnlyMemory<char>)
            ? columnValues.Select(value => value is ReadOnlyMemory<char> text ? text.ToString() : value).ToArray()
            : columnValues;
    }

    private static async Task<object?[]> ReadNonNullableValuesAsync<T>(
        ParquetRowGroupReader rowGroup,
        DataField field,
        int rowCount,
        CancellationToken cancellationToken)
        where T : struct
    {
        var values = new T[rowCount];
        await rowGroup.ReadAsync(field, values.AsMemory(), null, cancellationToken).ConfigureAwait(false);
        return values.Cast<object?>().ToArray();
    }

    private static async Task<object?[]> ReadNullableValuesAsync<T>(
        ParquetRowGroupReader rowGroup,
        DataField field,
        int rowCount,
        CancellationToken cancellationToken)
        where T : struct
    {
        var values = new T?[rowCount];
        await rowGroup.ReadAsync(field, values.AsMemory(), null, cancellationToken).ConfigureAwait(false);
        return values.Cast<object?>().ToArray();
    }

    private DataField[] SelectFields(DataField[] availableFields)
    {
        if (availableFields.Length == 0)
        {
            throw new InvalidDataException($"Parquet source '{_level.Path}' has no data fields.");
        }

        DataField[] selectedFields;
        if (_options.Columns is not { Length: > 0 })
        {
            selectedFields = availableFields;
        }
        else
        {
            var fieldsByName = availableFields.ToDictionary(field => field.Name, StringComparer.Ordinal);
            selectedFields = new DataField[_options.Columns.Length];
            for (var index = 0; index < _options.Columns.Length; index++)
            {
                if (!fieldsByName.TryGetValue(_options.Columns[index], out var field))
                {
                    throw new InvalidDataException(
                        $"Parquet source '{_level.Path}' does not contain configured column '{_options.Columns[index]}'.");
                }
                selectedFields[index] = field;
            }
        }

        if (selectedFields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != selectedFields.Length)
        {
            throw new InvalidDataException($"Parquet source '{_level.Path}' contains duplicate data field names.");
        }

        foreach (var binaryColumn in _options.BinaryColumns ?? Array.Empty<string>())
        {
            var field = selectedFields.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, binaryColumn, StringComparison.OrdinalIgnoreCase));
            if (field is null)
            {
                throw new InvalidDataException(
                    $"Parquet source '{_level.Path}' must include every Options.BinaryColumns entry in Options.Columns.");
            }
            if (field.ClrType != typeof(string) &&
                field.ClrType != typeof(ReadOnlyMemory<char>))
            {
                throw new InvalidDataException(
                    $"Parquet binary reference column '{binaryColumn}' in source '{_level.Path}' must use a string schema, not '{field.ClrType.FullName}'.");
            }
        }

        return selectedFields;
    }
}
