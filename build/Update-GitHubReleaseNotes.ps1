[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$Tag
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'The GitHub CLI (gh) is required to update release notes.'
}

$body = (& gh release view $Tag --json body --jq '.body')
if ($LASTEXITCODE -ne 0) {
    throw "Could not read the GitHub Release '$Tag'."
}
$body = ($body -join "`n").TrimEnd()

$installTemplate = @'
<!-- Migration4Dataverse installation instructions: begin -->
### Recommended: self-contained CLI (zero prerequisites)

Download the executable for Windows x64/ARM64, Linux x64/ARM64, or macOS ARM64 from the [Migration4Dataverse __VERSION__ release](https://github.com/rnwood/Migration4Dataverse/releases/tag/__TAG__). The CLI bundles its .NET and PowerShell runtimes and required modules; no .NET, PowerShell, or module installation is needed on the target machine.

```sh
m4d --help
m4d run ./migration-job.m4d
```

### PowerShell module (advanced use)

This option requires PowerShell 7.6 or later, which runs on the .NET 10 runtime. Install the module from the PowerShell Gallery:

```powershell
Install-Module Migration4Dataverse -RequiredVersion __VERSION__ -Scope CurrentUser
Invoke-M4dMigration -Path ./migration-job.m4d
```

The Gallery installation resolves the Dataverse PowerShell module dependency automatically.
<!-- Migration4Dataverse installation instructions: end -->
'@
$installBlock = $installTemplate.Replace('__VERSION__', $Version).Replace('__TAG__', $Tag).TrimEnd()

$updatedBody = $body
$managedBlockPattern = '(?s)<!-- Migration4Dataverse installation instructions: begin -->.*?<!-- Migration4Dataverse installation instructions: end -->'
if ($updatedBody -match $managedBlockPattern) {
    $updatedBody = [regex]::Replace(
        $updatedBody,
        $managedBlockPattern,
        [System.Text.RegularExpressions.MatchEvaluator]{ param($match) $installBlock })
}
else {
    $legacyBlockPattern = '(?s)\r?\n---\r?\n\r?\n### Install\r?\n\r?\n```powershell\r?\nInstall-Module Migration4Dataverse -RequiredVersion \d+\.\d+\.\d+ -Scope CurrentUser\r?\n```\s*$'
    $updatedBody = [regex]::Replace($updatedBody, $legacyBlockPattern, '')
    $updatedBody = if ([string]::IsNullOrWhiteSpace($updatedBody)) {
        $installBlock
    }
    else {
        "$($updatedBody.TrimEnd())`n`n---`n`n$installBlock"
    }
}

if (-not [string]::Equals($updatedBody, $body, [StringComparison]::Ordinal)) {
    $notesPath = Join-Path ([System.IO.Path]::GetTempPath()) ("migration4dataverse-release-{0}.md" -f [guid]::NewGuid())
    try {
        Set-Content -LiteralPath $notesPath -Value $updatedBody -NoNewline -Encoding utf8
        & gh release edit $Tag --notes-file $notesPath
        if ($LASTEXITCODE -ne 0) {
            throw "Could not update the GitHub Release '$Tag'."
        }
    }
    finally {
        Remove-Item -LiteralPath $notesPath -Force -ErrorAction SilentlyContinue
    }
}

Write-Information "GitHub Release '$Tag' has CLI and PowerShell installation instructions for $Version." -InformationAction Continue
