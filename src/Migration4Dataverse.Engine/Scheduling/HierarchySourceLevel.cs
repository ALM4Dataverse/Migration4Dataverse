using System.Data.Common;

namespace Migration4Dataverse.Engine.Scheduling;

public sealed class HierarchySourceLevel
{
    public HierarchySourceLevel(
        string jobName,
        string type,
        string? path,
        string keyProperty,
        string? parentKeyProperty,
        int rowsPerParent,
        long expectedRecordCount,
        IEnumerable<HierarchySourceLevel>? children = null,
        string? optionsJson = null,
        string? connectionId = null,
        string? identity = null,
        DbConnection? databaseConnection = null,
        object? connectorConnection = null,
        string? binaryStagingRoot = null,
        string? idProperty = null,
        string? formatType = null,
        string? formatOptionsJson = null,
        IEnumerable<string>? formatExtensions = null,
        string? formatObjectKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyProperty);
        ArgumentOutOfRangeException.ThrowIfNegative(rowsPerParent);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRecordCount);
        var normalizedParentKeyProperty = string.IsNullOrWhiteSpace(parentKeyProperty)
            ? null
            : parentKeyProperty;

        Type = type.Trim().ToLowerInvariant();
        if (Type is not ("file" or "jsonl" or "csv" or "parquet" or "excel" or "binary" or "custom" or "dataverse" or
            "sqlite" or "duckdb" or "sqlserver" or "postgresql" or "mysql" or "oracle" or
            "azureblob" or "s3" or "adlsgen2" or "gcs" or "http" or "odata" or "mongodb" or
            "salesforce" or "sharepoint" or "hubspot" or "zendesk" or "servicenow"))
        {
            throw new ArgumentException($"Unsupported source type '{type}'.", nameof(type));
        }

        if (Type is "file" or "jsonl" or "csv" or "parquet" or "excel" or "binary" && string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("File source types require a path.", nameof(path));
        }
        var normalizedFormatType = string.IsNullOrWhiteSpace(formatType)
            ? Type == "file" ? null : Type
            : formatType.Trim().ToLowerInvariant();
        if (Type == "file" && string.IsNullOrWhiteSpace(normalizedFormatType))
        {
            throw new ArgumentException("File sources require a format type.", nameof(formatType));
        }
        if (Type is "sqlite" or "duckdb" or "sqlserver" or "postgresql" or "mysql" or "oracle" &&
            databaseConnection is null)
        {
            throw new ArgumentException("Database source types require a database connection.", nameof(databaseConnection));
        }
        if (Type is "azureblob" or "s3" or "adlsgen2" or "gcs" or "http" or "odata" or
            "mongodb" or "salesforce" or "sharepoint" or "hubspot" or "zendesk" or
            "servicenow" && connectorConnection is null)
        {
            throw new ArgumentException(
                "Connector-backed source types require a connector connection.",
                nameof(connectorConnection));
        }
        if (Type == "custom" && string.IsNullOrWhiteSpace(identity))
        {
            throw new ArgumentException("Custom sources require a stable identity.", nameof(identity));
        }
        if (normalizedParentKeyProperty is null && rowsPerParent != 0)
        {
            throw new ArgumentException(
                $"Root source '{jobName}' must use zero rows per parent; received parent key " +
                $"'{parentKeyProperty ?? "<null>"}' and {rowsPerParent} rows per parent.",
                nameof(rowsPerParent));
        }
        StepId = jobName;
        Path = string.IsNullOrWhiteSpace(path) || Type is "dataverse" or "mongodb" or
            "salesforce" or "sharepoint" or "hubspot" or "zendesk" or "servicenow"
            ? null
            : Type is "azureblob" or "s3" or "adlsgen2" or "gcs" or "http" or "odata"
                ? path
                : System.IO.Path.GetFullPath(path);
        KeyProperty = keyProperty;
        ParentKeyProperty = normalizedParentKeyProperty;
        RowsPerParent = rowsPerParent;
        ExpectedRecordCount = expectedRecordCount;
        HasExpectedRecordCount = expectedRecordCount > 0;
        Children = (children ?? Array.Empty<HierarchySourceLevel>()).ToArray();
        OptionsJson = string.IsNullOrWhiteSpace(optionsJson) ? "{}" : optionsJson;
        FormatType = normalizedFormatType;
        FormatOptionsJson = string.IsNullOrWhiteSpace(formatOptionsJson)
            ? OptionsJson
            : formatOptionsJson;
        FormatExtensions = (formatExtensions ?? Array.Empty<string>())
            .Select(extension => extension.Trim())
            .Where(extension => extension.Length > 0)
            .ToArray();
        FormatObjectKey = formatObjectKey;
        ConnectionId = connectionId;
        Identity = identity;
        DatabaseConnection = databaseConnection;
        ConnectorConnection = connectorConnection;
        BinaryStagingRoot = string.IsNullOrWhiteSpace(binaryStagingRoot)
            ? null
            : System.IO.Path.GetFullPath(binaryStagingRoot);
        IdProperty = string.IsNullOrWhiteSpace(idProperty) ? null : idProperty;
    }

    public string StepId { get; }

    public string Type { get; }

    public string? Path { get; }

    public string KeyProperty { get; }

    public StringComparison KeyComparison =>
        Type == "dataverse" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public string? ParentKeyProperty { get; }

    public int RowsPerParent { get; }

    public long ExpectedRecordCount { get; internal set; }

    public bool HasExpectedRecordCount { get; internal set; }

    public IReadOnlyList<HierarchySourceLevel> Children { get; }

    public string OptionsJson { get; }

    public string? FormatType { get; }

    public string FormatOptionsJson { get; }

    public IReadOnlyList<string> FormatExtensions { get; }

    public string? FormatObjectKey { get; }

    public string? ConnectionId { get; }

    public string? Identity { get; }

    public DbConnection? DatabaseConnection { get; }

    public object? ConnectorConnection { get; }

    public string? BinaryStagingRoot { get; }

    public string? IdProperty { get; }
}
