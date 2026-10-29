# Clear-MigrationTargetTuning

Removes legacy Dataverse tuning profiles from a migration SQLite database. Current migrations no longer read or write these values; the cmdlet is retained to clean up profiles created by older versions.

## Parameters

| Parameter | Required | Description |
|---|---:|---|
| `-DatabasePath` | Yes | Existing migration SQLite database. A missing file is left untouched and returns `0`. |
| `-StepId` | One of `-StepId` or `-All` | Clear the legacy profile for this exact step ID. |
| `-All` | One of `-StepId` or `-All` | Clear every legacy Dataverse target tuning profile in the database. |

The cmdlet supports `-WhatIf` and `-Confirm`. It outputs the number of deleted profiles.

```powershell
# Clear one target's legacy profile.
Clear-MigrationTargetTuning -DatabasePath './state/migration.sqlite3' -StepId 'AccountsTarget'

# Clear all legacy profiles.
Clear-MigrationTargetTuning -DatabasePath './state/migration.sqlite3' -All
```
