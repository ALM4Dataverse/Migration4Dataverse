[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($env:PSGALLERY_API_KEY)) {
    throw 'Set PSGALLERY_API_KEY to a PowerShell Gallery API key before publishing.'
}

$testScriptPath = Join-Path $PSScriptRoot 'Test-MigrationModulePackage.ps1'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$packageOutputPath = Join-Path $repositoryRoot 'artifacts/packages'
$null = & $testScriptPath -Version $Version -SkipBuild -OutputPath $packageOutputPath
$packagePath = Join-Path $packageOutputPath "Migration4Dataverse.$Version.nupkg"
if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
    throw "The package '$packagePath' was not created."
}

$cliPublishScriptPath = Join-Path $PSScriptRoot 'Publish-MigrationCli.ps1'
$null = & $cliPublishScriptPath -Version $Version -SkipBuild

Publish-PSResource `
    -NupkgPath $packagePath `
    -Repository 'PSGallery' `
    -ApiKey $env:PSGALLERY_API_KEY `
    -SkipDependenciesCheck `
    -ErrorAction Stop

Write-Information "Published Migration4Dataverse $Version to PSGallery." -InformationAction Continue
