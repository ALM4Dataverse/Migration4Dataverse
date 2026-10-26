# Migration step configuration

`Invoke-Migration -Steps` accepts PowerShell objects; native hashtables are the simplest way to define a migration. The definitions are not a standalone JSON or SSIS package. A migration is a directed acyclic graph whose nodes read, transform, route, or write rows.

> [!NOTE]
> Put live connection objects in `-Connections`, not in the step definitions. This keeps credentials and runtime objects separate from the configuration used for run history and resume checks.

## A small migration

```powershell
$steps = @(
    @{
        Id = 'AccountsSource'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'csv'
            ConnectionId = 'files'
            Path = 'accounts.csv'
            Key = 'SourceId'
        }
    }
    @{
        Id = 'MapAccounts'
        Type = 'transform'
        Inputs = @('AccountsSource')
        Script = {
            param($row)
            @{
                new_sourceid = [string]$row.SourceId
                name = ([string]$row.Name).Trim()
            }
        }
    }
    @{
        Id = 'AccountsTarget'
        Type = 'target'
        Inputs = @('MapAccounts')
        Target = @{
            Type = 'dataverse'
            ConnectionId = 'crm'
            Table = 'account'
            Operation = 'Upsert'
            Options = @{ MatchOn = ,@('new_sourceid') }
        }
    }
)

$connections = @{
    files = [System.IO.DirectoryInfo]'/data/import'
    crm = $dataverseConnection
}

Invoke-Migration -DatabasePath './migration.sqlite3' `
    -RunName 'Account import' -Steps $steps -Connections $connections
```

## Node contract

Each node has a unique `Id`, a `Type`, and an `Inputs` array. `Inputs` names the node or nodes that provide rows; it does not automatically join multiple inputs. A graph must be acyclic, contain a root source or root callback, and route its work to a target or terminal callback.

| Type | Inputs | Additional configuration |
|---|---|---|
| `source` | None for a root reader, or keyed source inputs | `Source`; optionally `DependsOn` for completion-only barriers. |
| `transform` | Exactly one | Inline `Script`, or a supported database-backed `Transform`. |
| `filter` | Exactly one | Inline `Script`; truthy output retains the row. |
| `append` | Two or more | Concatenates the input streams without matching or deduplicating rows. |
| `join` | Exactly two, ordered child/left then parent/right | `JoinType`, aliases, and ordered key pairs. |
| `target` | Exactly one | `Target`. A target can feed a `join`, not an ordinary transform. |
| `callback` | Root or one or more | Root callbacks also define `ReadBatches`; every callback defines `ExecuteBatch`. |

Every referenced step ID must exist. A node may have multiple consumers. `sort` and `merge` are not supported `Invoke-Migration` step types and are rejected during configuration validation.

## Source dependencies

`Inputs` declares row flow. Use a keyed source input when a source row refers to one or more parent **source** rows. Use a `join` node to combine a child stream with a source or Dataverse target stream. Use `DependsOn` only when a source or join must wait for another step to finish without receiving its rows.

```powershell
Inputs = @(
    @{
        Job = 'OrdersSource'
        Keys = @{ Child = 'OrderId'; Parent = 'OrderId' }
        RowsPerParent = 2
    }
)
```

The full rules for null keys, unmatched parents, fixed child counts, join ordering, and completion barriers are in the [dependency guide](./concepts/dependencies.md).

## Transform and Filter semantics

- A Transform script receives one input row as `$row` and may emit zero, one, or multiple record objects. No output drops the row; multiple outputs expand it.
- Unless the script supplies an `Id`, each Transform output keeps the input `Id`. A source `Id` property initializes that value when present.
- A Filter script uses PowerShell output truthiness. A truthy result passes the original row unchanged; no output or a falsey result drops it.
- Scripts run as source text in engine-owned runspaces. They do not capture variables from the caller's interactive scope.

A connected Transform can use a `Transform` map whose `Type` is `sqlite`, `duckdb`, or `dataverse` and whose `ConnectionId` names the connection. Its PowerShell script receives `($row, $connection)` and runs once per row. DuckDB can instead use a prepared `Transform.Sql` statement with ordered `Transform.Parameters`; the statement's result rows become Transform output, while a statement without a result set passes through the input row. Per-batch and whole-dataset database transforms are not supported.

## Source properties

Every source has a `Source` hashtable. See the [source connector index](./connectors/sources/README.md) for connector-specific settings.

| Property | Required | Description |
|---|---:|---|
| `Type` | Yes | `jsonl`, `csv`, `parquet`, `sqlite`, `duckdb`, `dataverse`, or `custom`. |
| `ConnectionId` | Yes | Key in `-Connections`. File connectors expect a `System.IO.DirectoryInfo`; database connectors use an ADO.NET connection. |
| `Path` | File sources | Path relative to the source directory. Database and Dataverse sources omit it. |
| `Key` | Yes | Stable field used to order and checkpoint source rows. Root source keys must be non-empty, unique, and strictly increasing. Dataverse uses case-insensitive ordinal order. |
| `Id` | No | Source field copied to the row's `Id`, commonly the target record GUID. |
| `ExpectedRecordCount` | No | Positive expected row count. It takes precedence over a connector's count provider and is checked when reading completes. |
| `Options` | No | Connector-specific options. See the linked source guide. |
| `Custom` | Custom source | Callback definition; see [custom sources](./connectors/sources/custom.md). |

For row-linked child sources, rows are staged and ordered by their parent relationships; the source file itself need not be sorted by those parent keys. Parent source keys must be unique. See [recovery and replay](./concepts/recovery-and-replay.md) for stable-source requirements.

## Target properties

Every target has a `Target` hashtable. See the [target connector index](./connectors/targets/README.md) for connector-specific settings.

| Property | Required | Description |
|---|---:|---|
| `Type` | Yes | `dataverse`, `jsonl`, `csv`, `parquet`, `sqlite`, `duckdb`, or `custom`. |
| `ConnectionId` | Yes | Key in `-Connections`. File targets expect a `System.IO.DirectoryInfo`; database targets use an ADO.NET connection. |
| `Path` | File targets | Output path relative to the target directory. |
| `Table` | Dataverse and database targets | Dataverse logical table or database table name. |
| `Operation` | No | Defaults to `Upsert` for Dataverse and `Write` for other targets. |
| `BatchSize` | No | Positive override for `Invoke-Migration -BatchSize`; Dataverse also accepts `auto`. |
| `MaxConcurrency` | Dataverse only | Positive per-target limit or `auto`; the default is `auto`. |
| `Options` | No | Connector-specific settings, such as database schemas or Dataverse matching keys. |
| `Custom` | Custom target | Callback definition; see [custom targets](./connectors/targets/custom.md). |

## Callback nodes

Callbacks expose the batch protocol for integrations that need a custom source reader or batch handler. A root callback defines `ReadBatches`; a dependent callback defines one or more `Inputs` and only `ExecuteBatch`.

`ReadBatches` receives a `MigrationStepCheckpoint`, the resolved source connection, and a cursor. It returns at most one `MigrationWorkBatch` per call. Non-final batches must advance `CursorAfterJson`; the final batch sets `IsLast`. `ExecuteBatch` receives a leased `StoredBatch` and emits exactly one `MigrationBatchResult`.

A callback can return `MigrationDependencyOutput` values with `RequiredBatchIds` to wait for additional parent batches. This creates an explicit batch prerequisite; it does not join records. See the [dependency guide](./concepts/dependencies.md#callback-multi-parent-prerequisites).

## Connection map and history

Connection IDs resolve against the `-Connections` map. Reusing an ID shares the same runtime object. The caller owns connection creation, credentials, lifetime, and disposal. File paths are resolved below their `DirectoryInfo` connection roots.

The store records a sanitized snapshot of the graph and safe connection properties for run history and configuration previews. Redaction is best-effort; do not put credentials or secrets in step definitions. See [`Get-MigrationRun`](./cmdlets/get-migration-run.md), the [dashboard guide](./cmdlets/migration-dashboard.md), and [connections and callbacks](./concepts/connections-and-callbacks.md).
