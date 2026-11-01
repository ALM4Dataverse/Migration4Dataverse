# PowerShell cmdlets and functions

Use `Invoke-M4dMigration` to run a migration. The inspection and recovery commands expose run history, errors, captured rows, and the same recovery decisions as the dashboard.

## Run and manage migrations

| Command | Kind | Reference |
|---|---|---|
| `Invoke-M4dMigration` | Function | [Invoke-M4dMigration](./invoke-migration.md) |
| `New-M4dMigrationFile` | Function | [New-M4dMigrationFile](./new-migration-file.md) |
| `Get-M4dMigrationComponent` | Binary cmdlet | [Get-M4dMigrationComponent](./get-migration-component.md) |
| `Get-M4dMigrationRun` | Binary cmdlet | [Get-M4dMigrationRun](./get-migration-run.md) |
| `Get-M4dMigrationStep`, `Get-M4dMigrationBatch`, `Get-M4dMigrationError`, `Get-M4dMigrationRetryHistory` | Binary cmdlets | [Inspecting runs and recovering from errors](./migration-inspection-and-recovery.md) |
| `Get-M4dMigrationDebugRow`, `Get-M4dMigrationDebugRowBinary` | Binary cmdlets | [Inspecting runs and recovering from errors](./migration-inspection-and-recovery.md) |
| `Get-M4dMigrationConfigurationPreview`, `Get-M4dMigrationProcessMetric`, `Get-M4dMigrationDataverseConnectionThroughput` | Binary cmdlets | [Inspecting runs and recovering from errors](./migration-inspection-and-recovery.md) |
| `Invoke-M4dMigrationRecovery` | Binary cmdlet | [Inspecting runs and recovering from errors](./migration-inspection-and-recovery.md) |
| `Clear-M4dMigrationTargetTuning` | Binary cmdlet | [Clear-M4dMigrationTargetTuning](./clear-migration-target-tuning.md) |
| `Start-M4dMigrationDatabaseServer` | Binary cmdlet | [Start-M4dMigrationDatabaseServer](./migration-database-server.md) |
| `New-M4dMigrationBinaryValue` | Binary cmdlet | [New-M4dMigrationBinaryValue](./new-migration-binary-value.md) |
| `Start-M4dMigrationDashboard` | Function | [Migration dashboard](./migration-dashboard.md) |
| `Stop-M4dMigrationDashboard` | Function | [Migration dashboard](./migration-dashboard.md) |

## Lower-level store functions

These functions are exported for integrations that work directly with the migration store. `Invoke-M4dMigration` manages the store itself, so ordinary migrations do not need these commands.

| Command | Reference |
|---|---|
| `New-M4dMigrationJobStore` | [New-M4dMigrationJobStore](./new-migration-job-store.md) |
| `Complete-M4dMigrationBatch` | [Complete-M4dMigrationBatch](./complete-migration-batch.md) |
| `Set-M4dMigrationRunStatus` | [Set-M4dMigrationRunStatus](./set-migration-run-status.md) |

`Invoke-M4dMigrationGraph` is also exported for advanced integrations that supply graph objects directly. It exposes the internal graph engine rather than the supported `.m4d` file contract; use [`Invoke-M4dMigration`](./invoke-migration.md) for ordinary migrations. Its CLI action is grouped under `advanced`.
