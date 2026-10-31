using System.Text.Json;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.Connectors;

internal sealed class RecordFileFormatHandler(
    string name,
    string[] extensions,
    string contentType,
    Func<HierarchySourceLevel, IHierarchyRecordReader>? createSourceReader,
    Action<string, IReadOnlyList<string>, string, string, string?>? finalizeTarget)
{
    public string Name { get; } = name;

    public IReadOnlyList<string> Extensions { get; } = extensions;

    public string ContentType { get; } = contentType;

    public bool SupportsSource => createSourceReader is not null;

    public bool SupportsTarget => finalizeTarget is not null;

    public IHierarchyRecordReader CreateSourceReader(HierarchySourceLevel level)
    {
        return createSourceReader?.Invoke(level)
            ?? throw new InvalidOperationException($"File format '{Name}' does not support sources.");
    }

    public void FinalizeTarget(
        string targetPath,
        IReadOnlyList<string> batchFiles,
        string stepId,
        string optionsJson,
        string? binaryStagingRoot)
    {
        if (finalizeTarget is null)
        {
            throw new InvalidOperationException($"File format '{Name}' does not support targets.");
        }

        finalizeTarget(targetPath, batchFiles, stepId, optionsJson, binaryStagingRoot);
    }
}

internal static class RecordFileFormatRegistry
{
    private static readonly IReadOnlyDictionary<string, RecordFileFormatHandler> Formats =
        new Dictionary<string, RecordFileFormatHandler>(StringComparer.OrdinalIgnoreCase)
        {
            ["jsonl"] = new(
                "jsonl",
                [".jsonl", ".ndjson"],
                "application/x-ndjson",
                HierarchyBatchSource.CreateJsonLinesReader,
                FileBatchTargetWriter.FinalizeJsonLinesTarget),
            ["csv"] = new(
                "csv",
                [".csv"],
                "text/csv",
                level => new CsvHierarchyRecordReader(
                    level,
                    DeserializeOptions<CsvConnectorOptions>(level.FormatOptionsJson, level.StepId)),
                FileBatchTargetWriter.FinalizeCsvTarget),
            ["parquet"] = new(
                "parquet",
                [".parquet"],
                "application/vnd.apache.parquet",
                level => new ParquetHierarchyRecordReader(
                    level,
                    DeserializeOptions<ParquetConnectorOptions>(level.FormatOptionsJson, level)),
                FileBatchTargetWriter.FinalizeParquetTarget),
            ["binary"] = new(
                "binary",
                [".bin"],
                "application/octet-stream",
                level => new BinaryFileHierarchyRecordReader(level),
                FileBatchTargetWriter.FinalizeBinaryTarget),
            ["excel"] = new(
                "excel",
                [".xlsx", ".xlsm", ".xls"],
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                level => new ExcelHierarchyRecordReader(
                    level,
                    ExcelSourceOptions.FromJson(level.FormatOptionsJson, level.StepId)),
                null)
        };

    public static RecordFileFormatHandler GetRequired(string formatType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formatType);
        if (!Formats.TryGetValue(formatType.Trim(), out var handler))
        {
            throw new ArgumentException($"File format '{formatType}' is not supported.", nameof(formatType));
        }

        return handler;
    }

    public static string GetFileExtension(string formatType, IReadOnlyList<string>? customExtensions = null)
    {
        if (string.Equals(formatType, "custom", StringComparison.OrdinalIgnoreCase))
        {
            return customExtensions?.FirstOrDefault() ?? ".bin";
        }

        return GetRequired(formatType).Extensions[0];
    }

    public static string GetContentType(string formatType)
    {
        if (string.Equals(formatType, "custom", StringComparison.OrdinalIgnoreCase))
        {
            return "application/octet-stream";
        }

        return GetRequired(formatType).ContentType;
    }

    public static bool IsBuiltIn(string formatType)
    {
        return Formats.ContainsKey(formatType);
    }

    private static T DeserializeOptions<T>(string optionsJson, HierarchySourceLevel level)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(optionsJson, ObjectStorageConnectorSupport.JsonOptions)
                ?? throw new InvalidDataException(
                    $"Format options for source '{level.StepId}' are empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Format options for source '{level.StepId}' are invalid JSON.",
                exception);
        }
    }

    private static T DeserializeOptions<T>(string optionsJson, string stepId)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(optionsJson, ObjectStorageConnectorSupport.JsonOptions)
                ?? throw new InvalidDataException(
                    $"Format options for source '{stepId}' are empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Format options for source '{stepId}' are invalid JSON.",
                exception);
        }
    }
}
