[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [switch]$SkipBuild,

    [switch]$SkipImport,

    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repositoryRoot 'Migration4Dataverse.slnx'

if (-not $SkipBuild) {
    & dotnet build $solutionPath --configuration Release | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Building '$solutionPath' failed with exit code $LASTEXITCODE."
    }
}

$stageScriptPath = Join-Path $PSScriptRoot 'Stage-MigrationModule.ps1'
$stagedModulePath = & $stageScriptPath -Version $Version

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot 'artifacts/packages'
}
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $OutputPath) {
    Remove-Item -LiteralPath $OutputPath -Recurse -Force
}
$null = New-Item -ItemType Directory -Path $OutputPath -Force

if (-not (Get-Command Publish-PSResource -ErrorAction SilentlyContinue)) {
    throw 'Publish-PSResource is required. Install the Microsoft.PowerShell.PSResourceGet module.'
}

$repositoryName = 'M4D-{0}' -f [guid]::NewGuid().ToString('N')
$null = Register-PSResourceRepository -Name $repositoryName -Uri $OutputPath -Trusted -Force
try {
    $null = Publish-PSResource -Path $stagedModulePath -Repository $repositoryName -SkipDependenciesCheck -ErrorAction Stop
}
finally {
    $null = Unregister-PSResourceRepository -Name $repositoryName -ErrorAction SilentlyContinue
}

$package = Get-ChildItem -LiteralPath $OutputPath -Filter 'Migration4Dataverse.*.nupkg' -File |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if ($null -eq $package) {
    throw "No Migration4Dataverse nupkg was created in '$OutputPath'."
}

$maximumPackageBytes = 240000000
if ($package.Length -gt $maximumPackageBytes) {
    throw "The package is $($package.Length) bytes, above the $maximumPackageBytes-byte guard."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $entryNames = @($archive.Entries | ForEach-Object FullName)
    $requiredEntries = @(
        'Migration4Dataverse.psd1'
        'Migration4Dataverse.psm1'
        'LICENSE'
        'lib/net10.0/Migration4Dataverse.Engine.dll'
        'lib/net10.0/runtimes/linux-x64/native/libduckdb.so'
        'lib/net10.0/runtimes/linux-arm64/native/libduckdb.so'
        'lib/net10.0/runtimes/osx/native/libduckdb.dylib'
        'lib/net10.0/runtimes/win-x64/native/duckdb.dll'
        'lib/net10.0/runtimes/win-arm64/native/duckdb.dll'
    )

    foreach ($requiredEntry in $requiredEntries) {
        if ($entryNames -notcontains $requiredEntry) {
            throw "The package does not contain the required entry '$requiredEntry'."
        }
    }

    $nuspecEntry = $archive.GetEntry('Migration4Dataverse.nuspec')
    if ($null -eq $nuspecEntry) {
        throw 'The package does not contain Migration4Dataverse.nuspec.'
    }

    $reader = [System.IO.StreamReader]::new($nuspecEntry.Open())
    try {
        $nuspec = $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
    }

    if ($nuspec -notmatch '<dependency id="Rnwood\.Dataverse\.Data\.PowerShell" version="3\.3\.0"') {
        throw 'The package does not declare Rnwood.Dataverse.Data.PowerShell 3.3.0 as a dependency.'
    }
}
finally {
    $archive.Dispose()
}

if (-not $SkipImport) {
    $dependency = Get-Module -ListAvailable -Name 'Rnwood.Dataverse.Data.PowerShell' |
        Sort-Object Version -Descending |
        Select-Object -First 1
    if ($null -eq $dependency -or $dependency.Version -lt [version]'3.3.0') {
        throw 'Rnwood.Dataverse.Data.PowerShell 3.3.0 or later is required for the package import test.'
    }

    $stagedManifestPath = Join-Path $stagedModulePath 'Migration4Dataverse.psd1'
    $null = Test-ModuleManifest -Path $stagedManifestPath -ErrorAction Stop
    Import-Module -Name $stagedManifestPath -Force -ErrorAction Stop

    try {
        $expectedCommands = @(
            'Invoke-M4dMigration'
            'Get-M4dMigrationRun'
            'Get-M4dMigrationStep'
            'Get-M4dMigrationBatch'
            'Get-M4dMigrationError'
            'Get-M4dMigrationRetryHistory'
            'Get-M4dMigrationDebugRow'
            'Get-M4dMigrationDebugRowBinary'
            'Get-M4dMigrationConfigurationPreview'
            'Get-M4dMigrationProcessMetric'
            'Get-M4dMigrationDataverseConnectionThroughput'
            'Invoke-M4dMigrationRecovery'
            'Clear-M4dMigrationTargetTuning'
            'Start-M4dMigrationDatabaseServer'
            'New-M4dMigrationBinaryValue'
            'New-M4dMigrationJobStore'
            'Complete-M4dMigrationBatch'
            'Set-M4dMigrationRunStatus'
            'Start-M4dMigrationDashboard'
            'Stop-M4dMigrationDashboard'
        )
        $moduleCommands = @(Get-Command -Module Migration4Dataverse)
        foreach ($commandName in $expectedCommands) {
            if ($commandName -notin $moduleCommands.Name) {
                throw "Imported module does not expose '$commandName'."
            }
        }

        $legacyCommands = @(
            'Invoke-Migration'
            'Get-MigrationRun'
            'Get-MigrationStep'
            'Get-MigrationBatch'
            'Get-MigrationError'
            'Get-MigrationRetryHistory'
            'Get-MigrationDebugRow'
            'Get-MigrationDebugRowBinary'
            'Get-MigrationConfigurationPreview'
            'Get-MigrationProcessMetric'
            'Get-MigrationDataverseConnectionThroughput'
            'Invoke-MigrationRecovery'
            'Clear-MigrationTargetTuning'
            'Start-MigrationDatabaseServer'
            'New-MigrationBinaryValue'
            'New-MigrationJobStore'
            'Complete-MigrationBatch'
            'Set-MigrationRunStatus'
            'Start-MigrationDashboard'
            'Stop-MigrationDashboard'
        )
        foreach ($commandName in $legacyCommands) {
            if ($commandName -in $moduleCommands.Name) {
                throw "Imported module still exposes legacy command '$commandName'."
            }
        }

        $loadedModule = Get-Module -Name Migration4Dataverse
        if ($null -eq $loadedModule -or $loadedModule.Version -ne [version]$Version) {
            throw "Imported module version does not match '$Version'."
        }
    }
    finally {
        Remove-Module -Name Migration4Dataverse -Force -ErrorAction SilentlyContinue
    }
}

Write-Information "Validated Migration4Dataverse $Version package at '$($package.FullName)' ($($package.Length) bytes)." -InformationAction Continue
Write-Output $package.FullName
