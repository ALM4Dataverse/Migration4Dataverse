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
        DbConnection? databaseConnection = null)
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
        if (Type is not ("jsonl" or "csv" or "parquet" or "custom" or "dataverse" or "sqlite" or "duckdb"))
        {
            throw new ArgumentException($"Unsupported source type '{type}'.", nameof(type));
        }

        if (Type is "jsonl" or "csv" or "parquet" && string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("File source types require a path.", nameof(path));
        }
        if (Type is "sqlite" or "duckdb" && databaseConnection is null)
        {
            throw new ArgumentException("Database source types require a database connection.", nameof(databaseConnection));
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
        Path = string.IsNullOrWhiteSpace(path) || Type == "dataverse"
            ? null
            : System.IO.Path.GetFullPath(path);
        KeyProperty = keyProperty;
        ParentKeyProperty = normalizedParentKeyProperty;
        RowsPerParent = rowsPerParent;
        ExpectedRecordCount = expectedRecordCount;
        HasExpectedRecordCount = expectedRecordCount > 0;
        Children = (children ?? Array.Empty<HierarchySourceLevel>()).ToArray();
        OptionsJson = string.IsNullOrWhiteSpace(optionsJson) ? "{}" : optionsJson;
        ConnectionId = connectionId;
        Identity = identity;
        DatabaseConnection = databaseConnection;
    }

    public string StepId { get; }

    public string Type { get; }

    public string? Path { get; }

    public string KeyProperty { get; }

    public string? ParentKeyProperty { get; }

    public int RowsPerParent { get; }

    public long ExpectedRecordCount { get; internal set; }

    public bool HasExpectedRecordCount { get; internal set; }

    public IReadOnlyList<HierarchySourceLevel> Children { get; }

    public string OptionsJson { get; }

    public string? ConnectionId { get; }

    public string? Identity { get; }

    public DbConnection? DatabaseConnection { get; }
}
