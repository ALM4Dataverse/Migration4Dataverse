using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections;
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

    public static void WriteSingleFileTargetBatch(
        string targetFilePath,
        string runId,
        string stepId,
        string batchId,
        long sequence,
        string recordsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFilePath);
        WriteJsonLinesBatch(
            GetSingleFileBatchDirectory(targetFilePath, stepId),
            GetSingleFileRunDirectoryName(runId),
            batchId,
            sequence,
            recordsJson);
    }

    public static void FinalizeSingleFileTarget(
        string targetFilePath,
        string runId,
        string stepId,
        string formatType,
        string optionsJson,
        string? binaryStagingRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(formatType);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var batchDirectory = Path.Combine(
            GetSingleFileBatchDirectory(targetFilePath, stepId),
            GetSingleFileRunDirectoryName(runId));
        var batchFiles = Directory.Exists(batchDirectory)
            ? Directory.GetFiles(batchDirectory, "*.jsonl").OrderBy(path => path, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();

        RecordFileFormatRegistry.GetRequired(formatType).FinalizeTarget(
            targetFilePath,
            batchFiles,
            stepId,
            optionsJson,
            binaryStagingRoot);
    }

    public static IEnumerable<JsonElement> ReadSingleFileTargetRecords(
        string targetFilePath,
        string runId,
        string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        var batchDirectory = Path.Combine(
            GetSingleFileBatchDirectory(targetFilePath, stepId),
            GetSingleFileRunDirectoryName(runId));
        var batchFiles = Directory.Exists(batchDirectory)
            ? Directory.GetFiles(batchDirectory, "*.jsonl").OrderBy(path => path, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
        return ReadSingleFileTargetRecords(batchFiles, stepId);
    }

    public static IEnumerable<IReadOnlyList<JsonElement>> ReadSingleFileTargetBatches(
        string targetFilePath,
        string runId,
        string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        var batchDirectory = Path.Combine(
            GetSingleFileBatchDirectory(targetFilePath, stepId),
            GetSingleFileRunDirectoryName(runId));
        if (!Directory.Exists(batchDirectory))
        {
            yield break;
        }

        foreach (var batchFile in Directory.GetFiles(batchDirectory, "*.jsonl")
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            yield return ReadSingleBatch(batchFile);
        }
    }

    internal static void FinalizeBinaryTarget(
        string targetFilePath,
        IReadOnlyList<string> batchFiles,
        string stepId,
        string optionsJson,
        string? binaryStagingRoot)
    {
        if (string.IsNullOrWhiteSpace(binaryStagingRoot))
        {
            throw new ArgumentException(
                $"Binary file target '{stepId}' requires a binary staging directory.",
                nameof(binaryStagingRoot));
        }
        var options = DeserializeOptions<BinaryFileTargetOptions>(optionsJson, "binary file");
        options.Validate(stepId);
        StreamingSourceFileConverter.WriteAtomically(targetFilePath, output =>
        {
            using var records = ReadSingleFileTargetRecords(batchFiles, stepId).GetEnumerator();
            if (!records.MoveNext())
            {
                throw new InvalidDataException(
                    $"Binary file target '{stepId}' requires exactly one record.");
            }
            var value = MigrationBinaryValue.RestoreFromTransport(
                records.Current,
                binaryStagingRoot);
            if (value is not IDictionary fields ||
                !fields.Contains(options.ContentColumn) ||
                fields[options.ContentColumn] is not MigrationBinaryValue binaryValue)
            {
                throw new InvalidDataException(
                    $"Binary file target '{stepId}' requires a MigrationBinaryValue in field '{options.ContentColumn}'.");
            }
            if (records.MoveNext())
            {
                throw new InvalidDataException(
                    $"Binary file target '{stepId}' accepts exactly one record; received more than one.");
            }

            using var source = binaryValue.OpenRead();
            source.CopyTo(output);
        });
    }

    internal static void FinalizeJsonLinesTarget(
        string targetFilePath,
        IReadOnlyList<string> batchFiles,
        string stepId,
        string optionsJson,
        string? binaryStagingRoot)
    {
        _ = BinaryFileReferenceSupport.GetConfiguredBinaryColumns(optionsJson, stepId);
        StreamingSourceFileConverter.WriteAtomically(targetFilePath, output =>
        {
            using var writer = new StreamWriter(
                output,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1024 * 1024,
                leaveOpen: true);
            foreach (var record in ReadSingleFileTargetRecords(batchFiles, stepId))
            {
                var outputRecord = MaterializeSidecarReferences(
                    record,
                    targetFilePath,
                    stepId,
                    optionsJson,
                    binaryStagingRoot);
                writer.WriteLine(outputRecord.GetRawText());
            }
            writer.Flush();
        });
    }

    internal static void FinalizeCsvTarget(
        string targetFilePath,
        IReadOnlyList<string> batchFiles,
        string stepId,
        string optionsJson,
        string? binaryStagingRoot)
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
        foreach (var binaryColumn in BinaryFileReferenceSupport.GetConfiguredBinaryColumns(optionsJson, stepId))
        {
            if (!columns.Contains(binaryColumn, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"CSV target step '{stepId}' must include every Options.BinaryColumns entry in ColumnsByStep.{stepId}.");
            }
        }

        var configuration = options.CreateConfiguration();
        configuration.HasHeaderRecord = false;
        StreamingSourceFileConverter.WriteAtomically(targetFilePath, stream =>
        {
            using var textWriter = new StreamWriter(
                stream,
                options.CreateEncoding(),
                bufferSize: 1024 * 1024,
                leaveOpen: true);
            using var csv = new CsvWriter(textWriter, configuration);
            if (options.WriteHeaderRecord)
            {
                foreach (var column in columns)
                {
                    csv.WriteField(column);
                }
                csv.NextRecord();
            }

            foreach (var record in ReadSingleFileTargetRecords(batchFiles, stepId))
            {
                var outputRecord = MaterializeSidecarReferences(
                    record,
                    targetFilePath,
                    stepId,
                    optionsJson,
                    binaryStagingRoot,
                    outputColumns: new HashSet<string>(columns, StringComparer.Ordinal));
                foreach (var column in columns)
                {
                    if (!outputRecord.TryGetProperty(column, out var value))
                    {
                        throw new InvalidDataException(
                            $"CSV target record for step '{stepId}' is missing configured column '{column}'.");
                    }
                    csv.WriteField(ToCsvValue(value, options.NullValue));
                }
                csv.NextRecord();
            }
            csv.Flush();
            textWriter.Flush();
        });
    }

    internal static void FinalizeParquetTarget(
        string targetFilePath,
        IReadOnlyList<string> batchFiles,
        string stepId,
        string optionsJson,
        string? binaryStagingRoot)
    {
        var options = DeserializeOptions<ParquetConnectorOptions>(optionsJson, "Parquet");
        options.Validate();
        var schema = options.GetSchema(stepId).ToArray();
        var schemaByName = schema.ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var binaryColumn in BinaryFileReferenceSupport.GetConfiguredBinaryColumns(optionsJson, stepId))
        {
            if (!schemaByName.TryGetValue(binaryColumn, out var fieldSchema) ||
                fieldSchema.Type.Trim().ToLowerInvariant() is not ("string" or "json"))
            {
                throw new InvalidDataException(
                    $"Parquet binary reference column '{binaryColumn}' for target step '{stepId}' must be present in SchemaByStep.{stepId} with a string or json type.");
            }
        }
        if (batchFiles.Count == 0)
        {
            var parquetSchema = new ParquetSchema(schema.Select(ParquetConnectorOptions.CreateDataField));
            var parquetOptions = options.CreateParquetOptions(forWriting: true);
            StreamingSourceFileConverter.WriteAtomically(targetFilePath, stream =>
                ParquetSerializer.SerializeUntypedAsync(
                        Array.Empty<IDictionary<string, object?>>(),
                        parquetSchema,
                        stream,
                        parquetOptions,
                        new Dictionary<string, string>(StringComparer.Ordinal),
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult());
            return;
        }

        var fullTargetPath = Path.GetFullPath(targetFilePath);
        var targetDirectory = Path.GetDirectoryName(fullTargetPath)
            ?? throw new InvalidOperationException($"Target file '{fullTargetPath}' has no parent directory.");
        Directory.CreateDirectory(targetDirectory);
        var mergedJsonLinesPath = Path.Combine(
            targetDirectory,
            $".{Path.GetFileName(fullTargetPath)}.{Guid.NewGuid():N}.jsonl.tmp");
        try
        {
            using (var merged = new FileStream(
                       mergedJsonLinesPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 1024 * 1024,
                       FileOptions.SequentialScan))
            {
                using var writer = new StreamWriter(
                    merged,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 1024 * 1024,
                    leaveOpen: true);
                foreach (var batchFile in batchFiles)
                {
                    foreach (var record in ReadSingleFileTargetRecords([batchFile], stepId))
                    {
                        var outputRecord = MaterializeSidecarReferences(
                            record,
                            targetFilePath,
                            stepId,
                            optionsJson,
                            binaryStagingRoot,
                            schemaByName);
                        writer.WriteLine(JsonSerializer.Serialize(outputRecord, JsonOptions));
                    }
                }
                writer.Flush();
            }

            var sourceOptions = new ParquetConnectorOptions
            {
                CompressionMethod = options.CompressionMethod,
                CompressionLevel = options.CompressionLevel,
                TargetRowGroupSize = options.TargetRowGroupSize,
                MaxSourceRowGroupRows = options.MaxSourceRowGroupRows,
                DictionaryEncodingThreshold = options.DictionaryEncodingThreshold,
                DictionaryEncodingSampleSize = options.DictionaryEncodingSampleSize,
                TreatByteArrayAsString = options.TreatByteArrayAsString,
                TreatBigIntegersAsDates = options.TreatBigIntegersAsDates,
                UseDateOnlyTypeForDates = options.UseDateOnlyTypeForDates,
                SourceSchemaByStep = new Dictionary<string, ParquetFieldSchema[]>(StringComparer.Ordinal)
                {
                    [stepId] = schema
                }
            };
            StreamingSourceFileConverter.ConvertJsonLinesToParquet(
                mergedJsonLinesPath,
                targetFilePath,
                stepId,
                JsonSerializer.Serialize(sourceOptions, JsonOptions));
        }
        finally
        {
            if (File.Exists(mergedJsonLinesPath))
            {
                File.Delete(mergedJsonLinesPath);
            }
        }
    }

    private static JsonElement MaterializeSidecarReferences(
        JsonElement record,
        string targetFilePath,
        string stepId,
        string optionsJson,
        string? binaryStagingRoot,
        IReadOnlyDictionary<string, ParquetFieldSchema>? parquetSchema = null,
        IReadOnlySet<string>? outputColumns = null)
    {
        var binaryColumns = BinaryFileReferenceSupport.GetConfiguredBinaryColumns(optionsJson, stepId);
        var configuredColumns = new HashSet<string>(binaryColumns, StringComparer.OrdinalIgnoreCase);
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in record.EnumerateObject())
        {
            if (!BinaryFileReferenceSupport.TryReadTransportReference(
                    property.Value,
                    out var referencePath,
                    out var transportedBinaryValue))
            {
                fields.Add(property.Name, property.Value.Clone());
                continue;
            }

            if (binaryColumns.Length > 0 && !configuredColumns.Contains(property.Name))
            {
                throw new InvalidDataException(
                    $"Target step '{stepId}' contains a binary sidecar in column '{property.Name}' that is not listed in Options.BinaryColumns.");
            }
            if (outputColumns is not null && !outputColumns.Contains(property.Name))
            {
                throw new InvalidDataException(
                    $"CSV target step '{stepId}' cannot externalize binary column '{property.Name}' because it is not included in ColumnsByStep.{stepId}.");
            }
            if (parquetSchema is not null &&
                (!parquetSchema.TryGetValue(property.Name, out var fieldSchema) ||
                 fieldSchema.Type.Trim().ToLowerInvariant() is not ("string" or "json")))
            {
                throw new InvalidDataException(
                    $"Parquet binary sidecar column '{property.Name}' for target step '{stepId}' must use a string or json schema type.");
            }
            if (string.IsNullOrWhiteSpace(binaryStagingRoot))
            {
                throw new InvalidDataException(
                    $"Target step '{stepId}' requires a binary staging directory to write attachment sidecars.");
            }

            var binaryValue = MigrationBinaryValue.RestoreFromTransport(
                transportedBinaryValue,
                binaryStagingRoot) as MigrationBinaryValue
                ?? throw new InvalidDataException(
                    $"Binary sidecar in target column '{property.Name}' for step '{stepId}' has no binary content.");
            var relativeReference = WriteSidecarFile(
                targetFilePath,
                referencePath,
                binaryValue);
            fields.Add(property.Name, relativeReference);
        }
        return JsonSerializer.SerializeToElement(fields, JsonOptions);
    }

    private static string WriteSidecarFile(
        string targetFilePath,
        string relativePath,
        MigrationBinaryValue binaryValue)
    {
        var sidecarRoot = Path.GetFullPath(targetFilePath) + ".attachments";
        var outputPath = BinaryFileReferenceSupport.ResolveSidecarPath(sidecarRoot, relativePath);
        var outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException($"Attachment path '{outputPath}' has no parent directory.");
        Directory.CreateDirectory(outputDirectory);

        if (File.Exists(outputPath))
        {
            var existing = new FileInfo(outputPath);
            if (existing.Length != binaryValue.Length)
            {
                throw new IOException(
                    $"Attachment sidecar '{outputPath}' already exists with different content.");
            }
            using var existingStream = new FileStream(
                outputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                FileOptions.SequentialScan);
            var existingHash = Convert.ToHexString(SHA256.HashData(existingStream));
            if (!string.Equals(existingHash, binaryValue.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    $"Attachment sidecar '{outputPath}' already exists with different content.");
            }
        }
        else
        {
            StreamingSourceFileConverter.WriteAtomically(outputPath, output =>
            {
                using var source = binaryValue.OpenRead();
                source.CopyTo(output);
            });
        }

        var targetDirectory = Path.GetDirectoryName(Path.GetFullPath(targetFilePath))
            ?? throw new InvalidOperationException(
                $"Target file '{targetFilePath}' has no parent directory.");
        return Path.GetRelativePath(targetDirectory, outputPath).Replace('\\', '/');
    }

    private static IEnumerable<JsonElement> ReadSingleFileTargetRecords(
        IReadOnlyList<string> batchFiles,
        string stepId)
    {
        foreach (var batchFile in batchFiles)
        {
            using var stream = new FileStream(
                batchFile,
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
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException(
                        $"Staged file target batch '{batchFile}' contains a non-object record at line {recordNumber}.");
                }
                yield return document.RootElement.Clone();
            }
        }
    }

    private static IReadOnlyList<JsonElement> ReadSingleBatch(string batchFile)
    {
        var records = new List<JsonElement>();
        using var stream = new FileStream(
            batchFile,
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
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"Staged file target batch '{batchFile}' contains a non-object record at line {recordNumber}.");
            }
            records.Add(document.RootElement.Clone());
        }

        return records;
    }

    private static string GetSingleFileBatchDirectory(string targetFilePath, string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        var fullPath = Path.GetFullPath(targetFilePath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"Target file '{fullPath}' has no parent directory.");
        var stepHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stepId))).ToLowerInvariant();
        return Path.Combine(directory, $".{Path.GetFileName(fullPath)}.migration-batches", stepHash);
    }

    private static string GetSingleFileRunDirectoryName(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(runId));
        return $"run-{Convert.ToHexString(hash).ToLowerInvariant()}";
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
