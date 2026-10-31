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
        -Function New-M4dMigrationJobStore, Complete-M4dMigrationBatch, Set-M4dMigrationRunStatus, `
        Start-M4dMigrationDashboard, Stop-M4dMigrationDashboard `
        -Cmdlet Invoke-M4dMigration, Get-M4dMigrationRun, Get-M4dMigrationStep, Get-M4dMigrationBatch, `
        Get-M4dMigrationError, Get-M4dMigrationRetryHistory, Get-M4dMigrationDebugRow, `
        Get-M4dMigrationDebugRowBinary, Get-M4dMigrationConfigurationPreview, Get-M4dMigrationProcessMetric, `
        Get-M4dMigrationDataverseConnectionThroughput, Invoke-M4dMigrationRecovery, `
        Clear-M4dMigrationTargetTuning, Start-M4dMigrationDatabaseServer, New-M4dMigrationBinaryValue
