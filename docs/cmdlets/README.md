# PowerShell cmdlets and functions

Use `Invoke-Migration` to run a migration. The inspection and recovery commands expose run history, errors, captured rows, and the same recovery decisions as the dashboard.

## Run and manage migrations

| Command | Kind | Reference |
|---|---|---|
| `Invoke-Migration` | Binary cmdlet | [Invoke-Migration](./invoke-migration.md) |
| `Get-MigrationRun` | Binary cmdlet | [Get-MigrationRun](./get-migration-run.md) |
| `Get-MigrationStep`, `Get-MigrationBatch`, `Get-MigrationError`, `Get-MigrationRetryHistory` | Binary cmdlets | [Inspecting runs and recovering from errors](./migration-inspection-and-recovery.md) |
| `Get-MigrationDebugRow`, `Get-MigrationDebugRowBinary` | Binary cmdlets | [Inspecting runs and recovering from errors](./migration-inspection-and-recovery.md) |
| `Get-MigrationConfigurationPreview`, `Get-MigrationProcessMetric`, `Get-MigrationDataverseConnectionThroughput` | Binary cmdlets | [Inspecting runs and recovering from errors](./migration-inspection-and-recovery.md) |
| `Invoke-MigrationRecovery` | Binary cmdlet | [Inspecting runs and recovering from errors](./migration-inspection-and-recovery.md) |
| `Clear-MigrationTargetTuning` | Binary cmdlet | [Clear-MigrationTargetTuning](./clear-migration-target-tuning.md) |
| `Start-MigrationDatabaseServer` | Binary cmdlet | [Start-MigrationDatabaseServer](./migration-database-server.md) |
| `New-MigrationBinaryValue` | Binary cmdlet | [New-MigrationBinaryValue](./new-migration-binary-value.md) |
| `Start-MigrationDashboard` | Function | [Migration dashboard](./migration-dashboard.md) |
| `Stop-MigrationDashboard` | Function | [Migration dashboard](./migration-dashboard.md) |

## Lower-level store functions

These functions are exported for integrations that work directly with the migration store. `Invoke-Migration` manages the store itself, so ordinary migrations do not need these commands.

| Command | Reference |
|---|---|
| `New-MigrationJobStore` | [New-MigrationJobStore](./new-migration-job-store.md) |
| `Complete-MigrationBatch` | [Complete-MigrationBatch](./complete-migration-batch.md) |
| `Set-MigrationRunStatus` | [Set-MigrationRunStatus](./set-migration-run-status.md) |
