# Transform

The `transform` node runs a PowerShell script for each input row and emits each object written to the script's success output. It requires exactly one input and has no node-specific options.

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

The separate `Invoke-Migration` graph also supports per-row database transforms. A SQLite Transform's inline `Script` receives both the current row and its named database connection:

```powershell
@{
    # Looks up a normalized display name using the SQLite connection.
    Id = 'NormalizeWithLookup'
    Type = 'transform'
    Inputs = @('Records')
    Transform = @{ Type = 'sqlite'; ConnectionId = 'lookup-db' }
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

Database transforms currently run per row; whole-dataset and per-batch transform scopes are deferred. The connector requirements and full option tables are in [migration step configuration](../configuration.md#transform-and-filter-semantics).
