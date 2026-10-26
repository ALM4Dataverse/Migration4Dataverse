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
    'source-db' = New-MigrationDatabaseConnection -Provider SQLite -ConnectionString `
        "Data Source=$(Join-Path $dataDirectory 'source.sqlite')"
    'transform-db' = New-MigrationDatabaseConnection -Provider SQLite -ConnectionString `
        "Data Source=$(Join-Path $dataDirectory 'transform.sqlite')"
    'target-db' = New-MigrationDatabaseConnection -Provider SQLite -ConnectionString `
        "Data Source=$(Join-Path $dataDirectory 'output.sqlite')"
}

$steps = @(
    @{
        # Reads ordered records from the source SQLite database.
        Id = 'RecordsSource'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'sqlite'
            ConnectionId = 'source-db'
            Key = 'Id'
            Options = @{
                Query = 'SELECT Id, Name FROM records ORDER BY Id'
            }
        }
    }
    @{
        # Writes an audit row for each record and uppercases its name.
        Id = 'RecordTransform'
        Type = 'transform'
        Inputs = @('RecordsSource')
        Transform = @{
            Type = 'sqlite'
            ConnectionId = 'transform-db'
        }
        Script = {
            param($row, $connection)
            $command = $connection.CreateCommand()
            try {
                $command.CommandText = 'INSERT INTO audit (Id, OriginalName) VALUES (@id, @name)'
                $null = $command.Parameters.AddWithValue('@id', $row.Id)
                $null = $command.Parameters.AddWithValue('@name', $row.Name)
                $null = $command.ExecuteNonQuery()
            }
            finally {
                $command.Dispose()
            }
            @{
                Id = [string]$row.Id
                Name = ([string]$row.Name).ToUpperInvariant()
            }
        }
    }
    @{
        # Persists the transformed records in the target SQLite accounts table.
        Id = 'RecordsTarget'
        Type = 'target'
        Inputs = @('RecordTransform')
        Target = @{
            Type = 'sqlite'
            ConnectionId = 'target-db'
            Table = 'accounts'
        }
    }
)

Invoke-Migration -DatabasePath (Join-Path $dataDirectory 'migration.sqlite') `
    -RunName 'SQLite connector example' `
    -Steps $steps `
    -Connections $connections `
    -BatchSize 100
