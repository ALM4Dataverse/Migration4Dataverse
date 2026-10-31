using System.Collections;
using System.Management.Automation;
using System.Reflection;
using Migration4Dataverse.Engine.PowerShell;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.Tests.PowerShell;

public sealed class DataverseTargetConcurrencyTests
{
    [Fact]
    public void DataverseTargetsShareAFixedConnectionBudgetAndReportThrottleTelemetry()
    {
        var connection = new TestDataverseConnection
        {
            RecommendedDegreesOfParallelism = 4
        };
        using var controller = new PipelineDataverseConnectionController(
            "crm",
            connection,
            invocationMaxConcurrency: 64,
            disableRetriesForDiagnostics: false);
        controller.RegisterTarget("TargetOne", batchSize: 10, maxStepConcurrency: 64);
        controller.RegisterTarget("TargetTwo", batchSize: 10, maxStepConcurrency: 64);

        var leases = Enumerable.Range(0, 4)
            .Select(index => controller.TryAcquire(index % 2 == 0 ? "TargetOne" : "TargetTwo"))
            .ToArray();

        Assert.All(leases, lease => Assert.NotNull(lease));
        Assert.Null(controller.TryAcquire("TargetOne"));
        Assert.True(connection.InMemoryLogCollectionEnabled);

        connection.Logs.Add(Tuple.Create(DateTime.UtcNow, "Retrying request after HTTP 429 throttling."));
        controller.Complete(leases[0]!);
        controller.Complete(leases[1]!);

        var state = controller.GetDashboardState("TargetOne");
        Assert.Equal(4, state.ConnectionConcurrencyBudget);
        Assert.Equal(4, state.MaxConcurrency);
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
                   firstConcurrency: 2,
                   dataverseConnection: connection,
                   disableRetriesForDiagnostics: true)!)
        {
            Assert.Equal(0, connection.MaxRetryCount);
        }

        Assert.Equal(6, connection.MaxRetryCount);
    }

    [Fact]
    public void DataverseTargetsIgnoreInvocationBatchSizeByDefaultAndUseConnectionRecommendation()
    {
        using var directory = new TemporaryDirectory();
        using var executor = new PowerShellScriptExecutor();
        var connection = new TestDataverseConnection { RecommendedDegreesOfParallelism = 6 };

        using var stepSet = (MigrationStepSet)CreateGraph(
            directory.Path,
            executor,
            firstConcurrency: null,
            defaultBatchSize: 64,
            dataverseConnection: connection)!;
        var state = Assert.Single(stepSet.TargetControllers).GetDashboardState("TargetOne");

        Assert.Equal(10, state.BatchSize);
        Assert.Equal(6, state.MaxConcurrency);
        Assert.Equal("Fixed", state.Stage);
    }

    [Fact]
    public void DataverseTargetHonorsExplicitBatchSizeInsteadOfInvocationBatchSize()
    {
        using var directory = new TemporaryDirectory();
        using var executor = new PowerShellScriptExecutor();

        using var stepSet = (MigrationStepSet)CreateGraph(
            directory.Path,
            executor,
            firstConcurrency: null,
            defaultBatchSize: 64,
            dataverseConnection: new TestDataverseConnection { RecommendedDegreesOfParallelism = 6 },
            firstBatchSize: 25)!;
        var state = Assert.Single(stepSet.TargetControllers).GetDashboardState("TargetOne");

        Assert.Equal(25, state.BatchSize);
    }

    [Fact]
    public void DataverseTargetsCanSetIndependentFixedConcurrencyCapsOnTheSameConnection()
    {
        using var directory = new TemporaryDirectory();
        using var executor = new PowerShellScriptExecutor();

        using var stepSet = (MigrationStepSet)CreateGraph(directory.Path, executor, 2, 3)!;
        var controller = Assert.Single(stepSet.TargetControllers);

        Assert.Equal(2, controller.GetDashboardState("TargetOne").MaxConcurrency);
        Assert.Equal(3, controller.GetDashboardState("TargetTwo").MaxConcurrency);
    }

    [Theory]
    [InlineData("MaxConcurrency")]
    [InlineData("BatchSize")]
    public void DataverseTargetsRejectRemovedAutoSettings(string propertyName)
    {
        using var directory = new TemporaryDirectory();
        using var executor = new PowerShellScriptExecutor();

        var exception = Assert.Throws<TargetInvocationException>(() =>
            CreateGraph(
                directory.Path,
                executor,
                firstConcurrency: propertyName == "MaxConcurrency" ? "auto" : null,
                firstBatchSize: propertyName == "BatchSize" ? "auto" : null));

        Assert.IsType<ArgumentException>(exception.InnerException);
    }

    [Fact]
    public void DataverseTargetIgnoresLegacyPersistedTuningProfiles()
    {
        using var directory = new TemporaryDirectory();
        using var executor = new PowerShellScriptExecutor();
        var store = new SqliteMigrationStore(Path.Combine(directory.Path, "migration.sqlite3"));
        store.SaveDataverseTargetTuningProfile("TargetOne", batchSize: 25, maxConcurrency: 8);

        using var stepSet = (MigrationStepSet)CreateGraph(
            directory.Path,
            executor,
            firstConcurrency: null,
            store: store,
            dataverseConnection: new TestDataverseConnection { RecommendedDegreesOfParallelism = 4 })!;
        var state = Assert.Single(stepSet.TargetControllers).GetDashboardState("TargetOne");
        var profile = store.GetDataverseTargetTuningProfile("TargetOne");

        Assert.Equal(10, state.BatchSize);
        Assert.Equal(4, state.MaxConcurrency);
        Assert.NotNull(profile);
        Assert.Equal(25, profile.BatchSize);
        Assert.Equal(8, profile.MaxConcurrency);
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
        int defaultBatchSize = 10,
        int maxTargetConcurrency = 64,
        object? dataverseConnection = null,
        bool disableRetriesForDiagnostics = false,
        object? firstBatchSize = null)
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
                    Type = "file",
                    Format = new { Type = "jsonl" },
                    ConnectionId = "files",
                    Path = "source.jsonl",
                    Key = "id"
                }
            }),
            CreateTarget("TargetOne", firstConcurrency, firstBatchSize)
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
                disableRetriesForDiagnostics,
                false,
                null,
                null
            ]);
    }

    private static PSObject CreateTarget(string id, object? maxConcurrency, object? batchSize = null)
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
        if (batchSize is not null)
        {
            target["BatchSize"] = batchSize;
        }

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
