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
    'example-jsonl' = [System.IO.FileInfo]::new((Join-Path $targetDirectory 'accounts.jsonl'))
}

$steps = @(
    @{
        # Reads account records from the input JSON Lines file.
        Id = 'AccountsSource'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'jsonl'
            ConnectionId = 'example-files'
            Path = 'accounts.jsonl'
            Key = 'SourceId'
            Id = 'DataverseId'
            Options = @{}
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
        # Writes the normalized account rows to JSON Lines.
        Id = 'Accounts'
        Type = 'target'
        Inputs = @('NormalizeAccounts')
        Target = @{
            Type = 'jsonl'
            ConnectionId = 'example-jsonl'
            Operation = 'Write'
            BatchSize = 100
            Options = @{}
        }
    }
)

Invoke-Migration `
    -DatabasePath $databasePath `
    -RunName 'JSON Lines example' `
    -Steps $steps `
    -Connections $connections `
    -BatchSize 100 `
    -MaxDegreeOfParallelism 4 `
    -EnableDebugCapture
