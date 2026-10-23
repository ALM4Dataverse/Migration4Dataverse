# Cmdlets and functions

The PowerShell module exports two binary cmdlets and five module functions:

| Command | Kind | Reference |
|---|---|---|
| `Invoke-Migration` | Binary cmdlet | [Invoke-Migration](./invoke-migration.md) |
| `Get-MigrationRun` | Binary cmdlet | [Get-MigrationRun](./get-migration-run.md) |
| `New-MigrationJobStore` | Function | [New-MigrationJobStore](./new-migration-job-store.md) |
| `Complete-MigrationBatch` | Function | [Complete-MigrationBatch](./complete-migration-batch.md) |
| `Set-MigrationRunStatus` | Function | [Set-MigrationRunStatus](./set-migration-run-status.md) |
| `Start-MigrationDashboard` | Function | [Migration dashboard](./migration-dashboard.md) |
| `Stop-MigrationDashboard` | Function | [Migration dashboard](./migration-dashboard.md) |

`Invoke-Migration` is the generic orchestrator for typed connector steps or callback steps. `Get-MigrationRun` lists or inspects persisted execution history. The functions expose dashboard and lower-level store operations.
