using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Migration4Dataverse.Engine.PowerShell;

internal sealed class DataverseConnectionPool : IDisposable
{
    private static readonly TimeSpan RequestWindowDuration = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<
        string,
        ConcurrentDictionary<string, DataverseConnectionPool>> PoolsByDashboardRunKey = new(StringComparer.Ordinal);

    private readonly object _sync = new();
    private readonly object _monitorSync = new();
    private readonly Member[] _members;
    private readonly string _connectionId;
    private readonly Dictionary<string, IdentityWindow> _identityWindows = new(StringComparer.Ordinal);
    private readonly DataverseConnectionRetryMonitor[] _connectionMonitors;
    private readonly DataverseConnectionRetryMonitor _proxyMonitor;
    private readonly HttpClient? _httpClient;
    private readonly MethodInfo _executeMethod;
    private readonly PropertyInfo? _proxyCallerId;
    private readonly object? _proxyDefaultCallerId;
    private readonly int _configuredRetryCount;
    private readonly string? _dashboardRunKey;
    private TaskCompletionSource _memberAvailability = CreateMemberAvailabilitySignal();
    private int _nextMember;
    private bool _registeredForDashboardMetrics;
    private bool _disposed;

    private DataverseConnectionPool(
        string connectionId,
        IReadOnlyList<object> connections,
        bool disableRetries,
        string? runId,
        string? databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(connections);
        _connectionId = connectionId;
        _dashboardRunKey = CreateDashboardRunKey(databasePath, runId);
        if (connections.Count < 2)
        {
            throw new ArgumentException("A Dataverse connection pool requires at least two connections.", nameof(connections));
        }

        var serviceClientType = FindServiceClientType(connections[0].GetType());
        if (serviceClientType is null ||
            connections.Any(connection => !serviceClientType.IsInstanceOfType(connection)))
        {
            throw new ArgumentException(
                $"Connection slot '{connectionId}' must contain two or more compatible Dataverse ServiceClient instances.");
        }

        var serviceInterface = serviceClientType.GetInterfaces().FirstOrDefault(type =>
            string.Equals(type.FullName, "Microsoft.Xrm.Sdk.IOrganizationService", StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Dataverse connection type '{serviceClientType.FullName}' does not implement IOrganizationService.");
        _executeMethod = serviceInterface.GetMethods().Single(method =>
            string.Equals(method.Name, "Execute", StringComparison.Ordinal) &&
            method.GetParameters().Length == 1);

        var maxRetryProperty = serviceClientType.GetProperty(
            "MaxRetryCount",
            BindingFlags.Instance | BindingFlags.Public);
        if (maxRetryProperty is not { CanRead: true, CanWrite: true } ||
            maxRetryProperty.PropertyType != typeof(int))
        {
            throw new InvalidOperationException(
                $"Dataverse connection type '{serviceClientType.FullName}' does not expose a writable MaxRetryCount; " +
                "connection pooling cannot account for retry attempts safely.");
        }

        _configuredRetryCount = (int)(maxRetryProperty.GetValue(connections[0])
            ?? throw new InvalidOperationException("The Dataverse retry count could not be read."));
        if (connections.Any(connection =>
                !Equals(maxRetryProperty.GetValue(connection), _configuredRetryCount)))
        {
            throw new ArgumentException(
                $"Connections in slot '{connectionId}' must use the same MaxRetryCount.");
        }

        var baseConnectUrl = ReadRequiredStringProperty(connections[0], "ConnectedOrgUriActual");
        var targetVersion = serviceClientType.GetProperty(
            "ConnectedOrgVersion",
            BindingFlags.Instance | BindingFlags.Public)?.GetValue(connections[0]) as Version;
        var organizationRequestType = _executeMethod.GetParameters()[0].ParameterType;
        var probeRequest = Activator.CreateInstance(organizationRequestType, "WhoAmI")
            ?? throw new InvalidOperationException(
                $"Could not create a '{organizationRequestType.FullName}' request to identify pooled connections.");

        _members = new Member[connections.Count];
        _connectionMonitors = new DataverseConnectionRetryMonitor[_members.Length];
        var initializedMonitors = 0;
        object? pooledConnection = null;
        try
        {
            Guid? expectedOrganizationId = null;
            for (var index = 0; index < connections.Count; index++)
            {
                var connection = connections[index];
                var response = InvokeMethod(_executeMethod, connection, [probeRequest])
                    ?? throw new InvalidOperationException(
                        $"WhoAmI returned no response for a connection in slot '{connectionId}'.");
                var userId = ReadGuidResult(response, "UserId");
                var organizationId = ReadGuidResult(response, "OrganizationId");
                if (expectedOrganizationId is { } existingOrganizationId &&
                    existingOrganizationId != organizationId)
                {
                    throw new ArgumentException(
                        $"Connections in slot '{connectionId}' must all point to the same Dataverse organization.");
                }
                expectedOrganizationId = organizationId;
                var identity = $"{organizationId:N}:{userId:N}";
                var recommendedConcurrency = GetRecommendedDegreesOfParallelism(connection);
                if (!_identityWindows.TryGetValue(identity, out var identityWindow))
                {
                    identityWindow = new IdentityWindow(identity, recommendedConcurrency);
                    _identityWindows.Add(identity, identityWindow);
                }
                else
                {
                    identityWindow.MaxConcurrency = Math.Min(
                        identityWindow.MaxConcurrency,
                        recommendedConcurrency);
                }

                var memberHttpClient = CreateMemberHttpClient(connection);
                var member = new Member(
                    connection,
                    identityWindow,
                    memberHttpClient.Client,
                    memberHttpClient.IsOwned);
                member.RequestWindow.Add(1, Stopwatch.GetTimestamp());
                identityWindow.RequestWindow.Add(1, Stopwatch.GetTimestamp());
                _members[index] = member;
            }

            for (; initializedMonitors < _members.Length; initializedMonitors++)
            {
                _connectionMonitors[initializedMonitors] =
                    new DataverseConnectionRetryMonitor(_members[initializedMonitors].Connection, disableRetries: true);
            }

            var organizationServiceProxy = DispatchProxy.Create(
                serviceInterface,
                typeof(DataverseOrganizationServiceProxy));
            ((DataverseOrganizationServiceProxy)organizationServiceProxy).Initialize(this);

            _httpClient = new HttpClient(new DataversePoolHttpMessageHandler(this));
            var constructor = serviceClientType
                .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .SingleOrDefault(candidate =>
                {
                    var parameters = candidate.GetParameters();
                    return parameters.Length == 5 &&
                           parameters[0].ParameterType == serviceInterface &&
                           parameters[1].ParameterType == typeof(HttpClient) &&
                           parameters[2].ParameterType == typeof(string) &&
                           parameters[3].ParameterType == typeof(Version);
                })
                ?? throw new InvalidOperationException(
                    $"Dataverse connection type '{serviceClientType.FullName}' no longer exposes the injected-service constructor required for pooling.");

            pooledConnection = constructor.Invoke(
                [organizationServiceProxy, _httpClient, baseConnectUrl, targetVersion, null])
                ?? throw new InvalidOperationException("The pooled Dataverse ServiceClient constructor returned null.");
            Connection = pooledConnection;
            _proxyCallerId = serviceClientType.GetProperty(
                "CallerId",
                BindingFlags.Instance | BindingFlags.Public);
            _proxyDefaultCallerId = _proxyCallerId?.GetValue(Connection);
            var proxyRetryProperty = serviceClientType.GetProperty(
                "MaxRetryCount",
                BindingFlags.Instance | BindingFlags.Public);
            proxyRetryProperty?.SetValue(Connection, disableRetries ? 0 : _configuredRetryCount);
            _proxyMonitor = new DataverseConnectionRetryMonitor(Connection, disableRetries);
        }
        catch
        {
            for (var index = initializedMonitors - 1; index >= 0; index--)
            {
                _connectionMonitors[index].Dispose();
            }

            if (pooledConnection is IDisposable pooledDisposable)
            {
                pooledDisposable.Dispose();
            }
            _httpClient?.Dispose();
            foreach (var member in _members)
            {
                if (member?.OwnsHttpClient == true)
                {
                    member.HttpClient.Dispose();
                }
            }

            throw;
        }
    }

    public object Connection { get; }

    public bool HasRetryTelemetry =>
        _proxyMonitor.IsAvailable && _connectionMonitors.All(monitor => monitor.IsAvailable);

    public static DataverseConnectionPool Create(
        string connectionId,
        IReadOnlyList<object> connections,
        bool disableRetries,
        string? runId = null,
        string? databasePath = null)
    {
        var pool = new DataverseConnectionPool(connectionId, connections, disableRetries, runId, databasePath);
        if (pool._dashboardRunKey is { } dashboardRunKey)
        {
            var runPools = PoolsByDashboardRunKey.GetOrAdd(
                dashboardRunKey,
                _ => new ConcurrentDictionary<string, DataverseConnectionPool>(StringComparer.OrdinalIgnoreCase));
            if (!runPools.TryAdd(connectionId, pool))
            {
                pool.Dispose();
                throw new InvalidOperationException(
                    $"A Dataverse connection pool is already registered for '{connectionId}' in run '{runId}'.");
            }

            pool._registeredForDashboardMetrics = true;
        }

        return pool;
    }

    public static IReadOnlyList<DataverseConnectionPoolDashboardSnapshot> GetDashboardSnapshots(
        string databasePath,
        string runId)
    {
        var dashboardRunKey = CreateDashboardRunKey(databasePath, runId);
        if (dashboardRunKey is null ||
            !PoolsByDashboardRunKey.TryGetValue(dashboardRunKey, out var runPools))
        {
            return [];
        }

        return runPools
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Value.GetDashboardSnapshot())
            .ToArray();
    }

    public object? InvokeOrganizationService(MethodInfo method, object?[]? arguments)
    {
        ArgumentNullException.ThrowIfNull(method);
        var requestCost = DataverseRequestCostCalculator.Calculate(method, arguments);
        var member = SelectMember(requestCost);
        try
        {
            lock (member.Sync)
            {
                var callerId = _proxyCallerId?.GetValue(Connection);
                var callerIdWasOverridden = !Equals(callerId, _proxyDefaultCallerId);
                var memberCallerId = member.CallerIdProperty;
                var originalCallerId = memberCallerId?.GetValue(member.Connection);
                try
                {
                    if (callerIdWasOverridden && memberCallerId is { CanWrite: true })
                    {
                        memberCallerId.SetValue(member.Connection, callerId);
                    }

                    return InvokeMethod(method, member.Connection, arguments);
                }
                finally
                {
                    if (callerIdWasOverridden && memberCallerId is { CanWrite: true })
                    {
                        memberCallerId.SetValue(member.Connection, originalCallerId);
                    }
                }
            }
        }
        finally
        {
            ReleaseMember(member);
        }
    }

    public async Task<HttpResponseMessage> SendHttpRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var member = await SelectMemberAsync(1, cancellationToken).ConfigureAwait(false);
        if (member.HttpClient is null)
        {
            ReleaseMember(member);
            throw new InvalidOperationException(
                "A connection in the Dataverse pool does not expose its authenticated Web API HttpClient.");
        }

        return await SendAndReleaseAsync(member, request, cancellationToken).ConfigureAwait(false);
    }

    public DataverseRetryObservation ReadObservations()
    {
        lock (_monitorSync)
        {
            var retries = 0;
            var throttles = 0;
            var available = _proxyMonitor.IsAvailable;
            foreach (var monitor in _connectionMonitors.Append(_proxyMonitor))
            {
                var observation = monitor.ReadObservations();
                retries = checked(retries + observation.RetryCount);
                throttles = checked(throttles + observation.ThrottleCount);
                available &= observation.IsAvailable;
            }

            return new DataverseRetryObservation(retries, throttles, available);
        }
    }

    public int RecommendedDegreesOfParallelism
    {
        get
        {
            var total = _identityWindows.Values.Sum(window => (long)window.MaxConcurrency);
            return (int)Math.Min(total, int.MaxValue);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var memberAvailability = _memberAvailability;
            _memberAvailability = CreateMemberAvailabilitySignal();
            memberAvailability.TrySetResult();
        }

        if (_registeredForDashboardMetrics &&
            _dashboardRunKey is { } dashboardRunKey &&
            PoolsByDashboardRunKey.TryGetValue(dashboardRunKey, out var runPools))
        {
            runPools.TryRemove(_connectionId, out _);
            if (runPools.IsEmpty)
            {
                ((ICollection<KeyValuePair<string, ConcurrentDictionary<string, DataverseConnectionPool>>>)
                    PoolsByDashboardRunKey).Remove(new(dashboardRunKey, runPools));
            }
        }

        _proxyMonitor.Dispose();
        foreach (var monitor in _connectionMonitors)
        {
            monitor.Dispose();
        }

        if (Connection is IDisposable disposableConnection)
        {
            disposableConnection.Dispose();
        }
        _httpClient?.Dispose();
        foreach (var member in _members)
        {
            if (member.OwnsHttpClient)
            {
                member.HttpClient.Dispose();
            }
        }
    }

    private static string? CreateDashboardRunKey(string? databasePath, string? runId)
    {
        return string.IsNullOrWhiteSpace(databasePath) || string.IsNullOrWhiteSpace(runId)
            ? null
            : $"{Path.GetFullPath(databasePath)}\0{runId}";
    }

    private async Task<HttpResponseMessage> SendAndReleaseAsync(
        Member member,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            using var requestCopy = await CopyHttpRequestAsync(request, cancellationToken).ConfigureAwait(false);
            return await member.HttpClient.SendAsync(requestCopy, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseMember(member);
        }
    }

    private static async Task<HttpRequestMessage> CopyHttpRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };
        foreach (var header in request.Headers)
        {
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        foreach (var option in request.Options)
        {
            copy.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        }

        if (request.Content is not null)
        {
            var content = new ByteArrayContent(
                await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
            foreach (var header in request.Content.Headers)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            copy.Content = content;
        }

        return copy;
    }

    private Member SelectMember(int requestCost) =>
        SelectMemberAsync(requestCost, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    private async ValueTask<Member> SelectMemberAsync(
        int requestCost,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task availabilityTask;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var selected = TrySelectMemberLocked(requestCost);
                if (selected is not null)
                {
                    return selected;
                }

                availabilityTask = _memberAvailability.Task;
            }

            await availabilityTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private Member? TrySelectMemberLocked(int requestCost)
    {
        var now = Stopwatch.GetTimestamp();
        foreach (var identityWindow in _identityWindows.Values)
        {
            identityWindow.RequestWindow.RemoveExpired(now, RequestWindowDuration);
        }
        foreach (var member in _members)
        {
            member.RequestWindow.RemoveExpired(now, RequestWindowDuration);
        }

        var eligibleIdentities = _identityWindows.Values
            .Where(window => window.Active < window.MaxConcurrency)
            .ToArray();
        if (eligibleIdentities.Length == 0)
        {
            return null;
        }

        var leastLoadedIdentity = eligibleIdentities
            .OrderBy(window => window.RequestWindow.Total)
            .ThenBy(window => window.Active)
            .First();
        var candidates = _members
            .Where(member => ReferenceEquals(member.IdentityWindow, leastLoadedIdentity))
            .OrderBy(member => member.Active)
            .ThenBy(member => member.RequestWindow.Total)
            .ToArray();
        var leastActive = candidates[0].Active;
        var leastRequests = candidates[0].RequestWindow.Total;
        var leastLoadedMembers = candidates
            .Where(member => member.Active == leastActive &&
                             member.RequestWindow.Total == leastRequests)
            .ToArray();
        var selected = leastLoadedMembers[_nextMember++ % leastLoadedMembers.Length];
        selected.RequestWindow.Add(requestCost, now);
        selected.IdentityWindow.RequestWindow.Add(requestCost, now);
        selected.TotalRequests = checked(selected.TotalRequests + 1);
        selected.TotalRequestUnits = checked(selected.TotalRequestUnits + requestCost);
        selected.Active++;
        selected.IdentityWindow.Active++;
        return selected;
    }

    private DataverseConnectionPoolDashboardSnapshot GetDashboardSnapshot()
    {
        lock (_sync)
        {
            return new DataverseConnectionPoolDashboardSnapshot(
                _connectionId,
                _members.Select((member, index) => new DataverseConnectionPoolMemberDashboardSnapshot(
                    index + 1,
                    member.TotalRequests,
                    member.TotalRequestUnits,
                    member.Active)).ToArray());
        }
    }

    private void ReleaseMember(Member member)
    {
        lock (_sync)
        {
            if (member.Active <= 0 || member.IdentityWindow.Active <= 0)
            {
                throw new InvalidOperationException("The Dataverse connection pool active-request count underflowed.");
            }

            member.Active--;
            member.IdentityWindow.Active--;
            var memberAvailability = _memberAvailability;
            _memberAvailability = CreateMemberAvailabilitySignal();
            memberAvailability.TrySetResult();
        }
    }

    private static int GetRecommendedDegreesOfParallelism(object connection)
    {
        var property = connection.GetType().GetProperty(
            "RecommendedDegreesOfParallelism",
            BindingFlags.Instance | BindingFlags.Public);
        if (property?.GetValue(connection) is { } value)
        {
            var recommended = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (recommended > 0)
            {
                return recommended;
            }
        }

        return 4;
    }

    private static TaskCompletionSource CreateMemberAvailabilitySignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string ReadRequiredStringProperty(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public);
        var value = property?.GetValue(instance)?.ToString();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException(
                $"Dataverse connection type '{instance.GetType().FullName}' has no usable {propertyName} value.")
            : value;
    }

    private static Guid ReadGuidResult(object response, string resultName)
    {
        var results = response.GetType().GetProperty(
            "Results",
            BindingFlags.Instance | BindingFlags.Public)?.GetValue(response);
        var result = results switch
        {
            IDictionary dictionary when dictionary.Contains(resultName) => dictionary[resultName],
            _ => results?.GetType().GetProperty("Item", [typeof(string)])
                ?.GetValue(results, [resultName])
        };
        return result is Guid value && value != Guid.Empty
            ? value
            : throw new InvalidOperationException(
                $"Dataverse WhoAmI did not return a valid {resultName} value.");
    }

    private static MemberHttpClient CreateMemberHttpClient(object connection)
    {
        var connectionField = FindInstanceField(connection.GetType(), "_connectionSvc");
        var connectionService = connectionField?.GetValue(connection);
        var existingHttpClient = connectionService?.GetType().GetProperty(
                "WebApiHttpClient",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(connectionService) as HttpClient;
        if (existingHttpClient is not null)
        {
            return new MemberHttpClient(existingHttpClient, IsOwned: false);
        }

        var serviceClientType = FindServiceClientType(connection.GetType())
            ?? throw new InvalidOperationException(
                $"Dataverse connection type '{connection.GetType().FullName}' is not a ServiceClient.");
        var accessTokenProvider = serviceClientType.GetProperty(
                "GetAccessToken",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(connection) as Func<string, Task<string>>;
        if (accessTokenProvider is null)
        {
            throw new InvalidOperationException(
                $"Dataverse connection type '{serviceClientType.FullName}' does not expose an authenticated Web API HttpClient or token provider.");
        }

        var customHeadersProvider = serviceClientType.GetProperty(
                "GetCustomHeaders",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(connection) as Func<Task<Dictionary<string, string>>>;
        var organizationUri = new Uri(ReadRequiredStringProperty(connection, "ConnectedOrgUriActual"));
        var handler = new DataverseTokenAuthenticationHandler(
            accessTokenProvider,
            customHeadersProvider,
            organizationUri.ToString())
        {
            InnerHandler = new HttpClientHandler()
        };
        return new MemberHttpClient(
            new HttpClient(handler) { BaseAddress = organizationUri },
            IsOwned: true);
    }

    private static Type? FindServiceClientType(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.Name, "ServiceClient", StringComparison.Ordinal))
            {
                return current;
            }
        }

        return null;
    }

    private sealed record MemberHttpClient(HttpClient Client, bool IsOwned);

    private sealed class DataverseTokenAuthenticationHandler(
        Func<string, Task<string>> accessTokenProvider,
        Func<Task<Dictionary<string, string>>>? customHeadersProvider,
        string tokenResource) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var accessToken = await accessTokenProvider(tokenResource).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new InvalidOperationException(
                    "The Dataverse connection's token provider returned an empty access token.");
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            if (customHeadersProvider is not null)
            {
                var customHeaders = await customHeadersProvider().ConfigureAwait(false);
                if (customHeaders is not null)
                {
                    foreach (var header in customHeaders)
                    {
                        if (request.Headers.Contains(header.Key))
                        {
                            continue;
                        }

                        if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                        {
                            request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        }
                    }
                }
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private static FieldInfo? FindInstanceField(Type type, string name)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var field = current.GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (field is not null)
            {
                return field;
            }
        }

        return null;
    }

    private static object? InvokeMethod(MethodInfo method, object instance, object?[]? arguments)
    {
        try
        {
            return method.Invoke(instance, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private sealed class Member(
        object connection,
        IdentityWindow identityWindow,
        HttpClient httpClient,
        bool ownsHttpClient)
    {
        public object Connection { get; } = connection;

        public IdentityWindow IdentityWindow { get; } = identityWindow;

        public HttpClient HttpClient { get; } = httpClient;

        public bool OwnsHttpClient { get; } = ownsHttpClient;

        public object Sync { get; } = new();

        public PropertyInfo? CallerIdProperty { get; } = connection.GetType().GetProperty(
            "CallerId",
            BindingFlags.Instance | BindingFlags.Public);

        public RequestWindow RequestWindow { get; } = new();

        public long TotalRequests { get; set; }

        public long TotalRequestUnits { get; set; }

        public int Active { get; set; }
    }

    private sealed class IdentityWindow(string identity, int maxConcurrency)
    {
        public string Identity { get; } = identity;

        public int MaxConcurrency { get; set; } = maxConcurrency;

        public RequestWindow RequestWindow { get; } = new();

        public int Active { get; set; }
    }

    private sealed class RequestWindow
    {
        private readonly Queue<(long Timestamp, int Units)> _entries = new();

        public int Total { get; private set; }

        public void Add(int units, long timestamp)
        {
            _entries.Enqueue((timestamp, units));
            Total = checked(Total + units);
        }

        public void RemoveExpired(long now, TimeSpan duration)
        {
            var windowTicks = (long)(duration.TotalSeconds * Stopwatch.Frequency);
            while (_entries.TryPeek(out var entry) && now - entry.Timestamp >= windowTicks)
            {
                _entries.Dequeue();
                Total -= entry.Units;
            }
        }
    }

    private sealed class DataversePoolHttpMessageHandler(DataverseConnectionPool pool) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return pool.SendHttpRequestAsync(request, cancellationToken);
        }
    }
}

internal sealed record DataverseConnectionPoolDashboardSnapshot(
    string ConnectionId,
    IReadOnlyList<DataverseConnectionPoolMemberDashboardSnapshot> Members);

internal sealed record DataverseConnectionPoolMemberDashboardSnapshot(
    int MemberNumber,
    long TotalRequests,
    long TotalRequestUnits,
    int ActiveRequests);

internal class DataverseOrganizationServiceProxy : DispatchProxy
{
    private DataverseConnectionPool? _pool;

    public void Initialize(DataverseConnectionPool pool)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var pool = _pool ?? throw new InvalidOperationException("The Dataverse service proxy is not initialized.");
        var method = targetMethod ?? throw new InvalidOperationException("The Dataverse service proxy received no method.");
        return pool.InvokeOrganizationService(method, args);
    }
}

internal static class DataverseRequestCostCalculator
{
    public static int Calculate(MethodInfo method, object?[]? arguments)
    {
        if (!string.Equals(method.Name, "Execute", StringComparison.Ordinal) ||
            arguments is not { Length: > 0 } ||
            arguments[0] is not { } request)
        {
            return 1;
        }

        return CalculateRequest(request, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    private static int CalculateRequest(object request, HashSet<object> seen)
    {
        if (!seen.Add(request))
        {
            throw new InvalidDataException("A Dataverse batch request contains a cycle.");
        }

        try
        {
            var requestName = request.GetType().GetProperty(
                "RequestName",
                BindingFlags.Instance | BindingFlags.Public)?.GetValue(request)?.ToString()
                ?? request.GetType().Name;
            if (requestName.EndsWith("Request", StringComparison.OrdinalIgnoreCase))
            {
                requestName = requestName[..^"Request".Length];
            }

            if (string.Equals(requestName, "ExecuteMultiple", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(requestName, "ExecuteTransaction", StringComparison.OrdinalIgnoreCase))
            {
                var requests = GetRequiredEnumerable(request, "Requests", requestName);
                var cost = 0;
                foreach (var nestedRequest in requests)
                {
                    cost = checked(cost + CalculateRequest(
                        nestedRequest ?? throw new InvalidDataException(
                            $"Dataverse {requestName} contains a null request."),
                        seen));
                }

                return Math.Max(1, cost);
            }

            if (requestName.EndsWith("Multiple", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(requestName, "RetrieveMultiple", StringComparison.OrdinalIgnoreCase))
            {
                var targets = GetRequiredProperty(request, "Targets", requestName);
                var entities = targets.GetType().GetProperty(
                    "Entities",
                    BindingFlags.Instance | BindingFlags.Public)?.GetValue(targets) as IEnumerable;
                var targetsEnumerable = entities ?? targets as IEnumerable
                    ?? throw new InvalidDataException(
                        $"Dataverse bulk request '{requestName}' has an unsupported Targets collection.");
                var count = targetsEnumerable.Cast<object?>().Count();
                return Math.Max(1, count);
            }

            return 1;
        }
        finally
        {
            seen.Remove(request);
        }
    }

    private static object GetRequiredProperty(object instance, string propertyName, string requestName)
    {
        return instance.GetType().GetProperty(
                   propertyName,
                   BindingFlags.Instance | BindingFlags.Public)?.GetValue(instance)
               ?? throw new InvalidDataException(
                   $"Dataverse bulk request '{requestName}' does not expose a {propertyName} collection.");
    }

    private static IEnumerable GetRequiredEnumerable(object instance, string propertyName, string requestName)
    {
        return GetRequiredProperty(instance, propertyName, requestName) as IEnumerable
               ?? throw new InvalidDataException(
                   $"Dataverse batch request '{requestName}' has an unsupported {propertyName} collection.");
    }
}
