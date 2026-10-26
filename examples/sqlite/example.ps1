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
    'source-db' = [System.IO.FileInfo]::new((Join-Path $dataDirectory 'source.sqlite'))
    'target-db' = [System.IO.FileInfo]::new((Join-Path $dataDirectory 'output.sqlite'))
}

$steps = @(
    @{
        # Reads a parameterized, read-only query in stable key order.
        Id = 'RecordsSource'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'sqlite'
            ConnectionId = 'source-db'
            Key = 'Id'
            Options = @{
                Query = 'SELECT Id, Name FROM records WHERE Id >= @minimumId ORDER BY Id'
                Parameters = @{
                    minimumId = 'A001'
                }
            }
        }
    }
    @{
        # Runs one set-based SQL transform for each bounded input batch.
        Id = 'NormalizeBatch'
        Type = 'transform'
        Inputs = @('RecordsSource')
        Transform = @{
            Type = 'sqlite'
            Mode = 'per_batch'
            InitializeSql = @'
CREATE TABLE IF NOT EXISTS batch_rules (minimum_length INTEGER NOT NULL);
DELETE FROM batch_rules;
INSERT INTO batch_rules VALUES (4);
'@
            Sql = @'
SELECT __migration_parent_index, Id, upper(Name) AS Name
FROM __migration4dataverse_input
WHERE length(Name) >= (SELECT minimum_length FROM batch_rules)
ORDER BY Id
'@
        }
    }
    @{
        # Stages all normalized rows for a whole-dataset SQL join.
        Id = 'StagedRecords'
        Type = 'target'
        Inputs = @('NormalizeBatch')
        Target = @{
            Type = 'sqlite'
            Table = 'staged_records'
            Options = @{
                CreateTable = $true
                Schema = [ordered]@{
                    Id = 'TEXT'
                    Name = 'TEXT'
                }
                KeyColumns = @('Id')
                Indexes = @(
                    @{
                        Name = 'IX_staged_records_name'
                        Columns = @('Name')
                    }
                )
            }
        }
    }
    @{
        # Waits for staging to finish, then runs one SQL query across the full dataset.
        Id = 'EnrichedRecords'
        Type = 'transform'
        Inputs = @('StagedRecords')
        Transform = @{
            Type = 'sqlite'
            Mode = 'whole_dataset'
            Key = 'Id'
            InitializeSql = @'
CREATE TABLE IF NOT EXISTS dataset_rules (suffix TEXT NOT NULL);
DELETE FROM dataset_rules;
INSERT INTO dataset_rules VALUES ('!');
'@
            Sql = @'
SELECT s.Id, upper(s.Name) || (SELECT suffix FROM dataset_rules) AS Name
FROM staged_records AS s
ORDER BY s.Id
'@
        }
    }
    @{
        # Upserts the fully enriched output using the declared stable key.
        Id = 'RecordsTarget'
        Type = 'target'
        Inputs = @('EnrichedRecords')
        Target = @{
            Type = 'sqlite'
            ConnectionId = 'target-db'
            Table = 'accounts'
            Options = @{
                Schema = [ordered]@{
                    Id = 'TEXT'
                    Name = 'TEXT'
                }
                KeyColumns = @('Id')
                Indexes = @(
                    @{
                        Name = 'IX_accounts_name'
                        Columns = @('Name')
                    }
                )
            }
        }
    }
)

Invoke-Migration -DatabasePath (Join-Path $dataDirectory 'migration.sqlite') `
    -RunName 'SQLite connector example' `
    -Steps $steps `
    -Connections $connections `
    -BatchSize 100
