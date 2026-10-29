using System.Collections;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Migration4Dataverse.Engine.PowerShell;
using Microsoft.Xrm.Sdk;
using ServiceClient = Migration4Dataverse.Engine.Tests.PowerShell.Fakes.ServiceClient;

namespace Migration4Dataverse.Engine.Tests.PowerShell
{
public sealed class DataverseConnectionPoolTests
{
    [Fact]
    public void PoolRoutesByRollingRequestCostAndRestoresCallerRetrySettings()
    {
        using var first = new ServiceClient(Guid.NewGuid(), 5);
        using var second = new ServiceClient(Guid.NewGuid(), 5);

        using (var pool = DataverseConnectionPool.Create("crm", [first, second], disableRetries: false))
        {
            Assert.Equal(0, first.MaxRetryCount);
            Assert.Equal(0, second.MaxRetryCount);

            var service = Assert.IsAssignableFrom<IOrganizationService>(pool.Connection);
            service.Execute(new OrganizationRequest("CreateMultiple")
            {
                Targets = new object[] { new(), new(), new(), new() }
            });
            service.Execute(new OrganizationRequest("Update"));

            Assert.Equal(2, first.Requests.Count);
            Assert.Equal(2, second.Requests.Count);
        }

        Assert.Equal(5, first.MaxRetryCount);
        Assert.Equal(5, second.MaxRetryCount);
    }

    [Fact]
    public void PoolGroupsConnectionsByWhoAmIIdentity()
    {
        var userId = Guid.NewGuid();
        using var first = new ServiceClient(userId, 3);
        using var second = new ServiceClient(userId, 3);
        using var pool = DataverseConnectionPool.Create("crm", [first, second], disableRetries: false);
        var service = Assert.IsAssignableFrom<IOrganizationService>(pool.Connection);

        service.Execute(new OrganizationRequest("CreateMultiple")
        {
            Targets = Enumerable.Range(0, 8).Cast<object>().ToArray()
        });
        service.Execute(new OrganizationRequest("Update"));

        Assert.Equal(2, first.Requests.Count);
        Assert.Equal(2, second.Requests.Count);
    }

    [Fact]
    public async Task PoolLimitsEachIdentityAndWaitsWhenAllIdentityBudgetsAreFull()
    {
        using var firstStarted = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        using var thirdStarted = new ManualResetEventSlim();
        using var first = new ServiceClient(
            Guid.NewGuid(),
            5,
            recommendedDegreesOfParallelism: 1,
            onExecute: _ =>
            {
                firstStarted.Set();
                releaseFirst.Wait();
            });
        using var second = new ServiceClient(
            Guid.NewGuid(),
            5,
            recommendedDegreesOfParallelism: 1,
            onExecute: _ =>
            {
                secondStarted.Set();
                releaseSecond.Wait();
            });

        using var pool = DataverseConnectionPool.Create("crm", [first, second], disableRetries: false);
        Assert.Equal(2, pool.RecommendedDegreesOfParallelism);
        var service = Assert.IsAssignableFrom<IOrganizationService>(pool.Connection);
        Task<OrganizationResponse>? firstRequest = null;
        Task<OrganizationResponse>? secondRequest = null;
        Task<OrganizationResponse>? thirdRequest = null;
        try
        {
            firstRequest = Task.Factory.StartNew(
                () => service.Execute(new OrganizationRequest("Update")),
                TaskCreationOptions.LongRunning);
            Assert.True(SpinWait.SpinUntil(
                () => firstStarted.IsSet || secondStarted.IsSet,
                TimeSpan.FromSeconds(5)));

            secondRequest = Task.Factory.StartNew(
                () => service.Execute(new OrganizationRequest("Update")),
                TaskCreationOptions.LongRunning);
            Assert.True(SpinWait.SpinUntil(
                () => firstStarted.IsSet && secondStarted.IsSet,
                TimeSpan.FromSeconds(5)));

            thirdRequest = Task.Factory.StartNew(
                () =>
                {
                    thirdStarted.Set();
                    return service.Execute(new OrganizationRequest("Update"));
                },
                TaskCreationOptions.LongRunning);
            Assert.True(thirdStarted.Wait(TimeSpan.FromSeconds(5)));
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            Assert.False(thirdRequest.IsCompleted);

            releaseFirst.Set();
            await thirdRequest.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseFirst.Set();
            releaseSecond.Set();
        }

        Assert.NotNull(firstRequest);
        Assert.NotNull(secondRequest);
        Assert.NotNull(thirdRequest);
        await Task.WhenAll(firstRequest, secondRequest, thirdRequest);
    }

    [Fact]
    public void DashboardSnapshotsReportPerConnectionRequestUnitsAndDisappearAfterPoolDisposal()
    {
        const string runId = "pool-throughput-run";
        const string databasePath = "/tmp/pool-throughput.sqlite3";
        using var first = new ServiceClient(Guid.NewGuid(), 3);
        using var second = new ServiceClient(Guid.NewGuid(), 3);
        var pool = DataverseConnectionPool.Create(
            "crm",
            [first, second],
            disableRetries: false,
            runId: runId,
            databasePath: databasePath);
        var service = Assert.IsAssignableFrom<IOrganizationService>(pool.Connection);

        service.Execute(new OrganizationRequest("CreateMultiple")
        {
            Targets = Enumerable.Range(0, 4).Cast<object>().ToArray()
        });
        service.Execute(new OrganizationRequest("Update"));

        var snapshot = Assert.Single(
            DataverseConnectionPool.GetDashboardSnapshots(databasePath, runId));
        Assert.Equal("crm", snapshot.ConnectionId);
        Assert.Collection(
            snapshot.Members,
            firstMember =>
            {
                Assert.Equal(1, firstMember.MemberNumber);
                Assert.Equal(1, firstMember.TotalRequests);
                Assert.Equal(4, firstMember.TotalRequestUnits);
                Assert.Equal(0, firstMember.ActiveRequests);
            },
            secondMember =>
            {
                Assert.Equal(2, secondMember.MemberNumber);
                Assert.Equal(1, secondMember.TotalRequests);
                Assert.Equal(1, secondMember.TotalRequestUnits);
                Assert.Equal(0, secondMember.ActiveRequests);
            });

        pool.Dispose();
        Assert.Empty(DataverseConnectionPool.GetDashboardSnapshots(databasePath, runId));
    }

    [Fact]
    public void DiagnosticModeDisablesAndRestoresRetriesOnAllPooledConnections()
    {
        using var first = new ServiceClient(Guid.NewGuid(), 4);
        using var second = new ServiceClient(Guid.NewGuid(), 4);

        using (var pool = DataverseConnectionPool.Create("crm", [first, second], disableRetries: true))
        {
            Assert.Equal(0, first.MaxRetryCount);
            Assert.Equal(0, second.MaxRetryCount);
            Assert.Equal(0, ((ServiceClient)pool.Connection).MaxRetryCount);
        }

        Assert.Equal(4, first.MaxRetryCount);
        Assert.Equal(4, second.MaxRetryCount);
    }

    [Fact]
    public void PoolRejectsConnectionsThatCannotUseTheInjectedServiceClientConstructor()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            DataverseConnectionPool.Create("crm", [new object(), new object()], disableRetries: false));

        Assert.Contains("ServiceClient", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PoolRejectsConnectionsFromDifferentOrganizations()
    {
        using var first = new ServiceClient(Guid.NewGuid(), 3);
        using var second = new ServiceClient(
            Guid.NewGuid(),
            3,
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

        var exception = Assert.Throws<ArgumentException>(() =>
            DataverseConnectionPool.Create("crm", [first, second], disableRetries: false));

        Assert.Contains("same Dataverse organization", exception.Message, StringComparison.Ordinal);
    }
}
}

namespace Microsoft.Xrm.Sdk
{
    public interface IOrganizationService
    {
        OrganizationResponse Execute(OrganizationRequest request);
    }

    public sealed class OrganizationRequest(string requestName)
    {
        public string RequestName { get; } = requestName;

        public IEnumerable<object>? Requests { get; init; }

        public IEnumerable<object>? Targets { get; init; }
    }

    public sealed class OrganizationResponse
    {
        public IDictionary Results { get; } = new Hashtable();
    }
}

namespace Migration4Dataverse.Engine.Tests.PowerShell.Fakes
{
    internal sealed class ServiceClient : IOrganizationService, IDisposable
    {
        private readonly Guid _userId;
        private readonly Guid _organizationId;
        private readonly ConnectionService _connectionSvc;
        private readonly IOrganizationService? _injectedService;
        private readonly Action<OrganizationRequest>? _onExecute;

        public ServiceClient(
            Guid userId,
            int maxRetryCount,
            Guid organizationId = default,
            int recommendedDegreesOfParallelism = 4,
            Action<OrganizationRequest>? onExecute = null)
        {
            _userId = userId;
            _organizationId = organizationId == Guid.Empty
                ? Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
                : organizationId;
            MaxRetryCount = maxRetryCount;
            ConnectedOrgUriActual = new Uri("https://example.crm.dynamics.com");
            ConnectedOrgVersion = new Version(9, 2);
            RecommendedDegreesOfParallelism = recommendedDegreesOfParallelism;
            _onExecute = onExecute;
            _connectionSvc = new ConnectionService(new HttpClient());
        }

        internal ServiceClient(
            IOrganizationService organizationService,
            HttpClient httpClient,
            string baseConnectUrl,
            Version? targetVersion = null,
            object? logger = null)
        {
            _injectedService = organizationService;
            _connectionSvc = new ConnectionService(httpClient);
            ConnectedOrgUriActual = new Uri(baseConnectUrl);
            ConnectedOrgVersion = targetVersion ?? new Version(9, 2);
            RecommendedDegreesOfParallelism = 4;
        }

        public int MaxRetryCount { get; set; }

        public bool InMemoryLogCollectionEnabled { get; set; }

        public int RecommendedDegreesOfParallelism { get; }

        public Uri ConnectedOrgUriActual { get; }

        public Version ConnectedOrgVersion { get; }

        public Guid CallerId { get; set; }

        public List<OrganizationRequest> Requests { get; } = [];

        public IEnumerable<Tuple<DateTime, string>> GetAllLogs() => Array.Empty<Tuple<DateTime, string>>();

        public OrganizationResponse Execute(OrganizationRequest request)
        {
            if (_injectedService is not null)
            {
                return _injectedService.Execute(request);
            }

            Requests.Add(request);
            if (request.RequestName == "WhoAmI")
            {
                return new OrganizationResponse
                {
                    Results =
                    {
                        ["UserId"] = _userId,
                        ["OrganizationId"] = _organizationId
                    }
                };
            }

            _onExecute?.Invoke(request);
            return new OrganizationResponse();
        }

        public void Dispose()
        {
            _connectionSvc.WebApiHttpClient.Dispose();
        }

        private sealed class ConnectionService(HttpClient webApiHttpClient)
        {
            public HttpClient WebApiHttpClient { get; } = webApiHttpClient;
        }
    }
}
