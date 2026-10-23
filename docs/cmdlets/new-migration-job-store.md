# New-MigrationJobStore

Creates a `Migration4Dataverse.Engine.Storage.SqliteJobStore` instance for lower-level store operations.

## Parameters

| Parameter | Required | Description |
|---|---:|---|
| `-DatabasePath` | Yes | Non-empty path to a file-backed SQLite database. |

## Example

```powershell
$store = New-MigrationJobStore -DatabasePath './state/migration.sqlite3'
```

The store resolves the path, creates its parent directory when needed, and initializes its SQLite connection and schema. Use this function only when directly integrating with the lower-level store APIs; `Invoke-Migration` creates and manages its own store.
