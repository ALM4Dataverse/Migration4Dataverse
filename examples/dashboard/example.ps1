Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion -lt [version]'7.6') {
    throw 'Migration4Dataverse targets PowerShell 7.6 or later (.NET 10).'
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$databaseDirectory = Join-Path $PSScriptRoot 'state'
$databasePath = Join-Path $databaseDirectory 'dashboard-demo.sqlite3'
$moduleManifest = Join-Path $repositoryRoot 'src/Migration4Dataverse.PowerShell/Migration4Dataverse.PowerShell.psd1'
$null = [System.IO.Directory]::CreateDirectory($databaseDirectory)
Import-Module -Name $moduleManifest -Force -ErrorAction Stop

$runId = [guid]::NewGuid().ToString('N')
$connections = @{
    'synthetic-source' = @{
        Type = 'SyntheticSource'
        Dataset = 'DemoCustomers'
        State = 'Ready'
        AccessToken = 'synthetic-demo-token'
    }
    'synthetic-target' = @{
        Type = 'SyntheticTarget'
        Destination = 'Local demonstration sink'
        State = 'Ready'
        Password = 'synthetic-demo-password'
    }
}
$steps = @(
    @{
        # Generates 240 synthetic customer rows in small batches for the live dashboard.
        Id = 'Extract Customers'
        Type = 'callback'
        Inputs = @()
        SourceConnectionId = 'synthetic-source'
        TargetConnectionId = 'synthetic-target'
        ReadBatches = {
            param($checkpoint, $connection, $cursor)
            $sequence = if ($null -eq $cursor) {
                0
            }
            else {
                [int](ConvertFrom-Json -InputObject $cursor)
            }
            if ($sequence -ge 48) {
                return
            }

            $records = @(
                for ($offset = 1; $offset -le 5; $offset++) {
                    $customerId = ($sequence * 5) + $offset
                    [ordered]@{
                        SourceId = "CUS-$('{0:D4}' -f $customerId)"
                        Name = "Customer $customerId"
                        Segment = if ($customerId % 2 -eq 0) { 'Enterprise' } else { 'SMB' }
                        AccessToken = 'demo-token-value'
                    }
                }
            )
            $payload = ConvertTo-Json -InputObject ([ordered]@{ Records = $records }) -Depth 20 -Compress
            $nextCursor = ConvertTo-Json -InputObject ([string]($sequence + 1)) -Compress
            [Migration4Dataverse.Engine.Scheduling.MigrationWorkBatch]::new(
                $payload,
                $nextCursor,
                $sequence -eq 47)
        }
        ExecuteBatch = {
            param($batch, $sourceConnection, $targetConnection)
            Start-Sleep -Milliseconds 450
            $payload = ConvertFrom-Json -InputObject $batch.PayloadJson -AsHashtable -Depth 100
            $recordsJson = ConvertTo-Json -InputObject $payload.Records -Depth 100 -Compress
            $children = [Migration4Dataverse.Engine.Scheduling.MigrationDependencyOutput[]]@(
                [Migration4Dataverse.Engine.Scheduling.MigrationDependencyOutput]::new(
                    'Transform Customers',
                    $batch.PayloadJson))
            $stages = [Migration4Dataverse.Engine.Storage.MigrationDebugStage[]]@(
                [Migration4Dataverse.Engine.Storage.MigrationDebugStage]::new(
                    'Extracted source rows',
                    $recordsJson))
            [Migration4Dataverse.Engine.Scheduling.MigrationBatchResult]::new(
                ('{{"Succeeded":{0},"Failed":0}}' -f @($payload.Records).Count),
                $false,
                $children,
                $null,
                $stages)
        }
    }
    @{
        # Maps each synthetic customer into the demonstration target shape.
        Id = 'Transform Customers'
        Type = 'callback'
        Inputs = @('Extract Customers')
        ExecuteBatch = {
            param($batch, $sourceConnection, $targetConnection)
            Start-Sleep -Milliseconds 500
            $payload = ConvertFrom-Json -InputObject $batch.PayloadJson -AsHashtable -Depth 100
            $mapped = @(
                foreach ($source in $payload.Records) {
                    [ordered]@{
                        SourceId = [string]$source.SourceId
                        DisplayName = ([string]$source.Name).ToUpperInvariant()
                        CustomerType = [string]$source.Segment
                    }
                }
            )
            $mappedJson = ConvertTo-Json -InputObject $mapped -Depth 100 -Compress
            $child = [Migration4Dataverse.Engine.Scheduling.MigrationDependencyOutput[]]@(
                [Migration4Dataverse.Engine.Scheduling.MigrationDependencyOutput]::new(
                    'Load Customers',
                    (ConvertTo-Json -InputObject ([ordered]@{ Records = $mapped }) -Depth 100 -Compress)))
            $stages = [Migration4Dataverse.Engine.Storage.MigrationDebugStage[]]@(
                [Migration4Dataverse.Engine.Storage.MigrationDebugStage]::new(
                    'Mapped output rows',
                    $mappedJson))
            [Migration4Dataverse.Engine.Scheduling.MigrationBatchResult]::new(
                ('{{"Succeeded":{0},"Failed":0}}' -f $mapped.Count),
                $false,
                $child,
                $null,
                $stages)
        }
    }
    @{
        # Simulates writing the mapped rows so dashboard progress and lineage are visible.
        Id = 'Load Customers'
        Type = 'callback'
        Inputs = @('Transform Customers')
        TargetConnectionId = 'synthetic-target'
        ExecuteBatch = {
            param($batch, $sourceConnection, $targetConnection)
            Start-Sleep -Milliseconds 2000
            $payload = ConvertFrom-Json -InputObject $batch.PayloadJson -AsHashtable -Depth 100
            [Migration4Dataverse.Engine.Scheduling.MigrationBatchResult]::new(
                ('{{"Succeeded":{0},"Failed":0}}' -f @($payload.Records).Count),
                $false,
                $null,
                $null)
        }
    }
)

$dashboard = Start-MigrationDashboard `
    -DatabasePath $databasePath `
    -Steps $steps `
    -Connections $connections
Write-Host ''
Write-Host 'Dashboard demo is running. Open this URL while the migration runs:'
Write-Host "  $($dashboard.Url)"
Write-Host 'The sample runs for about a minute and remains available for completed-run inspection.'
Write-Host ''
Start-Sleep -Seconds 5

try {
    Invoke-Migration `
        -DatabasePath $databasePath `
        -RunName 'Dashboard UI demonstration' `
        -RunId $runId `
        -Steps $steps `
        -Connections $connections `
        -MaxDegreeOfParallelism 2 `
        -MaxQueuedBatches 4 `
        -EnableDebugCapture `
        -DebugCaptureRetentionDays 30

    Write-Host ''
    Write-Host 'Demo migration completed. The dashboard remains open for run and row-trace inspection.'
    Write-Host 'Press Enter to stop the dashboard server.'
    $null = Read-Host
}
finally {
    Stop-MigrationDashboard -Dashboard $dashboard
}
