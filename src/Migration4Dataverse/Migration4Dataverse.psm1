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

function New-MigrationJobStore {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$DatabasePath
    )

    [Migration4Dataverse.Engine.Storage.MigrationStoreFactory]::Open($DatabasePath)
}

function Complete-MigrationBatch {
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

function Set-MigrationRunStatus {
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

function Start-MigrationDashboard {
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

function Stop-MigrationDashboard {
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
        -Function New-MigrationJobStore, Complete-MigrationBatch, Set-MigrationRunStatus, `
        Start-MigrationDashboard, Stop-MigrationDashboard `
        -Cmdlet Invoke-Migration, Get-MigrationRun, Get-MigrationStep, Get-MigrationBatch, `
        Get-MigrationError, Get-MigrationRetryHistory, Get-MigrationDebugRow, `
        Get-MigrationDebugRowBinary, Get-MigrationConfigurationPreview, Get-MigrationProcessMetric, `
        Get-MigrationDataverseConnectionThroughput, Invoke-MigrationRecovery, `
        Clear-MigrationTargetTuning, Start-MigrationDatabaseServer, New-MigrationBinaryValue
