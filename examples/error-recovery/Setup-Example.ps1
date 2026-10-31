Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$dataDirectory = Join-Path $PSScriptRoot 'generated-data'
$outputDirectory = Join-Path $dataDirectory 'output'
$null = [System.IO.Directory]::CreateDirectory($outputDirectory)

$rows = @(
    '{"SourceId":"callback-row","RecordId":"00000000-0000-0000-0000-000000000004","Name":"callback-error"}'
    '{"SourceId":"filter-row","RecordId":"00000000-0000-0000-0000-000000000002","Name":"filter-error"}'
    '{"SourceId":"survivor-row","RecordId":"00000000-0000-0000-0000-000000000005","Name":"survivor"}'
    '{"SourceId":"target-row","RecordId":"00000000-0000-0000-0000-000000000003","Name":"target-error"}'
    '{"SourceId":"transform-row","RecordId":"00000000-0000-0000-0000-000000000001","Name":"transform-error"}'
)
[System.IO.File]::WriteAllLines(
    (Join-Path $dataDirectory 'rows.jsonl'),
    $rows,
    [System.Text.UTF8Encoding]::new($false))

Write-Host "Created the local error-recovery dataset at '$dataDirectory'."
