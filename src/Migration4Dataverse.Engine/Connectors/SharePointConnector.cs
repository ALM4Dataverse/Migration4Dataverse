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

    public bool IncludeDocumentContent { get; init; }

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
                var flattened = options.FlattenFields
                    ? RestJsonConnectorSupport.FlattenObject(record, "fields", preserveTopLevel: true)
                    : record.Clone();
                records.Add(options.IncludeDocumentContent
                    ? await AddDocumentContentAsync(
                        client,
                        level,
                        options,
                        record,
                        flattened,
                        timeout.Token).ConfigureAwait(false)
                    : flattened);
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

    private static async Task<JsonElement> AddDocumentContentAsync(
        HttpClient client,
        HierarchySourceLevel level,
        SharePointSourceOptions options,
        JsonElement listItem,
        JsonElement flattened,
        CancellationToken cancellationToken)
    {
        var stagingRoot = level.BinaryStagingRoot
            ?? throw new InvalidOperationException(
                $"SharePoint source '{level.StepId}' requires a binary staging directory to download document content.");
        if (!listItem.TryGetProperty("driveItem", out var driveItem) ||
            driveItem.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"SharePoint source '{level.StepId}' requested document content for a list item without a driveItem.");
        }

        var itemId = RestJsonConnectorSupport.GetString(driveItem, "id");
        var parentReference = driveItem.TryGetProperty("parentReference", out var parent)
            ? parent
            : default;
        var driveId = parentReference.ValueKind == JsonValueKind.Object
            ? RestJsonConnectorSupport.GetString(parentReference, "driveId")
            : null;
        if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(driveId))
        {
            throw new InvalidDataException(
                $"SharePoint source '{level.StepId}' received a driveItem without its ID or parent drive ID.");
        }
        var fileName = RestJsonConnectorSupport.GetString(driveItem, "name");
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidDataException(
                $"SharePoint source '{level.StepId}' received a driveItem without a file name.");
        }
        string? contentType = null;
        if (driveItem.TryGetProperty("file", out var file) &&
            file.ValueKind == JsonValueKind.Object)
        {
            contentType = RestJsonConnectorSupport.GetString(file, "mimeType");
        }

        var escapedItemId = Uri.EscapeDataString(itemId);
        var contentUri = RestJsonConnectorSupport.BuildUri(
            client,
            $"drives/{Uri.EscapeDataString(driveId)}/items/{escapedItemId}/content");
        using var request = new HttpRequestMessage(HttpMethod.Get, contentUri);
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        contentType ??= response.Content.Headers.ContentType?.MediaType;
        using var writer = MigrationBinaryValue.CreateWriter(stagingRoot, fileName, contentType);
        await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            await stream.CopyToAsync(writer.Stream, cancellationToken).ConfigureAwait(false);
        }
        var value = writer.Complete();
        contentType ??= value.DetectMimeType();

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in flattened.EnumerateObject())
        {
            if (!string.Equals(property.Name, "driveItem", StringComparison.Ordinal))
            {
                fields[property.Name] = property.Value.Clone();
            }
        }
        fields["FileName"] = fileName;
        fields["ContentType"] = contentType;
        fields["Length"] = value.Length;
        fields["Content"] = value;
        return JsonSerializer.SerializeToElement(
            MigrationBinaryValue.PrepareForTransport(fields, stagingRoot));
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
        if (options.IncludeDocumentContent &&
            !expand.Contains("driveItem", StringComparison.OrdinalIgnoreCase))
        {
            expand += ",driveItem";
        }
        uri = RestJsonConnectorSupport.AddQueryParameter(uri, "$expand", expand);
        if (!string.IsNullOrWhiteSpace(options.Filter))
        {
            uri = RestJsonConnectorSupport.AddQueryParameter(uri, "$filter", options.Filter);
        }
        return uri;
    }
}
