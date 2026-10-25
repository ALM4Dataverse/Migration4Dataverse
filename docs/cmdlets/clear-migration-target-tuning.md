# Clear-MigrationTargetTuning

Removes persisted Dataverse target auto-tuning profiles from a migration SQLite database. Profiles are keyed by target step ID and affect later runs using the same database; clearing a profile does not change a tuner already created by a running migration.

## Parameters

| Parameter | Required | Description |
|---|---:|---|
| `-DatabasePath` | Yes | Existing migration SQLite database. A missing file is left untouched and returns `0`. |
| `-StepId` | One of `-StepId` or `-All` | Clear the profile for this exact step ID. |
| `-All` | One of `-StepId` or `-All` | Clear every Dataverse target tuning profile in the database. |

The cmdlet supports `-WhatIf` and `-Confirm`. It outputs the number of deleted profiles.

```powershell
# Clear one target's learned values.
Clear-MigrationTargetTuning -DatabasePath './state/migration.sqlite3' -StepId 'AccountsTarget'

# Clear all learned values after changing the environment or tuning strategy.
Clear-MigrationTargetTuning -DatabasePath './state/migration.sqlite3' -All
```
