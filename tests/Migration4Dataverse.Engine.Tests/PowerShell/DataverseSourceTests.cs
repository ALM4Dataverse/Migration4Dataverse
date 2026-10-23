using System.Collections;
using System.Management.Automation;
using System.Reflection;
using System.Text.Json;
using Migration4Dataverse.Engine.PowerShell;
using Migration4Dataverse.Engine.Scheduling;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.Tests.PowerShell;

public sealed class DataverseSourceTests
{
    [Fact]
    public void DataverseSourceAddsModuleToRunspaceImports()
    {
        var step = PSObject.AsPSObject(new
        {
            Id = "Accounts",
            Type = "source",
            Inputs = Array.Empty<string>(),
            Source = new { Type = "dataverse" }
        });
        var factoryType = typeof(InvokeMigrationCommand).Assembly.GetType(
            "Migration4Dataverse.Engine.PowerShell.PowerShellMigrationStepFactory",
            throwOnError: true)!;
        var method = factoryType.GetMethod(
            "GetRequiredModulesForGraph",
            BindingFlags.Public | BindingFlags.Static)!;

        var modules = Assert.IsAssignableFrom<IEnumerable<string>>(
            method.Invoke(null, [new[] { step }]));

        Assert.Contains("Rnwood.Dataverse.Data.PowerShell", modules);
    }

    [Theory]
    [InlineData("simple")]
    [InlineData("fetchxml")]
    [InlineData("cds4sql")]
    public async Task DataverseSourceStreamsRecordsForEachQueryType(string queryType)
    {
        using var directory = new TemporaryDirectory();
        var modulePath = directory.WriteDataverseModule();
        using var executor = new PowerShellScriptExecutor(maxRunspaces: 2, modulesToImport: [modulePath]);
        var options = CreateOptions(queryType);
        var definition = new PowerShellDataverseSourceDefinition(
            "mock-connection",
            options,
            queryType);
        var readerFactory = new PowerShellHierarchyRecordReaderFactory(
            executor,
            new Dictionary<string, PowerShellSourceCallbackDefinition>(),
            new Dictionary<string, PowerShellDataverseSourceDefinition>
            {
                ["Accounts"] = definition
            });
        var level = new HierarchySourceLevel(
            "Accounts",
            "dataverse",
            path: null,
            keyProperty: "accountid",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 2);
        var source = new HierarchyBatchSource(level, batchSize: 1, readerFactory.CreateDelegate());

        var batches = await ReadAllAsync(
            source.CreateReader()(new MigrationStepCheckpoint("run", "Accounts", -1, null, null), CancellationToken.None));

        Assert.Equal(2, batches.Count);
        using var first = JsonDocument.Parse(batches[0].PayloadJson);
        using var second = JsonDocument.Parse(batches[1].PayloadJson);
        Assert.Equal(
            "001",
            first.RootElement.GetProperty("Records")[0]
                .GetProperty("Source")
                .GetProperty("accountid")
                .GetString());
        Assert.Equal(
            "002",
            second.RootElement.GetProperty("Records")[0]
                .GetProperty("Source")
                .GetProperty("accountid")
                .GetString());
    }

    [Fact]
    public async Task DataverseSourceRerunsOrderedQueryAndSkipsThroughCheckpoint()
    {
        using var directory = new TemporaryDirectory();
        var modulePath = directory.WriteDataverseModule();
        using var executor = new PowerShellScriptExecutor(modulesToImport: [modulePath]);
        var definition = new PowerShellDataverseSourceDefinition(
            "mock-connection",
            CreateOptions("simple"),
            "simple");
        var readerFactory = new PowerShellHierarchyRecordReaderFactory(
            executor,
            new Dictionary<string, PowerShellSourceCallbackDefinition>(),
            new Dictionary<string, PowerShellDataverseSourceDefinition>
            {
                ["Accounts"] = definition
            });
        var level = new HierarchySourceLevel(
            "Accounts",
            "dataverse",
            path: null,
            keyProperty: "accountid",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 2);
        var source = new HierarchyBatchSource(level, batchSize: 10, readerFactory.CreateDelegate());

        var batches = await ReadAllAsync(
            source.CreateReader()(new MigrationStepCheckpoint(
                "run",
                "Accounts",
                0,
                """{"LastKey":"001"}""",
                DateTimeOffset.UtcNow), CancellationToken.None));

        using var payload = JsonDocument.Parse(Assert.Single(batches).PayloadJson);
        var records = payload.RootElement.GetProperty("Records").EnumerateArray().ToArray();
        Assert.Single(records);
        Assert.Equal("002", records[0].GetProperty("Source").GetProperty("accountid").GetString());
    }

    [Fact]
    public async Task DataverseSimpleChildSourceOrdersByParentKeyThenSourceKey()
    {
        using var directory = new TemporaryDirectory();
        var modulePath = directory.WriteDataverseModule();
        using var executor = new PowerShellScriptExecutor(modulesToImport: [modulePath]);
        var definitions = new Dictionary<string, PowerShellDataverseSourceDefinition>
        {
            ["Accounts"] = new("mock-connection", CreateOptions("simple"), "simple"),
            ["Contacts"] = new("mock-connection", CreateOptions("simple", "contact"), "simple")
        };
        var contacts = new HierarchySourceLevel(
            "Contacts",
            "dataverse",
            path: null,
            keyProperty: "contactid",
            parentKeyProperty: "accountid",
            rowsPerParent: 0,
            expectedRecordCount: 3);
        var accounts = new HierarchySourceLevel(
            "Accounts",
            "dataverse",
            path: null,
            keyProperty: "accountid",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 2,
            children: [contacts]);
        var source = new HierarchyBatchSource(
            accounts,
            batchSize: 2,
            new PowerShellHierarchyRecordReaderFactory(
                executor,
                new Dictionary<string, PowerShellSourceCallbackDefinition>(),
                definitions).CreateDelegate());

        var batches = await ReadAllAsync(
            source.CreateReader()(new MigrationStepCheckpoint("run", "Accounts", -1, null, null), CancellationToken.None));

        using var payload = JsonDocument.Parse(Assert.Single(batches).PayloadJson);
        var rows = payload.RootElement.GetProperty("Records").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(
            2,
            rows[0].GetProperty("Children").GetProperty("Contacts").GetArrayLength());
        Assert.Single(rows[1].GetProperty("Children").GetProperty("Contacts").EnumerateArray());
    }

    [Theory]
    [InlineData("simple")]
    [InlineData("fetchxml")]
    public async Task DataverseSourceCountsWithTotalRecordCount(string queryType)
    {
        using var directory = new TemporaryDirectory();
        var modulePath = directory.WriteDataverseModule();
        using var executor = new PowerShellScriptExecutor(modulesToImport: [modulePath]);
        var definition = new PowerShellDataverseSourceDefinition(
            "mock-connection",
            CreateOptions(queryType),
            queryType);

        var count = await PowerShellDataverseHierarchyRecordReader.CountRecordsAsync(
            executor,
            "Accounts",
            "accountid",
            definition);

        Assert.Equal(2, count);
    }

    private static Hashtable CreateOptions(string queryType, string tableName = "account")
    {
        var options = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["QueryType"] = queryType
        };
        switch (queryType)
        {
            case "simple":
                options["TableName"] = tableName;
                options["FilterValues"] = new Hashtable[]
                {
                    new(StringComparer.OrdinalIgnoreCase) { ["name"] = "Contoso" }
                };
                options["PageSize"] = 1;
                break;
            case "fetchxml":
                options["FetchXml"] =
                    "<fetch><entity name='account'><order attribute='accountid' /></entity></fetch>";
                break;
            case "cds4sql":
                options["Sql"] = "SELECT accountid, name FROM account ORDER BY accountid";
                options["Parameters"] = new Hashtable { ["status"] = 1 };
                break;
        }

        return options;
    }

    private static async Task<List<MigrationWorkBatch>> ReadAllAsync(
        IAsyncEnumerable<MigrationWorkBatch> source)
    {
        var batches = new List<MigrationWorkBatch>();
        await foreach (var batch in source)
        {
            batches.Add(batch);
        }

        return batches;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"migration4dataverse-source-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string WriteDataverseModule()
        {
            var path = System.IO.Path.Combine(Path, "FakeDataverse.psm1");
            File.WriteAllText(
                path,
                """
                function Get-DataverseRecord {
                    [CmdletBinding()]
                    param(
                        [string]$TableName,
                        [string]$FetchXml,
                        [object]$Connection,
                        [switch]$TotalRecordCount,
                        [string[]]$OrderBy,
                        [hashtable[]]$FilterValues,
                        [int]$PageSize
                    )
                    if ($Connection -ne 'mock-connection') { throw 'Connection was not supplied.' }
                    if ($PSBoundParameters.ContainsKey('FetchXml')) {
                        if ($FetchXml -notmatch '<order attribute=''accountid''') { throw 'FetchXML was not passed.' }
                    }
                    else {
                        if ($TableName -eq 'account') {
                            if ($OrderBy.Count -ne 1 -or $OrderBy[0] -ne 'accountid') {
                                throw 'Root source key ordering was not applied.'
                            }
                            if ($FilterValues.Count -ne 1 -or $FilterValues[0]['name'] -ne 'Contoso') {
                                throw 'Simple query filters were not passed.'
                            }
                        }
                        elseif ($TableName -eq 'contact') {
                            if ($OrderBy.Count -ne 2 -or $OrderBy[0] -ne 'accountid' -or $OrderBy[1] -ne 'contactid') {
                                throw 'Row-linked source ordering was not applied.'
                            }
                        }
                        else { throw 'TableName was not passed.' }
                    }

                    if ($TotalRecordCount) {
                        2
                        return
                    }

                    if ($TableName -eq 'contact') {
                        [pscustomobject]@{ accountid = '001'; contactid = '101' }
                        [pscustomobject]@{ accountid = '001'; contactid = '102' }
                        [pscustomobject]@{ accountid = '002'; contactid = '201' }
                    }
                    else {
                        [pscustomobject]@{ accountid = '001'; name = 'Contoso' }
                        [pscustomobject]@{ accountid = '002'; name = 'Contoso' }
                    }
                }

                function Invoke-DataverseSql {
                    [CmdletBinding()]
                    param(
                        [string]$Sql,
                        [object]$Connection,
                        [object]$Parameters
                    )
                    if ($Connection -ne 'mock-connection') { throw 'SQL connection was not supplied.' }
                    if ($Sql -notmatch '^SELECT .* ORDER BY accountid$') { throw 'SQL query was not passed.' }
                    if ($Parameters['status'] -ne 1) { throw 'SQL parameters were not passed.' }

                    [pscustomobject]@{ accountid = '001'; name = 'Contoso' }
                    [pscustomobject]@{ accountid = '002'; name = 'Contoso' }
                }

                Export-ModuleMember -Function Get-DataverseRecord, Invoke-DataverseSql
                """);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
