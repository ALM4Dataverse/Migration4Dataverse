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
            "excel" => new ExcelHierarchyRecordReader(
                level,
                ExcelSourceOptions.FromJson(level.OptionsJson, level.StepId)),
            "salesforce" => new SalesforceHierarchyRecordReader(
                level,
                SalesforceSourceOptions.FromJson(level.OptionsJson, level.StepId)),
            "sharepoint" => new SharePointHierarchyRecordReader(
                level,
                SharePointSourceOptions.FromJson(level.OptionsJson, level.StepId)),
            "hubspot" => new HubSpotHierarchyRecordReader(
                level,
                HubSpotSourceOptions.FromJson(level.OptionsJson, level.StepId)),
            "zendesk" => new ZendeskHierarchyRecordReader(
                level,
                ZendeskSourceOptions.FromJson(level.OptionsJson, level.StepId)),
            "servicenow" => new ServiceNowHierarchyRecordReader(
                level,
                ServiceNowSourceOptions.FromJson(level.OptionsJson, level.StepId)),
            "sqlite" or "duckdb" or "sqlserver" or "postgresql" or "mysql" or "oracle" => new DatabaseHierarchyRecordReader(
                level,
                level.DatabaseConnection
                    ?? throw new InvalidOperationException(
                        $"Database source '{level.StepId}' has no runtime connection."),
                DatabaseSourceOptions.FromJson(level.OptionsJson, level.StepId)),
            "azureblob" => CreateObjectStorageReader(level, "azureblob"),
            "s3" => CreateObjectStorageReader(level, "s3"),
            "adlsgen2" => CreateObjectStorageReader(level, "adlsgen2"),
            "gcs" => CreateObjectStorageReader(level, "gcs"),
            "mongodb" => CreateMongoReader(level),
            "http" => CreateHttpReader(level, "http"),
            "odata" => CreateHttpReader(level, "odata"),
            _ => throw new ArgumentException(
                $"Source type '{level.Type}' is not a file connector.",
                nameof(level))
        };
    }

    private static IHierarchyRecordReader CreateObjectStorageReader(
        HierarchySourceLevel level,
        string connectorType)
    {
        var options = DeserializeOptions<ObjectStorageSourceOptions>(level.OptionsJson, level);
        options.Validate(level.StepId);
        var connection = level.ConnectorConnection
            ?? throw new InvalidOperationException(
                $"{connectorType} source '{level.StepId}' has no runtime connection.");
        var prefix = options.Prefix ?? level.Path;
        if (connectorType == "gcs")
        {
            var gcsClient = ObjectStorageConnectionSupport.GetGcsClient(
                connection,
                $"source '{level.StepId}'");
            var gcsBucket = ObjectStorageConnectionSupport.GetGcsBucket(
                options.Bucket,
                $"source '{level.StepId}'");
            var gcsItems = GoogleCloudStorageConnectorSupport.ListItemsAsync(
                    gcsClient,
                    gcsBucket,
                    options,
                    prefix,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return new ObjectStorageHierarchyRecordReader(level, gcsItems, options);
        }

        if (connectorType == "adlsgen2")
        {
            var fileSystem = DataLakeConnectionSupport.GetFileSystemClient(
                connection,
                options.FileSystem ?? options.Container,
                $"source '{level.StepId}'");
            var dataLakeItems = DataLakeConnectionSupport.ListItemsAsync(
                    fileSystem,
                    options,
                    prefix,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return new ObjectStorageHierarchyRecordReader(level, dataLakeItems, options);
        }

        if (connectorType == "azureblob")
        {
            var container = ObjectStorageConnectionSupport.GetAzureBlobContainer(
                connection,
                options.Container,
                $"source '{level.StepId}'");
            var items = ObjectStorageHierarchyRecordReader.ListAzureBlobItemsAsync(
                    container,
                    options,
                    prefix,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return new ObjectStorageHierarchyRecordReader(level, items, options);
        }

        var client = ObjectStorageConnectionSupport.GetS3Client(
            connection,
            $"source '{level.StepId}'");
        var bucket = ObjectStorageConnectionSupport.GetS3Bucket(
            options.Bucket,
            $"source '{level.StepId}'");
        var s3Items = ObjectStorageHierarchyRecordReader.ListS3ItemsAsync(
                client,
                bucket,
                options,
                prefix,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        return new ObjectStorageHierarchyRecordReader(level, s3Items, options);
    }

    private static IHierarchyRecordReader CreateMongoReader(HierarchySourceLevel level)
    {
        var options = DeserializeOptions<MongoSourceOptions>(level.OptionsJson, level);
        options.Validate(level.StepId);
        var connection = level.ConnectorConnection
            ?? throw new InvalidOperationException(
                $"MongoDB source '{level.StepId}' has no runtime connection.");
        var database = MongoConnectionSupport.GetDatabase(
            connection,
            options.Database,
            $"source '{level.StepId}'");
        return new MongoHierarchyRecordReader(level, database, options);
    }

    private static IHierarchyRecordReader CreateHttpReader(
        HierarchySourceLevel level,
        string connectorType)
    {
        var options = DeserializeOptions<HttpSourceOptions>(level.OptionsJson, level);
        if (connectorType == "odata")
        {
            options = HttpSourceOptions.ApplyODataDefaults(options);
        }
        options.Validate(level.StepId, connectorType);
        return new HttpHierarchyRecordReader(level, options, connectorType);
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
