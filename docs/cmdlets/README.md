# PowerShell cmdlets and functions

Use `Invoke-Migration` to run a migration. The remaining commands inspect runs, start the dashboard or database owner, manage tuning, or expose lower-level store operations.

## Run and manage migrations

| Command | Kind | Reference |
|---|---|---|
| `Invoke-Migration` | Binary cmdlet | [Invoke-Migration](./invoke-migration.md) |
| `Get-MigrationRun` | Binary cmdlet | [Get-MigrationRun](./get-migration-run.md) |
| `Clear-MigrationTargetTuning` | Binary cmdlet | [Clear-MigrationTargetTuning](./clear-migration-target-tuning.md) |
| `New-MigrationDatabaseConnection` | Binary cmdlet | [New-MigrationDatabaseConnection](./new-migration-database-connection.md) |
| `Start-MigrationDatabaseServer` | Binary cmdlet | [Start-MigrationDatabaseServer](./migration-database-server.md) |
| `Start-MigrationDashboard` | Function | [Migration dashboard](./migration-dashboard.md) |
| `Stop-MigrationDashboard` | Function | [Migration dashboard](./migration-dashboard.md) |

## Lower-level store functions

These functions are exported for integrations that work directly with the migration store. `Invoke-Migration` manages the store itself, so ordinary migrations do not need these commands.

| Command | Reference |
|---|---|
| `New-MigrationJobStore` | [New-MigrationJobStore](./new-migration-job-store.md) |
| `Complete-MigrationBatch` | [Complete-MigrationBatch](./complete-migration-batch.md) |
| `Set-MigrationRunStatus` | [Set-MigrationRunStatus](./set-migration-run-status.md) |
