using System.Globalization;
using System.Text;
using System.Text.Json;
using CsvHelper;
using Migration4Dataverse.Engine.Scheduling;
using Parquet;
using Parquet.Serialization;
using Parquet.Schema;

namespace Migration4Dataverse.Engine.Connectors;

public static class StreamingSourceFileConverter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static void ConvertJsonLinesToCsv(
        string jsonLinesPath,
        string targetPath,
        string jobName,
        string optionsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonLinesPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        var options = DeserializeOptions<CsvConnectorOptions>(optionsJson, jobName, "CSV");
        options.Validate();
        var columns = options.GetSourceColumns(jobName);
        var configuration = options.CreateConfiguration();
        configuration.HasHeaderRecord = false;

        WriteAtomically(targetPath, stream =>
        {
            using var textWriter = new StreamWriter(
                stream,
                options.CreateEncoding(),
                bufferSize: 1024 * 1024,
                leaveOpen: true);
            using var csv = new CsvWriter(textWriter, configuration);
            if (options.HasHeaderRecord)
            {
                foreach (var column in columns)
                {
                    csv.WriteField(column);
                }
                csv.NextRecord();
            }

            var culture = CsvConnectorOptions.ResolveCulture(options.Culture);
            foreach (var record in ReadJsonLines(jsonLinesPath))
            {
                foreach (var column in columns)
                {
                    if (!record.TryGetProperty(column, out var value))
                    {
                        throw new InvalidDataException(
                            $"JSON Lines source for job '{jobName}' is missing configured CSV column '{column}'.");
                    }
                    csv.WriteField(ToCsvValue(value, column, options, culture));
                }
                csv.NextRecord();
            }
            csv.Flush();
            textWriter.Flush();
        });
    }

    public static void ConvertJsonLinesToParquet(
        string jsonLinesPath,
        string targetPath,
        string jobName,
        string optionsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonLinesPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        var options = DeserializeOptions<ParquetConnectorOptions>(optionsJson, jobName, "Parquet");
        options.Validate();
        var fieldSchemas = options.GetSourceSchema(jobName);
        var fields = fieldSchemas.Select(ParquetConnectorOptions.CreateDataField).ToArray();
        var schema = new ParquetSchema(fields);
        var parquetOptions = options.CreateParquetOptions(forWriting: true);
        var batch = new List<IDictionary<string, object?>>(options.TargetRowGroupSize);
        var batchCount = 0;

        WriteAtomically(targetPath, stream =>
        {
            foreach (var record in ReadJsonLines(jsonLinesPath))
            {
                var row = new Dictionary<string, object?>(fieldSchemas.Count, StringComparer.Ordinal);
                foreach (var field in fieldSchemas)
                {
                    if (!record.TryGetProperty(field.Name, out var value))
                    {
                        throw new InvalidDataException(
                            $"JSON Lines source for job '{jobName}' is missing Parquet schema field '{field.Name}'.");
                    }
                    row.Add(field.Name, ParquetConnectorOptions.ConvertJsonValue(value, field));
                }
                batch.Add(row);
                if (batch.Count == options.TargetRowGroupSize)
                {
                    WriteParquetRowGroup(stream, batch, schema, parquetOptions, append: batchCount > 0);
                    batch.Clear();
                    batchCount++;
                }
            }

            if (batch.Count > 0)
            {
                WriteParquetRowGroup(stream, batch, schema, parquetOptions, append: batchCount > 0);
                batchCount++;
            }
            if (batchCount == 0)
            {
                throw new InvalidDataException($"JSON Lines source for job '{jobName}' contains no records.");
            }
        });
    }

    private static T DeserializeOptions<T>(string optionsJson, string jobName, string formatName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(optionsJson);
        try
        {
            return JsonSerializer.Deserialize<T>(optionsJson, JsonOptions)
                ?? throw new InvalidDataException($"{formatName} options for job '{jobName}' are empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"{formatName} options for job '{jobName}' are invalid JSON.",
                exception);
        }
    }

    private static IEnumerable<JsonElement> ReadJsonLines(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024 * 1024);

        long recordNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            recordNumber++;
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"JSON Lines source '{path}' contains invalid JSON at record {recordNumber}.",
                    exception);
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException(
                        $"JSON Lines source '{path}' record {recordNumber} is not an object.");
                }

                yield return document.RootElement.Clone();
            }
        }
    }

    private static string? ToCsvValue(
        JsonElement value,
        string columnName,
        CsvConnectorOptions options,
        CultureInfo culture)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return options.NullValue;
        }

        if (!options.ColumnTypes.TryGetValue(columnName, out var type) ||
            string.IsNullOrWhiteSpace(type) ||
            type.Equals("string", StringComparison.OrdinalIgnoreCase))
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
                _ => value.GetRawText()
            };
        }

        return type.Trim().ToLowerInvariant() switch
        {
            "bool" or "boolean" => value.GetBoolean() ? "true" : "false",
            "byte" => value.GetByte().ToString(culture),
            "short" or "int16" => value.GetInt16().ToString(culture),
            "int" or "int32" => value.GetInt32().ToString(culture),
            "long" or "int64" => value.GetInt64().ToString(culture),
            "float" or "single" => value.GetSingle().ToString(culture),
            "double" => value.GetDouble().ToString(culture),
            "decimal" => value.GetDecimal().ToString(culture),
            "guid" => value.GetGuid().ToString("D"),
            "date" => value.GetString(),
            "datetime" => value.GetDateTime().ToString("O", culture),
            "datetimeoffset" => value.GetDateTimeOffset().ToString("O", culture),
            "json" => value.GetRawText(),
            "binary" => value.GetString(),
            _ => throw new InvalidDataException(
                $"CSV column '{columnName}' declares unsupported type '{type}'.")
        };
    }

    private static void WriteParquetRowGroup(
        Stream stream,
        IReadOnlyCollection<IDictionary<string, object?>> batch,
        ParquetSchema schema,
        ParquetOptions options,
        bool append)
    {
        options.Append = append;
        ParquetSerializer.SerializeUntypedAsync(
                batch,
                schema,
                stream,
                options,
                new Dictionary<string, string>(StringComparer.Ordinal),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    private static void WriteAtomically(string targetPath, Action<FileStream> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentNullException.ThrowIfNull(write);
        var fullTargetPath = Path.GetFullPath(targetPath);
        var targetDirectory = Path.GetDirectoryName(fullTargetPath)
            ?? throw new InvalidOperationException($"Target file '{fullTargetPath}' has no parent directory.");
        Directory.CreateDirectory(targetDirectory);
        var temporaryPath = $"{fullTargetPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.None,
                       bufferSize: 1024 * 1024,
                       FileOptions.WriteThrough))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullTargetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
