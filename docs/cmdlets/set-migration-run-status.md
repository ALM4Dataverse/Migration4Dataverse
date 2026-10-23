# Set-MigrationRunStatus

Sets the status of a run through the lower-level store interface. The scheduler manages status transitions when using `Invoke-Migration`.

## Parameters

| Parameter | Required | Accepted values | Description |
|---|---:|---|---|
| `-Store` | Yes | — | Store object returned by `New-MigrationJobStore`. |
| `-RunId` | Yes | Non-empty string | Run to update. |
| `-Status` | Yes | `Running`, `Completed`, `Failed`, `Cancelled` | New `MigrationRunStatus` value; parsing is case-insensitive. |

## Example

```powershell
Set-MigrationRunStatus -Store $store -RunId $runId -Status Completed
```
