Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$dataDirectory = Join-Path $PSScriptRoot 'generated-data'
$null = [System.IO.Directory]::CreateDirectory($dataDirectory)
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$moduleManifest = Join-Path $repositoryRoot 'src/Migration4Dataverse.PowerShell/Migration4Dataverse.PowerShell.psd1'
Import-Module -Name $moduleManifest -Force -ErrorAction Stop

$source = [Microsoft.Data.Sqlite.SqliteConnection]::new(
    "Data Source=$(Join-Path $dataDirectory 'source.sqlite')")
$transform = [Microsoft.Data.Sqlite.SqliteConnection]::new(
    "Data Source=$(Join-Path $dataDirectory 'transform.sqlite')")
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

    $transform.Open()
    $command = $transform.CreateCommand()
    try {
        $command.CommandText = 'DROP TABLE IF EXISTS audit'
        $null = $command.ExecuteNonQuery()
        $command.CommandText = 'CREATE TABLE audit (Id TEXT NOT NULL, OriginalName TEXT NOT NULL)'
        $null = $command.ExecuteNonQuery()
    }
    finally {
        $command.Dispose()
    }
}
finally {
    $source.Dispose()
    $transform.Dispose()
}

Write-Host "SQLite source and transform databases are ready in '$dataDirectory'."
