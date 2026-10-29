using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.Connectors;

internal sealed class SalesforceSourceOptions
{
    private static readonly Regex ApiVersionPattern = new(
        @"^v\d+\.\d+$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex IdentifierPattern = new(
        @"^[A-Za-z_][A-Za-z0-9_.]*$",
        RegexOptions.CultureInvariant);

    public string? Soql { get; init; }

    public string? Object { get; init; }

    public string[]? Fields { get; init; }

    public string? Where { get; init; }

    public string? OrderBy { get; init; }

    public string ApiVersion { get; init; } = "v62.0";

    public int PageSize { get; init; } = 2000;

    public bool QueryAll { get; init; }

    public bool UseBulkApi { get; init; }

    public int BulkMaxRecords { get; init; } = 10_000;

    public int BulkPollIntervalMilliseconds { get; init; } = 1000;

    public int BulkTimeoutSeconds { get; init; } = 3600;

    public int TimeoutSeconds { get; init; } = 100;

    public int MaxPages { get; init; } = 100_000;

    public static SalesforceSourceOptions FromJson(string optionsJson, string stepId)
    {
        return RestJsonConnectorSupport.DeserializeOptions<SalesforceSourceOptions>(optionsJson, stepId);
    }

    public void Validate(string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        if (!ApiVersionPattern.IsMatch(ApiVersion))
        {
            throw new ArgumentException(
                $"Salesforce source '{stepId}' ApiVersion must look like 'v62.0'.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TimeoutSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPages);
        if (PageSize is < 200 or > 2000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PageSize),
                $"Salesforce source '{stepId}' PageSize must be between 200 and 2000.");
        }
        if (UseBulkApi)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BulkMaxRecords);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BulkPollIntervalMilliseconds);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BulkTimeoutSeconds);
            if (QueryAll)
            {
                throw new ArgumentException(
                    $"Salesforce source '{stepId}' cannot combine UseBulkApi with QueryAll.");
            }
        }
        if (!string.IsNullOrWhiteSpace(Soql))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(Object))
        {
            throw new ArgumentException(
                $"Salesforce source '{stepId}' requires Options.Soql or Options.Object.");
        }
        if (!IdentifierPattern.IsMatch(Object))
        {
            throw new ArgumentException(
                $"Salesforce source '{stepId}' Object contains unsupported characters.");
        }
        if (Fields is null || Fields.Length == 0)
        {
            throw new ArgumentException(
                $"Salesforce source '{stepId}' requires Options.Fields when Options.Soql is omitted.");
        }
        foreach (var field in Fields)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(field);
            if (!IdentifierPattern.IsMatch(field))
            {
                throw new ArgumentException(
                    $"Salesforce source '{stepId}' field '{field}' contains unsupported characters. " +
                    "Use Options.Soql for expressions such as toLabel().");
            }
        }
    }

    public string BuildSoql(string keyProperty)
    {
        if (!string.IsNullOrWhiteSpace(Soql))
        {
            return Soql.Trim();
        }
        var query = $"SELECT {string.Join(", ", Fields!)} FROM {Object}";
        if (!string.IsNullOrWhiteSpace(Where))
        {
            query += $" WHERE {Where.Trim()}";
        }
        var orderBy = string.IsNullOrWhiteSpace(OrderBy)
            ? keyProperty
            : OrderBy.Trim();
        if (!string.IsNullOrWhiteSpace(orderBy))
        {
            query += $" ORDER BY {orderBy}";
        }
        return query;
    }
}

internal sealed class SalesforceHierarchyRecordReader : BufferedJsonHierarchyRecordReader
{
    public SalesforceHierarchyRecordReader(HierarchySourceLevel level, SalesforceSourceOptions options)
        : base(level, "salesforce", cancellationToken => FetchRecordsAsync(level, options, cancellationToken))
    {
    }

    public static async Task<long> CountRecordsAsync(
        HierarchySourceLevel level,
        SalesforceSourceOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(level.StepId);
        if (!string.IsNullOrWhiteSpace(options.Soql))
        {
            throw new InvalidOperationException(
                $"Salesforce source '{level.StepId}' cannot derive a count from custom Options.Soql.");
        }
        var client = RestJsonConnectorSupport.GetHttpClient(
            level.ConnectorConnection,
            "Salesforce",
            $"source '{level.StepId}'");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var countSoql = $"SELECT COUNT() FROM {options.Object}";
        if (!string.IsNullOrWhiteSpace(options.Where))
        {
            countSoql += $" WHERE {options.Where.Trim()}";
        }
        var uri = RestJsonConnectorSupport.BuildUri(
            client,
            $"services/data/{options.ApiVersion}/query");
        uri = RestJsonConnectorSupport.AddQueryParameter(uri, "q", countSoql);
        var root = await RestJsonConnectorSupport.SendJsonAsync(
            client,
            HttpMethod.Get,
            uri,
            jsonBody: null,
            timeout.Token).ConfigureAwait(false);
        if (!root.TryGetProperty("totalSize", out var totalSize) ||
            !totalSize.TryGetInt64(out var count))
        {
            throw new InvalidDataException(
                $"Salesforce source '{level.StepId}' count response has no totalSize.");
        }
        return count;
    }

    private static async Task<IReadOnlyList<JsonElement>> FetchRecordsAsync(
        HierarchySourceLevel level,
        SalesforceSourceOptions options,
        CancellationToken cancellationToken)
    {
        options.Validate(level.StepId);
        var client = RestJsonConnectorSupport.GetHttpClient(
            level.ConnectorConnection,
            "Salesforce",
            $"source '{level.StepId}'");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        return options.UseBulkApi
            ? await FetchBulkRecordsAsync(client, level, options, timeout.Token).ConfigureAwait(false)
            : await FetchRestRecordsAsync(client, level, options, timeout.Token).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<JsonElement>> FetchRestRecordsAsync(
        HttpClient client,
        HierarchySourceLevel level,
        SalesforceSourceOptions options,
        CancellationToken cancellationToken)
    {
        var records = new List<JsonElement>();
        var operation = options.QueryAll ? "queryAll" : "query";
        var requestUri = RestJsonConnectorSupport.BuildUri(
            client,
            $"services/data/{options.ApiVersion}/{operation}");
        requestUri = RestJsonConnectorSupport.AddQueryParameter(
            requestUri,
            "q",
            options.BuildSoql(level.KeyProperty));
        for (var page = 0; page < options.MaxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.TryAddWithoutValidation(
                "Sforce-Query-Options",
                $"batchSize={options.PageSize.ToString(CultureInfo.InvariantCulture)}");
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var pageRecords = RestJsonConnectorSupport.RequireArray(
                root,
                "records",
                "Salesforce",
                level.StepId);
            foreach (var record in pageRecords.EnumerateArray())
            {
                records.Add(RestJsonConnectorSupport.RemoveProperty(record, "attributes"));
            }
            var nextLink = RestJsonConnectorSupport.GetString(root, "nextRecordsUrl");
            if (string.IsNullOrWhiteSpace(nextLink))
            {
                break;
            }
            requestUri = RestJsonConnectorSupport.ResolveNextLink(requestUri, nextLink);
        }
        return records;
    }

    private static async Task<IReadOnlyList<JsonElement>> FetchBulkRecordsAsync(
        HttpClient client,
        HierarchySourceLevel level,
        SalesforceSourceOptions options,
        CancellationToken cancellationToken)
    {
        var createUri = RestJsonConnectorSupport.BuildUri(
            client,
            $"services/data/{options.ApiVersion}/jobs/query");
        var createBody = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["operation"] = "query",
            ["query"] = options.BuildSoql(level.KeyProperty),
            ["contentType"] = "CSV",
            ["columnDelimiter"] = "COMMA",
            ["lineEnding"] = "LF"
        });
        var created = await RestJsonConnectorSupport.SendJsonAsync(
            client,
            HttpMethod.Post,
            createUri,
            createBody,
            cancellationToken).ConfigureAwait(false);
        var jobId = RestJsonConnectorSupport.GetString(created, "id");
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new InvalidDataException(
                $"Salesforce source '{level.StepId}' bulk query response has no job id.");
        }
        var jobUri = RestJsonConnectorSupport.BuildUri(
            client,
            $"services/data/{options.ApiVersion}/jobs/query/{jobId}");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(options.BulkTimeoutSeconds);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await RestJsonConnectorSupport.SendJsonAsync(
                client,
                HttpMethod.Get,
                jobUri,
                jsonBody: null,
                cancellationToken).ConfigureAwait(false);
            var state = RestJsonConnectorSupport.GetString(status, "state");
            if (string.Equals(state, "JobComplete", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            if (string.Equals(state, "Failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state, "Aborted", StringComparison.OrdinalIgnoreCase))
            {
                var error = RestJsonConnectorSupport.GetString(status, "errorMessage") ?? "unknown error";
                throw new InvalidDataException(
                    $"Salesforce source '{level.StepId}' bulk query {state}: {error}");
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Salesforce source '{level.StepId}' bulk query did not complete within " +
                    $"{options.BulkTimeoutSeconds} seconds.");
            }
            await Task.Delay(options.BulkPollIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
        }

        var records = new List<JsonElement>();
        var locator = string.Empty;
        for (var page = 0; page < options.MaxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resultUri = RestJsonConnectorSupport.BuildUri(
                client,
                $"services/data/{options.ApiVersion}/jobs/query/{jobId}/results");
            resultUri = RestJsonConnectorSupport.AddQueryParameter(
                resultUri,
                "maxRecords",
                options.BulkMaxRecords.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(locator))
            {
                resultUri = RestJsonConnectorSupport.AddQueryParameter(resultUri, "locator", locator);
            }
            using var response = await client.GetAsync(
                resultUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var csv = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            ParseBulkCsv(csv, records, level.StepId);
            locator = response.Headers.TryGetValues("Sforce-Locator", out var values)
                ? values.FirstOrDefault() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(locator) ||
                string.Equals(locator, "null", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }
        return records;
    }

    private static void ParseBulkCsv(
        string csv,
        ICollection<JsonElement> records,
        string stepId)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return;
        }
        using var textReader = new StringReader(csv);
        using var reader = new CsvReader(
            textReader,
            new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                MissingFieldFound = null,
                BadDataFound = null
            });
        if (!reader.Read())
        {
            return;
        }
        reader.ReadHeader();
        var headers = reader.HeaderRecord;
        if (headers is null || headers.Length == 0)
        {
            throw new InvalidDataException(
                $"Salesforce source '{stepId}' bulk query returned a CSV result without headers.");
        }
        while (reader.Read())
        {
            var record = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var header in headers)
            {
                var value = reader.GetField(header);
                record[header] = string.IsNullOrEmpty(value) ? null : value;
            }
            records.Add(JsonSerializer.SerializeToElement(record));
        }
    }
}
