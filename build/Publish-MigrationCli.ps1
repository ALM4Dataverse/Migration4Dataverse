[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string[]]$RuntimeIdentifiers = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-arm64'),

    [string]$OutputPath,

    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repositoryRoot 'src/Migration4Dataverse.Cli/Migration4Dataverse.Cli.csproj'
$moduleVersion = '3.3.0'
$moduleName = 'Rnwood.Dataverse.Data.PowerShell'

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot "artifacts/cli/$Version"
}
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)

if (Test-Path -LiteralPath $OutputPath) {
    throw "CLI output directory '$OutputPath' already exists. Choose a new output directory."
}
if ($RuntimeIdentifiers.Count -eq 0) {
    throw 'At least one runtime identifier is required.'
}

$module = Get-Module -ListAvailable -Name $moduleName |
    Where-Object { $_.Version -eq [version]$moduleVersion } |
    Select-Object -First 1
if ($null -eq $module) {
    throw "Install $moduleName $moduleVersion before publishing the CLI."
}

$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) "m4d-cli-$([guid]::NewGuid().ToString('N'))"
$modulesPath = Join-Path $stagingRoot 'modules'
$migrationModulePath = Join-Path $modulesPath "Migration4Dataverse/$Version"
$dataverseModulePath = Join-Path $modulesPath "$moduleName/$moduleVersion"
$dataverseManifestPath = Join-Path $dataverseModulePath "$moduleName.psd1"

try {
    $null = New-Item -ItemType Directory -Path $stagingRoot -Force
    if (-not $SkipBuild) {
        dotnet restore (Join-Path $repositoryRoot 'Migration4Dataverse.slnx') --locked-mode
        if ($LASTEXITCODE -ne 0) {
            throw 'Restoring the solution failed.'
        }
        dotnet build (Join-Path $repositoryRoot 'Migration4Dataverse.slnx') `
            --configuration Release `
            --no-restore
        if ($LASTEXITCODE -ne 0) {
            throw 'Building the solution failed.'
        }
    }

    $null = & (Join-Path $PSScriptRoot 'Stage-MigrationModule.ps1') `
        -Version $Version `
        -OutputPath $migrationModulePath

    $engineDirectory = Join-Path $migrationModulePath 'lib/net10.0'
    $engineAssemblyPath = Join-Path $engineDirectory 'Migration4Dataverse.Engine.dll'
    foreach ($engineFile in Get-ChildItem -LiteralPath $engineDirectory -Force) {
        if ($engineFile.FullName -ne $engineAssemblyPath) {
            Remove-Item -LiteralPath $engineFile.FullName -Recurse -Force
        }
    }

    Copy-Item -LiteralPath $module.ModuleBase -Destination $dataverseModulePath -Recurse
    if (-not (Test-Path -LiteralPath $dataverseManifestPath -PathType Leaf)) {
        throw "The Dataverse module manifest was not found at '$dataverseManifestPath'."
    }
    foreach ($framework in @('net462', 'net9.0')) {
        foreach ($component in @('cmdlets', 'loader')) {
            $unusedFrameworkPath = Join-Path $dataverseModulePath "$component/$framework"
            if (Test-Path -LiteralPath $unusedFrameworkPath) {
                Remove-Item -LiteralPath $unusedFrameworkPath -Recurse -Force
            }
        }
    }
    Get-ChildItem -LiteralPath $dataverseModulePath `
        -Filter 'Microsoft.Extensions.Logging.Abstractions.dll' `
        -File `
        -Recurse |
        Remove-Item -Force

    $bundledDataverseModule = Test-ModuleManifest -Path $dataverseManifestPath -ErrorAction Stop
    if ($bundledDataverseModule.Version -ne [version]$moduleVersion) {
        throw "The staged Dataverse module version '$($bundledDataverseModule.Version)' does not match '$moduleVersion'."
    }

    $null = New-Item -ItemType Directory -Path $OutputPath -Force
    foreach ($runtimeIdentifier in $RuntimeIdentifiers) {
        $publishPath = Join-Path $stagingRoot "publish/$runtimeIdentifier"
        $null = New-Item -ItemType Directory -Path $publishPath -Force
        dotnet publish $projectPath `
            --configuration Release `
            --runtime $runtimeIdentifier `
            --self-contained true `
            --no-restore `
            "-p:CliModulesDirectory=$modulesPath" `
            "-p:Version=$Version" `
            "-p:DataversePowerShellModuleVersion=$moduleVersion" `
            '-p:PublishSingleFile=true' `
            '--output' $publishPath
        if ($LASTEXITCODE -ne 0) {
            throw "Publishing the CLI for '$runtimeIdentifier' failed."
        }

        $executableName = if ($runtimeIdentifier.StartsWith('win-', [StringComparison]::OrdinalIgnoreCase)) {
            'm4d.exe'
        }
        else {
            'm4d'
        }
        $publishedExecutable = Join-Path $publishPath $executableName
        if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
            throw "The published executable '$publishedExecutable' was not found."
        }

        $assetExtension = if ($runtimeIdentifier.StartsWith('win-', [StringComparison]::OrdinalIgnoreCase)) {
            '.exe'
        }
        else {
            ''
        }
        $assetPath = Join-Path $OutputPath "Migration4Dataverse.Cli-$runtimeIdentifier$assetExtension"
        Copy-Item -LiteralPath $publishedExecutable -Destination $assetPath
        if (-not $IsWindows) {
            [System.IO.File]::SetUnixFileMode(
                $assetPath,
                [System.IO.UnixFileMode]::UserRead -bor
                [System.IO.UnixFileMode]::UserWrite -bor
                [System.IO.UnixFileMode]::UserExecute -bor
                [System.IO.UnixFileMode]::GroupRead -bor
                [System.IO.UnixFileMode]::GroupExecute -bor
                [System.IO.UnixFileMode]::OtherRead -bor
                [System.IO.UnixFileMode]::OtherExecute)
        }
        Write-Information "Published $runtimeIdentifier CLI to '$assetPath'." -InformationAction Continue
    }

    if ($RuntimeIdentifiers -contains 'linux-x64') {
        & (Join-Path $PSScriptRoot 'Test-MigrationCliPackage.ps1') `
            -ExecutablePath (Join-Path $OutputPath 'Migration4Dataverse.Cli-linux-x64')
    }

    Write-Output $OutputPath
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
