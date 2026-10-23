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
    'example-csv' = [System.IO.DirectoryInfo]::new($targetDirectory)
}
$sourceOptions = @{
    Encoding = 'utf-8'
    Culture = 'InvariantCulture'
    ColumnTypes = @{
        DataverseId = 'guid'
        Revenue = 'decimal'
    }
}
$targetOptions = @{
    Delimiter = ','
    Encoding = 'utf-8'
    EmitByteOrderMark = $false
    HasHeaderRecord = $true
    WriteHeaderRecord = $true
    Culture = 'InvariantCulture'
    NewLine = "`n"
    QuoteAllFields = $false
    ColumnTypes = @{ new_revenue = 'decimal' }
    ColumnsByStep = @{
        Accounts = @('new_name', 'new_sourceid', 'new_region', 'new_revenue')
    }
}

$steps = @(
    @{
        # Reads the account CSV with explicit types for its identifier and revenue.
        Id = 'AccountsSource'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'csv'
            ConnectionId = 'example-files'
            Path = 'accounts.csv'
            Key = 'SourceId'
            Id = 'DataverseId'
            Options = $sourceOptions
        }
    }
    @{
        # Normalizes names and maps source columns to the output CSV schema.
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
        # Writes the normalized account rows to CSV.
        Id = 'Accounts'
        Type = 'target'
        Inputs = @('NormalizeAccounts')
        Target = @{
            Type = 'csv'
            ConnectionId = 'example-csv'
            Path = 'accounts'
            Operation = 'Write'
            BatchSize = 100
            Options = $targetOptions
        }
    }
)

Invoke-Migration `
    -DatabasePath $databasePath `
    -RunName 'CSV example' `
    -Steps $steps `
    -Connections $connections `
    -BatchSize 100 `
    -MaxDegreeOfParallelism 4 `
    -EnableDebugCapture
