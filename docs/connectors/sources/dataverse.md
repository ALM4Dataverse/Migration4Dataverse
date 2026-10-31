# Dataverse source

The `dataverse` source reads rows through `Rnwood.Dataverse.Data.PowerShell`. This page documents the Migration4Dataverse adapter rules; it is not a copy of the upstream module's full cmdlet help.

> [!NOTE]
> For all query parameters and their semantics, see upstream [`Get-DataverseRecord`](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Get-DataverseRecord.md), [`Invoke-DataverseSql`](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Invoke-DataverseSql.md), and the [querying concept](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/core-concepts/querying.md).

> [!IMPORTANT]
> A Dataverse source needs a stable `Key` for ordered reading and resume. Keep the query and key values stable for the lifetime of a run.

## Configure a source

```powershell
Source = @{
    Type = 'dataverse'
    ConnectionId = 'crm'
    Key = 'new_migrationkey'
    Id = 'accountid'
    Options = @{
        QueryType = 'Simple'
        TableName = 'account'
        Columns = @('new_migrationkey', 'accountid', 'name')
        PageSize = 500
    }
}
```

`ConnectionId` selects the caller-owned Dataverse connection from `-Connections`; the module is loaded automatically when a typed Dataverse source or target is used. Omit `Path`. The adapter supplies the connection to the upstream command, so do not set a separate connection in `Options`.

Every returned row must include the configured `Key`. For a root source, key values must be non-empty, unique, and strictly increasing in case-insensitive ordinal order. Set `Id` to the source field containing the GUID when downstream targets need it; `Id` is optional. On resume, the query is rerun and rows through the saved source key are skipped.

For Simple queries, the adapter adds ascending ordering by `Key` when `OrderBy` is omitted. If you specify `OrderBy`, put `Key` first and ascending. FetchXML and SQL queries must explicitly order by `Key` ascending. The engine does not parse or rewrite those query languages.

Simple and FetchXML queries can obtain a total from `Get-DataverseRecord -RecordCount` before reading rows. `Cds4Sql` does not have an automatic count provider. A positive `ExpectedRecordCount` on the source takes precedence and is checked against the rows returned.

## File columns and attachments

For a Simple query, set `Options.FileColumns` to one or more File/Image column logical names. `Source.Id` must name the row's GUID column, and `Options.TableName` must identify the table. The adapter downloads each requested column with Dataverse's file-block API and stages the content separately from the migration batch.

```powershell
Source = @{
    Type = 'dataverse'
    ConnectionId = 'crm'
    Key = 'accountid'
    Id = 'accountid'
    Options = @{
        QueryType = 'Simple'
        TableName = 'account'
        Columns = @('accountid', 'name')
        FileColumns = @('new_contractfile')
    }
}
```

The mapped `new_contractfile` value is a `MigrationBinaryValue`, not an in-memory byte array or a Base64 string. It can pass through unchanged or be streamed through a mapping; see [`New-M4dMigrationBinaryValue`](../../cmdlets/new-migration-binary-value.md). `FileColumns` is currently supported only for Simple queries.

For Simple queries against `annotation` and `activitymimeattachment`, a non-empty `documentbody` or `body` field is recognized as Base64 attachment content and staged as a `MigrationBinaryValue`. Include the content column in the selected fields. The upstream Dataverse record cmdlet returns these bodies as Base64 strings, so decoding avoids storing Base64 in queued migration batches but the upstream row itself is materialized by that cmdlet.

| Dataverse source value | Type in the PowerShell mapping |
|---|---|
| Text / Boolean / numeric attributes | `System.String`, `Boolean`, or numeric JSON value |
| GUIDs | `System.String` |
| ISO date/time attributes | Usually `System.DateTime` after default PowerShell JSON parsing |
| Nested or provider-specific attribute values | JSON-compatible object / array; exact values follow the upstream cmdlet |
| File/Image columns and decoded attachment body | `MigrationBinaryValue` |

This table describes the migration adapter boundary, not the upstream Dataverse attribute catalog. For complete Dataverse attribute and query behavior, use the linked upstream cmdlet references.

## Simple query

`QueryType = 'Simple'` invokes `Get-DataverseRecord` using the connection and `TableName` from the migration configuration. Other supported `Options` are passed to the cmdlet.

```powershell
Options = @{
    QueryType = 'Simple'
    TableName = 'account'
    Columns = @('new_migrationkey', 'accountid', 'name')
    FilterValues = @(@{ statecode = 0 })
    OrderBy = @('new_migrationkey')
    PageSize = 500
}
```

Use the upstream [`Get-DataverseRecord` reference](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Get-DataverseRecord.md) for the complete option list, filters, links, and parameter behavior.

## FetchXML query

Set `QueryType = 'FetchXml'` and provide the FetchXML accepted by `Get-DataverseRecord`. Include an ascending `<order>` for the configured source key.

```powershell
Options = @{
    QueryType = 'FetchXml'
    FetchXml = @'
<fetch>
  <entity name="account">
    <attribute name="new_migrationkey" />
    <attribute name="accountid" />
    <attribute name="name" />
    <order attribute="new_migrationkey" />
  </entity>
</fetch>
'@
}
```

See the upstream [`Get-DataverseRecord` help](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Get-DataverseRecord.md) and Microsoft's [FetchXML overview](https://learn.microsoft.com/power-apps/developer/data-platform/fetchxml/overview).

## Cds4Sql query

Set `QueryType = 'Cds4Sql'` to run a read-only `SELECT` through `Invoke-DataverseSql`. Use parameters rather than interpolating values into the SQL text, and order the result by the configured source key.

```powershell
Options = @{
    QueryType = 'Cds4Sql'
    Sql = 'SELECT new_migrationkey, accountid, name FROM account WHERE statecode = @state ORDER BY new_migrationkey'
    Parameters = @{ state = 0 }
    Timeout = 120
}
```

The adapter rejects statements that are not a single read-only `SELECT`; it does not use this source to run DML. See upstream [`Invoke-DataverseSql`](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Invoke-DataverseSql.md) for Cds4Sql options and limits.

## Parallel source partitions

A root Dataverse source can split one logical read into explicitly configured, independently queried partitions. The adapter runs the partition readers concurrently, merges their rows by the configured ascending `Key`, and rejects duplicate or incorrectly ordered keys.

```powershell
Source = @{
    Type = 'dataverse'
    ConnectionId = 'crm'
    Key = 'accountid'
    Options = @{
        QueryType = 'FetchXml'
        FetchXml = @'
<fetch><entity name="account">
  <attribute name="accountid" />
  <order attribute="accountid" />
</entity></fetch>
'@
    }
    Partitions = @(
        @{
            Id = 'active'
            Options = @{
                FetchXml = @'
<fetch><entity name="account">
  <attribute name="accountid" />
  <filter><condition attribute="statecode" operator="eq" value="0" /></filter>
  <order attribute="accountid" />
</entity></fetch>
'@
            }
        }
        @{
            Id = 'inactive'
            Options = @{
                FetchXml = @'
<fetch><entity name="account">
  <attribute name="accountid" />
  <filter><condition attribute="statecode" operator="eq" value="1" /></filter>
  <order attribute="accountid" />
</entity></fetch>
'@
            }
        }
    )
}
```

Each partition needs a unique, stable `Id`. Its `Options` are merged over the source's `Options`: omitted fields inherit the base value, and supplied fields replace it. All partitions must use the source's `QueryType`, and each query must independently return rows in ascending `Key` order. You are responsible for making the partitions complete and non-overlapping; the adapter detects duplicate keys but cannot detect gaps. For Simple and FetchXML sources, count preflight sums the per-partition counts. Partitioned sources require at least two partitions, and the number of partitions cannot exceed `Invoke-M4dMigration -MaxDegreeOfParallelism`. The migration checkpoints a cursor for every partition, so preserve the partition IDs, filters, and query behavior when resuming a run.

## Connection pools and request balancing

`ConnectionId` can resolve to one connection or an array, for example `crm = @($crmAsUserA, $crmAsUserB)`. Pooled connections must belong to the same Dataverse organization and use the same `MaxRetryCount`. The same named pool is shared by typed Dataverse sources and targets.

For a pool, the adapter routes individual Dataverse service calls across members using calculated request units in a rolling five-minute window. Calls to `ExecuteMultiple` and `ExecuteTransaction` are expanded into their nested operations; bulk `*Multiple` requests with a `Targets` collection are charged by target count. `RetrieveMultiple` is a regular single service request. Retries are separate attempts. Each identity has an active-request limit based on its connection's `RecommendedDegreesOfParallelism` (falling back to 4); connections reporting the same Dataverse user share one limit. Only identities below their limit can receive new calls, and callers wait when all identity budgets are full. The adapter does not assume a universal request quota or dynamically change concurrency based on throttle signals; normal retries remain enabled. Pooled Cds4Sql sources must set `MaxDegreeOfParallelism` to `1`; the adapter rejects higher values because the upstream cmdlet's internal parallel requests cannot be individually routed through the pool.

> [!IMPORTANT]
> If using parallel `Partitions`, ensure the queries form a complete, disjoint set and keep their stable IDs and query behavior unchanged for the life of a run.

## Connections and authentication

Create and authenticate each connection using the upstream module, then place it in `-Connections`. See upstream [`Get-DataverseConnection`](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Get-DataverseConnection.md), the [connections concept](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/core-concepts/connections.md), and the [authentication guide](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/getting-started/authentication.md).
