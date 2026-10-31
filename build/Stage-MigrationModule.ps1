[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$moduleSourceDirectory = Join-Path $repositoryRoot 'src/Migration4Dataverse'
$engineOutputDirectory = Join-Path $repositoryRoot 'src/Migration4Dataverse.Engine/bin/Release/net10.0'

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot 'artifacts/Migration4Dataverse'
}
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)

$engineAssemblyPath = Join-Path $engineOutputDirectory 'Migration4Dataverse.Engine.dll'
if (-not (Test-Path -LiteralPath $engineAssemblyPath -PathType Leaf)) {
    throw "The Release engine output was not found at '$engineAssemblyPath'. Build Migration4Dataverse.slnx in Release configuration first."
}

$manifestSourcePath = Join-Path $moduleSourceDirectory 'Migration4Dataverse.psd1'
$moduleSourcePath = Join-Path $moduleSourceDirectory 'Migration4Dataverse.psm1'
$licenseSourcePath = Join-Path $repositoryRoot 'LICENSE'
foreach ($sourcePath in @($manifestSourcePath, $moduleSourcePath, $licenseSourcePath)) {
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Required module source file '$sourcePath' was not found."
    }
}

if (Test-Path -LiteralPath $OutputPath) {
    Remove-Item -LiteralPath $OutputPath -Recurse -Force
}

$libraryDirectory = Join-Path $OutputPath 'lib/net10.0'
$null = New-Item -ItemType Directory -Path $libraryDirectory -Force

Copy-Item -LiteralPath $manifestSourcePath -Destination (Join-Path $OutputPath 'Migration4Dataverse.psd1')
Copy-Item -LiteralPath $moduleSourcePath -Destination (Join-Path $OutputPath 'Migration4Dataverse.psm1')
Copy-Item -LiteralPath $licenseSourcePath -Destination (Join-Path $OutputPath 'LICENSE')
Get-ChildItem -LiteralPath $engineOutputDirectory -Force |
    Copy-Item -Destination $libraryDirectory -Recurse -Force

$stagedManifestPath = Join-Path $OutputPath 'Migration4Dataverse.psd1'
$manifestContent = Get-Content -LiteralPath $stagedManifestPath -Raw
$manifestVersionPattern = [regex]::new("(?m)^\s*ModuleVersion\s*=\s*'[^']*'")
if ($manifestVersionPattern.Matches($manifestContent).Count -eq 0) {
    throw "Could not find ModuleVersion in '$stagedManifestPath'."
}

$updatedManifestContent = $manifestVersionPattern.Replace(
    $manifestContent,
    "    ModuleVersion = '$Version'",
    1)
Set-Content -LiteralPath $stagedManifestPath -Value $updatedManifestContent -NoNewline -Encoding utf8

$stagedManifest = Import-PowerShellDataFile -LiteralPath $stagedManifestPath
if ($stagedManifest.ModuleVersion -ne $Version) {
    throw "The staged manifest version '$($stagedManifest.ModuleVersion)' does not match '$Version'."
}

$requiredDependency = @($stagedManifest.RequiredModules) |
    Where-Object { $_ -is [hashtable] -and $_.ModuleName -eq 'Rnwood.Dataverse.Data.PowerShell' } |
    Select-Object -First 1
if ($null -eq $requiredDependency -or $requiredDependency.ModuleVersion -ne '3.3.0') {
    throw 'The staged manifest must require Rnwood.Dataverse.Data.PowerShell 3.3.0 or later.'
}

Write-Information "Staged Migration4Dataverse $Version at '$OutputPath'." -InformationAction Continue
Write-Output $OutputPath
