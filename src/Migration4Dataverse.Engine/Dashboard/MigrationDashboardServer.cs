using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Web;
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
        var store = string.IsNullOrWhiteSpace(databasePath) ? null : MigrationStoreFactory.Open(databasePath);
        store?.PurgeExpiredDebugSnapshots();
        var listener = new HttpListener();
        listener.Prefixes.Add(uri.ToString());
        try
        {
            listener.Start();
            var stop = new CancellationTokenSource();
            var serverTask = RunServerAsync(listener, store, configurationJson, stop.Token);
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
        string? configurationJson,
        CancellationToken cancellationToken)
    {
        await foreach (var context in requestQueue.ReadAllAsync().ConfigureAwait(false))
        {
            await HandleRequestAsync(context, store, configurationJson, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task HandleRequestAsync(
        HttpListenerContext context,
        IMigrationStore? store,
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
            if (request.HttpMethod != "GET")
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

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString)
                .ToArray();
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
                        store.GetDebugSnapshots(
                            runId,
                            query["lineageId"],
                            query["batchId"],
                            ParseLimit(query["limit"], 500)),
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

                    await WriteJsonAsync(
                        response,
                        HttpStatusCode.OK,
                        store.GetDebugSnapshotPage(
                            runId,
                            query["stepId"] ?? throw new ArgumentException("A stepId query parameter is required."),
                            query["search"],
                            ParseLimit(query["limit"], 100),
                            ParseOffset(query["offset"]),
                            query["stageName"],
                            query["sortColumn"] ?? "record",
                            query["sortDirection"] ?? "descending",
                            ParseSnapshotFilters(query["filters"])),
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
                        store.GetDebugSnapshots(runId, lineageId: segments[4], limit: 2000),
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
