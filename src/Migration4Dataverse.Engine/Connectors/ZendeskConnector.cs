using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.Connectors;

internal sealed class ZendeskSourceOptions
{
    private static readonly Regex ResourcePattern = new(
        @"^[A-Za-z][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant);

    public string Resource { get; init; } = "tickets";

    public string? Query { get; init; }

    public string? RecordsPath { get; init; }

    public string? NextLinkPath { get; init; }

    public string? PageSizeParameter { get; init; }

    public int PageSize { get; init; } = 100;

    public int TimeoutSeconds { get; init; } = 100;

    public int MaxPages { get; init; } = 100_000;

    public static ZendeskSourceOptions FromJson(string optionsJson, string stepId)
    {
        return RestJsonConnectorSupport.DeserializeOptions<ZendeskSourceOptions>(optionsJson, stepId);
    }

    public void Validate(string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        if (!ResourcePattern.IsMatch(Resource))
        {
            throw new ArgumentException(
                $"Zendesk source '{stepId}' Resource contains unsupported characters.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TimeoutSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPages);
        if (PageSize > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PageSize),
                $"Zendesk source '{stepId}' PageSize must not exceed 100.");
        }
    }
}

internal sealed class ZendeskHierarchyRecordReader : BufferedJsonHierarchyRecordReader
{
    public ZendeskHierarchyRecordReader(HierarchySourceLevel level, ZendeskSourceOptions options)
        : base(level, "zendesk", cancellationToken => FetchRecordsAsync(level, options, cancellationToken))
    {
    }

    public static async Task<long> CountRecordsAsync(
        HierarchySourceLevel level,
        ZendeskSourceOptions options,
        CancellationToken cancellationToken)
    {
        options.Validate(level.StepId);
        if (string.IsNullOrWhiteSpace(options.Query))
        {
            throw new InvalidOperationException(
                $"Zendesk source '{level.StepId}' can derive a count only when Options.Query uses search.");
        }
        var client = RestJsonConnectorSupport.GetHttpClient(
            level.ConnectorConnection,
            "Zendesk",
            $"source '{level.StepId}'");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var uri = RestJsonConnectorSupport.BuildUri(client, "search/count.json");
        uri = RestJsonConnectorSupport.AddQueryParameter(uri, "query", options.Query);
        var root = await RestJsonConnectorSupport.SendJsonAsync(
            client,
            HttpMethod.Get,
            uri,
            jsonBody: null,
            timeout.Token).ConfigureAwait(false);
        if (!root.TryGetProperty("count", out var count) || !count.TryGetInt64(out var result))
        {
            throw new InvalidDataException(
                $"Zendesk source '{level.StepId}' search count response has no count.");
        }
        return result;
    }

    private static async Task<IReadOnlyList<JsonElement>> FetchRecordsAsync(
        HierarchySourceLevel level,
        ZendeskSourceOptions options,
        CancellationToken cancellationToken)
    {
        options.Validate(level.StepId);
        var client = RestJsonConnectorSupport.GetHttpClient(
            level.ConnectorConnection,
            "Zendesk",
            $"source '{level.StepId}'");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var records = new List<JsonElement>();
        var recordsPath = options.RecordsPath
            ?? (string.IsNullOrWhiteSpace(options.Query) ? options.Resource : "results");
        var pageSizeParameter = options.PageSizeParameter
            ?? (string.IsNullOrWhiteSpace(options.Query) ? "page[size]" : "per_page");
        var requestUri = BuildFirstRequest(client, options, pageSizeParameter);
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
                recordsPath,
                "Zendesk",
                level.StepId);
            foreach (var record in pageRecords.EnumerateArray())
            {
                records.Add(record.Clone());
            }
            var nextLink = GetNextLink(root, options);
            if (!string.IsNullOrWhiteSpace(nextLink))
            {
                requestUri = RestJsonConnectorSupport.ResolveNextLink(requestUri, nextLink);
                continue;
            }
            if (TryGetCursor(root, out var cursor))
            {
                requestUri = RestJsonConnectorSupport.SetQueryParameter(
                    requestUri,
                    "page[after]",
                    cursor);
                continue;
            }
            break;
        }
        return records;
    }

    private static Uri BuildFirstRequest(
        HttpClient client,
        ZendeskSourceOptions options,
        string pageSizeParameter)
    {
        var path = string.IsNullOrWhiteSpace(options.Query)
            ? $"{options.Resource}.json"
            : "search.json";
        var uri = RestJsonConnectorSupport.BuildUri(client, path);
        if (!string.IsNullOrWhiteSpace(options.Query))
        {
            uri = RestJsonConnectorSupport.AddQueryParameter(uri, "query", options.Query);
        }
        return RestJsonConnectorSupport.AddQueryParameter(
            uri,
            pageSizeParameter,
            options.PageSize.ToString(CultureInfo.InvariantCulture));
    }

    private static string? GetNextLink(JsonElement root, ZendeskSourceOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.NextLinkPath))
        {
            return RestJsonConnectorSupport.GetString(root, options.NextLinkPath);
        }
        return RestJsonConnectorSupport.GetString(root, "links.next")
            ?? RestJsonConnectorSupport.GetString(root, "next_page");
    }

    private static bool TryGetCursor(JsonElement root, out string cursor)
    {
        cursor = string.Empty;
        if (!root.TryGetProperty("meta", out var meta) ||
            meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("has_more", out var hasMore) ||
            hasMore.ValueKind != JsonValueKind.True ||
            !meta.TryGetProperty("after_cursor", out var afterCursor) ||
            afterCursor.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        cursor = afterCursor.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(cursor);
    }
}
