# ServiceNow source

The `servicenow` source reads records from the ServiceNow Table API. It is source-only. The caller supplies an authenticated `HttpClient` in `-Connections` with `BaseAddress` set to the ServiceNow instance URL, such as `https://example.service-now.com/`.

## Configuration

| Property | Required | Description |
|---|---:|---|
| `Type` | Yes | `servicenow`. |
| `ConnectionId` | Yes | ID whose runtime value is an authenticated ServiceNow `HttpClient`. |
| `Key` | Yes | Source property used as the row key, commonly `sys_id`. |
| `Id` | No | Optional source property copied to the row's `Id`. |
| `ExpectedRecordCount` | No | Positive expected number of records. The connector does not derive a count. |
| `Path` | No | Omit. Use `Options.Table`. |

| Option | Default | Description |
|---|---:|---|
| `Table` | Required | Table API name, such as `incident`, `problem`, or a custom table. |
| `Query` | `null` | Encoded query passed as `sysparm_query`. |
| `Fields` | All fields | Comma-separated field list passed as `sysparm_fields`. |
| `DisplayValue` | `null` | `true`, `false`, or `all` for `sysparm_display_value`. |
| `ExcludeReferenceLink` | `false` | Set `sysparm_exclude_reference_link=true`. |
| `PageSize` | `1000` | Records per page, from 1 to 10000. |
| `TimeoutSeconds` | `100` | Overall request timeout. |
| `MaxPages` | `100000` | Safety limit for offset paging. |

The connector reads `result` arrays and advances `sysparm_offset` by `PageSize` until a page contains fewer records. `DisplayValue = 'all'` returns display-value objects in fields; flatten or transform those values before using them as Dataverse columns. Reference fields may remain link objects unless `ExcludeReferenceLink` is enabled.

| ServiceNow JSON value | Type in the PowerShell mapping |
|---|---|
| Null, Boolean, number, string | `$null`, `Boolean`, `Int64`/`Double`, `String` |
| Reference or `DisplayValue = 'all'` object | PowerShell dictionary |
| IDs, numeric values returned as strings | `System.String`; convert explicitly when a typed value is needed |
| ISO dates / timestamps | Usually `System.DateTime` after default PowerShell JSON parsing |
| Binary content | Not included in Table API records by this connector |

## Example

```powershell
$serviceNow = [System.Net.Http.HttpClient]::new()
$serviceNow.BaseAddress = [Uri]'https://example.service-now.com/'
$serviceNow.DefaultRequestHeaders.Authorization =
    [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $serviceNowToken)

$steps = @(
    @{
        Id = 'Incidents'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'servicenow'
            ConnectionId = 'servicenow'
            Key = 'sys_id'
            Options = @{
                Table = 'incident'
                Query = 'active=true^ORDERBYnumber'
                Fields = @('sys_id', 'number', 'short_description', 'state', 'assigned_to')
                ExcludeReferenceLink = $true
            }
        }
    }
)

$connections = @{
    servicenow = $serviceNow
}
```

See [source connectors](./README.md) and [migration step configuration](../../configuration.md).
