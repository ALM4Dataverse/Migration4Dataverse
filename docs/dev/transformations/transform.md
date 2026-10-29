# Transform

The `transform` node runs a PowerShell script for each input row and emits each object written to the script's success output. It requires exactly one input. Apart from the required `Script`, it has no additional optional node settings.

```powershell
$node = @{
    # Trims each customer's name and emits the normalized row.
    Id = 'normalize-name'
    Type = 'transform'
    Inputs = @('records')
    Script = {
        param($row)
        @{
            Id = $row.Id
            Name = ([string]$row.Name).Trim()
        }
    }
}
```

The script receives the input row as its first argument. It may emit zero rows to drop that input, one row to replace it, or multiple rows to expand it. Each emitted value must serialize as a JSON object. Script blocks are executed as source text in an engine-owned runspace, so caller closure state is not captured. Script failures are reported with the node ID.

Transform is a standalone node; it is not an optional property on another step. The graph still allows at most one consumer per node. See the [pipeline reference](./README.md) for configuration and graph constraints.

## Database-backed migration graph transforms

The separate `Invoke-Migration` graph supports SQLite and DuckDB transforms. The engine creates a database file beside `-DatabasePath`, keyed by provider, run ID, and transform step ID. The same run/step reuses its stable file when resumed; distinct runs and steps get distinct files. Do not provide a `ConnectionId` for a SQLite or DuckDB Transform. `Transform.InitializeSql` runs when the graph is created, before any records or batches are processed, and can create lookup or configuration tables used by the transform. It runs again when a compatible run is resumed, so make it idempotent. An inline SQLite `Script` receives the current row and its engine-managed database connection:

```powershell
@{
    # Looks up a normalized display name using the SQLite connection.
    Id = 'NormalizeWithLookup'
    Type = 'transform'
    Inputs = @('Records')
    Transform = @{ Type = 'sqlite' }
    Script = {
        param($row, $connection)
        $command = $connection.CreateCommand()
        try {
            $command.CommandText = 'SELECT DisplayName FROM names WHERE SourceId = @id'
            $null = $command.Parameters.AddWithValue('@id', $row.Id)
            $row.Name = [string]$command.ExecuteScalar()
        }
        finally {
            $command.Dispose()
        }
        $row
    }
}
```

DuckDB transforms can use the same connection-aware PowerShell `Script` contract, or configure `Transform.Sql` with an ordered `Transform.Parameters` hashtable to execute a prepared statement once per input row. DuckDB statements use positional `?` placeholders; parameter values bind to row fields in hashtable order. Statements returning rows emit those rows; side-effect statements with no result set pass through their input row.

A Dataverse Transform uses `Transform = @{ Type = 'dataverse'; ConnectionId = 'crm' }`. Its `Script` receives `($row, $connection)` and can invoke `Invoke-DataverseSql -Connection $connection -Sql ... -Parameters ...` for Cds4Sql queries or updates. Cds4Sql is executed by the module; the Transform script determines which values are emitted downstream.

## SQLite and DuckDB set-based SQL transforms

SQLite and DuckDB support per-batch and whole-dataset SQL modes. Both execute one read-only `SELECT` or `WITH` statement and preserve output lineage. `InitializeSql` runs before any batch so the database can contain helper tables or other setup state. `Transform.CommandTimeoutSeconds` controls engine-run initialization and SQL commands (default `30`; `0` disables the timeout).

### Per-batch

`Mode = 'per_batch'` stages the input rows in the managed database's temporary table `__migration4dataverse_input`, executes `Transform.Sql`, then removes the temporary table. Include exactly one lineage column in the result: `__migration_parent_index` for one parent row or `__migration_parent_indices` containing a JSON array of parent row indexes. The engine strips that metadata before passing records downstream.

```powershell
Transform = @{
    Type = 'sqlite'
    Mode = 'per_batch'
    InitializeSql = @'
CREATE TABLE IF NOT EXISTS batch_rules (minimum_length INTEGER NOT NULL);
DELETE FROM batch_rules;
INSERT INTO batch_rules VALUES (4);
'@
    Sql = @'
SELECT __migration_parent_index, Id, upper(Name) AS Name
FROM __migration4dataverse_input
WHERE length(Name) >= (SELECT minimum_length FROM batch_rules)
ORDER BY Id
'@
}
```

### Whole-dataset

`Mode = 'whole_dataset'` uses one matching SQLite or DuckDB Target step as an explicit staging barrier. Omit `ConnectionId` and `Path` from the staging Target and `ConnectionId` from the Transform: the engine creates one managed database shared by those two steps. The target writes the completed dataset to a managed table; after the target finishes, the transform streams the configured query as a source. `Transform.Key` names the stable, ordered result key. This mode reads query results from the database rather than consuming the target's batch payloads, so the SQL must query the staged table(s) itself. `Transform.CountQuery` is not supported; provide `Transform.ExpectedRecordCount` if an exact count is known.

```powershell
@{
    Id = 'StagingTarget'
    Type = 'target'
    Inputs = @('NormalizedRows')
    Target = @{
        Type = 'sqlite'
        Table = 'staged_records'
        Options = @{
            CreateTable = $true
            Schema = [ordered]@{ Id = 'TEXT'; Name = 'TEXT' }
            KeyColumns = @('Id')
        }
    }
}
@{
    Id = 'WholeDatasetTransform'
    Type = 'transform'
    Inputs = @('StagingTarget')
    Transform = @{
        Type = 'sqlite'
        Mode = 'whole_dataset'
        Key = 'Id'
        Sql = 'SELECT Id, upper(Name) AS Name FROM staged_records ORDER BY Id'
    }
}
```

For per-batch and whole-dataset DuckDB SQL, `Transform.Parameters` is an ordered hashtable whose values bind to positional `?` placeholders. The per-batch temporary input table contains the current batch and its `__migration_parent_index`. Per-row DuckDB SQL instead maps those ordered parameters to input row fields. See the [DuckDB example](../../../examples/duckdb/example.ps1) for all three modes and setup SQL.

SQLite source queries and whole-dataset transform queries are restricted to one read-only `SELECT` or `WITH` statement. SQLite `query_only` mode also guards query execution and is restored when reading completes. The connector guides describe source [parameter and ordering requirements](../../connectors/sources/sqlite.md) and target [schema, key, and index management](../../connectors/targets/sqlite.md). The complete migration-step contract is in [configuration](../../configuration.md#transform-and-filter-semantics).
