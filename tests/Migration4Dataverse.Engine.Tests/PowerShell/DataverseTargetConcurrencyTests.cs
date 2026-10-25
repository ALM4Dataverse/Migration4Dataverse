using System.Collections;
using System.Management.Automation;
using System.Reflection;
using Migration4Dataverse.Engine.PowerShell;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.Tests.PowerShell;

public sealed class DataverseTargetConcurrencyTests
{
    [Fact]
    public void DataverseTargetsShareAnAdaptiveConnectionBudgetAndThrottleFeedback()
    {
        var connection = new TestDataverseConnection
        {
            RecommendedDegreesOfParallelism = 4
        };
        var meter = new PipelineThroughputMeter();
        using var controller = new PipelineAdaptiveDataverseConnectionController(
            "crm",
            connection,
            maxConcurrency: 8,
            meter,
            new PipelineBatchTuningCoordinator(),
            disableRetriesForDiagnostics: false);
        controller.RegisterTarget(
            "TargetOne", 10, autoBatchSize: false, autoConcurrency: true,
            batchSize: 10, maxStepConcurrency: 1, null, null, null);
        controller.RegisterTarget(
            "TargetTwo", 10, autoBatchSize: false, autoConcurrency: true,
            batchSize: 10, maxStepConcurrency: 1, null, null, null);
        controller.CompleteRegistration();

        var leases = Enumerable.Range(0, 4)
            .Select(index => controller.TryAcquire(index % 2 == 0 ? "TargetOne" : "TargetTwo"))
            .ToArray();

        Assert.All(leases, lease => Assert.NotNull(lease));
        Assert.Null(controller.TryAcquire("TargetOne"));
        Assert.True(connection.InMemoryLogCollectionEnabled);

        connection.Logs.Add(Tuple.Create(DateTime.UtcNow, "Retrying request after HTTP 429 throttling."));
        meter.Record(1);
        controller.Complete(leases[0]!, rowsWritten: 1, succeeded: true);
        controller.Complete(leases[1]!, rowsWritten: 1, succeeded: true);

        var state = controller.GetDashboardState("TargetOne");
        Assert.Equal(2, state.ConnectionConcurrencyBudget);
        Assert.Equal(1, state.RetryCount);
        Assert.Equal(1, state.ThrottleCount);
        Assert.True(state.RetryTelemetryAvailable);

        leases[2]!.Dispose();
        leases[3]!.Dispose();
        controller.Dispose();
        Assert.False(connection.InMemoryLogCollectionEnabled);
    }

    [Fact]
    public void DiagnosticNoRetryModeTemporarilySetsAndRestoresServiceClientRetryCount()
    {
        var connection = new TestDataverseConnection { MaxRetryCount = 7 };

        using (var monitor = new DataverseConnectionRetryMonitor(connection, disableRetries: true))
        {
            Assert.Equal(0, connection.MaxRetryCount);
        }

        Assert.Equal(7, connection.MaxRetryCount);
    }

    [Fact]
    public void GraphNoRetryDiagnosticModeRestoresCallerOwnedConnectionSettings()
    {
        using var directory = new TemporaryDirectory();
        using var executor = new PowerShellScriptExecutor();
        var connection = new TestDataverseConnection { MaxRetryCount = 6 };

        using (var stepSet = (IDisposable)CreateGraph(
                   directory.Path,
                   executor,
                   firstConcurrency: "auto",
                   dataverseConnection: connection,
                   disableRetriesForDiagnostics: true)!)
        {
            Assert.Equal(0, connection.MaxRetryCount);
        }

        Assert.Equal(6, connection.MaxRetryCount);
    }

    [Fact]
    public void DataverseTargetsDefaultToAutoTuningPerTarget()
    {
        using var directory = new TemporaryDirectory();
        using var executor = new PowerShellScriptExecutor();

        using var stepSet = (IDisposable)CreateGraph(directory.Path, executor, null, 2)!;
    }

    [Fact]
    public void DataverseTargetsAcceptIndependentConcurrencyOnTheSameConnection()
    {
        using var directory = new TemporaryDirectory();
        using var executor = new PowerShellScriptExecutor();

        using var stepSet = (IDisposable)CreateGraph(directory.Path, executor, 2, 3)!;
    }

    [Fact]
    public void DataverseTargetsAcceptExplicitAutoSettings()
    {
        using var directory = new TemporaryDirectory();
        using var executor = new PowerShellScriptExecutor();

        using var stepSet = (IDisposable)CreateGraph(directory.Path, executor, "auto", "auto")!;
    }

    [Fact]
    public void DataverseTargetLoadsStoredTuningForMatchingStepIdAndRespectsCurrentCaps()
    {
        using var directory = new TemporaryDirectory();
        using var executor = new PowerShellScriptExecutor();
        var store = new SqliteMigrationStore(Path.Combine(directory.Path, "migration.sqlite3"));
        store.SaveDataverseTargetTuningProfile("TargetOne", batchSize: 25, maxConcurrency: 8);

        using var stepSet = (MigrationStepSet)CreateGraph(
            directory.Path,
            executor,
            firstConcurrency: "auto",
            store: store,
            defaultBatchSize: 10,
            maxTargetConcurrency: 4)!;
        var controller = Assert.Single(stepSet.TargetControllers);
        var state = controller.GetDashboardState("TargetOne");

        Assert.Equal(10, state.BatchSize);
        Assert.Equal(4, state.MaxConcurrency);
        Assert.Equal("BatchSize", state.Stage);
    }

    [Fact]
    public void DataverseTargetPersistsTuningOnceTheBestValuesAreFound()
    {
        using var directory = new TemporaryDirectory();
        using var executor = new PowerShellScriptExecutor();
        var store = new SqliteMigrationStore(Path.Combine(directory.Path, "migration.sqlite3"));
        using var stepSet = (MigrationStepSet)CreateGraph(
            directory.Path,
            executor,
            firstConcurrency: "auto",
            store: store,
            defaultBatchSize: 1,
            maxTargetConcurrency: 2)!;
        var controller = Assert.Single(stepSet.TargetControllers);
        for (var index = 0; index < 3; index++)
        {
            using var lease = controller.TryAcquire("TargetOne");
            Assert.NotNull(lease);
            stepSet.ThroughputMeter.Record(1);
            controller.Complete(lease, rowsWritten: 1, succeeded: true);
        }

        var profile = store.GetDataverseTargetTuningProfile("TargetOne");
        Assert.NotNull(profile);
        Assert.Equal(1, profile.BatchSize);
        Assert.Equal(2, profile.MaxConcurrency);
        Assert.Equal(2, profile.AlgorithmVersion);
        Assert.Equal("crm", profile.ConnectionId);
    }

    [Theory]
    [InlineData("crm", 3)]
    [InlineData("other", 4)]
    public void OnlyPipelineProfilesForTheSameConnectionSeedSharedConcurrency(
        string profileConnectionId,
        int expectedConcurrency)
    {
        using var directory = new TemporaryDirectory();
        using var executor = new PowerShellScriptExecutor();
        var store = new SqliteMigrationStore(Path.Combine(directory.Path, "migration.sqlite3"));
        store.SaveDataverseTargetTuningProfile(
            "TargetOne",
            batchSize: 10,
            maxConcurrency: 3,
            algorithmVersion: 2,
            connectionId: profileConnectionId);

        using var stepSet = (MigrationStepSet)CreateGraph(
            directory.Path,
            executor,
            firstConcurrency: "auto",
            store: store,
            maxTargetConcurrency: 4)!;

        Assert.Equal(
            expectedConcurrency,
            Assert.Single(stepSet.TargetControllers).GetDashboardState("TargetOne").MaxConcurrency);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void DataverseTargetRejectsNonPositiveConcurrency(int maxConcurrency)
    {
        using var directory = new TemporaryDirectory();
        using var executor = new PowerShellScriptExecutor();

        var exception = Assert.Throws<TargetInvocationException>(() =>
            CreateGraph(directory.Path, executor, maxConcurrency));

        Assert.IsType<ArgumentOutOfRangeException>(exception.InnerException);
    }

    private static object? CreateGraph(
        string directory,
        PowerShellScriptExecutor executor,
        object? firstConcurrency,
        object? secondConcurrency = null,
        SqliteMigrationStore? store = null,
        int defaultBatchSize = 100,
        int maxTargetConcurrency = 32,
        object? dataverseConnection = null,
        bool disableRetriesForDiagnostics = false)
    {
        var steps = new List<PSObject>
        {
            PSObject.AsPSObject(new
            {
                Id = "Source",
                Type = "source",
                Inputs = Array.Empty<string>(),
                Source = new
                {
                    Type = "jsonl",
                    ConnectionId = "files",
                    Path = "source.jsonl",
                    Key = "id"
                }
            }),
            CreateTarget("TargetOne", firstConcurrency)
        };
        if (secondConcurrency is not null)
        {
            steps.Add(CreateTarget("TargetTwo", secondConcurrency));
        }

        var connections = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["files"] = new DirectoryInfo(directory),
            ["crm"] = dataverseConnection ?? new object()
        };
        var factoryType = typeof(InvokeMigrationCommand).Assembly.GetType(
            "Migration4Dataverse.Engine.PowerShell.PowerShellMigrationStepFactory",
            throwOnError: true)!;
        var createGraph = factoryType.GetMethod(
            "CreateGraph",
            BindingFlags.Public | BindingFlags.Static)!;

        return createGraph.Invoke(
            null,
            [
                steps,
                connections,
                executor,
                defaultBatchSize,
                false,
                store,
                maxTargetConcurrency,
                disableRetriesForDiagnostics
            ]);
    }

    private static PSObject CreateTarget(string id, object? maxConcurrency)
    {
        var target = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["Type"] = "dataverse",
            ["ConnectionId"] = "crm",
            ["Table"] = "account"
        };
        if (maxConcurrency is not null)
        {
            target["MaxConcurrency"] = maxConcurrency;
        }
        target["BatchSize"] = "auto";

        return PSObject.AsPSObject(new
        {
            Id = id,
            Type = "target",
            Inputs = new[] { "Source" },
            Target = target
        });
    }

    private sealed class TestDataverseConnection
    {
        public bool InMemoryLogCollectionEnabled { get; set; }

        public int MaxRetryCount { get; set; }

        public int RecommendedDegreesOfParallelism { get; set; }

        public List<Tuple<DateTime, string>> Logs { get; } = [];

        public IEnumerable<Tuple<DateTime, string>> GetAllLogs() => Logs;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"migration4dataverse-target-concurrency-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            File.WriteAllText(System.IO.Path.Combine(Path, "source.jsonl"), "{\"id\":\"1\"}");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
