using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.Connectors;

internal sealed class ServiceNowSourceOptions
{
    private static readonly Regex TablePattern = new(
        @"^[A-Za-z][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant);

    public string? Table { get; init; }

    public string? Query { get; init; }

    public string[]? Fields { get; init; }

    public string? DisplayValue { get; init; }

    public bool ExcludeReferenceLink { get; init; }

    public int PageSize { get; init; } = 1000;

    public int TimeoutSeconds { get; init; } = 100;

    public int MaxPages { get; init; } = 100_000;

    public static ServiceNowSourceOptions FromJson(string optionsJson, string stepId)
    {
        return RestJsonConnectorSupport.DeserializeOptions<ServiceNowSourceOptions>(optionsJson, stepId);
    }

    public void Validate(string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Table);
        if (!TablePattern.IsMatch(Table))
        {
            throw new ArgumentException(
                $"ServiceNow source '{stepId}' Table contains unsupported characters.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TimeoutSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPages);
        if (PageSize > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PageSize),
                $"ServiceNow source '{stepId}' PageSize must not exceed 10000.");
        }
        if (!string.IsNullOrWhiteSpace(DisplayValue) &&
            DisplayValue.Trim().ToLowerInvariant() is not ("true" or "false" or "all"))
        {
            throw new ArgumentException(
                $"ServiceNow source '{stepId}' DisplayValue must be true, false, or all.");
        }
        if (Fields is not null)
        {
            foreach (var field in Fields)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(field);
            }
        }
    }
}

internal sealed class ServiceNowHierarchyRecordReader : BufferedJsonHierarchyRecordReader
{
    public ServiceNowHierarchyRecordReader(HierarchySourceLevel level, ServiceNowSourceOptions options)
        : base(level, "servicenow", cancellationToken => FetchRecordsAsync(level, options, cancellationToken))
    {
    }

    private static async Task<IReadOnlyList<JsonElement>> FetchRecordsAsync(
        HierarchySourceLevel level,
        ServiceNowSourceOptions options,
        CancellationToken cancellationToken)
    {
        options.Validate(level.StepId);
        var client = RestJsonConnectorSupport.GetHttpClient(
            level.ConnectorConnection,
            "ServiceNow",
            $"source '{level.StepId}'");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var records = new List<JsonElement>();
        for (var page = 0; page < options.MaxPages; page++)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var offset = page * options.PageSize;
            var uri = BuildRequest(client, options, offset);
            var root = await RestJsonConnectorSupport.SendJsonAsync(
                client,
                HttpMethod.Get,
                uri,
                jsonBody: null,
                timeout.Token).ConfigureAwait(false);
            var pageRecords = RestJsonConnectorSupport.RequireArray(
                root,
                "result",
                "ServiceNow",
                level.StepId);
            foreach (var record in pageRecords.EnumerateArray())
            {
                records.Add(record.Clone());
            }
            if (pageRecords.GetArrayLength() < options.PageSize)
            {
                break;
            }
        }
        return records;
    }

    private static Uri BuildRequest(
        HttpClient client,
        ServiceNowSourceOptions options,
        int offset)
    {
        var table = Uri.EscapeDataString(options.Table!);
        var uri = RestJsonConnectorSupport.BuildUri(client, $"api/now/table/{table}");
        uri = RestJsonConnectorSupport.AddQueryParameter(
            uri,
            "sysparm_limit",
            options.PageSize.ToString(CultureInfo.InvariantCulture));
        uri = RestJsonConnectorSupport.AddQueryParameter(
            uri,
            "sysparm_offset",
            offset.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(options.Query))
        {
            uri = RestJsonConnectorSupport.AddQueryParameter(uri, "sysparm_query", options.Query);
        }
        if (options.Fields is { Length: > 0 })
        {
            uri = RestJsonConnectorSupport.AddQueryParameter(
                uri,
                "sysparm_fields",
                string.Join(",", options.Fields));
        }
        if (!string.IsNullOrWhiteSpace(options.DisplayValue))
        {
            uri = RestJsonConnectorSupport.AddQueryParameter(
                uri,
                "sysparm_display_value",
                options.DisplayValue.Trim().ToLowerInvariant());
        }
        if (options.ExcludeReferenceLink)
        {
            uri = RestJsonConnectorSupport.AddQueryParameter(
                uri,
                "sysparm_exclude_reference_link",
                "true");
        }
        return uri;
    }
}
