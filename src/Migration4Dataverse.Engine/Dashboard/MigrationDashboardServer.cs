using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.Web;
using Migration4Dataverse.Engine.Connectors;
using Migration4Dataverse.Engine.PowerShell;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.Dashboard;

public static class MigrationDashboardServer
{
    private const string DefaultUrl = "http://127.0.0.1:5180/";
    private const int MaxConcurrentRequests = 8;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Task<MigrationDashboardHandle> StartAsync(
        string databasePath,
        string url = DefaultUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        return StartWithConfigurationAsync(
            databasePath,
            url,
            configurationJson: null,
            cancellationToken: cancellationToken);
    }

    public static Task<MigrationDashboardHandle> StartWithConfigurationAsync(
        string? databasePath,
        string url,
        string? configurationJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (string.IsNullOrWhiteSpace(databasePath) && string.IsNullOrWhiteSpace(configurationJson))
        {
            throw new ArgumentException("A database path, configuration, or both must be provided.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(configurationJson))
        {
            using var configuration = JsonDocument.Parse(configurationJson);
            if (configuration.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Dashboard configuration must be a JSON object.", nameof(configurationJson));
            }
        }

        var uri = ValidateUrl(url);
        var resolvedDatabasePath = string.IsNullOrWhiteSpace(databasePath)
            ? null
            : Path.GetFullPath(databasePath);
        var store = resolvedDatabasePath is null ? null : MigrationStoreFactory.Open(resolvedDatabasePath);
        store?.PurgeExpiredDebugSnapshots();
        var listener = new HttpListener();
        listener.Prefixes.Add(uri.ToString());
        try
        {
            listener.Start();
            var stop = new CancellationTokenSource();
            var serverTask = RunServerAsync(listener, store, resolvedDatabasePath, configurationJson, stop.Token);
            return Task.FromResult(new MigrationDashboardHandle(listener, serverTask, stop, uri));
        }
        catch
        {
            listener.Close();
            throw;
        }
    }

    private static async Task RunServerAsync(
        HttpListener listener,
        IMigrationStore? store,
        string? databasePath,
        string? configurationJson,
        CancellationToken cancellationToken)
    {
        var requestQueue = Channel.CreateBounded<HttpListenerContext>(
            new BoundedChannelOptions(MaxConcurrentRequests)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = false,
                AllowSynchronousContinuations = false
            });
        var workers = Enumerable.Range(0, MaxConcurrentRequests)
            .Select(_ => ProcessRequestsAsync(
                requestQueue.Reader,
                store,
                databasePath,
                configurationJson,
                cancellationToken))
            .ToArray();

        try
        {
            while (!cancellationToken.IsCancellationRequested && listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (HttpListenerException) when (cancellationToken.IsCancellationRequested || !listener.IsListening)
                {
                    return;
                }

                try
                {
                    await requestQueue.Writer.WriteAsync(context, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    context.Response.Close();
                    throw;
                }
            }
        }
        finally
        {
            requestQueue.Writer.TryComplete();
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
    }

    private static async Task ProcessRequestsAsync(
        ChannelReader<HttpListenerContext> requestQueue,
        IMigrationStore? store,
        string? databasePath,
        string? configurationJson,
        CancellationToken cancellationToken)
    {
        await foreach (var context in requestQueue.ReadAllAsync().ConfigureAwait(false))
        {
            await HandleRequestAsync(context, store, databasePath, configurationJson, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task HandleRequestAsync(
        HttpListenerContext context,
        IMigrationStore? store,
        string? databasePath,
        string? configurationJson,
        CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
            "connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";

        try
        {
            if (!IsLoopbackHost(request.Url?.Host))
            {
                await WriteJsonAsync(response, HttpStatusCode.BadRequest, new
                {
                    message = "The dashboard accepts loopback requests only."
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            var path = request.Url?.AbsolutePath ?? "/";
            var query = HttpUtility.ParseQueryString(request.Url?.Query ?? string.Empty);
            var rawSegments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var isBinaryArtifactHead = request.HttpMethod == "HEAD" &&
                rawSegments.Length == 4 &&
                rawSegments[0] == "api" &&
                rawSegments[1] == "runs" &&
                rawSegments[3] == "binary";
            if (request.HttpMethod != "GET" && !isBinaryArtifactHead)
            {
                await WriteJsonAsync(response, HttpStatusCode.MethodNotAllowed, new
                {
                    message = "The migration dashboard API is read-only."
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (path == "/api/runs")
            {
                await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        store?.GetDashboardRuns() ?? Array.Empty<MigrationRunSummary>(),
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (path == "/api/configuration")
            {
                if (string.IsNullOrWhiteSpace(configurationJson))
                {
                    await WriteJsonAsync(response, HttpStatusCode.NotFound, new
                    {
                        message = "No configuration preview was supplied to this dashboard."
                    }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                using var configuration = JsonDocument.Parse(configurationJson);
                await WriteJsonAsync(response, HttpStatusCode.OK, configuration.RootElement.Clone(), cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (path == "/api/process-metrics")
            {
                using var process = Process.GetCurrentProcess();
                process.Refresh();
                var memoryLimitBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                await WriteJsonAsync(response, HttpStatusCode.OK, new
                {
                    cpuTimeMilliseconds = process.TotalProcessorTime.TotalMilliseconds,
                    logicalProcessorCount = Math.Max(1, Environment.ProcessorCount),
                    workingSetBytes = process.WorkingSet64,
                    managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
                    memoryLimitBytes = memoryLimitBytes > 0 ? memoryLimitBytes : (long?)null,
                    capturedUtc = DateTimeOffset.UtcNow
                }, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (path == "/health")
            {
                await WriteJsonAsync(response, HttpStatusCode.OK, new { status = "ok" }, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var segments = rawSegments.Select(Uri.UnescapeDataString).ToArray();
            if (segments.Length >= 3 && segments[0] == "api" && segments[1] == "runs")
            {
                if (store is null)
                {
                    throw new KeyNotFoundException("This configuration-only dashboard has no run history database.");
                }

                var runId = segments[2];
                if (segments.Length == 3)
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        store.GetDashboardRun(runId),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (segments.Length == 4 && segments[3] == "binary")
                {
                    if (!store.GetDashboardRun(runId).DebugCaptureEnabled)
                    {
                        await WriteJsonAsync(response, HttpStatusCode.NotFound, new
                        {
                            message = "Debug capture was not enabled for this run."
                        }, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    var snapshot = store.GetDebugSnapshot(
                        runId,
                        query["stepId"] ?? throw new ArgumentException("A stepId query parameter is required."),
                        query["batchId"] ?? throw new ArgumentException("A batchId query parameter is required."),
                        query["stageName"] ?? throw new ArgumentException("A stageName query parameter is required."),
                        ParseRequiredInteger(query["recordIndex"], "recordIndex", minimum: 0),
                        ParseRequiredInteger(query["attempt"], "attempt", minimum: 1),
                        query["lineageId"] ?? throw new ArgumentException("A lineageId query parameter is required."),
                        ParseCapturedUtc(query["capturedUtc"]));
                    if (snapshot is null)
                    {
                        await WriteJsonAsync(response, HttpStatusCode.NotFound, new
                        {
                            message = "The captured row snapshot was not found."
                        }, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    var propertyName = query["property"] ??
                        throw new ArgumentException("A property query parameter is required.");
                    using var payload = JsonDocument.Parse(snapshot.PayloadJson);
                    if (payload.RootElement.ValueKind != JsonValueKind.Object ||
                        !payload.RootElement.TryGetProperty(propertyName, out var propertyValue))
                    {
                        await WriteJsonAsync(response, HttpStatusCode.NotFound, new
                        {
                            message = "The captured property was not found."
                        }, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    var transportValue = JsonSerializer.Deserialize<object?>(propertyValue.GetRawText());
                    MigrationBinaryValue binaryValue;
                    try
                    {
                        binaryValue = MigrationBinaryValue.RestoreFromTransport(
                            transportValue,
                            MigrationBinaryValue.GetRunStagingRoot(databasePath, runId)) as MigrationBinaryValue
                            ?? throw new InvalidDataException("The captured property is not a binary artifact.");
                    }
                    catch (FileNotFoundException)
                    {
                        await WriteJsonAsync(response, HttpStatusCode.Gone, new
                        {
                            message = "The staged file is no longer available."
                        }, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    catch (DirectoryNotFoundException)
                    {
                        await WriteJsonAsync(response, HttpStatusCode.Gone, new
                        {
                            message = "The staged file is no longer available."
                        }, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    catch (InvalidDataException)
                    {
                        await WriteJsonAsync(response, HttpStatusCode.NotFound, new
                        {
                            message = "The captured property is not a valid binary artifact."
                        }, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    await WriteBinaryAsync(
                        response,
                        binaryValue,
                        request.HttpMethod == "HEAD",
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (segments.Length == 4 && segments[3] == "dataverse-connection-throughput")
                {
                    if (string.IsNullOrWhiteSpace(databasePath))
                    {
                        throw new InvalidOperationException(
                            "A database path is required to read Dataverse connection throughput.");
                    }

                    _ = store.GetDashboardRun(runId);
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        DataverseConnectionPool.GetDashboardSnapshots(databasePath, runId),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (segments.Length == 5 && segments[3] == "batches")
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        store.GetDashboardBatches(
                            runId,
                            segments[4],
                            ParseLimit(query["limit"], 100)),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (segments.Length == 5 && segments[3] == "batch-errors")
                {
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        store.GetDashboardBatchErrors(
                            runId,
                            segments[4],
                            ParseLimit(query["limit"], 100),
                            ParseOffset(query["offset"])),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (segments.Length == 4 && segments[3] == "snapshots")
                {
                    if (!store.GetDashboardRun(runId).DebugCaptureEnabled)
                    {
                        await WriteJsonAsync(response, HttpStatusCode.NotFound, new
                        {
                            message = "Debug capture was not enabled for this run."
                        }, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        SanitizeSnapshots(store.GetDebugSnapshots(
                            runId,
                            query["lineageId"],
                            query["batchId"],
                            ParseLimit(query["limit"], 500))),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (segments.Length == 4 && segments[3] == "rows")
                {
                    if (!store.GetDashboardRun(runId).DebugCaptureEnabled)
                    {
                        await WriteJsonAsync(response, HttpStatusCode.NotFound, new
                        {
                            message = "Debug capture was not enabled for this run."
                        }, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    var snapshotPage = store.GetDebugSnapshotPage(
                        runId,
                        query["stepId"] ?? throw new ArgumentException("A stepId query parameter is required."),
                        query["search"],
                        ParseLimit(query["limit"], 100),
                        ParseOffset(query["offset"]),
                        query["stageName"],
                        query["sortColumn"] ?? "record",
                        query["sortDirection"] ?? "descending",
                        ParseSnapshotFilters(query["filters"]));
                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        SanitizeSnapshotPage(snapshotPage),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (segments.Length == 5 && segments[3] == "trace")
                {
                    if (!store.GetDashboardRun(runId).DebugCaptureEnabled)
                    {
                        await WriteJsonAsync(response, HttpStatusCode.NotFound, new
                        {
                            message = "Debug capture was not enabled for this run."
                        }, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        SanitizeSnapshots(store.GetDebugSnapshots(runId, lineageId: segments[4], limit: 2000)),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            if (path.StartsWith("/api/", StringComparison.Ordinal))
            {
                await WriteJsonAsync(response, HttpStatusCode.NotFound, new { message = "API route not found." }, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var assetPath = path.TrimStart('/');
            if (string.IsNullOrWhiteSpace(assetPath) || string.IsNullOrWhiteSpace(Path.GetExtension(assetPath)))
            {
                assetPath = "index.html";
            }
            await WriteAssetAsync(response, assetPath, cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException exception)
        {
            await WriteJsonAsync(response, HttpStatusCode.NotFound, new { message = exception.Message }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            await WriteJsonAsync(response, HttpStatusCode.BadRequest, new { message = exception.Message }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            await WriteJsonAsync(response, HttpStatusCode.BadRequest, new { message = exception.Message }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError(
                "Dashboard request for '{0}' failed: {1}",
                request.Url?.AbsolutePath ?? string.Empty,
                exception);
            await WriteJsonAsync(response, HttpStatusCode.InternalServerError, new
            {
                message = "The dashboard request failed."
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            response.Close();
        }
    }

    private static Uri ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            !uri.IsLoopback ||
            uri.AbsolutePath != "/")
        {
            throw new ArgumentException(
                "The dashboard URL must be an HTTP loopback URL such as http://127.0.0.1:5180/. " +
                "Remote binding is disabled because migration data may be sensitive.",
                nameof(url));
        }

        return uri;
    }

    private static bool IsLoopbackHost(string? host) =>
        host is not null &&
        (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
         host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
         host.Equals("::1", StringComparison.OrdinalIgnoreCase) ||
         host.Equals("[::1]", StringComparison.OrdinalIgnoreCase));

    private static int ParseLimit(string? value, int defaultValue)
    {
        if (value is null)
        {
            return defaultValue;
        }

        if (!int.TryParse(value, out var limit) || limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "The requested page size must be between 1 and 500.");
        }

        return limit;
    }

    private static int ParseOffset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        if (!int.TryParse(value, out var offset) || offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Offset must be a non-negative integer.");
        }

        return offset;
    }

    private static int ParseRequiredInteger(string? value, string parameterName, int minimum)
    {
        if (!int.TryParse(value, out var result) || result < minimum)
        {
            throw new ArgumentException(
                $"The {parameterName} query parameter must be an integer greater than or equal to {minimum}.",
                parameterName);
        }

        return result;
    }

    private static DateTimeOffset ParseCapturedUtc(string? value)
    {
        if (!DateTimeOffset.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var capturedUtc))
        {
            throw new ArgumentException("A valid capturedUtc query parameter is required.", nameof(value));
        }

        return capturedUtc;
    }

    private static IReadOnlyDictionary<string, string> ParseSnapshotFilters(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        if (value.Length > 16_384)
        {
            throw new ArgumentException("Snapshot filters exceed the maximum supported size.", nameof(value));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(value);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Snapshot filters must be a JSON object of string values.", nameof(value), exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Snapshot filters must be a JSON object of string values.", nameof(value));
            }

            var filters = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (filters.Count == 64)
                {
                    throw new ArgumentException("At most 64 snapshot column filters may be supplied.", nameof(value));
                }

                if (string.IsNullOrWhiteSpace(property.Name) ||
                    property.Value.ValueKind != JsonValueKind.String ||
                    property.Value.GetString() is not { Length: <= 1024 } filterValue)
                {
                    throw new ArgumentException("Snapshot filters must have non-empty names and string values up to 1024 characters.", nameof(value));
                }

                filters[property.Name] = filterValue;
            }

            return filters;
        }
    }

    private static async Task WriteJsonAsync(
        HttpListenerResponse response,
        HttpStatusCode statusCode,
        object value,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json; charset=utf-8";
        response.Headers["Cache-Control"] = "no-store";
        await WriteBodyAsync(response, json, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteBinaryAsync(
        HttpListenerResponse response,
        MigrationBinaryValue binaryValue,
        bool headOnly,
        CancellationToken cancellationToken)
    {
        response.StatusCode = (int)HttpStatusCode.OK;
        var storedContentType = binaryValue.Representation.Equals("base64", StringComparison.OrdinalIgnoreCase)
            ? "text/plain; charset=us-ascii"
            : binaryValue.ContentType;
        response.ContentType = MediaTypeHeaderValue.TryParse(storedContentType, out var contentType)
            ? contentType.ToString()
            : "application/octet-stream";
        response.ContentLength64 = binaryValue.Length;
        response.Headers["Cache-Control"] = "no-store";
        var fileName = binaryValue.FileName ?? "attachment";
        fileName = new string(fileName.Replace('\\', '/').Split('/').Last().Where(character => !char.IsControl(character)).ToArray());
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "attachment";
        }
        response.Headers["Content-Disposition"] = new ContentDispositionHeaderValue("attachment")
        {
            FileNameStar = fileName
        }.ToString();
        if (headOnly)
        {
            return;
        }

        await using var stream = binaryValue.OpenRead();
        await stream.CopyToAsync(response.OutputStream, 64 * 1024, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<MigrationDebugSnapshot> SanitizeSnapshots(
        IReadOnlyList<MigrationDebugSnapshot> snapshots) =>
        snapshots.Select(SanitizeSnapshot).ToArray();

    private static MigrationDebugSnapshotPage SanitizeSnapshotPage(MigrationDebugSnapshotPage page) =>
        page with { Items = SanitizeSnapshots(page.Items) };

    private static MigrationDebugSnapshot SanitizeSnapshot(MigrationDebugSnapshot snapshot)
    {
        var payload = JsonNode.Parse(snapshot.PayloadJson);
        RemoveBinaryArtifactPaths(payload);
        return snapshot with { PayloadJson = payload?.ToJsonString(JsonOptions) ?? "null" };
    }

    private static void RemoveBinaryArtifactPaths(JsonNode? value)
    {
        if (value is JsonObject record)
        {
            if (record["__migration4dataverse_binary_v1"] is JsonObject descriptor)
            {
                descriptor.Remove("Path");
            }
            foreach (var child in record.ToArray())
            {
                RemoveBinaryArtifactPaths(child.Value);
            }
        }
        else if (value is JsonArray values)
        {
            foreach (var item in values)
            {
                RemoveBinaryArtifactPaths(item);
            }
        }
    }

    private static async Task WriteAssetAsync(
        HttpListenerResponse response,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var normalizedPath = relativePath.Replace('\\', '/');
        if (normalizedPath.Split('/').Any(segment => segment is "." or ".."))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }

        var assembly = typeof(MigrationDashboardServer).Assembly;
        var resourceName = $"Migration4Dataverse.Engine.Dashboard.wwwroot/{normalizedPath}";
        using var resource = assembly.GetManifestResourceStream(resourceName);
        if (resource is null)
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        response.ContentType = GetContentType(normalizedPath);
        response.Headers["Cache-Control"] = normalizedPath == "index.html"
            ? "no-cache"
            : "public,max-age=31536000,immutable";
        await resource.CopyToAsync(response.OutputStream, cancellationToken).ConfigureAwait(false);
    }

    private static string GetContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream"
        };

    private static async Task WriteBodyAsync(
        HttpListenerResponse response,
        string body,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class MigrationDashboardHandle : IAsyncDisposable, IDisposable
{
    private readonly HttpListener _listener;
    private readonly Task _serverTask;
    private readonly CancellationTokenSource _stop;
    private bool _disposed;

    internal MigrationDashboardHandle(
        HttpListener listener,
        Task serverTask,
        CancellationTokenSource stop,
        Uri url)
    {
        _listener = listener;
        _serverTask = serverTask;
        _stop = stop;
        Url = url;
    }

    public Uri Url { get; }

    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default) =>
        _serverTask.WaitAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _stop.Cancel();
        _listener.Close();
        await _serverTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _listener.Close();
            _stop.Dispose();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
