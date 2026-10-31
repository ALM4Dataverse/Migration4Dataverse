using System.CommandLine;
using System.Management.Automation;
using System.Text.Json;
using Migration4Dataverse.Cli;

namespace Migration4Dataverse.Cli.Tests;

public sealed class PowerShellCliRuntimeTests
{
    [Fact]
    public void RunsScriptsThatInvokeTheMigrationCmdlet()
    {
        using var fixture = new CliFixture();
        using var runtime = fixture.CreateRuntime();
        var scriptPath = fixture.WriteScript(
            "self-invoking.ps1",
            """
            Invoke-M4dMigration -DatabasePath 'run.sqlite' -RunName 'self-invoking' `
                -Steps @(@{ Id = 'Source'; Type = 'source' }) -Connections @{}
            """);

        var result = runtime.Invoke(
            fixture.GetCommand(runtime, "Invoke-M4dMigration"),
            new Dictionary<string, CliParameterValue>(),
            scriptPath,
            null,
            CancellationToken.None);

        Assert.Equal(ExitCodes.Success, result);
    }

    [Fact]
    public void DetectsMigrationCommandInvokedFromFunctionInScript()
    {
        using var fixture = new CliFixture();
        using var runtime = fixture.CreateRuntime();
        var scriptPath = fixture.WriteScript(
            "self-invoking-function.ps1",
            """
            function InvokeMigrationNow {
                Invoke-M4dMigration -DatabasePath 'run.sqlite' -RunName 'inside-function' `
                    -Steps @(@{ Id = 'Source'; Type = 'source' }) -Connections @{}
            }
            InvokeMigrationNow
            """);

        var result = runtime.Invoke(
            fixture.GetCommand(runtime, "Invoke-M4dMigration"),
            new Dictionary<string, CliParameterValue>(),
            scriptPath,
            null,
            CancellationToken.None);

        Assert.Equal(ExitCodes.Success, result);
    }

    [Fact]
    public void LoadsTheModuleAndRunsAJobDefinedByTheScript()
    {
        using var fixture = new CliFixture();
        using var runtime = fixture.CreateRuntime();
        var scriptPath = fixture.WriteScript(
            "defined-job.ps1",
            """
            $Steps = @(@{ Id = 'Source'; Type = 'source' })
            $Connections = @{}
            $DatabasePath = 'job.sqlite'
            $RunName = 'defined-job'
            """);

        var result = runtime.Invoke(
            fixture.GetCommand(runtime, "Invoke-M4dMigration"),
            new Dictionary<string, CliParameterValue>(),
            scriptPath,
            null,
            CancellationToken.None);

        Assert.Equal(ExitCodes.Success, result);
    }

    [Fact]
    public void RunsJobWhenMigrationCommandAppearsOnlyInUninvokedFunction()
    {
        using var fixture = new CliFixture();
        using var runtime = fixture.CreateRuntime();
        var scriptPath = fixture.WriteScript(
            "job-with-unused-function.ps1",
            """
            if ($false) {
                Invoke-M4dMigration -Steps @(@{ Id = 'UnusedBranch'; Type = 'source' }) -Connections @{}
            }
            function InvokeMigrationLater {
                Invoke-M4dMigration -Steps @(@{ Id = 'Unused'; Type = 'source' }) -Connections @{}
            }
            $Steps = @(@{ Id = 'Source'; Type = 'source' })
            $Connections = @{}
            """);

        var result = runtime.Invoke(
            fixture.GetCommand(runtime, "Invoke-M4dMigration"),
            new Dictionary<string, CliParameterValue>
            {
                ["RunName"] = new("from-cli")
            },
            scriptPath,
            null,
            CancellationToken.None);

        Assert.Equal(ExitCodes.Success, result);
        Assert.Contains("RunId", fixture.Output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(
            fixture.Output.ToString().Contains("from-cli", StringComparison.Ordinal),
            $"Expected the CLI RunName in the output. Actual output: {fixture.Output}");
    }

    [Fact]
    public void AppliesCliParameterDefaultsToScriptInvokedMigration()
    {
        using var fixture = new CliFixture();
        using var runtime = fixture.CreateRuntime();
        var scriptPath = fixture.WriteScript(
            "self-invoking-with-cli-defaults.ps1",
            """
            Invoke-M4dMigration -Steps @(@{ Id = 'Source'; Type = 'source' }) -Connections @{}
            """);
        var parameters = new Dictionary<string, CliParameterValue>
        {
            ["DatabasePath"] = new("cli.sqlite"),
            ["RunName"] = new("from-cli")
        };

        var result = runtime.Invoke(
            fixture.GetCommand(runtime, "Invoke-M4dMigration"),
            parameters,
            scriptPath,
            null,
            CancellationToken.None);

        Assert.Equal(ExitCodes.Success, result);
        Assert.Contains("cli.sqlite", fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("from-cli", fixture.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptArgumentsTakePrecedenceOverCliParameterDefaults()
    {
        using var fixture = new CliFixture();
        using var runtime = fixture.CreateRuntime();
        var scriptPath = fixture.WriteScript(
            "self-invoking-with-explicit-arguments.ps1",
            """
            Invoke-M4dMigration -DatabasePath 'script.sqlite' -RunName 'from-script' `
                -Steps @(@{ Id = 'Source'; Type = 'source' }) -Connections @{}
            """);
        var parameters = new Dictionary<string, CliParameterValue>
        {
            ["DatabasePath"] = new("cli.sqlite"),
            ["RunName"] = new("from-cli")
        };

        var result = runtime.Invoke(
            fixture.GetCommand(runtime, "Invoke-M4dMigration"),
            parameters,
            scriptPath,
            null,
            CancellationToken.None);

        Assert.Equal(ExitCodes.Success, result);
        Assert.Contains("DatabasePath=script.sqlite", fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("RunId=from-script", fixture.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void InvokesAllModuleExportsAndUsesCommonDatabasePathForStoreFunctions()
    {
        using var fixture = new CliFixture();
        using var runtime = fixture.CreateRuntime();
        var exportedCommands = runtime.GetExportedCommands();
        Assert.Contains(exportedCommands, command => command.Name == "Get-M4dMigrationRun");
        Assert.Contains(exportedCommands, command => command.Name == "Complete-M4dMigrationBatch");

        var root = new CliCommandApp(runtime, CancellationToken.None).Build();
        var getRunParse = root.Parse(
        [
            "get-m4d-migration-run",
            "--database-path", "migration.sqlite",
            "--run-id", "run-123"
        ]);
        Assert.Empty(getRunParse.Errors);
        var getRunExitCode = getRunParse.Invoke();
        Assert.True(
            getRunExitCode == ExitCodes.Success,
            $"Exit code {getRunExitCode}. Output: {fixture.Output} Error: {fixture.Error}");

        var completeBatchParse = root.Parse(
        [
            "complete-m4d-migration-batch",
            "--database-path", "migration.sqlite",
            "--batch-id", "batch-1",
            "--worker-id", "worker-1",
            "--outcomes-json", "{}"
        ]);
        Assert.Empty(completeBatchParse.Errors);
        var completeBatchExitCode = completeBatchParse.Invoke();
        Assert.True(
            completeBatchExitCode == ExitCodes.Success,
            $"Exit code {completeBatchExitCode}. Output: {fixture.Output} Error: {fixture.Error}");
    }

    [Fact]
    public void ReturnsDistinctCodesForInvalidJobsAndMigrationFailures()
    {
        using var fixture = new CliFixture();
        using var runtime = fixture.CreateRuntime();
        var invalidJobPath = fixture.WriteScript("invalid-job.ps1", "$Connections = @{}");
        var failedJobPath = fixture.WriteScript(
            "failed-job.ps1",
            """
            $Steps = @(@{ Id = 'Source'; Type = 'source' })
            """);
        var failedJobParameters = new Dictionary<string, CliParameterValue>
        {
            ["Fail"] = new(true)
        };
        var migrationCommand = fixture.GetCommand(runtime, "Invoke-M4dMigration");

        Assert.Equal(
            ExitCodes.InvalidArgumentsOrJob,
            runtime.Invoke(
                migrationCommand,
                new Dictionary<string, CliParameterValue>(),
                invalidJobPath,
                null,
                CancellationToken.None));
        Assert.Equal(
            ExitCodes.MigrationFailure,
            runtime.Invoke(
                migrationCommand,
                failedJobParameters,
                failedJobPath,
                null,
                CancellationToken.None));
    }

    [Fact]
    public void EmitsValidJsonEventsForCommands()
    {
        using var fixture = new CliFixture();
        using var runtime = fixture.CreateRuntime();
        using var output = new StringWriter();
        var eventWriter = new CliEventWriter(output);
        var scriptPath = fixture.WriteScript(
            "json-job.ps1",
            """
            Write-Output 'script output'
            $Steps = @(@{ Id = 'Source'; Type = 'source' })
            """);

        var result = runtime.Invoke(
            fixture.GetCommand(runtime, "Invoke-M4dMigration"),
            new Dictionary<string, CliParameterValue>(),
            scriptPath,
            eventWriter,
            CancellationToken.None);

        Assert.Equal(ExitCodes.Success, result);
        var eventTypes = output.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.GetProperty("type").GetString();
            })
            .ToArray();
        Assert.Contains("output", eventTypes);
    }

    [Fact]
    public void ExposesObjectArrayParametersAsJsonOptions()
    {
        using var fixture = new CliFixture();
        using var runtime = fixture.CreateRuntime();
        var root = new CliCommandApp(runtime, CancellationToken.None).Build();

        var parseResult = root.Parse(
        [
            "start-m4d-migration-dashboard",
            "--steps-json", "[{\"Id\":\"Rows\",\"Type\":\"source\"}]",
            "--connections-json", "{}"
        ]);
        Assert.Empty(parseResult.Errors);
        Assert.Empty(parseResult.Errors);
    }

    [Fact]
    public void RejectsModuleStartupWhenPinnedBundleIsMissing()
    {
        using var fixture = new CliFixture(createMigrationModule: false);

        Assert.Throws<FileNotFoundException>(() => fixture.CreateRuntime());
    }

    private sealed class CliFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            $"m4d-cli-tests-{Guid.NewGuid():N}");

        public string ModulesPath { get; }
        public StringWriter Output { get; } = new();
        public StringWriter Error { get; } = new();

        public CliFixture(bool createMigrationModule = true)
        {
            Directory.CreateDirectory(_directory);
            ModulesPath = Path.Combine(_directory, "modules");
            CreateModule(
                "Rnwood.Dataverse.Data.PowerShell",
                "3.3.0",
                "Rnwood.Dataverse.Data.PowerShell.psd1",
                "Rnwood.Dataverse.Data.PowerShell.psm1",
                string.Empty,
                null);

            if (createMigrationModule)
            {
                CreateModule(
                    "Migration4Dataverse",
                    "0.1.0",
                    "Migration4Dataverse.psd1",
                    "Migration4Dataverse.psm1",
                    """
                    function Invoke-M4dMigration {
                        [CmdletBinding()]
                        param(
                            [string]$DatabasePath,
                            [string]$RunName,
                            [PSObject[]]$Steps,
                            [System.Collections.IDictionary]$Connections,
                            [switch]$Fail
                        )
                        if ($Fail) {
                            throw 'Intentional test migration failure.'
                        }
                        "DatabasePath=$DatabasePath; RunId=$RunName"
                    }

                    function Get-M4dMigrationRun {
                        [CmdletBinding()]
                        param([string]$DatabasePath, [string]$RunId, [int]$Limit = 50)
                        [pscustomobject]@{ DatabasePath = $DatabasePath; RunId = $RunId; Limit = $Limit }
                    }

                    function New-M4dMigrationJobStore {
                        [CmdletBinding()]
                        param([string]$DatabasePath)
                        [pscustomobject]@{ DatabasePath = $DatabasePath }
                    }

                    function Complete-M4dMigrationBatch {
                        [CmdletBinding()]
                        param(
                            [object]$Store,
                            [string]$BatchId,
                            [string]$WorkerId,
                            [string]$OutcomesJson,
                            [switch]$HasErrors
                        )
                        [pscustomobject]@{ DatabasePath = $Store.DatabasePath; BatchId = $BatchId }
                    }

                    function Start-M4dMigrationDashboard {
                        [CmdletBinding()]
                        param([object[]]$Steps, [System.Collections.IDictionary]$Connections)
                        [pscustomobject]@{ StepCount = $Steps.Count; ConnectionCount = $Connections.Count }
                    }

                    Export-ModuleMember -Function Invoke-M4dMigration, Get-M4dMigrationRun, `
                        New-M4dMigrationJobStore, Complete-M4dMigrationBatch, Start-M4dMigrationDashboard
                    """,
                    new[]
                    {
                        "Invoke-M4dMigration",
                        "Get-M4dMigrationRun",
                        "New-M4dMigrationJobStore",
                        "Complete-M4dMigrationBatch",
                        "Start-M4dMigrationDashboard"
                    });
            }
        }

        public PowerShellCliRuntime CreateRuntime()
            => new(ModulesPath, Output, Error);

        public CommandInfo GetCommand(PowerShellCliRuntime runtime, string name) =>
            Assert.Single(runtime.GetExportedCommands(), command => command.Name == name);

        public string WriteScript(string name, string content)
        {
            var path = Path.Combine(_directory, name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        private void CreateModule(
            string moduleName,
            string version,
            string manifestName,
            string rootModuleName,
            string moduleContent,
            string[]? exportedFunctions)
        {
            var moduleDirectory = Path.Combine(ModulesPath, moduleName, version);
            Directory.CreateDirectory(moduleDirectory);
            var functions = exportedFunctions is null
                ? string.Empty
                : $"FunctionsToExport = @({string.Join(", ", exportedFunctions.Select(name => $"'{name}'"))})";
            File.WriteAllText(
                Path.Combine(moduleDirectory, manifestName),
                $$"""
                @{
                    RootModule = '{{rootModuleName}}'
                    ModuleVersion = '{{version}}'
                    GUID = '1de2409a-111a-4f3d-b8bf-91a6b63cedd1'
                    {{functions}}
                    CmdletsToExport = @()
                    VariablesToExport = @()
                    AliasesToExport = @()
                }
                """);
            File.WriteAllText(Path.Combine(moduleDirectory, rootModuleName), moduleContent);
        }
    }
}
