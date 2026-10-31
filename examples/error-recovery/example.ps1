Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion -lt [version]'7.6') {
    throw 'Migration4Dataverse targets PowerShell 7.6 or later (.NET 10).'
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$dataDirectory = Join-Path $PSScriptRoot 'generated-data'
$outputDirectory = Join-Path $dataDirectory 'output'
$databasePath = Join-Path $dataDirectory 'migration.sqlite3'
$sourcePath = Join-Path $dataDirectory 'rows.jsonl'
if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "Run 'Setup-Example.ps1' first; the dataset '$sourcePath' is missing."
}

$null = [System.IO.Directory]::CreateDirectory($outputDirectory)
$moduleManifest = Join-Path $repositoryRoot 'src/Migration4Dataverse.PowerShell/Migration4Dataverse.PowerShell.psd1'
Import-Module -Name $moduleManifest -Force -ErrorAction Stop
$connections = @{
    files = [System.IO.DirectoryInfo]$dataDirectory
    target = [System.IO.DirectoryInfo]$outputDirectory
}
$dashboard = Start-MigrationDashboard -DatabasePath $databasePath

try {
    Write-Host 'The local dashboard is available at:'
    Write-Host "  $($dashboard.Url)"
    Write-Host 'Open it in a browser. Each prompt pauses after an expected failure so you can inspect it before recovery.'

    $sourceRunId = [guid]::NewGuid().ToString()
    $sourceSteps = @(
        @{
            Id = 'Source'
            Type = 'callback'
            Inputs = @()
            ReadBatches = {
                param($checkpoint, $connection, $cursor)
                if ($null -eq $cursor) {
                    [Migration4Dataverse.Engine.Scheduling.MigrationWorkBatch]::new(
                        '{"Records":[{"Id":"source-reader-row","Name":"source-reader"}]}',
                        '"after-one-batch"',
                        $false)
                }
                else {
                    throw 'Intentional source-reader failure for the recovery demonstration.'
                }
            }
            ExecuteBatch = {
                [Migration4Dataverse.Engine.Scheduling.MigrationBatchResult]::new(
                    '{"Succeeded":1}',
                    $false,
                    $null,
                    $null)
            }
        }
    )
    $sourceFailed = $false
    try {
        $null = Invoke-Migration -DatabasePath $databasePath -RunName 'Source reader continuation' `
            -RunId $sourceRunId -Steps $sourceSteps -Connections $connections `
            -BatchSize 1 -MaxAttempts 5 -SourceReadMaxAttempts 1 -MaxDegreeOfParallelism 1 `
            -EnableDebugCapture
    }
    catch {
        $sourceFailed = $true
        Write-Host "Expected source-reader failure: $($_.Exception.Message)"
    }
    if (-not $sourceFailed) {
        throw 'The source-reader demonstration did not produce its expected failure.'
    }
    Get-MigrationError -DatabasePath $databasePath -RunId $sourceRunId | Format-List Scope, StepId, Message
    $null = Read-Host 'Inspect the Source error in the dashboard, then press Enter to continue the source step'
    $null = Invoke-MigrationRecovery -DatabasePath $databasePath -RunId $sourceRunId `
        -StepId 'Source' -ContinueAfterFailure
    $null = Invoke-Migration -DatabasePath $databasePath -RunName 'Source reader continuation' `
        -RunId $sourceRunId -Steps $sourceSteps -Connections $connections `
        -BatchSize 1 -MaxAttempts 5 -SourceReadMaxAttempts 1 -MaxDegreeOfParallelism 1 `
        -EnableDebugCapture
    Write-Host "Source continuation completed. RunId: $sourceRunId"

    $transformRunId = [guid]::NewGuid().ToString()
    $transformSteps = @(
        @{
            Id = 'Rows'
            Type = 'source'
            Inputs = @()
            Source = @{
                Type = 'file'
                Format = @{ Type = 'jsonl' }
                ConnectionId = 'files'
                Path = 'rows.jsonl'
                Key = 'SourceId'
                Id = 'RecordId'
            }
        }
        @{
            Id = 'Transform'
            Type = 'transform'
            Inputs = @('Rows')
            Script = {
                param($row)
                if ($row.Name -eq 'transform-error') {
                    throw 'Correct the transform-row Name before retrying.'
                }
                $row
            }
        }
        @{
            Id = 'Target'
            Type = 'target'
            Inputs = @('Transform')
            Target = @{
                Type = 'file'
                Format = @{ Type = 'jsonl' }
                ConnectionId = 'target'
                Path = "$transformRunId-transform.jsonl"
            }
        }
    )
    $transformFailed = $false
    try {
        $null = Invoke-Migration -DatabasePath $databasePath -RunName 'Transform row edit and retry' `
            -RunId $transformRunId -Steps $transformSteps -Connections $connections `
            -BatchSize 1 -MaxAttempts 1 -MaxDegreeOfParallelism 1 -EnableDebugCapture
    }
    catch {
        $transformFailed = $true
        Write-Host "Expected Transform row failure: $($_.Exception.Message)"
    }
    if (-not $transformFailed) {
        throw 'The Transform demonstration did not produce its expected failure.'
    }
    Get-MigrationError -DatabasePath $databasePath -RunId $transformRunId -StepId 'Transform' |
        Format-List Scope, StepId, BatchId, RowErrors
    $null = Read-Host 'Inspect the Transform row error, then press Enter to edit and retry that row'
    $transformError = Get-MigrationError -DatabasePath $databasePath -RunId $transformRunId `
        -StepId 'Transform' | Where-Object Scope -eq 'Batch' | Select-Object -First 1
    $transformRowError = $transformError.RowErrors | Where-Object CanRetry | Select-Object -First 1
    $transformPayload = ConvertFrom-Json -InputObject $transformRowError.PayloadJson -AsHashtable
    $transformPayload.Name = 'fixed-transform'
    $transformPayloadJson = ConvertTo-Json -InputObject $transformPayload -Depth 100 -Compress
    $null = Invoke-MigrationRecovery -DatabasePath $databasePath -RunId $transformRunId `
        -StepId $transformError.StepId -BatchId $transformError.BatchId `
        -RecordIndex $transformRowError.RecordIndex -PayloadJson $transformPayloadJson
    $null = Invoke-Migration -DatabasePath $databasePath -RunName 'Transform row edit and retry' `
        -RunId $transformRunId -Steps $transformSteps -Connections $connections `
        -BatchSize 1 -MaxAttempts 1 -MaxDegreeOfParallelism 1 -EnableDebugCapture
    Write-Host "Transform row retry completed. RunId: $transformRunId"

    $filterRunId = [guid]::NewGuid().ToString()
    $filterSteps = @(
        @{
            Id = 'Rows'
            Type = 'source'
            Inputs = @()
            Source = @{
                Type = 'file'
                Format = @{ Type = 'jsonl' }
                ConnectionId = 'files'
                Path = 'rows.jsonl'
                Key = 'SourceId'
                Id = 'RecordId'
            }
        }
        @{
            Id = 'Filter'
            Type = 'filter'
            Inputs = @('Rows')
            Script = {
                param($row)
                if ($row.Name -eq 'filter-error') {
                    throw 'Correct the filter-row Name before retrying.'
                }
                $true
            }
        }
        @{
            Id = 'Target'
            Type = 'target'
            Inputs = @('Filter')
            Target = @{
                Type = 'file'
                Format = @{ Type = 'jsonl' }
                ConnectionId = 'target'
                Path = "$filterRunId-filter.jsonl"
            }
        }
    )
    $filterFailed = $false
    try {
        $null = Invoke-Migration -DatabasePath $databasePath -RunName 'Filter row edit and retry' `
            -RunId $filterRunId -Steps $filterSteps -Connections $connections `
            -BatchSize 1 -MaxAttempts 1 -MaxDegreeOfParallelism 1 -EnableDebugCapture
    }
    catch {
        $filterFailed = $true
        Write-Host "Expected Filter row failure: $($_.Exception.Message)"
    }
    if (-not $filterFailed) {
        throw 'The Filter demonstration did not produce its expected failure.'
    }
    Get-MigrationError -DatabasePath $databasePath -RunId $filterRunId -StepId 'Filter' |
        Format-List Scope, StepId, BatchId, RowErrors
    $null = Read-Host 'Inspect the Filter row error, then press Enter to edit and retry that row'
    $filterError = Get-MigrationError -DatabasePath $databasePath -RunId $filterRunId `
        -StepId 'Filter' | Where-Object Scope -eq 'Batch' | Select-Object -First 1
    $filterRowError = $filterError.RowErrors | Where-Object CanRetry | Select-Object -First 1
    $filterPayload = ConvertFrom-Json -InputObject $filterRowError.PayloadJson -AsHashtable
    $filterPayload.Name = 'fixed-filter'
    $filterPayloadJson = ConvertTo-Json -InputObject $filterPayload -Depth 100 -Compress
    $null = Invoke-MigrationRecovery -DatabasePath $databasePath -RunId $filterRunId `
        -StepId $filterError.StepId -BatchId $filterError.BatchId `
        -RecordIndex $filterRowError.RecordIndex -PayloadJson $filterPayloadJson
    $null = Invoke-Migration -DatabasePath $databasePath -RunName 'Filter row edit and retry' `
        -RunId $filterRunId -Steps $filterSteps -Connections $connections `
        -BatchSize 1 -MaxAttempts 1 -MaxDegreeOfParallelism 1 -EnableDebugCapture
    Write-Host "Filter row retry completed. RunId: $filterRunId"

    $targetRunId = [guid]::NewGuid().ToString()
    $targetGatePath = Join-Path $dataDirectory "$targetRunId-target-enabled.txt"
    $connections.targetGate = $targetGatePath
    $targetSteps = @(
        @{
            Id = 'Rows'
            Type = 'source'
            Inputs = @()
            Source = @{
                Type = 'file'
                Format = @{ Type = 'jsonl' }
                ConnectionId = 'files'
                Path = 'rows.jsonl'
                Key = 'SourceId'
                Id = 'RecordId'
            }
        }
        @{
            Id = 'Target'
            Type = 'target'
            Inputs = @('Rows')
            Target = @{
                Type = 'custom'
                ConnectionId = 'targetGate'
                Custom = @{
                    Identity = 'error-recovery-demo-target-v1'
                    Callback = {
                        param($connection, $records, $context)
                        if (-not (Test-Path -LiteralPath $connection -PathType Leaf) -and
                            @($records | Where-Object { $_.Name -eq 'target-error' }).Count -gt 0) {
                            throw 'Enable the target gate before retrying the failed batch.'
                        }
                        return $true
                    }
                }
            }
        }
    )
    $targetFailed = $false
    try {
        $null = Invoke-Migration -DatabasePath $databasePath -RunName 'Target batch replay' `
            -RunId $targetRunId -Steps $targetSteps -Connections $connections `
            -BatchSize 1 -MaxAttempts 1 -MaxDegreeOfParallelism 1 -EnableDebugCapture
    }
    catch {
        $targetFailed = $true
        Write-Host "Expected Target batch failure: $($_.Exception.Message)"
    }
    if (-not $targetFailed) {
        throw 'The Target demonstration did not produce its expected failure.'
    }
    $targetError = Get-MigrationError -DatabasePath $databasePath -RunId $targetRunId `
        -StepId 'Target' | Where-Object Scope -eq 'Batch' | Select-Object -First 1
    if ($null -eq $targetError) {
        throw 'The Target demonstration did not retain its failed batch.'
    }
    $targetError | Format-List Scope, StepId, BatchId, Message
    $null = Read-Host 'Inspect the Target error. You can queue replay in the dashboard now; press Enter to enable the target and resume via PowerShell'
    $targetBatch = Get-MigrationBatch -DatabasePath $databasePath -RunId $targetRunId `
        -StepId 'Target' | Where-Object BatchId -eq $targetError.BatchId | Select-Object -First 1
    if ($null -eq $targetBatch) {
        throw "The failed Target batch '$($targetError.BatchId)' could not be inspected."
    }
    if ($targetBatch.State -eq 'CompletedWithErrors') {
        $null = Invoke-MigrationRecovery -DatabasePath $databasePath -RunId $targetRunId -RetryFailedBatches
    }
    elseif ($targetBatch.State -eq 'Pending') {
        Write-Host 'Target batch replay was queued in the dashboard.'
    }
    else {
        throw "Unexpected Target batch state '$($targetBatch.State)' before replay."
    }
    [System.IO.File]::WriteAllText($targetGatePath, 'enabled', [System.Text.UTF8Encoding]::new($false))
    $null = Invoke-Migration -DatabasePath $databasePath -RunName 'Target batch replay' `
        -RunId $targetRunId -Steps $targetSteps -Connections $connections `
        -BatchSize 1 -MaxAttempts 1 -MaxDegreeOfParallelism 1 -EnableDebugCapture
    Write-Host "Target batch replay completed. RunId: $targetRunId"

    $callbackRunId = [guid]::NewGuid().ToString()
    $callbackSteps = @(
        @{
            Id = 'Rows'
            Type = 'source'
            Inputs = @()
            Source = @{
                Type = 'file'
                Format = @{ Type = 'jsonl' }
                ConnectionId = 'files'
                Path = 'rows.jsonl'
                Key = 'SourceId'
                Id = 'RecordId'
            }
        }
        @{
            Id = 'Callback'
            Type = 'callback'
            Inputs = @('Rows')
            ExecuteBatch = {
                param($batch)
                $payload = ConvertFrom-Json -InputObject $batch.PayloadJson -AsHashtable
                $row = $payload.Records[0]
                if ($row.Name -eq 'callback-error') {
                    $rowError = [Migration4Dataverse.Engine.Storage.MigrationRowError]::new(
                        0,
                        'Correct the callback-row Name before retrying.')
                    return [Migration4Dataverse.Engine.Scheduling.MigrationBatchResult]::new(
                        '{"succeeded":0,"failed":[{"recordIndex":0,"error":"Correct the callback-row Name before retrying."}]}',
                        $true,
                        $null,
                        'The callback rejected one input row.',
                        $null,
                        0,
                        [Migration4Dataverse.Engine.Storage.MigrationRowError[]]@($rowError))
                }
                [Migration4Dataverse.Engine.Scheduling.MigrationBatchResult]::new(
                    '{"Succeeded":1}',
                    $false,
                    $null,
                    $null)
            }
        }
    )
    $callbackFailed = $false
    try {
        $null = Invoke-Migration -DatabasePath $databasePath -RunName 'Callback row edit and retry' `
            -RunId $callbackRunId -Steps $callbackSteps -Connections $connections `
            -BatchSize 1 -MaxAttempts 1 -MaxDegreeOfParallelism 1 -EnableDebugCapture
    }
    catch {
        $callbackFailed = $true
        Write-Host "Expected callback row failure: $($_.Exception.Message)"
    }
    if (-not $callbackFailed) {
        throw 'The callback demonstration did not produce its expected failure.'
    }
    Get-MigrationError -DatabasePath $databasePath -RunId $callbackRunId -StepId 'Callback' |
        Format-List Scope, StepId, BatchId, RowErrors
    $null = Read-Host 'Inspect the callback row error, then press Enter to edit and retry that row'
    $callbackError = Get-MigrationError -DatabasePath $databasePath -RunId $callbackRunId `
        -StepId 'Callback' | Where-Object Scope -eq 'Batch' | Select-Object -First 1
    $callbackRowError = $callbackError.RowErrors | Where-Object CanRetry | Select-Object -First 1
    $callbackPayload = ConvertFrom-Json -InputObject $callbackRowError.PayloadJson -AsHashtable
    $callbackPayload.Name = 'fixed-callback'
    $callbackPayloadJson = ConvertTo-Json -InputObject $callbackPayload -Depth 100 -Compress
    $null = Invoke-MigrationRecovery -DatabasePath $databasePath -RunId $callbackRunId `
        -StepId $callbackError.StepId -BatchId $callbackError.BatchId `
        -RecordIndex $callbackRowError.RecordIndex -PayloadJson $callbackPayloadJson
    $null = Invoke-Migration -DatabasePath $databasePath -RunName 'Callback row edit and retry' `
        -RunId $callbackRunId -Steps $callbackSteps -Connections $connections `
        -BatchSize 1 -MaxAttempts 1 -MaxDegreeOfParallelism 1 -EnableDebugCapture
    Write-Host "Callback row retry completed. RunId: $callbackRunId"

    Write-Host 'All recovery examples completed. Run states and retained error history are available in the dashboard and inspection cmdlets.'
    Write-Host "Database: $databasePath"
    $null = Read-Host 'Press Enter to stop the dashboard'
}
finally {
    Stop-MigrationDashboard -Dashboard $dashboard
}
