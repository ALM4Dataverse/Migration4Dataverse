using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.Connectors;

internal sealed class HubSpotSourceOptions
{
    private static readonly Regex ObjectTypePattern = new(
        @"^[A-Za-z][A-Za-z0-9_]*$",
        RegexOptions.CultureInvariant);

    public string ObjectType { get; init; } = "contacts";

    public string[]? Properties { get; init; }

    public JsonElement? FilterGroups { get; init; }

    public JsonElement? Sorts { get; init; }

    public int PageSize { get; init; } = 100;

    public bool FlattenProperties { get; init; } = true;

    public int TimeoutSeconds { get; init; } = 100;

    public int MaxPages { get; init; } = 100_000;

    public bool UsesSearch => FilterGroups is not null || Sorts is not null;

    public static HubSpotSourceOptions FromJson(string optionsJson, string stepId)
    {
        return RestJsonConnectorSupport.DeserializeOptions<HubSpotSourceOptions>(optionsJson, stepId);
    }

    public void Validate(string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        if (!ObjectTypePattern.IsMatch(ObjectType))
        {
            throw new ArgumentException(
                $"HubSpot source '{stepId}' ObjectType contains unsupported characters.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TimeoutSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPages);
        if (PageSize > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PageSize),
                $"HubSpot source '{stepId}' PageSize must not exceed 100.");
        }
        if (Properties is not null)
        {
            foreach (var property in Properties)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(property);
            }
        }
        if (FilterGroups is { } filterGroups && filterGroups.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException(
                $"HubSpot source '{stepId}' FilterGroups must be a JSON array.");
        }
        if (Sorts is { } sorts && sorts.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException(
                $"HubSpot source '{stepId}' Sorts must be a JSON array.");
        }
    }
}

internal sealed class HubSpotHierarchyRecordReader : BufferedJsonHierarchyRecordReader
{
    public HubSpotHierarchyRecordReader(HierarchySourceLevel level, HubSpotSourceOptions options)
        : base(level, "hubspot", cancellationToken => FetchRecordsAsync(level, options, cancellationToken))
    {
    }

    public static async Task<long> CountRecordsAsync(
        HierarchySourceLevel level,
        HubSpotSourceOptions options,
        CancellationToken cancellationToken)
    {
        options.Validate(level.StepId);
        if (!options.UsesSearch)
        {
            throw new InvalidOperationException(
                $"HubSpot source '{level.StepId}' can derive a count only when FilterGroups or Sorts use the search API.");
        }
        var client = RestJsonConnectorSupport.GetHttpClient(
            level.ConnectorConnection,
            "HubSpot",
            $"source '{level.StepId}'");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var body = CreateSearchBody(options, after: null, limit: 1);
        var root = await RestJsonConnectorSupport.SendJsonAsync(
            client,
            HttpMethod.Post,
            RestJsonConnectorSupport.BuildUri(
                client,
                $"crm/v3/objects/{options.ObjectType}/search"),
            body,
            timeout.Token).ConfigureAwait(false);
        if (!root.TryGetProperty("total", out var total) || !total.TryGetInt64(out var count))
        {
            throw new InvalidDataException(
                $"HubSpot source '{level.StepId}' search response has no total.");
        }
        return count;
    }

    private static async Task<IReadOnlyList<JsonElement>> FetchRecordsAsync(
        HierarchySourceLevel level,
        HubSpotSourceOptions options,
        CancellationToken cancellationToken)
    {
        options.Validate(level.StepId);
        var client = RestJsonConnectorSupport.GetHttpClient(
            level.ConnectorConnection,
            "HubSpot",
            $"source '{level.StepId}'");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        return options.UsesSearch
            ? await FetchSearchRecordsAsync(client, level, options, timeout.Token).ConfigureAwait(false)
            : await FetchListRecordsAsync(client, level, options, timeout.Token).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<JsonElement>> FetchListRecordsAsync(
        HttpClient client,
        HierarchySourceLevel level,
        HubSpotSourceOptions options,
        CancellationToken cancellationToken)
    {
        var records = new List<JsonElement>();
        string? after = null;
        for (var page = 0; page < options.MaxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = RestJsonConnectorSupport.BuildUri(
                client,
                $"crm/v3/objects/{options.ObjectType}");
            uri = RestJsonConnectorSupport.AddQueryParameter(
                uri,
                "limit",
                options.PageSize.ToString(CultureInfo.InvariantCulture));
            if (options.Properties is { Length: > 0 })
            {
                uri = RestJsonConnectorSupport.AddQueryParameter(
                    uri,
                    "properties",
                    string.Join(",", options.Properties));
            }
            if (!string.IsNullOrWhiteSpace(after))
            {
                uri = RestJsonConnectorSupport.AddQueryParameter(uri, "after", after);
            }
            var root = await RestJsonConnectorSupport.SendJsonAsync(
                client,
                HttpMethod.Get,
                uri,
                jsonBody: null,
                cancellationToken).ConfigureAwait(false);
            AddRecords(root, level, options, records);
            after = RestJsonConnectorSupport.GetString(root, "paging.next.after");
            if (string.IsNullOrWhiteSpace(after))
            {
                break;
            }
        }
        return records;
    }

    private static async Task<IReadOnlyList<JsonElement>> FetchSearchRecordsAsync(
        HttpClient client,
        HierarchySourceLevel level,
        HubSpotSourceOptions options,
        CancellationToken cancellationToken)
    {
        var records = new List<JsonElement>();
        var uri = RestJsonConnectorSupport.BuildUri(
            client,
            $"crm/v3/objects/{options.ObjectType}/search");
        string? after = null;
        for (var page = 0; page < options.MaxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = CreateSearchBody(options, after, options.PageSize);
            var root = await RestJsonConnectorSupport.SendJsonAsync(
                client,
                HttpMethod.Post,
                uri,
                body,
                cancellationToken).ConfigureAwait(false);
            AddRecords(root, level, options, records);
            after = RestJsonConnectorSupport.GetString(root, "paging.next.after");
            if (string.IsNullOrWhiteSpace(after))
            {
                break;
            }
        }
        return records;
    }

    private static string CreateSearchBody(
        HubSpotSourceOptions options,
        string? after,
        int limit)
    {
        var body = new Dictionary<string, object?>
        {
            ["limit"] = limit,
            ["filterGroups"] = options.FilterGroups ?? JsonSerializer.SerializeToElement(Array.Empty<object>())
        };
        if (options.Properties is { Length: > 0 })
        {
            body["properties"] = options.Properties;
        }
        if (options.Sorts is { } sorts)
        {
            body["sorts"] = sorts;
        }
        if (!string.IsNullOrWhiteSpace(after))
        {
            body["after"] = after;
        }
        return JsonSerializer.Serialize(body);
    }

    private static void AddRecords(
        JsonElement root,
        HierarchySourceLevel level,
        HubSpotSourceOptions options,
        ICollection<JsonElement> records)
    {
        var pageRecords = RestJsonConnectorSupport.RequireArray(
            root,
            "results",
            "HubSpot",
            level.StepId);
        foreach (var record in pageRecords.EnumerateArray())
        {
            records.Add(options.FlattenProperties
                ? RestJsonConnectorSupport.FlattenObject(record, "properties", preserveTopLevel: true)
                : record.Clone());
        }
    }
}
