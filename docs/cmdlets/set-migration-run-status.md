# Set-M4dMigrationRunStatus

Sets the status of a run through the lower-level store interface. The scheduler manages status transitions when using `Invoke-M4dMigration`.

## Parameters

| Parameter | Required | Accepted values | Description |
|---|---:|---|---|
| `-Store` | Yes | — | Store object returned by `New-M4dMigrationJobStore`. |
| `-RunId` | Yes | Non-empty string | Run to update. |
| `-Status` | Yes | `Running`, `Completed`, `Failed`, `Cancelled` | New `MigrationRunStatus` value; parsing is case-insensitive. |

## Example

```powershell
Set-M4dMigrationRunStatus -Store $store -RunId $runId -Status Completed
```
