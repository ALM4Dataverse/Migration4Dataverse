Set-StrictMode -Version Latest

$packagedEngineAssemblyPath = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot 'lib/net10.0/Migration4Dataverse.Engine.dll'))
$developmentEngineAssemblyPath = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '../Migration4Dataverse.Engine/bin/Release/net10.0/Migration4Dataverse.Engine.dll'))
$engineAssemblyPath = if (Test-Path -LiteralPath $packagedEngineAssemblyPath -PathType Leaf) {
    $packagedEngineAssemblyPath
}
elseif (Test-Path -LiteralPath $developmentEngineAssemblyPath -PathType Leaf) {
    $developmentEngineAssemblyPath
}
else {
    throw "Migration4Dataverse.Engine.dll was not found at '$packagedEngineAssemblyPath' or '$developmentEngineAssemblyPath'. Run build/Stage-MigrationModule.ps1 or build the engine in Release configuration first."
}

[void][System.Reflection.Assembly]::LoadFrom($engineAssemblyPath)
Import-Module -Name $engineAssemblyPath -Scope Local

function New-M4dMigrationJobStore {
<#
.SYNOPSIS
Opens a migration database store for advanced PowerShell integrations.

.DESCRIPTION
Initializes a file-backed SQLite migration store. Ordinary migrations should use Invoke-M4dMigration, which manages the store automatically.

.PARAMETER DatabasePath
Path to a file-backed SQLite migration database.
#>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$DatabasePath
    )

    [Migration4Dataverse.Engine.Storage.MigrationStoreFactory]::Open($DatabasePath)
}

function New-M4dMigrationFile {
<#
.SYNOPSIS
Creates a commented .m4d migration template.

.DESCRIPTION
Creates a new .m4d file containing the complete migration definition shape, lifecycle hooks, and option guidance.

.PARAMETER Path
Path where the new .m4d file will be created.

.PARAMETER Force
Replace an existing file.
#>
    [CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
    param(
        [Parameter(Mandatory, Position = 0)]
        [ValidateNotNullOrEmpty()]
        [string]$Path,

        [switch]$Force
    )

    if ([System.IO.Path]::GetExtension($Path) -ine '.m4d') {
        throw "Migration definition files must use the .m4d extension: '$Path'."
    }

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    if ($PSCmdlet.ShouldProcess($resolvedPath, 'Create migration definition')) {
        $template = @'
# Migration4Dataverse migration definition.
# This file is executable PowerShell; only run files that you trust.
# Run it with: m4d run ./migration.m4d

$Migration = @{
    Steps = @(
        # Add source, transform, filter, append, target, or callback nodes here.
        # Example source and target nodes:
        # @{
        #     Id = 'ReadRows'
        #     Type = 'source'
        #     Inputs = @()
        #     Source = @{
        #         Type = 'file'
        #         Format = @{ Type = 'jsonl' }
        #         ConnectionId = 'files'
        #         Path = 'input.jsonl'
        #         Key = 'Id'
        #     }
        # }
        # @{
        #     Id = 'WriteRows'
        #     Type = 'target'
        #     Inputs = @('ReadRows')
        #     Target = @{
        #         Type = 'file'
        #         Format = @{ Type = 'jsonl' }
        #         ConnectionId = 'files'
        #         Path = 'output.jsonl'
        #     }
        # }
    )
    Connections = @{
        # Supply named connection objects referenced by ConnectionId.
        # files = [System.IO.DirectoryInfo]'/path/to/files'
    }
    RunOptions = @{
        # These are defaults. Explicit Invoke-M4dMigration parameters and CLI options win.
        # DatabasePath = './migration.sqlite3'
        # RunName = 'My migration'
        # RunId = 'existing-run-id'
        # StepHash = 'expected-step-hash'
        # MaxDegreeOfParallelism = 64
        # MaxQueuedBatches = 64
        # MaxQueuedBatchesPerStep = 32
        # MaxAttempts = 5
        # MaxErrorRatePercent = 0
        # SourceReadMaxAttempts = 3
        # SourceReadRetryDelaySeconds = 2
        # DataverseNoRetryDiagnosticMode = $true
        # ModulesToImport = @('Example.Module')
        # RequiredModules = @(@{ ModuleName = 'Example.Module'; ModuleVersion = '1.0' })
        # ScriptsToLoad = @('./helpers.ps1')
        # SharedScripts = @({ function Get-ExampleValue { 'value' } })
        # BatchSize = 10
        # EnableDebugCapture = $true
        # WhatIf = $true
        # DebugCaptureRetentionDays = 30
    }
    PreRun = {
        param($context)
        # Initialize live resources on $context.Connections.
    }
    PostRun = {
        param($context)
        # Release resources. Inspect $context.Result, $context.Error, and
        # $context.PostRunError as appropriate.
    }
}
'@
        $mode = if ($Force) {
            [System.IO.FileMode]::Create
        }
        else {
            [System.IO.FileMode]::CreateNew
        }
        $stream = [System.IO.File]::Open(
            $resolvedPath,
            $mode,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        try {
            $writer = [System.IO.StreamWriter]::new(
                $stream,
                [System.Text.UTF8Encoding]::new($false))
            try {
                $writer.Write($template)
            }
            finally {
                $writer.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
        Get-Item -LiteralPath $resolvedPath
    }
}

function Invoke-M4dMigration {
<#
.SYNOPSIS
Runs a migration defined in a .m4d file.

.DESCRIPTION
Loads a trusted PowerShell migration definition, runs its optional lifecycle hooks, and invokes the migration graph. Values supplied directly to this command override defaults in the file's RunOptions.

.PARAMETER Path
Path to a .m4d migration definition.

.PARAMETER DatabasePath
Path to the file-backed SQLite migration database.

.PARAMETER RunName
Human-readable name for this migration run.

.PARAMETER RunId
ID of an existing run to resume.

.PARAMETER StepHash
Expected migration step hash when resuming a run.

.PARAMETER MaxDegreeOfParallelism
Maximum number of batches to process concurrently.

.PARAMETER MaxQueuedBatches
Maximum number of queued batches across the migration.

.PARAMETER MaxQueuedBatchesPerStep
Maximum number of queued batches for each step.

.PARAMETER MaxAttempts
Maximum number of attempts for a batch.

.PARAMETER MaxErrorRatePercent
Maximum allowed row error rate before the migration fails.

.PARAMETER SourceReadMaxAttempts
Maximum number of attempts for a source read.

.PARAMETER SourceReadRetryDelaySeconds
Delay between source read retries.

.PARAMETER DataverseNoRetryDiagnosticMode
Disable retry behavior for Dataverse and source-reader diagnostics.

.PARAMETER ModulesToImport
PowerShell modules to import for migration scripts.

.PARAMETER RequiredModules
PowerShell module specifications to require.

.PARAMETER ScriptsToLoad
PowerShell script files to load before running the migration.

.PARAMETER SharedScripts
Shared PowerShell scriptblocks available to migration steps.

.PARAMETER BatchSize
Number of rows in each batch.

.PARAMETER EnableDebugCapture
Capture row data for migration troubleshooting.

.PARAMETER WhatIf
Preview the migration without writing target rows or invoking batch callbacks.

.PARAMETER DebugCaptureRetentionDays
Number of days to retain captured debug data.
#>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, Position = 0)]
        [ValidateNotNullOrEmpty()]
        [string]$Path,

        [string]$DatabasePath,
        [string]$RunName,
        [string]$RunId,
        [string]$StepHash,

        [ValidateRange(1, [int]::MaxValue)]
        [int]$MaxDegreeOfParallelism,

        [ValidateRange(1, [int]::MaxValue)]
        [int]$MaxQueuedBatches,

        [ValidateRange(1, [int]::MaxValue)]
        [int]$MaxQueuedBatchesPerStep,

        [ValidateRange(1, [int]::MaxValue)]
        [int]$MaxAttempts,

        [ValidateRange(0, 100)]
        [double]$MaxErrorRatePercent,

        [ValidateRange(1, [int]::MaxValue)]
        [int]$SourceReadMaxAttempts,

        [ValidateRange(0, 3600)]
        [int]$SourceReadRetryDelaySeconds,

        [switch]$DataverseNoRetryDiagnosticMode,
        [string[]]$ModulesToImport,
        [Microsoft.PowerShell.Commands.ModuleSpecification[]]$RequiredModules,
        [string[]]$ScriptsToLoad,
        [scriptblock[]]$SharedScripts,

        [ValidateRange(1, 1000)]
        [int]$BatchSize,

        [switch]$EnableDebugCapture,
        [switch]$WhatIf,

        [ValidateRange(1, 3650)]
        [int]$DebugCaptureRetentionDays
    )

    $Parameters = [System.Collections.Hashtable]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($key in $PSBoundParameters.Keys) {
        if ($key -ne 'Path') {
            $Parameters[$key] = $PSBoundParameters[$key]
        }
    }

    $ErrorActionPreference = 'Stop'
    $resolvedPath = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    if ([System.IO.Path]::GetExtension($resolvedPath) -ine '.m4d') {
        throw "Migration definitions must use the .m4d extension: '$resolvedPath'."
    }

    $definitionText = [System.IO.File]::ReadAllText($resolvedPath)
    $definitionTokens = $null
    $parseErrors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseInput(
        $definitionText,
        [ref]$definitionTokens,
        [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) {
        throw "The .m4d file '$resolvedPath' has parse errors: $($parseErrors.Message -join '; ')"
    }

    $Migration = $null
    $escapedPath = $resolvedPath.Replace("'", "''", [System.StringComparison]::Ordinal)
    $scriptContext = "`$PSScriptRoot = [System.IO.Path]::GetDirectoryName('$escapedPath'); " +
        "`$PSCommandPath = '$escapedPath';"
    $definitionScript = [scriptblock]::Create("$scriptContext`n$definitionText")
    $null = . $definitionScript
    if ($Migration -isnot [System.Collections.IDictionary]) {
        throw "The .m4d file '$resolvedPath' must define a root `$Migration hashtable."
    }

    $steps = $Migration['Steps']
    $connections = $Migration['Connections']
    $preRun = $Migration['PreRun']
    $postRun = $Migration['PostRun']
    $runOptions = $Migration['RunOptions']
    $supportedProperties = @('Steps', 'Connections', 'PreRun', 'PostRun', 'RunOptions')
    foreach ($key in $Migration.Keys) {
        if ($key -notin $supportedProperties) {
            throw "Unsupported .m4d property '$key'. Supported properties are: $($supportedProperties -join ', ')."
        }
    }
    if (-not $Migration.Contains('Steps') -or $null -eq $steps) {
        throw "The .m4d file '$resolvedPath' must define Steps."
    }
    if ($Migration.Contains('Connections') -and
        $null -ne $connections -and
        $connections -isnot [System.Collections.IDictionary]) {
        throw 'Migration Connections must be a hashtable.'
    }
    if ($Migration.Contains('PreRun') -and $null -ne $preRun -and $preRun -isnot [scriptblock]) {
        throw 'PreRun must be a scriptblock.'
    }
    if ($Migration.Contains('PostRun') -and $null -ne $postRun -and $postRun -isnot [scriptblock]) {
        throw 'PostRun must be a scriptblock.'
    }
    if ($Migration.Contains('RunOptions') -and $null -ne $runOptions -and
        $runOptions -isnot [System.Collections.IDictionary]) {
        throw 'RunOptions must be a hashtable.'
    }

    $invokeCommand = Get-Command Invoke-M4dMigrationGraph -CommandType Cmdlet -ErrorAction Stop
    $invokeParameters = [System.Collections.Hashtable]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($source in @($runOptions, $Parameters)) {
        if ($null -eq $source) {
            continue
        }
        foreach ($key in $source.Keys) {
            if ($key -in @('Steps', 'Connections')) {
                throw "Set '$key' on the root .m4d definition, not in RunOptions or Parameters."
            }
            if (-not $invokeCommand.Parameters.ContainsKey([string]$key)) {
                throw "Invoke-M4dMigration has no '$key' parameter."
            }
            $invokeParameters[[string]$key] = $source[$key]
        }
    }

    $connections = if ($Migration.Contains('Connections') -and $null -ne $connections) {
        $connections
    }
    else {
        [System.Collections.Hashtable]::new([System.StringComparer]::Ordinal)
    }
    $context = [pscustomobject]@{
        Path = $resolvedPath
        Migration = $Migration
        Parameters = $invokeParameters
        Connections = $connections
        Result = $null
        Error = $null
        PostRunError = $null
    }

    $invokeParameters['Steps'] = $steps
    $invokeParameters['Connections'] = $context.Connections
    if (-not $invokeParameters.ContainsKey('DatabasePath')) {
        $invokeParameters['DatabasePath'] = Join-Path (Get-Location) 'migration.sqlite3'
    }
    if (-not $invokeParameters.ContainsKey('RunName')) {
        $invokeParameters['RunName'] = [System.IO.Path]::GetFileNameWithoutExtension($resolvedPath)
    }
    $hashComponents = @()
    if ($invokeParameters.ContainsKey('AdditionalStepHashComponents') -and
        $null -ne $invokeParameters['AdditionalStepHashComponents']) {
        $hashComponents = @($invokeParameters['AdditionalStepHashComponents'])
    }
    $definitionHash = [System.Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData(
            [System.Text.Encoding]::UTF8.GetBytes($definitionText))).ToLowerInvariant()
    $hashComponents += "M4dDefinitionFile:$definitionHash"
    if ($hashComponents.Count -gt 0) {
        $invokeParameters['AdditionalStepHashComponents'] = $hashComponents
    }
    $invokeParameters['ErrorAction'] = 'Stop'

    $migrationError = $null
    $postRunError = $null
    $migrationStarted = $false
    $migrationPhase = 'PreRun'
    try {
        if ($preRun -is [scriptblock]) {
            $null = & $preRun $context
        }
        if ($global:__m4dCliStartDashboard -eq $true) {
            $migrationPhase = 'Dashboard startup'
            if ($steps.Count -gt 0) {
                $dashboard = Start-M4dMigrationDashboard `
                    -DatabasePath $invokeParameters['DatabasePath'] `
                    -Steps $steps `
                    -Connections $context.Connections
            }
            else {
                $dashboard = Start-M4dMigrationDashboard -DatabasePath $invokeParameters['DatabasePath']
            }
            $global:__m4dCliDashboardHandle = $dashboard
        }
        $migrationPhase = 'Migration execution'
        $migrationStarted = $true
        $invokeParameters['Connections'] = $context.Connections
        $context.Result = Invoke-M4dMigrationGraph @invokeParameters
    }
    catch {
        $migrationError = $_
        $context.Error = $_
    }
    finally {
        if ($postRun -is [scriptblock]) {
            try {
                $null = & $postRun $context
            }
            catch {
                $postRunError = $_
                $context.PostRunError = $_
            }
        }
    }

    if ($null -ne $migrationError -and $null -ne $postRunError) {
        $aggregate = [System.AggregateException]::new(
            "Migration and PostRun both failed. Migration: $($migrationError.Exception.Message) PostRun: $($postRunError.Exception.Message)",
            [System.Exception[]]@($migrationError.Exception, $postRunError.Exception))
        $errorRecord = [System.Management.Automation.ErrorRecord]::new(
            [System.InvalidOperationException]::new(
                "Migration lifecycle failed: $($aggregate.Message)",
                $aggregate),
            'M4dMigrationLifecycleFailure',
            [System.Management.Automation.ErrorCategory]::OperationStopped,
            $resolvedPath)
        $PSCmdlet.ThrowTerminatingError($errorRecord)
    }
    if ($null -ne $migrationError) {
        if (-not $migrationStarted) {
            $failurePhase = if ($migrationPhase -eq 'Dashboard startup') {
                'Dashboard startup failed'
            }
            else {
                'PreRun failed'
            }
            $lifecycleException = [System.InvalidOperationException]::new(
                "Migration lifecycle failed: ${failurePhase}: $($migrationError.Exception.Message)",
                $migrationError.Exception)
            $errorRecord = [System.Management.Automation.ErrorRecord]::new(
                $lifecycleException,
                'M4dMigrationLifecycleFailure',
                [System.Management.Automation.ErrorCategory]::OperationStopped,
                $resolvedPath)
            $PSCmdlet.ThrowTerminatingError($errorRecord)
        }
        if ($migrationError.Exception -is [System.Management.Automation.ParameterBindingException] -or
            $migrationError.Exception -is [System.Management.Automation.ValidationMetadataException] -or
            $migrationError.FullyQualifiedErrorId -match 'MissingMandatoryParameter|ParameterBinding') {
            throw $migrationError
        }
        $executionException = [System.InvalidOperationException]::new(
            "Migration execution failed: $($migrationError.Exception.Message)",
            $migrationError.Exception)
        $errorRecord = [System.Management.Automation.ErrorRecord]::new(
            $executionException,
            'M4dMigrationExecutionFailure',
            [System.Management.Automation.ErrorCategory]::OperationStopped,
            $resolvedPath)
        $PSCmdlet.ThrowTerminatingError($errorRecord)
    }
    if ($null -ne $postRunError) {
        $lifecycleException = [System.InvalidOperationException]::new(
            "Migration lifecycle failed: PostRun failed: $($postRunError.Exception.Message)",
            $postRunError.Exception)
        $errorRecord = [System.Management.Automation.ErrorRecord]::new(
            $lifecycleException,
            'M4dMigrationLifecycleFailure',
            [System.Management.Automation.ErrorCategory]::OperationStopped,
            $resolvedPath)
        $PSCmdlet.ThrowTerminatingError($errorRecord)
    }

    $context.Result
}

function Complete-M4dMigrationBatch {
<#
.SYNOPSIS
Completes a leased batch through the lower-level migration store interface.

.DESCRIPTION
Records batch outcomes and optional dependency output. The scheduler normally performs batch completion itself.

.PARAMETER Store
Store object returned by New-M4dMigrationJobStore.

.PARAMETER BatchId
ID of the batch being completed.

.PARAMETER WorkerId
Worker that owns the batch lease.

.PARAMETER OutcomesJson
JSON object containing the batch outcomes.

.PARAMETER HasErrors
Marks the completion as containing row errors.

.PARAMETER ChildTable
Child job or table name for one dependency output. Supply together with ChildPayloadJson.

.PARAMETER ChildPayloadJson
JSON payload for the child work. Supply together with ChildTable.

.PARAMETER LastError
Optional final error text stored with the batch.
#>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Store,
        [Parameter(Mandatory)]
        [string]$BatchId,
        [Parameter(Mandatory)]
        [string]$WorkerId,
        [Parameter(Mandatory)]
        [string]$OutcomesJson,
        [switch]$HasErrors,
        [string]$ChildTable,
        [string]$ChildPayloadJson,
        [string]$LastError
    )

    if ([string]::IsNullOrWhiteSpace($ChildTable) -xor [string]::IsNullOrWhiteSpace($ChildPayloadJson)) {
        throw 'ChildTable and ChildPayloadJson must either both be provided or both be omitted.'
    }

    $dependencyEvents = [System.Collections.Generic.List[Migration4Dataverse.Engine.Storage.DependencyEvent]]::new()
    if (-not [string]::IsNullOrWhiteSpace($ChildTable)) {
        $dependencyEvents.Add(
            [Migration4Dataverse.Engine.Storage.DependencyEvent]::Create(
                $BatchId,
                $ChildTable,
                $ChildPayloadJson))
    }

    $Store.CompleteBatch(
        $BatchId,
        $WorkerId,
        $OutcomesJson,
        $HasErrors.IsPresent,
        $dependencyEvents,
        $LastError,
        $null,
        0)
}

function Set-M4dMigrationRunStatus {
<#
.SYNOPSIS
Sets a run status through the lower-level migration store interface.

.DESCRIPTION
Updates the status of a migration run. Invoke-M4dMigration manages status transitions when running a migration.

.PARAMETER Store
Store object returned by New-M4dMigrationJobStore.

.PARAMETER RunId
ID of the run to update.

.PARAMETER Status
New MigrationRunStatus value: Running, Completed, Failed, or Cancelled.
#>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Store,
        [Parameter(Mandatory)]
        [string]$RunId,
        [Parameter(Mandatory)]
        [ValidateSet('Running', 'Completed', 'Failed', 'Cancelled')]
        [string]$Status
    )

    $statusValue = [System.Enum]::Parse(
        [Migration4Dataverse.Engine.Storage.MigrationRunStatus],
        $Status,
        $false)
    $Store.SetRunStatus($RunId, $statusValue, $null)
}

function Start-M4dMigrationDashboard {
<#
.SYNOPSIS
Starts the local Migration4Dataverse dashboard.

.DESCRIPTION
Starts the engine-hosted dashboard for persisted run history, configuration previews, and captured migration data. The dashboard does not start or resume migrations.

.PARAMETER DatabasePath
File-backed SQLite migration database whose stored runs are displayed.

.PARAMETER Steps
Step definitions to preview, using the same objects as Invoke-M4dMigration.

.PARAMETER Connections
Connection map used with Steps. Cannot be supplied without steps.

.PARAMETER Url
HTTP loopback address and port for the dashboard. Non-loopback URLs are rejected.
#>
    [CmdletBinding()]
    param(
        [string]$DatabasePath,
        [object[]]$Steps = @(),
        [System.Collections.IDictionary]$Connections,
        [ValidateNotNullOrEmpty()]
        [string]$Url = 'http://127.0.0.1:5180'
    )

    if ($Steps.Count -gt 0 -and $null -eq $Connections) {
        $Connections = [System.Collections.Hashtable]::new([System.StringComparer]::Ordinal)
    }
    if ($Steps.Count -eq 0 -and $null -ne $Connections -and $Connections.Count -gt 0) {
        throw 'Connections can only be supplied when Steps are also provided.'
    }
    if ([string]::IsNullOrWhiteSpace($DatabasePath) -and $Steps.Count -eq 0) {
        throw 'Provide DatabasePath, Steps, or both to start the migration dashboard.'
    }
    $configurationJson = if ($Steps.Count -gt 0) {
        [Migration4Dataverse.Engine.PowerShell.MigrationConfigurationSnapshot]::CreateJson(
            [System.Management.Automation.PSObject[]]$Steps,
            $Connections)
    }

    $dashboard = [Migration4Dataverse.Engine.Dashboard.MigrationDashboardServer]::StartWithConfigurationAsync(
        $DatabasePath,
        $Url,
        $configurationJson).GetAwaiter().GetResult()
    Write-Information "Migration dashboard listening at $($dashboard.Url)" -InformationAction Continue
    $dashboard
}

function Stop-M4dMigrationDashboard {
<#
.SYNOPSIS
Stops and disposes a dashboard server handle.

.DESCRIPTION
Stops the dashboard represented by the supplied handle. A handle is local to the PowerShell process that started it.

.PARAMETER Dashboard
Dashboard handle returned by Start-M4dMigrationDashboard.
#>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, ValueFromPipeline)]
        [Migration4Dataverse.Engine.Dashboard.MigrationDashboardHandle]$Dashboard
    )

    process {
        $null = $Dashboard.DisposeAsync().AsTask().GetAwaiter().GetResult()
    }
}

Export-ModuleMember `
        -Function New-M4dMigrationJobStore, New-M4dMigrationFile, Invoke-M4dMigration, Complete-M4dMigrationBatch, Set-M4dMigrationRunStatus, `
        Start-M4dMigrationDashboard, Stop-M4dMigrationDashboard `
        -Cmdlet Invoke-M4dMigrationGraph, Get-M4dMigrationRun, Get-M4dMigrationStep, Get-M4dMigrationBatch, `
        Get-M4dMigrationError, Get-M4dMigrationRetryHistory, Get-M4dMigrationDebugRow, `
        Get-M4dMigrationDebugRowBinary, Get-M4dMigrationConfigurationPreview, Get-M4dMigrationProcessMetric, `
        Get-M4dMigrationDataverseConnectionThroughput, Invoke-M4dMigrationRecovery, `
        Clear-M4dMigrationTargetTuning, Start-M4dMigrationDatabaseServer, New-M4dMigrationBinaryValue
