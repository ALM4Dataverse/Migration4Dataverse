using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.Connectors;

internal abstract class BufferedJsonHierarchyRecordReader : IHierarchyRecordReader
{
    private readonly string _temporaryDirectory;
    private readonly IHierarchyRecordReader _inner;
    private bool _disposed;

    protected BufferedJsonHierarchyRecordReader(
        HierarchySourceLevel level,
        string connectorType,
        Func<CancellationToken, Task<IReadOnlyList<JsonElement>>> fetch)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(fetch);
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"migration4dataverse-{connectorType}",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temporaryDirectory);
        var path = Path.Combine(_temporaryDirectory, "records.jsonl");
        try
        {
            var records = fetch(CancellationToken.None).GetAwaiter().GetResult();
            var sorted = records.ToList();
            if (sorted.Any(record => record.ValueKind != JsonValueKind.Object))
            {
                throw new InvalidDataException(
                    $"{connectorType} source '{level.StepId}' records must all be JSON objects.");
            }
            sorted.Sort((left, right) => HttpConnectorSupport.CompareRecords(
                left,
                right,
                level.ParentKeyProperty,
                level.KeyProperty,
                level.StepId));
            using var writer = new StreamWriter(
                path,
                append: false,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            foreach (var record in sorted)
            {
                writer.WriteLine(record.GetRawText());
            }
        }
        catch
        {
            TryDeleteDirectory(_temporaryDirectory);
            throw;
        }

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
        TryDeleteDirectory(_temporaryDirectory);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class RestJsonConnectorSupport
{
    public static T DeserializeOptions<T>(string optionsJson, string stepId)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(
                string.IsNullOrWhiteSpace(optionsJson) ? "{}" : optionsJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException($"Source options for job '{stepId}' are empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Source options for job '{stepId}' are invalid JSON.",
                exception);
        }
    }

    public static HttpClient GetHttpClient(object? connection, string connectorType, string role)
    {
        if (connection is not HttpClient client)
        {
            throw new ArgumentException(
                $"{connectorType} sources require an HttpClient connection for {role}; " +
                $"received '{connection?.GetType().FullName ?? "<null>"}'.");
        }
        if (client.BaseAddress is null || !client.BaseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException(
                $"{connectorType} HttpClient connection for {role} must have an absolute BaseAddress.");
        }
        return client;
    }

    public static Uri BuildUri(HttpClient client, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var baseAddress = client.BaseAddress
            ?? throw new ArgumentException("The HttpClient connection must have a BaseAddress.");
        var baseText = baseAddress.AbsoluteUri;
        if (!baseText.EndsWith("/", StringComparison.Ordinal))
        {
            baseText += "/";
        }
        return new Uri(new Uri(baseText, UriKind.Absolute), relativePath.TrimStart('/'));
    }

    public static Uri AddQueryParameter(Uri uri, string name, string value)
    {
        var builder = new UriBuilder(uri);
        var query = builder.Query.TrimStart('?');
        var parameter = $"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";
        builder.Query = string.IsNullOrEmpty(query) ? parameter : $"{query}&{parameter}";
        return builder.Uri;
    }

    public static Uri SetQueryParameter(Uri uri, string name, string value)
    {
        var builder = new UriBuilder(uri);
        var encodedName = Uri.EscapeDataString(name);
        var parameters = builder.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(parameter => !parameter.StartsWith(encodedName + "=", StringComparison.Ordinal))
            .ToList();
        parameters.Add($"{encodedName}={Uri.EscapeDataString(value)}");
        builder.Query = string.Join("&", parameters);
        return builder.Uri;
    }

    public static bool HasQueryParameter(Uri uri, string name)
    {
        var query = uri.Query.TrimStart('?');
        return query
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Any(parameter => parameter.StartsWith(
                Uri.EscapeDataString(name) + "=",
                StringComparison.Ordinal));
    }

    public static async Task<JsonElement> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        Uri uri,
        string? jsonBody,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(content);
        return document.RootElement.Clone();
    }

    public static async Task<(JsonElement Root, HttpResponseMessage Response)> SendJsonWithResponseAsync(
        HttpClient client,
        HttpMethod method,
        Uri uri,
        string? jsonBody,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }
        var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(content);
            return (document.RootElement.Clone(), response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public static JsonElement RequireArray(JsonElement root, string path, string connectorType, string stepId)
    {
        var element = HttpConnectorSupport.FindElement(root, path);
        if (element is null || element.Value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"{connectorType} source '{stepId}' response does not contain a records array at " +
                $"'{path}'.");
        }
        return element.Value;
    }

    public static string? GetString(JsonElement element, string path)
    {
        var value = HttpConnectorSupport.FindElement(element, path);
        return value is { ValueKind: JsonValueKind.String }
            ? value.Value.GetString()
            : null;
    }

    public static Uri ResolveNextLink(Uri currentUri, string? nextLink)
    {
        if (string.IsNullOrWhiteSpace(nextLink))
        {
            throw new ArgumentException("The next-link value must not be empty.", nameof(nextLink));
        }
        return Uri.TryCreate(nextLink, UriKind.Absolute, out var absolute)
            ? absolute
            : new Uri(currentUri, nextLink);
    }

    public static JsonElement RemoveProperty(JsonElement source, string propertyName)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(source.GetRawText());
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            obj.Remove(propertyName);
            return JsonSerializer.SerializeToElement(obj);
        }
        return source;
    }

    public static JsonElement FlattenObject(
        JsonElement source,
        string nestedProperty,
        bool preserveTopLevel)
    {
        var result = new System.Text.Json.Nodes.JsonObject();
        if (preserveTopLevel)
        {
            foreach (var property in source.EnumerateObject())
            {
                if (!property.NameEquals(nestedProperty))
                {
                    result[property.Name] = System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText());
                }
            }
        }
        if (source.TryGetProperty(nestedProperty, out var nested) &&
            nested.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in nested.EnumerateObject())
            {
                result[property.Name] = System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText());
            }
        }
        return JsonSerializer.SerializeToElement(result);
    }

    public static string FormatInvariant(object value)
    {
        return value switch
        {
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }

    public static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, string? jsonBody)
    {
        var request = new HttpRequestMessage(method, uri);
        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        return request;
    }
}
