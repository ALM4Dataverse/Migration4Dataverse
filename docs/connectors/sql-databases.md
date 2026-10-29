# SQL database connectors

The `sqlserver`, `postgresql`, `mysql`, and `oracle` connectors read from and write to relational databases through caller-supplied ADO.NET connections. The engine opens a closed connection while a source reader or target batch is active and closes it again; it does not dispose the caller's connection. Target writes for the same connection are serialized.

Put the live connection in `-Connections`; never put credentials or connection strings in the step definition.

```powershell
$connections = @{
    sourceDb = [Microsoft.Data.SqlClient.SqlConnection]::new($env:SOURCE_SQL_CONNECTION)
    targetDb = [Microsoft.Data.SqlClient.SqlConnection]::new($env:TARGET_SQL_CONNECTION)
}
```

## Source

A database source requires `Options.Query`, one read-only `SELECT` or `WITH` statement. Column names become record field names. `Source.Key` must identify a column in the result and the query should order by that key for deterministic resume.

| Option | Default | Description |
|---|---:|---|
| `Query` | Required | One read-only `SELECT` or `WITH` statement. |
| `Parameters` | Empty | Named values bound to query placeholders. |
| `CountQuery` | `null` | Optional scalar count query using the same parameters. |
| `CommandTimeoutSeconds` | `30` | ADO.NET command timeout; `0` disables the timeout. |

When no positive `ExpectedRecordCount` is set, the engine normally derives progress from `SELECT COUNT(*) FROM (<Query>) AS __migration_count`. SQL Server sources without an explicit `CountQuery` skip this provider; SQL Server queries with `ORDER BY` cannot be wrapped, so set `CountQuery` or `ExpectedRecordCount` when you need a total. Oracle uses an unaliased derived table because Oracle does not accept `AS` for a table alias.

Parameter placeholders depend on the provider:

| Type | Placeholder | Example |
|---|---|---|
| `sqlserver` | `@name` | `WHERE id >= @minimumId` |
| `postgresql` | `@name` | `WHERE id >= @minimumId` |
| `mysql` | `@name` | `WHERE id >= @minimumId` |
| `oracle` | `:name` | `WHERE id >= :minimumId` |

## Target

A database target requires `Target.Table`. `Target.Operation` is accepted for compatibility but database targets always use their configured key behavior; `KeyColumns` enables upsert, and no keys means insert-only.

| Option | Default | Description |
|---|---:|---|
| `Schema` | `null` | Optional ordered column-to-SQL-type map. If omitted and the table exists, the engine reads the actual schema. If the table is missing, the schema is inferred from the first batch. |
| `KeyColumns` | Empty | Ordered columns used for update/upsert. With keys, an existing table should have a matching unique key. |
| `CreateTable` | `true` for SQL Server, PostgreSQL, MySQL, and Oracle | When `true`, create a missing table, including a primary key from `KeyColumns`. When `false`, a missing table fails. |
| `Indexes` | Empty | Supported only for SQLite; other database targets reject index declarations. |
| `CommandTimeoutSeconds` | `30` | ADO.NET command timeout; `0` disables the timeout. |

If `Schema` is supplied, existing table columns and canonical provider types are validated before writing. When `Schema` is omitted and `CreateTable` is `true`, creation is deferred until the first non-empty batch so the engine can infer types. Existing objects are never altered or dropped.

Once the target and marker tables exist, each batch's records and its run/step/batch content-hash marker commit in one transaction. The first batch may create tables separately because MySQL and Oracle commit DDL implicitly. An identical retry is ignored; the same batch identity with different records fails. The engine reserves the internal marker table `__migration4dataverse_batches` (Oracle uppercases quoted identifiers).

```powershell
$steps = @(
    @{
        Id = 'Records'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'postgresql'
            ConnectionId = 'sourceDb'
            Key = 'id'
            Options = @{
                Query = 'SELECT id, name FROM records ORDER BY id'
                Parameters = @{ }
            }
        }
    }
    @{
        Id = 'RecordsTarget'
        Type = 'target'
        Inputs = @('Records')
        Target = @{
            Type = 'postgresql'
            ConnectionId = 'targetDb'
            Table = 'public.records'
            Options = @{
                KeyColumns = @('id')
                Schema = [ordered]@{
                    id = 'BIGINT'
                    name = 'TEXT'
                }
            }
        }
    }
)
```

## Provider details

### SQL Server

- Connection type: `Microsoft.Data.SqlClient.SqlConnection`.
- Unqualified tables default to the `dbo` schema; use `schema.table` to select another schema.
- Identifiers are quoted with brackets.
- Upsert uses `UPDATE`, then `INSERT` when `@@ROWCOUNT` is zero. A unique index or primary key on the key columns is strongly recommended.
- Common supported types include `BIT`, integer types, `DECIMAL`/`NUMERIC`, `MONEY`, floating-point types, date/time types, character types, binary types, `UNIQUEIDENTIFIER`, and `XML`.

### PostgreSQL

- Connection type: `Npgsql.NpgsqlConnection`.
- Unqualified tables default to the `public` schema.
- Identifiers are quoted with double quotes.
- Upsert uses `INSERT ... ON CONFLICT ... DO UPDATE`, so the key columns must be backed by a unique constraint or index.
- Common supported types include `BOOLEAN`, integer types, `REAL`, `DOUBLE PRECISION`, `NUMERIC`/`DECIMAL`, character types, `BYTEA`, date/time types, `UUID`, `JSON`, `JSONB`, `SERIAL`, and `BIGSERIAL`.

### MySQL

- Connection type: `MySqlConnector.MySqlConnection`.
- An unqualified table uses the connection's current database; use `database.table` to qualify it.
- Identifiers are quoted with backticks.
- Upsert uses `INSERT ... ON DUPLICATE KEY UPDATE`, so the key columns must be backed by a unique key.
- Common supported types include `BOOLEAN`/`BOOL`, `BIT`, integer types, `DECIMAL`/`NUMERIC`, `FLOAT`, `DOUBLE`, date/time types, `YEAR`, character types, text/blob types, `JSON`, `BINARY`, and `VARBINARY`.

### Oracle

- Connection type: `Oracle.ManagedDataAccess.Client.OracleConnection`.
- An unqualified table uses the current user's schema. Quoted identifiers are uppercased to match Oracle's default unquoted identifier behavior.
- Query parameters use `:name` placeholders.
- Upsert runs a PL/SQL block that updates the key row and inserts when `SQL%ROWCOUNT` is zero.
- Common supported types include `BOOLEAN`, `NUMBER`, `FLOAT`, `BINARY_FLOAT`, `BINARY_DOUBLE`, date/time types, `CHAR`/`NCHAR`, `VARCHAR2`/`NVARCHAR2`, LOB types, `RAW`, `ROWID`, `XMLTYPE`, and interval types.

See [source connectors](./sources/README.md), [target connectors](./targets/README.md), and [migration step configuration](../configuration.md).
