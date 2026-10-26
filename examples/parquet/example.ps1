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
    'example-parquet' = [System.IO.DirectoryInfo]::new($targetDirectory)
}
$sourceOptions = @{
    CompressionMethod = 'Snappy'
    CompressionLevel = 'Optimal'
    MaxSourceRowGroupRows = 250000
}
$targetOptions = @{
    CompressionMethod = 'Snappy'
    CompressionLevel = 'Optimal'
    TargetRowGroupSize = 100000
    SchemaByStep = @{
        Accounts = @(
            @{ Name = 'new_name'; Type = 'string'; Nullable = $false }
            @{ Name = 'new_sourceid'; Type = 'string'; Nullable = $false }
            @{ Name = 'new_region'; Type = 'string'; Nullable = $false }
            @{ Name = 'new_revenue'; Type = 'decimal'; Precision = 18; Scale = 2; Nullable = $false }
        )
    }
}

$steps = @(
    @{
        # Reads account records from the input Parquet file.
        Id = 'AccountsSource'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'parquet'
            ConnectionId = 'example-files'
            Path = 'accounts.parquet'
            Key = 'SourceId'
            Id = 'DataverseId'
            Options = $sourceOptions
        }
    }
    @{
        # Normalizes names and maps source columns to the output schema.
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
        # Writes the normalized account rows to Parquet.
        Id = 'Accounts'
        Type = 'target'
        Inputs = @('NormalizeAccounts')
        Target = @{
            Type = 'parquet'
            ConnectionId = 'example-parquet'
            Path = 'accounts.parquet'
            Operation = 'Write'
            BatchSize = 100
            Options = $targetOptions
        }
    }
)

Invoke-Migration `
    -DatabasePath $databasePath `
    -RunName 'Parquet example' `
    -Steps $steps `
    -Connections $connections `
    -BatchSize 100 `
    -MaxDegreeOfParallelism 4 `
    -EnableDebugCapture
