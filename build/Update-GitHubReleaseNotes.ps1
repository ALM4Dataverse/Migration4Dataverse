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
### Install

```powershell
Install-Module Migration4Dataverse -RequiredVersion __VERSION__ -Scope CurrentUser
```
'@
$installBlock = $installTemplate.Replace('__VERSION__', $Version)

if ($body -notmatch [regex]::Escape("Migration4Dataverse -RequiredVersion $Version")) {
    $body = if ([string]::IsNullOrWhiteSpace($body)) {
        $installBlock
    }
    else {
        "$body`n`n---`n`n$installBlock"
    }

    $notesPath = Join-Path ([System.IO.Path]::GetTempPath()) ("migration4dataverse-release-{0}.md" -f [guid]::NewGuid())
    try {
        Set-Content -LiteralPath $notesPath -Value $body -NoNewline -Encoding utf8
        & gh release edit $Tag --notes-file $notesPath
        if ($LASTEXITCODE -ne 0) {
            throw "Could not update the GitHub Release '$Tag'."
        }
    }
    finally {
        Remove-Item -LiteralPath $notesPath -Force -ErrorAction SilentlyContinue
    }
}

Write-Information "Updated GitHub Release '$Tag' with installation instructions for $Version." -InformationAction Continue
