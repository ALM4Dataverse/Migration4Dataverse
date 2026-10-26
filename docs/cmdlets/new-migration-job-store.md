# New-MigrationJobStore

Opens a migration database store for advanced PowerShell integrations. Ordinary migrations should use `Invoke-Migration`, which manages the store automatically.

## Parameters

| Parameter | Required | Description |
|---|---:|---|
| `-DatabasePath` | Yes | Non-empty path to a file-backed SQLite database. |

## Example

```powershell
$store = New-MigrationJobStore -DatabasePath './state/migration.sqlite3'
```

The store resolves the path, creates its parent directory when needed, and initializes the SQLite schema through the database owner. The returned object is an implementation-level handle; use it only with the documented lower-level functions such as [`Complete-MigrationBatch`](./complete-migration-batch.md) and [`Set-MigrationRunStatus`](./set-migration-run-status.md).
