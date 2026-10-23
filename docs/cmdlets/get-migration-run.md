# Get-MigrationRun

Lists persisted migration execution runs or returns one run, including its status, step graph, safe configuration snapshot, and connection instance snapshots.

## Parameters

| Parameter | Required | Default / validation | Description |
|---|---:|---|---|
| `-DatabasePath` | Yes | Non-empty path | SQLite job store used by `Invoke-Migration` and the dashboard. |
| `-RunId` | No | All recent runs | Return one run by its stable run ID. |
| `-Limit` | No | `50`; range `1..1000` | Maximum number of runs when listing history. Ignored when `-RunId` is supplied. |

## Examples

```powershell
Get-MigrationRun -DatabasePath './state/migration.sqlite3'
Get-MigrationRun -DatabasePath './state/migration.sqlite3' -Limit 100
Get-MigrationRun -DatabasePath './state/migration.sqlite3' -RunId $runId
```

The cmdlet emits `MigrationRunSummary` objects. Step summaries include processed, pending, and in-flight input-row counts, filtered rows, uncountable batch counts, and an expected input total when it is final and known. A source's configured or provider-resolved expected count is surfaced as its expected total. Full step and connection properties are included when the run was created by a compatible version of `Invoke-Migration`; common credential and secret property names are redacted. Older runs remain listable but do not gain configuration data that was not persisted at the time. Connection snapshots record safe properties observed at run start and completion, not a live object reference.

`Get-MigrationRun` is read-only. Use [Migration dashboard](./migration-dashboard.md) for the interactive run selector, graph, batch drill-down, and configuration preview.
