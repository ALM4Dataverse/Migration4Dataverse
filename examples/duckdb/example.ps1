Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion -lt [version]'7.6') {
    throw 'Migration4Dataverse targets PowerShell 7.6 or later (.NET 10).'
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$dataDirectory = Join-Path $PSScriptRoot 'generated-data'
$moduleManifest = Join-Path $repositoryRoot 'src/Migration4Dataverse.PowerShell/Migration4Dataverse.PowerShell.psd1'
Import-Module -Name $moduleManifest -Force -ErrorAction Stop

$connections = @{
    'source-db' = [DuckDB.NET.Data.DuckDBConnection]::new(
        "Data Source=$(Join-Path $dataDirectory 'source.duckdb')")
    'transform-db' = [DuckDB.NET.Data.DuckDBConnection]::new(
        "Data Source=$(Join-Path $dataDirectory 'transform.duckdb')")
    'target-db' = [DuckDB.NET.Data.DuckDBConnection]::new(
        "Data Source=$(Join-Path $dataDirectory 'output.duckdb')")
}

$steps = @(
    @{
        # Reads ordered records from the source DuckDB database.
        Id = 'RecordsSource'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'duckdb'
            ConnectionId = 'source-db'
            Key = 'Id'
            Options = @{
                Query = 'SELECT Id, Name FROM records ORDER BY Id'
            }
        }
    }
    @{
        # Adds an audit row for each input record in the transform database.
        Id = 'RecordTransform'
        Type = 'transform'
        Inputs = @('RecordsSource')
        Transform = @{
            Type = 'duckdb'
            ConnectionId = 'transform-db'
            Sql = 'INSERT INTO audit VALUES (?, ?)'
            Parameters = [ordered]@{
                id = 'Id'
                name = 'Name'
            }
        }
    }
    @{
        # Persists the records in the target DuckDB accounts table.
        Id = 'RecordsTarget'
        Type = 'target'
        Inputs = @('RecordTransform')
        Target = @{
            Type = 'duckdb'
            ConnectionId = 'target-db'
            Table = 'accounts'
        }
    }
)

Invoke-Migration -DatabasePath (Join-Path $dataDirectory 'migration.sqlite') `
    -RunName 'DuckDB connector example' `
    -Steps $steps `
    -Connections $connections `
    -BatchSize 100
