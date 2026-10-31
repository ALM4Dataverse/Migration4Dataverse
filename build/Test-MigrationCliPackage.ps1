[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ExecutablePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ExecutablePath = [System.IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
    throw "CLI executable '$ExecutablePath' was not found."
}

function Invoke-Cli {
    param([string[]]$Arguments)

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($ExecutablePath)
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $null = $startInfo.Environment.Remove('PSModulePath')
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Could not start '$ExecutablePath'."
    }
    try {
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(180000)) {
            $process.Kill($true)
            throw "CLI invocation timed out: $($Arguments -join ' ')"
        }
        [pscustomobject]@{
            ExitCode = $process.ExitCode
            StandardOutput = $stdoutTask.GetAwaiter().GetResult()
            StandardError = $stderrTask.GetAwaiter().GetResult()
        }
    }
    finally {
        $process.Dispose()
    }
}

function Assert-JsonEvents {
    param([string]$Output)

    $events = @(
        $Output -split '\r?\n' |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            ForEach-Object { ConvertFrom-Json -InputObject $_ -ErrorAction Stop }
    )
    if ($events.Count -eq 0) {
        throw 'The CLI did not emit any JSON events.'
    }
    $events
}

$noArguments = Invoke-Cli -Arguments @()
if ($noArguments.ExitCode -ne 2 -or
    -not $noArguments.StandardOutput.Contains('run', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The CLI did not show its command help when launched without arguments.'
}

$help = Invoke-Cli -Arguments @('--help')
if ($help.ExitCode -ne 0) {
    throw "CLI help failed with exit code $($help.ExitCode): $($help.StandardError)"
}

$expectedCommands = @(
    'run',
    'get-run',
    'get-step',
    'get-batch',
    'get-error',
    'get-retry-history',
    'get-debug-row',
    'get-debug-row-binary',
    'get-configuration-preview',
    'get-process-metric',
    'get-dataverse-connection-throughput',
    'invoke-recovery',
    'clear-target-tuning',
    'start-database-server',
    'new-binary-value',
    'new-job-store',
    'complete-batch',
    'set-run-status',
    'start-dashboard',
    'stop-dashboard'
)
foreach ($command in $expectedCommands) {
    if (-not $help.StandardOutput.Contains($command, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The CLI help does not expose the exported command '$command'."
    }
}

$runHelp = Invoke-Cli -Arguments @('run', '--help')
if ($runHelp.ExitCode -ne 0 -or
    -not $runHelp.StandardOutput.Contains('Runs a PowerShell-defined migration graph', [StringComparison]::Ordinal)) {
    throw 'The run command did not show the migration cmdlet help synopsis.'
}

$getRunHelp = Invoke-Cli -Arguments @('get-run', '--help')
if ($getRunHelp.ExitCode -ne 0 -or
    -not $getRunHelp.StandardOutput.Contains('SQLite job store used by Invoke-M4dMigration', [StringComparison]::Ordinal)) {
    throw 'The get-run command did not show the PowerShell parameter help description.'
}

$parseFailure = Invoke-Cli -Arguments @(
    'run',
    '--batch-size', 'not-a-number',
    '--json'
)
if ($parseFailure.ExitCode -ne 2) {
    throw "Invalid CLI arguments returned $($parseFailure.ExitCode), expected 2."
}
$parseEvents = Assert-JsonEvents -Output $parseFailure.StandardOutput
if ($parseEvents[-1].type -ne 'error' -or $parseEvents[-1].exitCode -ne 2) {
    throw 'Invalid arguments did not produce the expected JSON error event.'
}

$workDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "m4d-cli-package-test-$([guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $workDirectory
try {
    $inputPath = Join-Path $workDirectory 'input.jsonl'
    @(
        '{"SourceId":"row-1","RecordId":"00000000-0000-0000-0000-000000000001","Name":"first"}'
        '{"SourceId":"row-2","RecordId":"00000000-0000-0000-0000-000000000002","Name":"second"}'
    ) | Set-Content -LiteralPath $inputPath -Encoding utf8NoBOM

    $scriptPath = Join-Path $workDirectory 'job.ps1'
    @'
$Steps = @(
    @{
        Id = 'Rows'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'file'
            Format = @{ Type = 'jsonl' }
            ConnectionId = 'files'
            Path = 'input.jsonl'
            Key = 'SourceId'
            Id = 'RecordId'
        }
    }
    @{
        Id = 'Output'
        Type = 'target'
        Inputs = @('Rows')
        Target = @{
            Type = 'file'
            Format = @{ Type = 'jsonl' }
            ConnectionId = 'target'
            Path = 'output.jsonl'
        }
    }
)
$Connections = @{
    files = [System.IO.DirectoryInfo]$PSScriptRoot
    target = [System.IO.DirectoryInfo]$PSScriptRoot
}
'@ | Set-Content -LiteralPath $scriptPath -Encoding utf8NoBOM

    $databasePath = Join-Path $workDirectory 'migration.sqlite3'
    $migration = Invoke-Cli -Arguments @(
        'run',
        '--script', $scriptPath,
        '--database-path', $databasePath,
        '--run-name', 'CLI package smoke test',
        '--batch-size', '1',
        '--max-degree-of-parallelism', '1',
        '--json'
    )
    if ($migration.ExitCode -ne 0) {
        $lastEvents = @(
            $migration.StandardOutput -split '\r?\n' |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                Select-Object -Last 5
        ) -join [Environment]::NewLine
        throw "The bundled file-to-file migration failed with exit code $($migration.ExitCode). Last events: $lastEvents Error: $($migration.StandardError)"
    }
    $migrationEvents = Assert-JsonEvents -Output $migration.StandardOutput
    if ($migrationEvents[-1].type -ne 'completed' -or $migrationEvents[-1].exitCode -ne 0) {
        throw 'The migration did not emit a successful completion event.'
    }

    $outputPath = Join-Path $workDirectory 'output.jsonl'
    if (-not (Test-Path -LiteralPath $outputPath -PathType Leaf)) {
        throw 'The bundled migration did not create its target file.'
    }
    $rows = @(Get-Content -LiteralPath $outputPath | ForEach-Object { ConvertFrom-Json -InputObject $_ })
    if ($rows.Count -ne 2 -or $rows[0].Name -ne 'first' -or $rows[1].Name -ne 'second') {
        throw 'The bundled migration output did not contain both expected rows.'
    }

    Write-Information 'The self-contained CLI package passed its command, parser, JSON, and local migration smoke tests.' -InformationAction Continue
}
finally {
    Remove-Item -LiteralPath $workDirectory -Recurse -Force
}
