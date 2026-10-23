Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion -lt [version]'7.6') {
    throw 'Migration4Dataverse targets PowerShell 7.6 or later (.NET 10).'
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$dataDirectory = Join-Path $PSScriptRoot 'generated-data'
$targetDirectory = Join-Path $PSScriptRoot 'output'
$databasePath = Join-Path $dataDirectory 'migration.sqlite3'
$moduleManifest = Join-Path $repositoryRoot 'src/Migration4Dataverse.PowerShell/Migration4Dataverse.PowerShell.psd1'
Import-Module -Name $moduleManifest -Force -ErrorAction Stop
$null = [System.IO.Directory]::CreateDirectory($targetDirectory)

$connections = @{
    'example-files' = [System.IO.DirectoryInfo]::new($dataDirectory)
    'example-output' = [System.IO.DirectoryInfo]::new($targetDirectory)
}
$sourceCallback = {
    param($Connection, $Cursor, $PageSize, $StepId, $Path)

    $filePath = Join-Path $Connection.FullName $Path
    $start = if ($null -eq $Cursor) { 0 } else { [int]$Cursor }
    $reader = [System.IO.StreamReader]::new($filePath, [System.Text.Encoding]::UTF8)
    $records = [System.Collections.Generic.List[object]]::new()
    try {
        for ($lineNumber = 0; $lineNumber -lt $start; $lineNumber++) {
            if ($null -eq $reader.ReadLine()) {
                return @{ Records = @(); NextCursor = $null; Done = $true }
            }
        }
        while ($records.Count -lt $PageSize) {
            $line = $reader.ReadLine()
            if ($null -eq $line) { break }
            $records.Add((ConvertFrom-Json -InputObject $line -AsHashtable -ErrorAction Stop))
        }
        $done = $reader.EndOfStream
    }
    finally {
        $reader.Dispose()
    }

    @{
        Records = $records.ToArray()
        NextCursor = if ($done) { $null } else { [string]($start + $records.Count) }
        Done = [bool]$done
    }
}
$targetCallback = {
    param($Connection, $Records, $Context)

    $runDirectory = Join-Path $Context.TargetPath $Context.RunId
    $null = [System.IO.Directory]::CreateDirectory($runDirectory)
    $sequence = ([long]$Context.Sequence).ToString(
        'D12',
        [System.Globalization.CultureInfo]::InvariantCulture)
    $targetPath = Join-Path $runDirectory ("{0}-{1}.jsonl" -f $sequence, $Context.BatchId)
    $lines = @(
        foreach ($record in $Records) {
            ConvertTo-Json -InputObject $record -Depth 100 -Compress -ErrorAction Stop
        }
    )
    $content = [string]::Join("`n", $lines) + "`n"
    $encoding = [System.Text.UTF8Encoding]::new($false)
    if ([System.IO.File]::Exists($targetPath)) {
        if ([System.IO.File]::ReadAllText($targetPath, $encoding) -cne $content) {
            throw "Custom target batch '$targetPath' exists with different content."
        }
        return $true
    }

    [System.IO.File]::WriteAllText($targetPath, $content, $encoding)
    return $true
}

$steps = @(
    @{
        # Reads account rows from JSON Lines files in resumable pages.
        Id = 'AccountsSource'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'custom'
            ConnectionId = 'example-files'
            Path = 'accounts.jsonl'
            Key = 'SourceId'
            Id = 'DataverseId'
            Options = @{}
            Custom = @{
                Identity = 'example-jsonl-paged-source-v1'
                PageSize = 100
                Modules = @()
                Callback = $sourceCallback
            }
        }
    }
    @{
        # Normalizes the source fields into the target account schema.
        Id = 'NormalizeAccounts'
        Type = 'transform'
        Inputs = @('AccountsSource')
        Script = {
            param($row)
            @{
                new_name = ([string]$row.Name).Trim().ToUpperInvariant()
                new_sourceid = [string]$row.SourceId
                new_region = [string]$row.Region
                new_revenue = [decimal]$row.Revenue
            }
        }
    }
    @{
        # Writes each account batch to an idempotent, run-scoped JSON Lines file.
        Id = 'Accounts'
        Type = 'target'
        Inputs = @('NormalizeAccounts')
        Target = @{
            Type = 'custom'
            ConnectionId = 'example-output'
            Path = 'accounts'
            Operation = 'Write'
            BatchSize = 100
            Options = @{}
            Custom = @{
                Identity = 'example-idempotent-jsonl-target-v1'
                MaxConcurrency = 1
                Modules = @()
                Callback = $targetCallback
            }
        }
    }
)

Invoke-Migration `
    -DatabasePath $databasePath `
    -RunName 'Custom connector example' `
    -Steps $steps `
    -Connections $connections `
    -BatchSize 100 `
    -MaxDegreeOfParallelism 4 `
    -EnableDebugCapture
