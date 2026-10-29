# Salesforce Sales Cloud source

The `salesforce` source reads Salesforce Sales Cloud records through the REST or Bulk API 2.0 query interfaces. It is source-only. The caller supplies an authenticated `System.Net.Http.HttpClient` in `-Connections`; its `BaseAddress` must be the Salesforce instance URL, such as `https://example.my.salesforce.com/`.

The engine does not create or refresh Salesforce credentials. Configure OAuth, a connected app, or another supported authentication flow in the client before passing it to `Invoke-Migration`, and keep tokens out of step definitions.

## Configuration

| Property | Required | Description |
|---|---:|---|
| `Type` | Yes | `salesforce`. |
| `ConnectionId` | Yes | ID whose runtime value is an authenticated `HttpClient` with an absolute instance `BaseAddress`. |
| `Key` | Yes | Source property used as the row key. For REST queries it is also the default `ORDER BY` field. |
| `Id` | No | Optional source property copied to the row's `Id`. |
| `ExpectedRecordCount` | No | Positive expected number of records; `0` (default) uses the REST `COUNT()` provider when `Options.Soql` is omitted. |
| `Path` | No | Omit. Salesforce uses `Options`, not a source path. |

| Option | Default | Description |
|---|---:|---|
| `Soql` | `null` | Full SOQL query. When set, `Object`, `Fields`, `Where`, and `OrderBy` are ignored. |
| `Object` | Required unless `Soql` is set | Salesforce object API name, such as `Account` or `CustomObject__c`. |
| `Fields` | Required unless `Soql` is set | Fields to select, such as `Id`, `Name`, or `Owner.Name`. |
| `Where` | `null` | Optional SOQL predicate appended to the generated query. |
| `OrderBy` | `Key` | Optional SOQL ordering appended to the generated query. The source key must still be selected. |
| `ApiVersion` | `v62.0` | Salesforce REST API version. |
| `PageSize` | `2000` | REST query batch size, from 200 to 2000. |
| `QueryAll` | `false` | Use the REST `queryAll` endpoint for deleted and archived records. |
| `UseBulkApi` | `false` | Use Bulk API 2.0 query jobs instead of REST paging. |
| `BulkMaxRecords` | `10000` | Maximum records requested per Bulk API results page. |
| `BulkPollIntervalMilliseconds` | `1000` | Delay between Bulk API job status checks. |
| `BulkTimeoutSeconds` | `3600` | Maximum time to wait for a Bulk API job to complete. |
| `TimeoutSeconds` | `100` | Overall REST request timeout. |
| `MaxPages` | `100000` | Safety limit for REST or Bulk result paging. |

REST reads follow `nextRecordsUrl` until Salesforce reports no next page. The engine removes the Salesforce `attributes` object from each record, sorts records by the configured parent and key properties, and then streams them through the standard JSONL reader. `QueryAll` cannot be combined with `UseBulkApi`.

Bulk API mode creates a query job, polls it until `JobComplete`, then reads CSV result pages using the `Sforce-Locator` header. Empty CSV values become `null`. Bulk API queries are useful for large extracts, but the connector still materializes the fetched records before the migration reader starts.

When `Options.Soql` is omitted, the source count provider runs `SELECT COUNT() FROM <Object>` with the same `Where` predicate. A positive `ExpectedRecordCount` takes precedence. Custom `Soql` has no derived count.

## Example

```powershell
$salesforce = [System.Net.Http.HttpClient]::new()
$salesforce.BaseAddress = [Uri]'https://example.my.salesforce.com/'
$salesforce.DefaultRequestHeaders.Authorization =
    [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $accessToken)

$steps = @(
    @{
        Id = 'Accounts'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'salesforce'
            ConnectionId = 'salesforce'
            Key = 'Id'
            Options = @{
                Object = 'Account'
                Fields = @('Id', 'Name', 'BillingCountry', 'Owner.Name')
                Where = 'IsDeleted = false'
                UseBulkApi = $true
            }
        }
    }
)

$connections = @{
    salesforce = $salesforce
}
```

See [source connectors](./README.md) and [migration step configuration](../../configuration.md).
