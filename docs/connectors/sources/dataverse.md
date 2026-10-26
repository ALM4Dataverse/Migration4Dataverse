# Dataverse source

The `dataverse` source streams records from a caller-owned Dataverse connection using `Rnwood.Dataverse.Data.PowerShell`. It supports three query forms:

1. `Simple` — `Get-DataverseRecord`'s Simple parameter set.
2. `FetchXml` — `Get-DataverseRecord -FetchXml`.
3. `Cds4Sql` — a read-only `SELECT` through `Invoke-DataverseSql`.

The module must be available to the embedded PowerShell runtime. `Invoke-Migration` imports `Rnwood.Dataverse.Data.PowerShell` automatically when a typed Dataverse source or target is used. The caller creates and owns the connection object.

## Common source configuration

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
        FilterValues = @(@{ name = 'Contoso' })
        PageSize = 500
    }
}
```

`Key` is a non-empty string property used for deterministic ordering and resume checkpoints. Prefer a unique, immutable text key; keep the GUID primary key separately in `Id`. `Id` names the source property converted to the target record GUID. The query must return both properties. Dataverse records are streamed with bounded buffering; on resume, the query is rerun and rows through the last completed key are skipped. Keep the query stable between runs.

The source key must be a non-empty string in every output record. For a root source, keys must be unique and strictly increasing using case-insensitive ordinal comparison, matching Dataverse ordering. Row-linked child records are staged and ordered by the engine; child keys must be unique within each non-null parent relationship.

The Simple query automatically requests ascending order by `Key` when `OrderBy` is omitted. If `OrderBy` is supplied, keep `Key` first and ascending so paged reads are stable; further sort fields may follow. FetchXML and SQL queries should explicitly order by `Key` ascending for stable paging. Row-linked child rows may arrive in any parent-key order because the engine stages them before release. The engine does not rewrite or parse those query languages.

`Path` is not used and must be omitted. Dataverse connector settings belong in `Source.Options`. Unless a positive `ExpectedRecordCount` is configured, Simple and FetchXML sources request the matching count from `Get-DataverseRecord -RecordCount` before row streaming; Cds4Sql sources do not have an automatic count provider. The count is checked against the returned rows. A positive `ExpectedRecordCount` takes precedence and skips the count request.

## Simple query

This mode invokes `Get-DataverseRecord` with `TableName`, the connection resolved from `ConnectionId`, and supported parameters from `Options`.

```powershell
Options = @{
    QueryType = 'Simple'
    TableName = 'account'
    Columns = @('new_migrationkey', 'accountid', 'name', 'telephone1')
    FilterValues = @(
        @{ 'name:Like' = 'Contoso%' }
        @{ 'statecode' = 0 }
    )
    OrderBy = @('new_migrationkey', 'name')
    PageSize = 500
}
```

Hashtable filters use the module's `FilterValues` syntax: keys may be `column` or `column:Operator`; multiple hashtables are ORed, and entries within a hashtable are ANDed. Operators are names from the Dataverse SDK `ConditionOperator` enum. Values can be literals, arrays, null, or nested operator/value hashtables. `Links` accepts simplified link hashtables or module `DataverseLinkEntity`/SDK `LinkEntity` objects. `Criteria` accepts a Dataverse SDK `FilterExpression`. A source step can pass these runtime objects in its `Options` hashtable.

### Simple options

All properties are optional except `QueryType` and `TableName`. `Connection` is always supplied from the source step's `ConnectionId` and cannot be overridden. Option names are case-insensitive.

| Option | Type / default | Description |
|---|---|---|
| `QueryType` | String; `Simple` | Selects this query mode. |
| `TableName` | String; required | Dataverse logical table name. |
| `FilterValues` | Hashtable array | Include filter groups. Each hashtable's conditions combine with AND; multiple groups combine with OR. |
| `Criteria` | `FilterExpression` | Additional Dataverse SDK filter expression. |
| `Links` | `DataverseLinkEntity[]` or simplified hashtables | Related-table links for the query. |
| `ExcludeFilterValues` | Hashtable array | Exclude filters using the same syntax as `FilterValues`. |
| `ExcludeFilterOr` | Switch; false | Combine exclude filter groups with OR instead of AND. |
| `ActiveOnly` | Switch; false | Restrict results to active records. |
| `Id` | GUID array | Restrict results to the listed record IDs. |
| `Name` | String array | Restrict results to listed primary-attribute values. |
| `ExcludeId` | GUID array | Exclude the listed record IDs. |
| `Columns` | String array | Columns to return; supports the module's `:Raw` and `:Display` suffixes. |
| `ExcludeColumns` | String array | Columns to omit; ignored when `Columns` is specified. |
| `OrderBy` | String array | Fields to order by. A trailing `-` sorts descending. Keep ascending `Key` first for stable paging; additional sort fields may follow. |
| `Top` | Int32; all rows | Maximum number of records to return. |
| `PageSize` | Int32; `1000` | Dataverse page request size. The module automatically pages through the query. |
| `LookupValuesReturnName` | Switch; false | Return lookup names instead of IDs. |
| `IncludeSystemColumns` | Switch; false | Include system columns. |
| `IncludeFileAndImageMetadataColumns` | Switch; false | Include file/image metadata columns. |
| `Unpublished` | Switch; false | Read unpublished records. |
| `VerboseRecordCount` | Switch; false | Write the matching record count to verbose output. |
| `RecordCount` | Switch; false | Not supported as true: it replaces the record stream with a count result. |

The complete `Get-DataverseRecord` behavior is documented in the [module help](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Get-DataverseRecord.md).

## FetchXML query

Use the `FetchXml` option with the FetchXml parameter set of `Get-DataverseRecord`. Add an ascending FetchXML `<order>` for the configured source `Key` to keep paging stable. Row-linked child rows are staged and ordered by the engine, so they do not need to be grouped by their parent key. FetchXML paging is handled by the module; the source reader streams each returned row.

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
    <filter>
      <condition attribute="statecode" operator="eq" value="0" />
    </filter>
  </entity>
</fetch>
'@
    LookupValuesReturnName = $false
    Unpublished = $false
}
```

| Option | Type / default | Description |
|---|---|---|
| `QueryType` | String; `FetchXml` | Selects FetchXML mode. |
| `FetchXml` | String; required | FetchXML query. |
| `LookupValuesReturnName` | Switch; false | Return lookup names instead of IDs. |
| `Unpublished` | Switch; false | Read unpublished records. |
| `VerboseRecordCount` | Switch; false | Write matching count to verbose output. |
| `RecordCount` | Switch; false | Not supported as true because it replaces rows with a count result. |

See [Microsoft Learn: FetchXML overview](https://learn.microsoft.com/power-apps/developer/data-platform/fetchxml/overview) and the module's [Get-DataverseRecord help](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Get-DataverseRecord.md).

## Sql4Cds query

Use `QueryType = 'Cds4Sql'` and a read-only `SELECT` statement. The source calls `Invoke-DataverseSql -Sql ... -Connection ...`; it does not execute DML. Write `ORDER BY <Key> ASC` so the paged output is stable. Row-linked child records are staged and ordered by their parent keys before release.

```powershell
Options = @{
    QueryType = 'Cds4Sql'
    Sql = 'SELECT new_migrationkey, accountid, name FROM account WHERE statecode = @state ORDER BY new_migrationkey'
    Parameters = @{ state = 0 }
    BatchSize = 500
    Timeout = 120
}
```

Use `Parameters` for `@name` SQL parameters instead of interpolating values into the SQL string. Row-linked child rows are staged and ordered by the engine rather than requiring an `ORDER BY` on the parent key. The query must begin with `SELECT`; semicolon-separated statements and DML are rejected.

| Option | Type / default | Description |
|---|---|---|
| `QueryType` | String; `Cds4Sql` | Selects Sql4Cds mode. |
| `Sql` | String; required | Read-only `SELECT` statement. |
| `Parameters` | Hashtable or PSObject | Values referenced by `@parameterName` placeholders. |
| `UseTdsEndpoint` | Switch; false | Use the Dataverse TDS endpoint where supported. |
| `Timeout` | Int32 | Sql4Cds query timeout. |
| `BatchSize` | Int32 | Sql4Cds batch size. |
| `MaxDegreeOfParallelism` | Int32 | Sql4Cds query execution parallelism. |
| `BypassCustomPluginExecution` | Switch; false | Sql4Cds option to bypass custom plug-in execution. |
| `UseBulkDelete` | Switch; false | Sql4Cds bulk-delete option; source queries must still be SELECT-only. |
| `ReturnEntityReferenceAsGuid` | Switch; false | Return lookup values as GUIDs instead of `SqlEntityReference` values. |
| `UseLocalTimezone` | Switch; false | Use local time zone behavior for query results. |
| `DataSourceName` | String | Name for the primary datasource in double-dot SQL references. |

See the module's [Invoke-DataverseSql help](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Invoke-DataverseSql.md) for Sql4Cds semantics and limits.
