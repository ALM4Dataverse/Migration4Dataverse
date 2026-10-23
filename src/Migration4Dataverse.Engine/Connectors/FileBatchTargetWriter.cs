using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CsvHelper;
using Migration4Dataverse.Engine.Scheduling;
using Parquet;
using Parquet.Serialization;
using Parquet.Schema;

namespace Migration4Dataverse.Engine.Connectors;

public static class FileBatchTargetWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static void WriteCsvBatch(
        string targetDirectory,
        string runId,
        string stepId,
        string batchId,
        long sequence,
        string recordsJson,
        string optionsJson)
    {
        var options = DeserializeOptions<CsvConnectorOptions>(optionsJson, "CSV");
        options.Validate();
        if (options.ColumnsByStep is null ||
            !options.ColumnsByStep.TryGetValue(stepId, out var columns) ||
            columns is null ||
            columns.Length == 0)
        {
            throw new InvalidOperationException(
                $"CSV target options must declare ColumnsByStep.{stepId}.");
        }

        var records = ParseRecords(recordsJson, stepId);
        var configuration = options.CreateConfiguration();
        configuration.HasHeaderRecord = false;

        WriteBatchFile(
            targetDirectory,
            runId,
            batchId,
            sequence,
            ".csv",
            stream =>
            {
                using var textWriter = new StringWriter(CultureInfo.InvariantCulture);
                using (var csv = new CsvWriter(textWriter, configuration))
                {
                    if (options.WriteHeaderRecord)
                    {
                        foreach (var column in columns)
                        {
                            csv.WriteField(column);
                        }
                        csv.NextRecord();
                    }

                    foreach (var record in records)
                    {
                        foreach (var column in columns)
                        {
                            if (!record.TryGetProperty(column, out var value))
                            {
                                throw new InvalidDataException(
                                    $"CSV target record for step '{stepId}' is missing configured column '{column}'.");
                            }
                            csv.WriteField(ToCsvValue(value, options.NullValue));
                        }
                        csv.NextRecord();
                    }
                    csv.Flush();
                }

                var encoding = options.CreateEncoding();
                var preamble = encoding.GetPreamble();
                if (preamble.Length > 0)
                {
                    stream.Write(preamble);
                }
                var bytes = encoding.GetBytes(textWriter.ToString());
                stream.Write(bytes);
            });
    }

    public static void WriteJsonLinesBatch(
        string targetDirectory,
        string runId,
        string batchId,
        long sequence,
        string recordsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordsJson);
        using var document = JsonDocument.Parse(recordsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array ||
            document.RootElement.GetArrayLength() == 0)
        {
            throw new InvalidDataException("A JSON Lines target batch must contain a non-empty record array.");
        }

        WriteBatchFile(
            targetDirectory,
            runId,
            batchId,
            sequence,
            ".jsonl",
            stream =>
            {
                using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 1024,
                    leaveOpen: true);
                foreach (var record in document.RootElement.EnumerateArray())
                {
                    if (record.ValueKind != JsonValueKind.Object)
                    {
                        throw new InvalidDataException(
                            "A JSON Lines target batch contains a non-object record.");
                    }
                    writer.Write(record.GetRawText());
                    writer.Write('\n');
                }
                writer.Flush();
            });
    }

    public static void WriteParquetBatch(
        string targetDirectory,
        string runId,
        string jobName,
        string batchId,
        long sequence,
        string recordsJson,
        string optionsJson)
    {
        var options = DeserializeOptions<ParquetConnectorOptions>(optionsJson, "Parquet");
        options.Validate();
        var fieldSchemas = options.GetSchema(jobName);
        var fields = fieldSchemas.Select(ParquetConnectorOptions.CreateDataField).ToArray();
        var schema = new ParquetSchema(fields);
        var records = ParseRecords(recordsJson, jobName);
        var parquetRecords = new List<IDictionary<string, object?>>(records.Count);
        foreach (var record in records)
        {
            var row = new Dictionary<string, object?>(fieldSchemas.Count, StringComparer.Ordinal);
            foreach (var field in fieldSchemas)
            {
                if (!record.TryGetProperty(field.Name, out var value))
                {
                    throw new InvalidDataException(
                        $"Parquet target record for job '{jobName}' is missing schema field '{field.Name}'.");
                }

                row.Add(field.Name, ParquetConnectorOptions.ConvertJsonValue(value, field)!);
            }
            parquetRecords.Add(row);
        }

        var parquetOptions = options.CreateParquetOptions(forWriting: true);
        WriteBatchFile(
            targetDirectory,
            runId,
            batchId,
            sequence,
            ".parquet",
            stream => ParquetSerializer.SerializeUntypedAsync(
                    parquetRecords,
                    schema,
                    stream,
                    parquetOptions,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult());
    }

    private static T DeserializeOptions<T>(string optionsJson, string formatName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(optionsJson);
        try
        {
            return JsonSerializer.Deserialize<T>(optionsJson, JsonOptions)
                ?? throw new InvalidDataException($"{formatName} options JSON did not contain an object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{formatName} target options JSON is invalid.", exception);
        }
    }

    private static List<JsonElement> ParseRecords(string recordsJson, string jobName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordsJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        using var document = JsonDocument.Parse(recordsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array ||
            document.RootElement.GetArrayLength() == 0)
        {
            throw new InvalidDataException($"Target batch for job '{jobName}' must contain a non-empty record array.");
        }

        var records = new List<JsonElement>(document.RootElement.GetArrayLength());
        foreach (var record in document.RootElement.EnumerateArray())
        {
            if (record.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Target batch for job '{jobName}' contains a non-object record.");
            }
            records.Add(record.Clone());
        }

        return records;
    }

    private static string? ToCsvValue(JsonElement value, string nullValue)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => nullValue,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
            _ => throw new InvalidDataException($"CSV target cannot serialize JSON value kind '{value.ValueKind}'.")
        };
    }

    private static void WriteBatchFile(
        string targetDirectory,
        string runId,
        string batchId,
        long sequence,
        string extension,
        Action<FileStream> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentNullException.ThrowIfNull(write);
        if (!string.Equals(Path.GetFileName(batchId), batchId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The batch ID must be a single filename-safe segment.", nameof(batchId));
        }

        var runDirectory = Path.Combine(Path.GetFullPath(targetDirectory), runId);
        Directory.CreateDirectory(runDirectory);
        var sequenceText = sequence.ToString("D12", CultureInfo.InvariantCulture);
        var targetPath = Path.Combine(runDirectory, $"{sequenceText}-{batchId}{extension}");
        var temporaryPath = $"{targetPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 1024 * 1024,
                       FileOptions.WriteThrough))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(targetPath))
            {
                if (!FilesEqual(temporaryPath, targetPath))
                {
                    throw new IOException(
                        $"Target batch file '{targetPath}' already exists with different content.");
                }
                return;
            }

            try
            {
                File.Move(temporaryPath, targetPath);
            }
            catch (IOException) when (File.Exists(targetPath))
            {
                if (!FilesEqual(temporaryPath, targetPath))
                {
                    throw new IOException(
                        $"Target batch file '{targetPath}' was concurrently created with different content.");
                }
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool FilesEqual(string firstPath, string secondPath)
    {
        using var first = new FileStream(firstPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        using var second = new FileStream(secondPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        if (first.Length != second.Length)
        {
            return false;
        }

        Span<byte> firstBuffer = stackalloc byte[64 * 1024];
        Span<byte> secondBuffer = stackalloc byte[64 * 1024];
        while (true)
        {
            var firstRead = first.Read(firstBuffer);
            var secondRead = second.Read(secondBuffer);
            if (firstRead != secondRead)
            {
                return false;
            }
            if (firstRead == 0)
            {
                return true;
            }
            if (!CryptographicOperations.FixedTimeEquals(
                    firstBuffer[..firstRead],
                    secondBuffer[..secondRead]))
            {
                return false;
            }
        }
    }
}
