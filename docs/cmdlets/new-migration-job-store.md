# New-MigrationJobStore

Opens an `IMigrationStore` for lower-level store operations. It connects to an existing same-user database owner or starts an implicit owner in the current process.

## Parameters

| Parameter | Required | Description |
|---|---:|---|
| `-DatabasePath` | Yes | Non-empty path to a file-backed SQLite database. |

## Example

```powershell
$store = New-MigrationJobStore -DatabasePath './state/migration.sqlite3'
```

The store resolves the path, creates its parent directory when needed, and initializes the SQLite schema through the owner. Use this function only when directly integrating with the lower-level store APIs; `Invoke-Migration` creates and manages its own store.
