using System.Collections;
using System.Management.Automation;
using System.Reflection;
using Migration4Dataverse.Engine.PowerShell;

namespace Migration4Dataverse.Engine.Tests.PowerShell;

public sealed class DataverseTargetConcurrencyTests
{
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
        object? secondConcurrency = null)
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
            ["crm"] = new object()
        };
        var factoryType = typeof(InvokeMigrationCommand).Assembly.GetType(
            "Migration4Dataverse.Engine.PowerShell.PowerShellMigrationStepFactory",
            throwOnError: true)!;
        var createGraph = factoryType.GetMethod(
            "CreateGraph",
            BindingFlags.Public | BindingFlags.Static)!;

        return createGraph.Invoke(null, [steps, connections, executor, 100, false, null, 32]);
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
