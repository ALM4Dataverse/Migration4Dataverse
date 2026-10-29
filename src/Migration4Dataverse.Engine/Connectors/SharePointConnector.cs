using System.Text.Json;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.Connectors;

internal sealed class SharePointSourceOptions
{
    public string? SiteId { get; init; }

    public string? ListId { get; init; }

    public string[]? Fields { get; init; }

    public string? Filter { get; init; }

    public string? Expand { get; init; }

    public int PageSize { get; init; } = 100;

    public bool FlattenFields { get; init; } = true;

    public int TimeoutSeconds { get; init; } = 100;

    public int MaxPages { get; init; } = 100_000;

    public static SharePointSourceOptions FromJson(string optionsJson, string stepId)
    {
        return RestJsonConnectorSupport.DeserializeOptions<SharePointSourceOptions>(optionsJson, stepId);
    }

    public void Validate(string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(SiteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ListId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TimeoutSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPages);
        if (PageSize > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PageSize),
                $"SharePoint source '{stepId}' PageSize must not exceed 100.");
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

internal sealed class SharePointHierarchyRecordReader : BufferedJsonHierarchyRecordReader
{
    public SharePointHierarchyRecordReader(HierarchySourceLevel level, SharePointSourceOptions options)
        : base(level, "sharepoint", cancellationToken => FetchRecordsAsync(level, options, cancellationToken))
    {
    }

    private static async Task<IReadOnlyList<JsonElement>> FetchRecordsAsync(
        HierarchySourceLevel level,
        SharePointSourceOptions options,
        CancellationToken cancellationToken)
    {
        options.Validate(level.StepId);
        var client = RestJsonConnectorSupport.GetHttpClient(
            level.ConnectorConnection,
            "SharePoint",
            $"source '{level.StepId}'");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var records = new List<JsonElement>();
        var requestUri = BuildFirstRequest(client, options);
        for (var page = 0; page < options.MaxPages; page++)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var root = await RestJsonConnectorSupport.SendJsonAsync(
                client,
                HttpMethod.Get,
                requestUri,
                jsonBody: null,
                timeout.Token).ConfigureAwait(false);
            var pageRecords = RestJsonConnectorSupport.RequireArray(
                root,
                "value",
                "SharePoint",
                level.StepId);
            foreach (var record in pageRecords.EnumerateArray())
            {
                records.Add(options.FlattenFields
                    ? RestJsonConnectorSupport.FlattenObject(record, "fields", preserveTopLevel: true)
                    : record.Clone());
            }
            var nextLink = RestJsonConnectorSupport.GetString(root, "@odata.nextLink");
            if (string.IsNullOrWhiteSpace(nextLink))
            {
                break;
            }
            requestUri = RestJsonConnectorSupport.ResolveNextLink(requestUri, nextLink);
        }
        return records;
    }

    private static Uri BuildFirstRequest(HttpClient client, SharePointSourceOptions options)
    {
        var siteId = Uri.EscapeDataString(options.SiteId!);
        var listId = Uri.EscapeDataString(options.ListId!);
        var uri = RestJsonConnectorSupport.BuildUri(
            client,
            $"sites/{siteId}/lists/{listId}/items");
        uri = RestJsonConnectorSupport.AddQueryParameter(
            uri,
            "$top",
            options.PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var expand = options.Expand;
        if (string.IsNullOrWhiteSpace(expand) &&
            options.Fields is { Length: > 0 })
        {
            expand = $"fields($select={string.Join(",", options.Fields)})";
        }
        if (string.IsNullOrWhiteSpace(expand))
        {
            expand = "fields";
        }
        uri = RestJsonConnectorSupport.AddQueryParameter(uri, "$expand", expand);
        if (!string.IsNullOrWhiteSpace(options.Filter))
        {
            uri = RestJsonConnectorSupport.AddQueryParameter(uri, "$filter", options.Filter);
        }
        return uri;
    }
}
