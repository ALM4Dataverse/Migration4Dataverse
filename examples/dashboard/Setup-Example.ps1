Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion -lt [version]'7.6') {
    throw 'Migration4Dataverse targets PowerShell 7.6 or later (.NET 10).'
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$databaseDirectory = Join-Path $PSScriptRoot 'state'
$null = [System.IO.Directory]::CreateDirectory($databaseDirectory)

dotnet build (Join-Path $repositoryRoot 'src/Migration4Dataverse.Engine/Migration4Dataverse.Engine.csproj') `
    --configuration Release `
    --no-incremental
if ($LASTEXITCODE -ne 0) {
    throw 'Building Migration4Dataverse.Engine failed.'
}

Write-Host "Dashboard demo is ready. Run: pwsh '$PSScriptRoot/example.ps1'"
