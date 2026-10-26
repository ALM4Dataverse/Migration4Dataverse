Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$dataDirectory = Join-Path $PSScriptRoot 'generated-data'
$null = [System.IO.Directory]::CreateDirectory($dataDirectory)
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$moduleManifest = Join-Path $repositoryRoot 'src/Migration4Dataverse.PowerShell/Migration4Dataverse.PowerShell.psd1'
Import-Module -Name $moduleManifest -Force -ErrorAction Stop

$source = [Migration4Dataverse.Engine.Connectors.MigrationDatabaseConnectionFactory]::Create(
    'SQLite',
    "Data Source=$(Join-Path $dataDirectory 'source.sqlite')")
$target = [Migration4Dataverse.Engine.Connectors.MigrationDatabaseConnectionFactory]::Create(
    'SQLite',
    "Data Source=$(Join-Path $dataDirectory 'output.sqlite')")
try {
    $source.Open()
    $command = $source.CreateCommand()
    try {
        $command.CommandText = 'DROP TABLE IF EXISTS records'
        $null = $command.ExecuteNonQuery()
        $command.CommandText = 'CREATE TABLE records (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL)'
        $null = $command.ExecuteNonQuery()
        $command.CommandText = "INSERT INTO records VALUES ('A001', 'North account'), ('A002', 'South account')"
        $null = $command.ExecuteNonQuery()
    }
    finally {
        $command.Dispose()
    }

    $target.Open()
    $command = $target.CreateCommand()
    try {
        $command.CommandText = 'DROP TABLE IF EXISTS accounts'
        $null = $command.ExecuteNonQuery()
        $command.CommandText = 'CREATE TABLE accounts (Id TEXT NOT NULL, Name TEXT NOT NULL)'
        $null = $command.ExecuteNonQuery()
        $command.CommandText = 'CREATE UNIQUE INDEX UX_accounts_id ON accounts (Id)'
        $null = $command.ExecuteNonQuery()
        $command.CommandText = 'CREATE INDEX IX_accounts_name ON accounts (Name)'
        $null = $command.ExecuteNonQuery()
    }
    finally {
        $command.Dispose()
    }
}
finally {
    $source.Dispose()
    $target.Dispose()
}

Write-Host "SQLite source and validate-only target databases are ready in '$dataDirectory'. Transform and staging databases are managed by Invoke-Migration."
