using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.Connectors;

public sealed class HttpSourceOptions
{
    public string? RecordsPath { get; init; }

    public string? NextLinkPath { get; init; }

    public string? NextLinkHeader { get; init; }

    public string? TotalCountPath { get; init; }

    public string? PageSizeParameter { get; init; }

    public string? OffsetParameter { get; init; }

    public int PageSize { get; init; } = 1000;

    public string Method { get; init; } = "GET";

    public string? Body { get; init; }

    public string? ContentType { get; init; }

    public Dictionary<string, string>? Headers { get; init; }

    public int TimeoutSeconds { get; init; } = 100;

    public int MaxPages { get; init; } = 100_000;

    public void Validate(string stepId, string connectorType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        if (Method.Trim().ToUpperInvariant() is not ("GET" or "POST"))
        {
            throw new ArgumentException(
                $"HTTP source '{stepId}' Method must be GET or POST.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TimeoutSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPages);
        if (Headers is not null && Headers.Keys.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException($"HTTP source '{stepId}' header names must not be empty.");
        }
        if (connectorType == "odata")
        {
            if (string.IsNullOrWhiteSpace(RecordsPath) ||
                string.IsNullOrWhiteSpace(NextLinkPath) ||
                string.IsNullOrWhiteSpace(TotalCountPath) ||
                string.IsNullOrWhiteSpace(PageSizeParameter) ||
                string.IsNullOrWhiteSpace(OffsetParameter))
            {
                throw new ArgumentException(
                    $"OData source '{stepId}' requires RecordsPath, NextLinkPath, TotalCountPath, " +
                    "PageSizeParameter, and OffsetParameter.");
            }
        }
    }

    public static HttpSourceOptions CreateODataDefaults()
    {
        return new HttpSourceOptions
        {
            RecordsPath = "value",
            NextLinkPath = "@odata.nextLink",
            TotalCountPath = "@odata.count",
            PageSizeParameter = "$top",
            OffsetParameter = "$skip"
        };
    }

    public static HttpSourceOptions ApplyODataDefaults(HttpSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new HttpSourceOptions
        {
            RecordsPath = options.RecordsPath ?? "value",
            NextLinkPath = options.NextLinkPath ?? "@odata.nextLink",
            NextLinkHeader = options.NextLinkHeader,
            TotalCountPath = options.TotalCountPath ?? "@odata.count",
            PageSizeParameter = options.PageSizeParameter ?? "$top",
            OffsetParameter = options.OffsetParameter ?? "$skip",
            PageSize = options.PageSize,
            Method = options.Method,
            Body = options.Body,
            ContentType = options.ContentType,
            Headers = options.Headers,
            TimeoutSeconds = options.TimeoutSeconds,
            MaxPages = options.MaxPages
        };
    }
}

public sealed class HttpHierarchyRecordReader : IHierarchyRecordReader
{
    private readonly HierarchySourceLevel _level;
    private readonly string _temporaryDirectory;
    private readonly IHierarchyRecordReader _inner;
    private bool _disposed;

    public HttpHierarchyRecordReader(
        HierarchySourceLevel level,
        HttpSourceOptions options,
        string connectorType)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(level.StepId, connectorType);
        _level = level;
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "migration4dataverse-http",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporaryDirectory);
        var path = Path.Combine(_temporaryDirectory, "records.jsonl");
        FetchRecordsAsync(level, options, connectorType, path, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        var fileLevel = new HierarchySourceLevel(
            level.StepId,
            "jsonl",
            path,
            level.KeyProperty,
            level.ParentKeyProperty,
            level.RowsPerParent,
            expectedRecordCount: 0);
        _inner = HierarchyRecordReaderFactory.CreateFileReader(fileLevel);
    }

    public long RecordCount => _inner.RecordCount;

    public ValueTask<JsonElement?> PeekAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _inner.PeekAsync(cancellationToken);
    }

    public JsonElement Take()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _inner.Take();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await _inner.DisposeAsync().ConfigureAwait(false);
        try
        {
            if (Directory.Exists(_temporaryDirectory))
            {
                Directory.Delete(_temporaryDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static async Task FetchRecordsAsync(
        HierarchySourceLevel level,
        HttpSourceOptions options,
        string connectorType,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var (client, ownsClient, baseUri) = HttpConnectorSupport.ResolveConnection(
            level.ConnectorConnection
                ?? throw new InvalidOperationException(
                    $"HTTP source '{level.StepId}' has no runtime connection."));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        try
        {
            var records = await FetchRecordsAsync(
                    client,
                    baseUri,
                    level,
                    options,
                    connectorType,
                    timeout.Token)
                .ConfigureAwait(false);
            await using var stream = new FileStream(
                outputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                FileOptions.Asynchronous);
            await using var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            foreach (var record in records)
            {
                await writer.WriteLineAsync(record.GetRawText()).ConfigureAwait(false);
            }
        }
        finally
        {
            if (ownsClient)
            {
                client.Dispose();
            }
        }
    }

    private static async Task<List<JsonElement>> FetchRecordsAsync(
        HttpClient client,
        Uri? baseUri,
        HierarchySourceLevel level,
        HttpSourceOptions options,
        string connectorType,
        CancellationToken cancellationToken)
    {
        var records = new List<JsonElement>();
        var offset = 0;
        string? nextUrl = null;
        for (var page = 0; page < options.MaxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestUrl = nextUrl ?? HttpConnectorSupport.AddPageParameters(
                HttpConnectorSupport.ResolveUrl(baseUri, level.Path),
                options,
                offset,
                offset == 0);
            using var request = new HttpRequestMessage(
                new HttpMethod(options.Method.Trim().ToUpperInvariant()),
                requestUrl);
            if (!string.IsNullOrEmpty(options.Body))
            {
                request.Content = new StringContent(options.Body);
                if (!string.IsNullOrWhiteSpace(options.ContentType))
                {
                    request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(options.ContentType);
                }
            }
            if (options.Headers is not null)
            {
                foreach (var (name, value) in options.Headers)
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }

            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(content);
            var recordsElement = HttpConnectorSupport.FindElement(document.RootElement, options.RecordsPath);
            if (recordsElement is null || recordsElement.Value.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    $"HTTP source '{level.StepId}' response does not contain a records array at " +
                    $"'{options.RecordsPath ?? "<root>"}'.");
            }

            var pageRecords = recordsElement.Value.EnumerateArray().Select(record => record.Clone()).ToArray();
            if (pageRecords.Any(record => record.ValueKind != JsonValueKind.Object))
            {
                throw new InvalidDataException(
                    $"HTTP source '{level.StepId}' records must all be JSON objects.");
            }
            records.AddRange(pageRecords);

            var nextLink = HttpConnectorSupport.GetNextLink(
                document.RootElement,
                response,
                options);
            if (!string.IsNullOrWhiteSpace(nextLink))
            {
                nextUrl = HttpConnectorSupport.ResolveUrl(
                    new Uri(requestUrl),
                    nextLink).ToString();
                offset += options.PageSize;
                continue;
            }

            if (string.IsNullOrWhiteSpace(options.OffsetParameter) ||
                pageRecords.Length < options.PageSize)
            {
                break;
            }
            offset += options.PageSize;
            nextUrl = null;
        }

        records.Sort((left, right) => HttpConnectorSupport.CompareRecords(
            left,
            right,
            level.ParentKeyProperty,
            level.KeyProperty,
            level.StepId));
        return records;
    }
}

internal static class HttpConnectorSupport
{
    public static (HttpClient Client, bool OwnsClient, Uri? BaseUri) ResolveConnection(object connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection is HttpClient client)
        {
            return (client, false, null);
        }
        if (connection is Uri uri)
        {
            return (new HttpClient(), true, uri);
        }
        if (connection is string text &&
            Uri.TryCreate(text, UriKind.Absolute, out var stringUri))
        {
            return (new HttpClient(), true, stringUri);
        }

        throw new ArgumentException(
            $"HTTP sources require a Uri, absolute URL string, or HttpClient connection; " +
            $"received '{connection.GetType().FullName}'.");
    }

    public static Uri ResolveUrl(Uri? baseUri, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) &&
            Uri.TryCreate(path, UriKind.Absolute, out var absolute))
        {
            return absolute;
        }
        if (baseUri is null)
        {
            throw new ArgumentException(
                "An HTTP source with an HttpClient connection must declare an absolute Path.");
        }
        return string.IsNullOrWhiteSpace(path) ? baseUri : new Uri(baseUri, path);
    }

    public static string AddPageParameters(
        Uri url,
        HttpSourceOptions options,
        int offset,
        bool firstPage)
    {
        var result = url.ToString();
        if (firstPage &&
            !string.IsNullOrWhiteSpace(options.PageSizeParameter) &&
            !ContainsQueryParameter(result, options.PageSizeParameter))
        {
            result = AppendQueryParameter(
                result,
                options.PageSizeParameter,
                options.PageSize.ToString(CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrWhiteSpace(options.OffsetParameter) &&
            !ContainsQueryParameter(result, options.OffsetParameter))
        {
            result = AppendQueryParameter(
                result,
                options.OffsetParameter,
                offset.ToString(CultureInfo.InvariantCulture));
        }
        return result;
    }

    public static JsonElement? FindElement(JsonElement root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "$")
        {
            return root;
        }
        var current = root;
        if (current.ValueKind == JsonValueKind.Object &&
            current.TryGetProperty(path, out var direct))
        {
            return direct;
        }
        foreach (var segment in path
                     .TrimStart('$', '/')
                     .Split(new[] { '.', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }
        return current;
    }

    public static string? GetNextLink(
        JsonElement root,
        HttpResponseMessage response,
        HttpSourceOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.NextLinkPath))
        {
            var nextLink = FindElement(root, options.NextLinkPath);
            if (nextLink is { ValueKind: JsonValueKind.String } &&
                !string.IsNullOrWhiteSpace(nextLink.Value.GetString()))
            {
                return nextLink.Value.GetString();
            }
        }
        if (!string.IsNullOrWhiteSpace(options.NextLinkHeader) &&
            response.Headers.TryGetValues(options.NextLinkHeader, out var values))
        {
            foreach (var value in values)
            {
                var link = ParseLinkHeader(value);
                if (link is not null)
                {
                    return link;
                }
            }
        }
        return null;
    }

    public static string? ParseLinkHeader(string value)
    {
        foreach (var part in value.Split(','))
        {
            var segments = part.Split(';', StringSplitOptions.TrimEntries);
            if (segments.Length < 2 ||
                !segments.Skip(1).Any(segment =>
                    segment.Equals("rel=\"next\"", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("rel=next", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var start = segments[0].IndexOf('<');
            var end = segments[0].IndexOf('>');
            if (start >= 0 && end > start)
            {
                return segments[0][(start + 1)..end];
            }
        }
        return null;
    }

    public static int CompareRecords(
        JsonElement left,
        JsonElement right,
        string? parentKeyProperty,
        string keyProperty,
        string stepId)
    {
        if (parentKeyProperty is not null)
        {
            var parentComparison = string.Compare(
                GetKey(left, parentKeyProperty, stepId),
                GetKey(right, parentKeyProperty, stepId),
                StringComparison.Ordinal);
            if (parentComparison != 0)
            {
                return parentComparison;
            }
        }
        return string.Compare(
            GetKey(left, keyProperty, stepId),
            GetKey(right, keyProperty, stepId),
            StringComparison.Ordinal);
    }

    private static string GetKey(JsonElement record, string propertyName, string stepId)
    {
        if (!record.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidDataException(
                $"HTTP source '{stepId}' record is missing key property '{propertyName}'.");
        }
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : value.GetRawText();
    }

    private static bool ContainsQueryParameter(string url, string name)
    {
        var queryStart = url.IndexOf('?');
        if (queryStart < 0)
        {
            return false;
        }
        var query = url[(queryStart + 1)..];
        return query
            .Split('&')
            .Any(parameter => parameter.StartsWith(name + "=", StringComparison.Ordinal));
    }

    private static string AppendQueryParameter(string url, string name, string value)
    {
        var separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}{name}={Uri.EscapeDataString(value)}";
    }
}
