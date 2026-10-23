using System.Text.Json;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.Connectors;

public static class HierarchyRecordReaderFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static IHierarchyRecordReader CreateFileReader(HierarchySourceLevel level)
    {
        ArgumentNullException.ThrowIfNull(level);
        return level.Type switch
        {
            "jsonl" => HierarchyBatchSource.CreateJsonLinesReader(level),
            "csv" => new CsvHierarchyRecordReader(
                level,
                DeserializeOptions<CsvConnectorOptions>(level.OptionsJson, level)),
            "parquet" => new ParquetHierarchyRecordReader(
                level,
                DeserializeOptions<ParquetConnectorOptions>(level.OptionsJson, level)),
            "sqlite" or "duckdb" => new DatabaseHierarchyRecordReader(
                level,
                level.DatabaseConnection
                    ?? throw new InvalidOperationException(
                        $"Database source '{level.StepId}' has no runtime connection."),
                DatabaseSourceOptions.FromJson(level.OptionsJson, level.StepId)),
            _ => throw new ArgumentException(
                $"Source type '{level.Type}' is not a file connector.",
                nameof(level))
        };
    }

    public static Task<long> GetMetadataRecordCountAsync(
        HierarchySourceLevel level,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(level);
        if (level.Type != "parquet")
        {
            throw new ArgumentException(
                "Metadata record counts are available only for Parquet sources.",
                nameof(level));
        }

        var options = DeserializeOptions<ParquetConnectorOptions>(level.OptionsJson, level);
        return ParquetHierarchyRecordReader.GetMetadataRecordCountAsync(
            level.Path
                ?? throw new ArgumentException($"Parquet source '{level.StepId}' has no file path."),
            options.CreateParquetOptions(forWriting: false),
            cancellationToken);
    }

    private static T DeserializeOptions<T>(string optionsJson, HierarchySourceLevel level)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(optionsJson, JsonOptions)
                ?? throw new InvalidDataException($"Source options for job '{level.StepId}' are empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Source options for job '{level.StepId}' are invalid JSON.",
                exception);
        }
    }
}
