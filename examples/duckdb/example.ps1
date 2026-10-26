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
    'source-db' = [System.IO.FileInfo]::new((Join-Path $dataDirectory 'source.duckdb'))
    'target-db' = [System.IO.FileInfo]::new((Join-Path $dataDirectory 'output.duckdb'))
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
        # Runs one prepared SQL statement for each record.
        Id = 'RecordTransform'
        Type = 'transform'
        Inputs = @('RecordsSource')
        Transform = @{
            Type = 'duckdb'
            InitializeSql = @'
CREATE TABLE IF NOT EXISTS row_rules (suffix VARCHAR);
DELETE FROM row_rules;
INSERT INTO row_rules VALUES ('!');
'@
            Sql = 'SELECT ? AS Id, upper(?) || (SELECT suffix FROM row_rules) AS Name'
            Parameters = [ordered]@{
                id = 'Id'
                name = 'Name'
            }
        }
    }
    @{
        # Normalizes each bounded batch with set-based DuckDB SQL.
        Id = 'BatchTransform'
        Type = 'transform'
        Inputs = @('RecordTransform')
        Transform = @{
            Type = 'duckdb'
            Mode = 'per_batch'
            InitializeSql = @'
CREATE TABLE IF NOT EXISTS batch_rules (minimum_length INTEGER);
DELETE FROM batch_rules;
INSERT INTO batch_rules VALUES (4);
'@
            Sql = @'
SELECT __migration_parent_index, Id, upper(Name) AS Name
FROM __migration4dataverse_input
WHERE length(Name) >= ?
ORDER BY Id
'@
            Parameters = [ordered]@{
                minimumLength = 4
            }
        }
    }
    @{
        # Stages rows in the same engine-managed database as the whole-dataset transform.
        Id = 'StagedRecords'
        Type = 'target'
        Inputs = @('BatchTransform')
        Target = @{
            Type = 'duckdb'
            Table = 'staged_accounts'
        }
    }
    @{
        # Reads the completed staged dataset after its target barrier.
        Id = 'WholeDatasetTransform'
        Type = 'transform'
        Inputs = @('StagedRecords')
        Transform = @{
            Type = 'duckdb'
            Mode = 'whole_dataset'
            Key = 'Id'
            InitializeSql = @'
CREATE TABLE IF NOT EXISTS dataset_rules (suffix VARCHAR);
DELETE FROM dataset_rules;
INSERT INTO dataset_rules VALUES ('!');
'@
            Sql = @'
SELECT Id, upper(Name) || (SELECT suffix FROM dataset_rules) AS Name
FROM staged_accounts
WHERE length(Name) >= ?
ORDER BY Id
'@
            Parameters = [ordered]@{
                minimumLength = 4
            }
        }
    }
    @{
        # Writes completed records to the configured DuckDB database file.
        Id = 'RecordsTarget'
        Type = 'target'
        Inputs = @('WholeDatasetTransform')
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
