using System.CommandLine;
using System.Management.Automation;
using System.Text.Json;
using Migration4Dataverse.Cli;

namespace Migration4Dataverse.Cli.Tests;

public sealed class PowerShellCliRuntimeTests
{
    [Fact]
    public void RejectsPs1FilesAsMigrationDefinitions()
    {
        using var fixture = new CliFixture();
        using var runtime = fixture.CreateRuntime();
        var scriptPath = fixture.WriteScript(
            "self-invoking.ps1",
            "$Migration = @{ Steps = @(@{ Id = 'Source'; Type = 'source' }); Connections = @{} }");

        var result = runtime.Invoke(
            fixture.GetCommand(runtime, "Invoke-M4dMigration"),
            new Dictionary<string, CliParameterValue>(),
            scriptPath,
            null,
            CancellationToken.None);

        Assert.Equal(ExitCodes.InvalidArgumentsOrJob, result);
        Assert.Contains(".m4d", fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LoadsTheModuleAndRunsAJobDefinedByTheScript()
    {
        using var fixture = new CliFixture();
        using var runtime = fixture.CreateRuntime();
        var scriptPath = fixture.WriteScript(
            "defined-job.m4d",
            """
            $Migration = @{
                Steps = @(@{ Id = 'Source'; Type = 'source' })
                Connections = @{}
                RunOptions = @{
                    DatabasePath = 'job.sqlite'
                    RunName = 'defined-job'
                }
            }
            """);

        var result = runtime.Invoke(
            fixture.GetCommand(runtime, "Invoke-M4dMigration"),
            new Dictionary<string, CliParameterValue>(),
            scriptPath,
            null,
            CancellationToken.None);

        Assert.True(
            result == ExitCodes.Success,
            $"Exit code {result}. Output: {fixture.Output} Error: {fixture.Error}");
    }

    [Fact]
    public void RunsM4dDefinitionFromTheRunCommand()
    {
        using var fixture = new CliFixture();
        using var runtime = fixture.CreateRuntime();
        var definitionPath = fixture.WriteScript(
            "cli-run.m4d",
            """
            $Migration = @{
                Steps = @(@{ Id = 'Source'; Type = 'source' })
                Connections = @{}
            }
            """);
        var root = new CliCommandApp(runtime, CancellationToken.None).Build();
        var parseResult = root.Parse(
        [
            "run",
            definitionPath,
            "--database-path", "cli.sqlite",
            "--run-name", "cli-run",
            "--no-dashboard"
        ]);

        Assert.Empty(parseResult.Errors);
        var result = parseResult.Invoke();

        Assert.True(
            result == ExitCodes.Success,
            $"Exit code {result}. Output: {fixture.Output} Error: {fixture.Error}");
        Assert.Contains("DatabasePath=cli.sqlite", fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("RunId=cli-run", fixture.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RunStartsDashboardByDefaultAndWaitsForItToStop()
    {
        using var fixture = new CliFixture();
        using var runtime = fixture.CreateRuntime();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var definitionPath = fixture.WriteScript(
            "cli-run-dashboard.m4d",
            """
            $Migration = @{
                Steps = @(@{ Id = 'Source'; Type = 'source' })
                Connections = @{}
                RunOptions = @{ DatabasePath = 'dashboard.sqlite' }
                PreRun = {
                    param($context)
                    $context.Connections['example-dataverse'] = @{ Configured = $true }
                }
            }
            """);
        var root = new CliCommandApp(runtime, cancellation.Token).Build();
        var parseResult = root.Parse(["run", definitionPath]);

        Assert.Empty(parseResult.Errors);
        var result = parseResult.Invoke();

        Assert.Equal(ExitCodes.Success, result);
        Assert.Contains(
            "Migration dashboard listening at http://127.0.0.1:5180",
            fixture.Output.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("DashboardConnections=example-dataverse", fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("DatabasePath=dashboard.sqlite", fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("DashboardStopped", fixture.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CreatesMigrationTemplateFromCliAction()
    {
        using var fixture = new CliFixture();
        using var runtime = fixture.CreateRuntime();
        var path = Path.Combine(fixture.ModulesPath, "new-migration.m4d");
        var root = new CliCommandApp(runtime, CancellationToken.None).Build();
        var parseResult = root.Parse(["new-file", "--path", path]);

        Assert.Empty(parseResult.Errors);
        Assert.Equal(ExitCodes.Success, parseResult.Invoke());
        var template = File.ReadAllText(path);
        Assert.Contains("$Migration = @{", template, StringComparison.Ordinal);
        Assert.Contains("RunOptions = @{", template, StringComparison.Ordinal);
        Assert.Contains("PreRun = {", template, StringComparison.Ordinal);
        Assert.Contains("PostRun = {", template, StringComparison.Ordinal);
    }

    [Fact]
    public void RunUsesPositionalDefinitionAndNoScriptOption()
    {
        using var fixture = new CliFixture();
        using var runtime = fixture.CreateRuntime();
        var root = new CliCommandApp(runtime, CancellationToken.None).Build();

        var parseResult = root.Parse(["run", "--script", "migration.m4d"]);

        Assert.NotEmpty(parseResult.Errors);
    }

    [Fact]
    public void RunsM4dDefinitionAndRunsLifecycleHooks()
    {
        using var fixture = new CliFixture();
        using var runtime = fixture.CreateRuntime();
        var scriptPath = fixture.WriteScript(
            "job-with-hooks.m4d",
            """
            $Migration = @{
                Steps = @(@{ Id = 'Source'; Type = 'source' })
                Connections = @{}
                RunOptions = @{ RunName = 'from-file' }
                PreRun = {
                    param($context)
                    $context.Connections['initialized'] = $true
                }
                PostRun = {
                    param($context)
                    Write-Information "PostRun:$($context.Result)"
                }
            }
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
        Assert.Contains("PostRun:", fixture.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RunsPostHookAfterPreRunFailure()
    {
        using var fixture = new CliFixture();
        using var runtime = fixture.CreateRuntime();
        var definitionPath = fixture.WriteScript(
            "pre-run-failure.m4d",
            """
            $Migration = @{
                Steps = @(@{ Id = 'Source'; Type = 'source' })
                PreRun = { throw 'pre-run failed' }
                PostRun = {
                    param($context)
                    Write-Information "PostRun:$($context.Error)"
                }
            }
            """);

        var result = runtime.Invoke(
            fixture.GetCommand(runtime, "Invoke-M4dMigration"),
            new Dictionary<string, CliParameterValue>(),
            definitionPath,
            null,
            CancellationToken.None);

        Assert.True(
            result == ExitCodes.MigrationFailure,
            $"Exit code {result}. Output: {fixture.Output} Error: {fixture.Error}");
        Assert.Contains("PostRun:", fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("pre-run failed", fixture.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsMigrationAndPostHookFailuresTogether()
    {
        using var fixture = new CliFixture();
        using var runtime = fixture.CreateRuntime();
        var definitionPath = fixture.WriteScript(
            "migration-and-post-run-failure.m4d",
            """
            $Migration = @{
                Steps = @(@{ Id = 'Source'; Type = 'source' })
                RunOptions = @{ Fail = $true }
                PostRun = { throw 'post-run failed' }
            }
            """);

        var result = runtime.Invoke(
            fixture.GetCommand(runtime, "Invoke-M4dMigration"),
            new Dictionary<string, CliParameterValue>(),
            definitionPath,
            null,
            CancellationToken.None);

        Assert.Equal(ExitCodes.MigrationFailure, result);
        Assert.Contains("Intentional test migration failure", fixture.Error.ToString(), StringComparison.Ordinal);
        Assert.Contains("post-run failed", fixture.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AppliesCliOptionsToMigrationDefinition()
    {
        using var fixture = new CliFixture();
        using var runtime = fixture.CreateRuntime();
        var scriptPath = fixture.WriteScript(
            "self-invoking-with-cli-defaults.m4d",
            """
            $Migration = @{
                Steps = @(@{ Id = 'Source'; Type = 'source' })
                Connections = @{}
            }
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
    public void ExplicitCliOptionsOverrideDefinitionRunOptions()
    {
        using var fixture = new CliFixture();
        using var runtime = fixture.CreateRuntime();
        var scriptPath = fixture.WriteScript(
            "self-invoking-with-explicit-arguments.m4d",
            """
            $Migration = @{
                Steps = @(@{ Id = 'Source'; Type = 'source' })
                Connections = @{}
                RunOptions = @{
                    DatabasePath = 'script.sqlite'
                    RunName = 'from-definition'
                }
            }
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
        Assert.Contains("DatabasePath=cli.sqlite", fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("RunId=from-cli", fixture.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void InvokesAllModuleExportsAndUsesCommonDatabasePathForStoreFunctions()
    {
        using var fixture = new CliFixture();
        using var runtime = fixture.CreateRuntime();
        var exportedCommands = runtime.GetExportedCommands();
        Assert.Contains(exportedCommands, command => command.Name == "Get-M4dMigrationRun");
        Assert.Contains(exportedCommands, command => command.Name == "Complete-M4dMigrationBatch");
        Assert.Contains(exportedCommands, command => command.Name == "Invoke-M4dMigration");
        Assert.Contains(exportedCommands, command => command.Name == "New-M4dMigrationFile");

        var root = new CliCommandApp(runtime, CancellationToken.None).Build();
        var getRunParse = root.Parse(
        [
            "get-run",
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
            "advanced",
            "complete-batch",
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

        var runCommand = root.Subcommands.Single(command => command.Name == "run");
        Assert.Contains("Runs a test migration definition", runCommand.Description, StringComparison.Ordinal);
        Assert.Contains(runCommand.Options, option => option.Name == "--no-dashboard");
        var databasePathOption = runCommand.Options.Single(option => option.Name == "--database-path");
        Assert.Contains(
            "SQLite migration database",
            databasePathOption.Description,
            StringComparison.Ordinal);
        Assert.DoesNotContain(root.Subcommands, command => command.Name == "complete-batch");
        Assert.Contains(root.Subcommands, command => command.Name == "new-file");
        Assert.Contains(root.Subcommands, command => command.Name == "advanced");
        Assert.DoesNotContain(
            root.Subcommands.Select(command => command.Name),
            name => name.Contains("m4d-migration", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExposesMigrationComponentCatalogAsCliAction()
    {
        using var fixture = new CliFixture();
        using var runtime = fixture.CreateRuntime();
        var root = new CliCommandApp(runtime, CancellationToken.None).Build();
        var parseResult = root.Parse(
        [
            "get-component",
            "--category", "Source",
            "--name", "file",
            "--detailed"
        ]);

        Assert.Empty(parseResult.Errors);
        Assert.Equal(ExitCodes.Success, parseResult.Invoke());
        Assert.Contains("Name", fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("file", fixture.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("True", fixture.Output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReturnsDistinctCodesForInvalidJobsAndMigrationFailures()
    {
        using var fixture = new CliFixture();
        using var runtime = fixture.CreateRuntime();
        var invalidJobPath = fixture.WriteScript("invalid-job.m4d", "$Migration = @{ Connections = @{} }");
        var failedJobPath = fixture.WriteScript(
            "failed-job.m4d",
            """
            $Migration = @{
                Steps = @(@{ Id = 'Source'; Type = 'source' })
                Connections = @{}
                RunOptions = @{ Fail = $true }
            }
            """);
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
                new Dictionary<string, CliParameterValue>(),
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
            "json-job.m4d",
            """
            $Migration = @{
                Steps = @(@{ Id = 'Source'; Type = 'source' })
                Connections = @{}
            }
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
            "start-dashboard",
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
                    function Invoke-M4dMigrationGraph {
                        [CmdletBinding()]
                        param(
                            [string]$DatabasePath,
                            [string]$RunName,
                            [PSObject[]]$Steps,
                            [System.Collections.IDictionary]$Connections,
                            [switch]$Fail,
                            [switch]$WhatIf
                        )
                        if ($Fail) {
                            throw 'Intentional test migration failure.'
                        }
                        "DatabasePath=$DatabasePath; RunId=$RunName"
                    }

                    function Invoke-M4dMigration {
                    <#
                    .SYNOPSIS
                    Runs a test migration definition.
                    .PARAMETER Path
                    Path to a .m4d migration definition.
                    .PARAMETER DatabasePath
                    Path to the file-backed SQLite migration database.
                    .PARAMETER RunName
                    Human-readable test run name.
                    .PARAMETER Fail
                    Intentionally fail the test migration.
                    .PARAMETER WhatIf
                    Preview the migration.
                    #>
                        [CmdletBinding()]
                        param(
                            [Parameter(Mandatory)]
                            [string]$Path,
                            [string]$DatabasePath,
                            [string]$RunName,
                            [switch]$Fail,
                            [switch]$WhatIf
                        )
                        $ErrorActionPreference = 'Stop'
                        $definitionText = [System.IO.File]::ReadAllText($Path)
                        $Migration = $null
                        $definitionScript = [scriptblock]::Create($definitionText)
                        $null = . $definitionScript
                        if ($Migration -isnot [System.Collections.IDictionary] -or
                            -not $Migration.Contains('Steps')) {
                            throw 'The migration definition must define Steps.'
                        }
                        $invokeParameters = @{}
                        if ($Migration.RunOptions) {
                            foreach ($key in $Migration.RunOptions.Keys) {
                                $invokeParameters[$key] = $Migration.RunOptions[$key]
                            }
                        }
                        foreach ($key in $PSBoundParameters.Keys) {
                            if ($key -notin @('Path', 'ErrorAction', 'WarningAction', 'InformationAction', 'Verbose', 'Debug')) {
                                $invokeParameters[$key] = $PSBoundParameters[$key]
                            }
                        }
                        if (-not $invokeParameters.ContainsKey('DatabasePath')) {
                            $invokeParameters['DatabasePath'] = 'migration.sqlite3'
                        }
                        if (-not $invokeParameters.ContainsKey('RunName')) {
                            $invokeParameters['RunName'] = [System.IO.Path]::GetFileNameWithoutExtension($Path)
                        }
                        $context = [pscustomobject]@{
                            Connections = if ($Migration.Connections) { $Migration.Connections } else { @{} }
                            Result = $null
                            Error = $null
                            PostRunError = $null
                        }
                        $invokeParameters['Steps'] = $Migration.Steps
                        $invokeParameters['Connections'] = $context.Connections
                        if ($Fail) {
                            $invokeParameters['Fail'] = $true
                        }
                        if ($WhatIf) {
                            $invokeParameters['WhatIf'] = $true
                        }
                        $migrationError = $null
                        $postRunError = $null
                        $migrationStarted = $false
                        try {
                            if ($Migration.PreRun) {
                                $null = & $Migration.PreRun $context
                            }
                            if ($global:__m4dCliStartDashboard -eq $true) {
                                $dashboard = Start-M4dMigrationDashboard `
                                    -DatabasePath $invokeParameters['DatabasePath'] `
                                    -Steps $Migration.Steps `
                                    -Connections $context.Connections
                                $global:__m4dCliDashboardHandle = $dashboard
                            }
                            $migrationStarted = $true
                            $invokeParameters['Connections'] = $context.Connections
                            $context.Result = Invoke-M4dMigrationGraph @invokeParameters
                        }
                        catch {
                            $migrationError = $_
                            $context.Error = $_
                        }
                        finally {
                            if ($Migration.PostRun) {
                                try {
                                    $null = & $Migration.PostRun $context
                                }
                                catch {
                                    $postRunError = $_
                                    $context.PostRunError = $_
                                }
                            }
                        }
                        if ($migrationError -and $postRunError) {
                            $aggregate = [System.AggregateException]::new(
                                "Migration failed: $($migrationError.Exception.Message); PostRun failed: $($postRunError.Exception.Message)",
                                [System.Exception[]]@($migrationError.Exception, $postRunError.Exception))
                            $PSCmdlet.ThrowTerminatingError([System.Management.Automation.ErrorRecord]::new(
                                [System.InvalidOperationException]::new(
                                    "Migration lifecycle failed: $($aggregate.Message)",
                                    $aggregate),
                                'M4dMigrationLifecycleFailure',
                                [System.Management.Automation.ErrorCategory]::OperationStopped,
                                $Path))
                        }
                        if ($migrationError) {
                            if (-not $migrationStarted) {
                                $lifecycleException = [System.InvalidOperationException]::new(
                                    "Migration lifecycle failed: PreRun failed: $($migrationError.Exception.Message)",
                                    $migrationError.Exception)
                                $PSCmdlet.ThrowTerminatingError([System.Management.Automation.ErrorRecord]::new(
                                    $lifecycleException,
                                    'M4dMigrationLifecycleFailure',
                                    [System.Management.Automation.ErrorCategory]::OperationStopped,
                                    $Path))
                            }
                            $executionException = [System.InvalidOperationException]::new(
                                "Migration execution failed: $($migrationError.Exception.Message)",
                                $migrationError.Exception)
                            $PSCmdlet.ThrowTerminatingError([System.Management.Automation.ErrorRecord]::new(
                                $executionException,
                                'M4dMigrationExecutionFailure',
                                [System.Management.Automation.ErrorCategory]::OperationStopped,
                                $Path))
                        }
                        if ($postRunError) {
                            $lifecycleException = [System.InvalidOperationException]::new(
                                "Migration lifecycle failed: PostRun failed: $($postRunError.Exception.Message)",
                                $postRunError.Exception)
                            $PSCmdlet.ThrowTerminatingError([System.Management.Automation.ErrorRecord]::new(
                                $lifecycleException,
                                'M4dMigrationLifecycleFailure',
                                [System.Management.Automation.ErrorCategory]::OperationStopped,
                                $Path))
                        }
                        $context.Result
                    }

                    function New-M4dMigrationFile {
                    <#
                    .SYNOPSIS
                    Creates a test .m4d migration template.
                    .PARAMETER Path
                    Path where the new .m4d file will be created.
                    .PARAMETER Force
                    Replace an existing file.
                    #>
                        [CmdletBinding(SupportsShouldProcess)]
                        param(
                            [Parameter(Mandatory, Position = 0)]
                            [string]$Path,
                            [switch]$Force
                        )
                        if ($PSCmdlet.ShouldProcess($Path, 'Create migration definition')) {
                            if ((Test-Path -LiteralPath $Path) -and -not $Force) {
                                throw "The file '$Path' already exists."
                            }
                            $template = '$Migration = @{ Steps = @(); Connections = @{}; RunOptions = @{}; PreRun = {}; PostRun = {} }'
                            [System.IO.File]::WriteAllText($Path, $template)
                            Get-Item -LiteralPath $Path
                        }
                    }

                    function Get-M4dMigrationRun {
                    <#
                    .SYNOPSIS
                    Lists test migration runs.
                    .PARAMETER DatabasePath
                    SQLite migration store to inspect.
                    .PARAMETER RunId
                    ID of one run to return.
                    .PARAMETER Limit
                    Maximum number of runs to list.
                    #>
                        [CmdletBinding()]
                        param([string]$DatabasePath, [string]$RunId, [int]$Limit = 50)
                        [pscustomobject]@{ DatabasePath = $DatabasePath; RunId = $RunId; Limit = $Limit }
                    }

                    function Get-M4dMigrationComponent {
                    <#
                    .SYNOPSIS
                    Lists migration components with help.
                    .PARAMETER Category
                    Filter by component category.
                    .PARAMETER Name
                    Filter by component name.
                    .PARAMETER Detailed
                    Return full component help.
                    #>
                        [CmdletBinding()]
                        param([string]$Category, [string]$Name, [switch]$Detailed)
                        [pscustomobject]@{ Category = $Category; Name = $Name; Detailed = [bool]$Detailed }
                    }

                    function New-M4dMigrationJobStore {
                    <#
                    .SYNOPSIS
                    Opens a test migration store.
                    .PARAMETER DatabasePath
                    Path to the test migration store.
                    #>
                        [CmdletBinding()]
                        param([string]$DatabasePath)
                        [pscustomobject]@{ DatabasePath = $DatabasePath }
                    }

                    function Complete-M4dMigrationBatch {
                    <#
                    .SYNOPSIS
                    Completes a test migration batch.
                    .PARAMETER Store
                    Test migration store.
                    .PARAMETER BatchId
                    ID of the batch to complete.
                    .PARAMETER WorkerId
                    ID of the worker completing the batch.
                    .PARAMETER OutcomesJson
                    JSON batch outcomes.
                    .PARAMETER HasErrors
                    Whether the batch contains row errors.
                    #>
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
                    <#
                    .SYNOPSIS
                    Starts a test migration dashboard.
                    .PARAMETER DatabasePath
                    Path to the test migration database.
                    .PARAMETER Steps
                    Step definitions to preview.
                    .PARAMETER Connections
                    Connections used by the preview.
                    #>
                        [CmdletBinding()]
                        param([string]$DatabasePath, [object[]]$Steps, [System.Collections.IDictionary]$Connections)
                        Write-Information "Migration dashboard listening at http://127.0.0.1:5180" -InformationAction Continue
                        $connectionIds = if ($Connections) { $Connections.Keys -join ',' } else { '' }
                        Write-Information "DashboardConnections=$connectionIds" -InformationAction Continue
                        [pscustomobject]@{
                            Url = 'http://127.0.0.1:5180'
                            DatabasePath = $DatabasePath
                            StepCount = $Steps.Count
                            ConnectionCount = $Connections.Count
                        }
                    }

                    function Stop-M4dMigrationDashboard {
                    <#
                    .SYNOPSIS
                    Stops a test migration dashboard.
                    .PARAMETER Dashboard
                    Dashboard handle returned by Start-M4dMigrationDashboard.
                    #>
                        [CmdletBinding()]
                        param([Parameter(Mandatory)][object]$Dashboard)
                        Write-Information 'DashboardStopped' -InformationAction Continue
                    }

                    Export-ModuleMember -Function Invoke-M4dMigration, New-M4dMigrationFile, Get-M4dMigrationRun, `
                        Get-M4dMigrationComponent, `
                        New-M4dMigrationJobStore, Complete-M4dMigrationBatch, Start-M4dMigrationDashboard, `
                        Stop-M4dMigrationDashboard
                    """,
                    new[]
                    {
                        "Invoke-M4dMigration",
                        "New-M4dMigrationFile",
                        "Get-M4dMigrationRun",
                        "Get-M4dMigrationComponent",
                        "New-M4dMigrationJobStore",
                        "Complete-M4dMigrationBatch",
                        "Start-M4dMigrationDashboard",
                        "Stop-M4dMigrationDashboard"
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
