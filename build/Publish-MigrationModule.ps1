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
$packagePath = & $testScriptPath -Version $Version -SkipBuild
if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
    throw "The package '$packagePath' was not created."
}

Publish-PSResource `
    -NupkgPath $packagePath `
    -Repository 'PSGallery' `
    -ApiKey $env:PSGALLERY_API_KEY `
    -SkipDependenciesCheck `
    -ErrorAction Stop

Write-Information "Published Migration4Dataverse $Version to PSGallery." -InformationAction Continue
