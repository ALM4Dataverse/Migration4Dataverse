# Start-MigrationDatabaseServer

Starts the local owner for a file-backed migration database and keeps the PowerShell process alive until stopped. The owner is the only Migration4Dataverse process that opens that database; other module processes use same-user local IPC for store operations.

## Parameters

| Parameter | Required | Default / validation | Description |
|---|---:|---|---|
| `-DatabasePath` | Yes | Non-empty path | File-backed SQLite migration database to own. |

```powershell
Start-MigrationDatabaseServer -DatabasePath './state/migration.sqlite3'
```

Start this command in a dedicated PowerShell process and leave it running while clients use the database. Press Ctrl+C only after active runs and other store clients have finished. Stopping the owner while a client is working interrupts its store calls; the run remains recoverable after the owner is restarted.

The owner also starts implicitly when `Invoke-Migration`, `Start-MigrationDashboard`, `Get-MigrationRun`, `Clear-MigrationTargetTuning`, or `New-MigrationJobStore` first opens a database without an existing owner. The implicit owner lives in that caller's process. Starting the explicit server first is recommended when multiple independent processes need a stable owner.

Schedulers, workers, live connection objects, and PowerShell callbacks remain in their caller processes. The owner centralizes SQLite access only, so multiple distinct run IDs can execute concurrently through the same database. A per-run scheduler lease still prevents two schedulers from owning the same run ID.

The IPC endpoint is local and restricted to the current operating-system user. It is not a remote database service and does not provide multi-user authorization.
