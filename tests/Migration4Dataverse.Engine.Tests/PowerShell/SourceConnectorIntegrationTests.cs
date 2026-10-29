using System.Collections;
using System.Management.Automation;
using System.Net;
using System.Text;
using System.Text.Json;
using Migration4Dataverse.Engine.PowerShell;
using Migration4Dataverse.Engine.Scheduling;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.Tests.PowerShell;

public sealed class SourceConnectorIntegrationTests
{
    [Fact]
    public void ExcelSourceRunsThroughMigrationGraph()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = directory.GetPath("contacts.xlsx");
        Connectors.ExcelConnectorTests.CreateWorkbook(sourcePath, "Contacts", """
            <row r="1">
              <c r="A1" t="inlineStr"><is><t>id</t></is></c>
              <c r="B1" t="inlineStr"><is><t>name</t></is></c>
            </row>
            <row r="2">
              <c r="A2"><v>2</v></c>
              <c r="B2" t="inlineStr"><is><t>Two</t></is></c>
            </row>
            <row r="3">
              <c r="A3"><v>1</v></c>
              <c r="B3" t="inlineStr"><is><t>One</t></is></c>
            </row>
            """);
        var targetDirectory = directory.CreateDirectory("target");
        var steps = new Hashtable[]
        {
            SourceStep("Source", "excel", new Hashtable(StringComparer.OrdinalIgnoreCase)
            {
                ["Sheet"] = "Contacts"
            }),
            TargetStep("Target")
        };

        RunMigration(
            directory.GetPath("migration.sqlite3"),
            steps,
            new Hashtable(StringComparer.OrdinalIgnoreCase)
            {
                ["source"] = new FileInfo(sourcePath),
                ["target"] = new DirectoryInfo(targetDirectory)
            });

        var rows = ReadJsonLines(Path.Combine(targetDirectory, "accounts.jsonl"));
        Assert.Equal(new[] { "One", "Two" }, rows.Select(row => row.GetProperty("name").GetString()));
    }

    [Fact]
    public void SalesforceSourceRunsThroughMigrationGraphAndCountProvider()
    {
        using var directory = new TemporaryDirectory();
        var targetDirectory = directory.CreateDirectory("target");
        var requests = new List<string>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            requests.Add(query);
            return query.Contains("SELECT COUNT()", StringComparison.Ordinal)
                ? JsonResponse("""{"totalSize":2,"done":true,"records":[]}""")
                : JsonResponse(
                    """{"totalSize":2,"done":true,"records":[{"attributes":{"type":"Account"},"Id":"a2","Name":"Two"},{"attributes":{"type":"Account"},"Id":"a1","Name":"One"}]}""");
        }))
        {
            BaseAddress = new Uri("https://example.test/")
        };
        var steps = new Hashtable[]
        {
            SourceStep("Source", "salesforce", new Hashtable(StringComparer.OrdinalIgnoreCase)
            {
                ["Object"] = "Account",
                ["Fields"] = new[] { "Id", "Name" }
            }),
            TargetStep("Target")
        };

        RunMigration(
            directory.GetPath("migration.sqlite3"),
            steps,
            new Hashtable(StringComparer.OrdinalIgnoreCase)
            {
                ["source"] = client,
                ["target"] = new DirectoryInfo(targetDirectory)
            });

        var rows = ReadJsonLines(Path.Combine(targetDirectory, "accounts.jsonl"));
        Assert.Equal(new[] { "One", "Two" }, rows.Select(row => row.GetProperty("Name").GetString()));
        Assert.Contains(requests, query => query.Contains("SELECT COUNT()", StringComparison.Ordinal));
        Assert.Contains(requests, query => query.Contains("SELECT Id, Name FROM Account", StringComparison.Ordinal));
    }

    private static Hashtable SourceStep(string id, string type, Hashtable options)
    {
        return new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = id,
            ["Type"] = "source",
            ["Inputs"] = Array.Empty<string>(),
            ["Source"] = new Hashtable(StringComparer.OrdinalIgnoreCase)
            {
                ["Type"] = type,
                ["ConnectionId"] = "source",
                ["Key"] = type == "salesforce" ? "Id" : "id",
                ["Options"] = options
            }
        };
    }

    private static Hashtable TargetStep(string id)
    {
        return new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = id,
            ["Type"] = "target",
            ["Inputs"] = new[] { "Source" },
            ["Target"] = new Hashtable(StringComparer.OrdinalIgnoreCase)
            {
                ["Type"] = "jsonl",
                ["ConnectionId"] = "target",
                ["Operation"] = "Write",
                ["Path"] = "accounts.jsonl",
                ["Options"] = new Hashtable()
            }
        };
    }

    private static void RunMigration(
        string databasePath,
        Hashtable[] steps,
        Hashtable connections)
    {
        using var powershell = System.Management.Automation.PowerShell.Create();
        powershell.AddCommand("Import-Module")
            .AddParameter("Name", typeof(InvokeMigrationCommand).Assembly.Location);
        powershell.Invoke();
        Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));

        powershell.Commands.Clear();
        powershell.AddCommand("Invoke-Migration")
            .AddParameter("DatabasePath", databasePath)
            .AddParameter("RunName", "Source connector integration")
            .AddParameter("RunId", Guid.NewGuid().ToString("N"))
            .AddParameter("BatchSize", 1)
            .AddParameter("MaxAttempts", 1)
            .AddParameter("Steps", steps)
            .AddParameter("Connections", connections)
            .Invoke();

        Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
    }

    private static List<JsonElement> ReadJsonLines(string path)
    {
        return File.ReadLines(path)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            $"migration4dataverse-tests-{Guid.NewGuid():N}");

        public TemporaryDirectory()
        {
            Directory.CreateDirectory(_path);
        }

        public string GetPath(string name)
        {
            return Path.Combine(_path, name);
        }

        public string CreateDirectory(string name)
        {
            var path = GetPath(name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }
    }
}
