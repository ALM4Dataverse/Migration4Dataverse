using System.Collections;
using System.Management.Automation;
using System.Reflection;
using Migration4Dataverse.Engine.PowerShell;
using Migration4Dataverse.Engine.Scheduling;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.Tests.PowerShell;

public sealed class DataverseTargetOperationsTests
{
    [Fact]
    public async Task DispatchesConfiguredDataverseOperationsToModuleCmdlets()
    {
        using var directory = new TemporaryDirectory();
        var modulePath = directory.WriteDataverseModule();
        using var executor = new PowerShellScriptExecutor(
            modulesToImport:
            [
                modulePath,
                typeof(InvokeMigrationCommand).Assembly.Location
            ]);
        var store = new SqliteMigrationStore(directory.DatabasePath);
        var steps = new[]
        {
            CreateSource("UpsertSource", "upsert.jsonl"),
            CreateTarget("UpsertTarget", "UpsertSource", "upsert", "Upsert", matchOn: ["code"]),
            CreateTarget("NoLookupDisabledTarget", "UpsertSource", "upsert-disabled", "Upsert", matchOn: ["code"], noLookup: false),
            CreateSource("CreateSource", "create.jsonl"),
            CreateTarget("CreateTarget", "CreateSource", "create", "Create"),
            CreateSource("UpdateSource", "update.jsonl"),
            CreateTarget("UpdateTarget", "UpdateSource", "update", "Update", matchOn: ["code"]),
            CreateSource("DeleteSource", "delete.jsonl"),
            CreateTarget("DeleteTarget", "DeleteSource", "delete", "Delete", matchOn: ["code"]),
            CreateSource("IfNeededSource", "if-needed.jsonl"),
            CreateTarget(
                "IfNeededTarget",
                "IfNeededSource",
                "if-needed",
                "CreateOrUpdateIfNeeded",
                matchOn: ["code"],
                lookupColumns: new Hashtable(StringComparer.OrdinalIgnoreCase)
                {
                    ["parentcustomerid"] = "new_salesforceid"
                },
                noLookup: true,
                bypassBusinessLogicExecution: ["CustomSync", "CustomAsync"],
                bypassBusinessLogicExecutionStepIds: [Guid.Parse("11111111-2222-3333-4444-555555555555")])
        };
        var connections = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["files"] = new DirectoryInfo(directory.Path),
            ["crm"] = new object()
        };
        var factory = typeof(InvokeMigrationCommand).Assembly.GetType(
            "Migration4Dataverse.Engine.PowerShell.PowerShellMigrationStepFactory",
            throwOnError: true)!;
        var createGraph = factory.GetMethod("CreateGraph", BindingFlags.Public | BindingFlags.Static)!;
        using var stepSet = (IDisposable)createGraph.Invoke(
            null,
            [steps, connections, executor, 1, false, store, 8, false, false, null, null])!;
        var definitions = (IReadOnlyList<MigrationStepDefinition>)stepSet.GetType()
            .GetProperty("Steps", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(stepSet)!;

        var result = await new DependencyScheduler(store).ExecuteAsync(
            "dataverse-operations-run",
            "dataverse-operations",
            "hash-1",
            definitions,
            new DependencySchedulerOptions
            {
                MaxDegreeOfParallelism = 8,
                MaxAttempts = 1
            });

        Assert.Equal("dataverse-operations-run", result.RunId);
        var calls = File.ReadAllLines(directory.CallsPath);
        Assert.Contains("upsert:Set:match=code:no-lookup", calls);
        Assert.Contains("upsert-disabled:Set:match=code", calls);
        Assert.Contains("create:Set:create-only:no-lookup", calls);
        Assert.Contains("update:Set:no-create:match=code:no-lookup", calls);
        Assert.Contains("delete:Remove:match=code", calls);
        Assert.Contains("if-needed:LookupColumns:parentcustomerid=new_salesforceid", calls);
        Assert.Contains("if-needed:Set:match=code:no-lookup:bypass=CustomSync,CustomAsync:bypass-stepids=11111111-2222-3333-4444-555555555555", calls);
    }

    [Fact]
    public async Task DataverseTransformReceivesConnectionForSql4Cds()
    {
        using var directory = new TemporaryDirectory();
        var modulePath = directory.WriteDataverseModule();
        using var executor = new PowerShellScriptExecutor(
            modulesToImport:
            [
                modulePath,
                typeof(InvokeMigrationCommand).Assembly.Location
            ]);
        var store = new SqliteMigrationStore(directory.DatabasePath);
        var steps = new[]
        {
            CreateSource("SqlSource", "upsert.jsonl"),
            PSObject.AsPSObject(new
            {
                Id = "SqlTransform",
                Type = "transform",
                Inputs = new[] { "SqlSource" },
                Transform = new Hashtable(StringComparer.OrdinalIgnoreCase)
                {
                    ["Type"] = "dataverse",
                    ["ConnectionId"] = "crm"
                },
                Script = ScriptBlock.Create("""
                    param($row, $connection)
                    $result = Invoke-DataverseSql `
                        -Connection $connection `
                        -Sql 'SELECT @code AS value' `
                        -Parameters @{ code = $row.code }
                    @{ value = [string]$result.value }
                    """)
            }),
            PSObject.AsPSObject(new
            {
                Id = "SqlTarget",
                Type = "target",
                Inputs = new[] { "SqlTransform" },
                Target = new Hashtable(StringComparer.OrdinalIgnoreCase)
                {
                    ["Type"] = "custom",
                    ["ConnectionId"] = "files",
                    ["Custom"] = new Hashtable(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Identity"] = "sql-transform-target-v1",
                        ["Callback"] = ScriptBlock.Create("""
                            param($connection, $records, $context)
                            if ($records.Count -ne 1 -or $records[0].value -ne 'one') {
                                throw 'SQL4CDS transform output was not passed to the target.'
                            }
                            Add-Content -LiteralPath (Join-Path $connection.FullName 'sql-transform.txt') `
                                -Value $records[0].value
                            return $true
                            """)
                    }
                }
            })
        };
        var connections = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["files"] = new DirectoryInfo(directory.Path),
            ["crm"] = new object()
        };
        var factory = typeof(InvokeMigrationCommand).Assembly.GetType(
            "Migration4Dataverse.Engine.PowerShell.PowerShellMigrationStepFactory",
            throwOnError: true)!;
        var createGraph = factory.GetMethod("CreateGraph", BindingFlags.Public | BindingFlags.Static)!;
        using var stepSet = (IDisposable)createGraph.Invoke(
            null,
            [steps, connections, executor, 1, false, store, 8, false, false, null, null])!;
        var definitions = (IReadOnlyList<MigrationStepDefinition>)stepSet.GetType()
            .GetProperty("Steps", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(stepSet)!;

        await new DependencyScheduler(store).ExecuteAsync(
            "dataverse-transform-run",
            "dataverse-transform",
            "hash-1",
            definitions,
            new DependencySchedulerOptions
            {
                MaxDegreeOfParallelism = 8,
                MaxAttempts = 1
            });

        Assert.Equal("one", File.ReadAllText(System.IO.Path.Combine(directory.Path, "sql-transform.txt")).Trim());
        Assert.Contains(
            "SQL:SELECT @code AS value:one",
            File.ReadAllLines(directory.CallsPath));
    }

    [Theory]
    [InlineData("CustomAPI")]
    [InlineData("Write")]
    public void RejectsUnsupportedDataverseOperations(string operation)
    {
        using var directory = new TemporaryDirectory();
        using var executor = new PowerShellScriptExecutor(
            modulesToImport: [typeof(InvokeMigrationCommand).Assembly.Location]);
        var connections = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["files"] = new DirectoryInfo(directory.Path),
            ["crm"] = new object()
        };
        var steps = new[]
        {
            CreateSource("Source", "upsert.jsonl"),
            CreateTarget("Target", "Source", "target", operation)
        };
        var factory = typeof(InvokeMigrationCommand).Assembly.GetType(
            "Migration4Dataverse.Engine.PowerShell.PowerShellMigrationStepFactory",
            throwOnError: true)!;
        var createGraph = factory.GetMethod("CreateGraph", BindingFlags.Public | BindingFlags.Static)!;

        var exception = Assert.Throws<TargetInvocationException>(
            () => createGraph.Invoke(null, [steps, connections, executor, 1, false, null, 8, false, false, null, null]));

        Assert.IsType<ArgumentException>(exception.InnerException);
        Assert.Contains("Dataverse target operation", exception.InnerException.Message);
    }

    [Fact]
    public void RejectsSetOnlyOptionsForDeleteTargets()
    {
        using var directory = new TemporaryDirectory();
        using var executor = new PowerShellScriptExecutor(
            modulesToImport: [typeof(InvokeMigrationCommand).Assembly.Location]);
        var connections = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["files"] = new DirectoryInfo(directory.Path),
            ["crm"] = new object()
        };
        var steps = new[]
        {
            CreateSource("Source", "upsert.jsonl"),
            CreateTarget("DeleteTarget", "Source", "target", "Delete", matchOn: ["code"], noLookup: true)
        };
        var factory = typeof(InvokeMigrationCommand).Assembly.GetType(
            "Migration4Dataverse.Engine.PowerShell.PowerShellMigrationStepFactory",
            throwOnError: true)!;
        var createGraph = factory.GetMethod("CreateGraph", BindingFlags.Public | BindingFlags.Static)!;

        var exception = Assert.Throws<TargetInvocationException>(
            () => createGraph.Invoke(null, [steps, connections, executor, 1, false, null, 8, false, false, null, null]));

        Assert.IsType<ArgumentException>(exception.InnerException);
        Assert.Contains("does not support Set-DataverseRecord options", exception.InnerException.Message);
        Assert.Contains("NoLookup", exception.InnerException.Message);
    }

    private static PSObject CreateSource(string id, string fileName) =>
        PSObject.AsPSObject(new
        {
            Id = id,
            Type = "source",
            Inputs = Array.Empty<string>(),
            Source = new
            {
                Type = "file",
                Format = new { Type = "jsonl" },
                ConnectionId = "files",
                Path = fileName,
                Key = "code"
            }
        });

    private static PSObject CreateTarget(
        string id,
        string input,
        string table,
        string operation,
        string[]? matchOn = null,
        Hashtable? lookupColumns = null,
        bool? noLookup = null,
        string[]? bypassBusinessLogicExecution = null,
        Guid[]? bypassBusinessLogicExecutionStepIds = null)
    {
        var options = new Hashtable(StringComparer.OrdinalIgnoreCase);
        if (matchOn is not null)
        {
            options["MatchOn"] = new[] { matchOn };
        }
        if (lookupColumns is not null)
        {
            options["LookupColumns"] = lookupColumns;
        }
        if (noLookup.HasValue)
        {
            options["NoLookup"] = noLookup.Value;
        }
        if (bypassBusinessLogicExecution is not null)
        {
            options["BypassBusinessLogicExecution"] = bypassBusinessLogicExecution;
        }
        if (bypassBusinessLogicExecutionStepIds is not null)
        {
            options["BypassBusinessLogicExecutionStepIds"] = bypassBusinessLogicExecutionStepIds;
        }
        return PSObject.AsPSObject(new
        {
            Id = id,
            Type = "target",
            Inputs = new[] { input },
            Target = new Hashtable(StringComparer.OrdinalIgnoreCase)
            {
                ["Type"] = "dataverse",
                ["ConnectionId"] = "crm",
                ["Table"] = table,
                ["Operation"] = operation,
                ["BatchSize"] = 1,
                ["MaxConcurrency"] = 1,
                ["Options"] = options
            }
        });
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"migration4dataverse-target-operations-{Guid.NewGuid():N}");

        public TemporaryDirectory()
        {
            Directory.CreateDirectory(_directory);
            Path = _directory;
            DatabasePath = System.IO.Path.Combine(_directory, "migration.sqlite3");
            CallsPath = System.IO.Path.Combine(_directory, "calls.txt");
            foreach (var file in new[] { "upsert.jsonl", "create.jsonl", "update.jsonl", "delete.jsonl", "if-needed.jsonl" })
            {
                File.WriteAllText(System.IO.Path.Combine(_directory, file), """{"code":"one"}""" + Environment.NewLine);
            }
        }

        public string Path { get; }

        public string DatabasePath { get; }

        public string CallsPath { get; }

        public string WriteDataverseModule()
        {
            var modulePath = System.IO.Path.Combine(_directory, "DataverseMock.psm1");
            File.WriteAllText(modulePath, """
                function Set-DataverseRecord {
                    [CmdletBinding()]
                    param(
                        [Parameter(ValueFromPipeline = $true)] $InputObject,
                        [string] $TableName,
                        [object] $Connection,
                        [int] $BatchSize,
                        [int] $MaxDegreeOfParallelism,
                        [int] $Retries,
                        [int] $InitialRetryDelay,
                        [string[][]] $MatchOn,
                        [hashtable] $LookupColumns,
                        [switch] $NoLookup,
                        [object[]] $BypassBusinessLogicExecution,
                        [guid[]] $BypassBusinessLogicExecutionStepIds,
                        [switch] $CreateOnly,
                        [switch] $NoCreate,
                        [switch] $Upsert,
                        [switch] $PassThru
                    )
                    process {
                        if ($InputObject -is [System.Collections.IDictionary]) {
                            throw 'Set-DataverseRecord must receive record objects, not dictionaries.'
                        }
                        $details = [System.Collections.Generic.List[string]]::new()
                        if ($CreateOnly) { $details.Add('create-only') }
                        if ($NoCreate) { $details.Add('no-create') }
                        if ($Upsert) { $details.Add('upsert') }
                        if ($MatchOn) { $details.Add('match=' + ($MatchOn[0] -join ',')) }
                        if ($NoLookup) { $details.Add('no-lookup') }
                        if ($BypassBusinessLogicExecution) {
                            $details.Add('bypass=' + ($BypassBusinessLogicExecution -join ','))
                        }
                        if ($BypassBusinessLogicExecutionStepIds) {
                            $details.Add('bypass-stepids=' + ($BypassBusinessLogicExecutionStepIds -join ','))
                        }
                        Add-Content -LiteralPath (Join-Path $PSScriptRoot 'calls.txt') -Value "${TableName}:Set:$($details -join ':')"
                        if ($LookupColumns) {
                            Add-Content -LiteralPath (Join-Path $PSScriptRoot 'calls.txt') `
                                -Value "${TableName}:LookupColumns:parentcustomerid=$($LookupColumns.parentcustomerid)"
                        }
                    }
                }
                function Remove-DataverseRecord {
                    [CmdletBinding()]
                    param(
                        [Parameter(ValueFromPipeline = $true)] $InputObject,
                        [string] $TableName,
                        [object] $Connection,
                        [int] $BatchSize,
                        [int] $MaxDegreeOfParallelism,
                        [int] $Retries,
                        [int] $InitialRetryDelay,
                        [string[][]] $MatchOn
                    )
                    process {
                        if ($InputObject -is [System.Collections.IDictionary]) {
                            throw 'Remove-DataverseRecord must receive record objects, not dictionaries.'
                        }
                        $match = if ($MatchOn) { 'match=' + ($MatchOn[0] -join ',') } else { 'id' }
                        Add-Content -LiteralPath (Join-Path $PSScriptRoot 'calls.txt') -Value "${TableName}:Remove:$match"
                    }
                }
                function Invoke-DataverseSql {
                    [CmdletBinding()]
                    param(
                        [object] $Connection,
                        [string] $Sql,
                        [hashtable] $Parameters
                    )
                    Add-Content -LiteralPath (Join-Path $PSScriptRoot 'calls.txt') `
                        -Value "SQL:${Sql}:$($Parameters.code)"
                    [pscustomobject]@{ value = $Parameters.code }
                }
                Export-ModuleMember -Function Set-DataverseRecord, Remove-DataverseRecord, Invoke-DataverseSql
                """);
            return modulePath;
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
