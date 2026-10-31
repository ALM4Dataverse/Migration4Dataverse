# HubSpot source

The `hubspot` source reads HubSpot CRM objects through the HubSpot CRM API v3. It is source-only. The caller supplies an authenticated `HttpClient` in `-Connections` with `BaseAddress` set to `https://api.hubapi.com/`.

The connector uses the list endpoint for unfiltered reads and the search endpoint when `FilterGroups` or `Sorts` is configured. Search requests use cursor paging through `paging.next.after`.

## Configuration

| Property | Required | Description |
|---|---:|---|
| `Type` | Yes | `hubspot`. |
| `ConnectionId` | Yes | ID whose runtime value is an authenticated HubSpot `HttpClient`. |
| `Key` | Yes | Source property used as the row key. With `FlattenProperties = $true`, use `id` or a flattened property name. |
| `Id` | No | Optional source property copied to the row's `Id`. |
| `ExpectedRecordCount` | No | Positive expected number of records. Search reads can derive `total`; unfiltered list reads cannot. |
| `Path` | No | Omit. Use `Options.ObjectType`. |

| Option | Default | Description |
|---|---:|---|
| `ObjectType` | `contacts` | CRM object type, such as `contacts`, `companies`, `deals`, or `tickets`. |
| `Properties` | HubSpot defaults | Property names to request. |
| `FilterGroups` | `null` | HubSpot search `filterGroups` array. Setting it selects the search API. |
| `Sorts` | `null` | HubSpot search `sorts` array. Setting it selects the search API. |
| `PageSize` | `100` | Records per page, from 1 to 100. |
| `FlattenProperties` | `true` | Merge each object's `properties` into the top-level record while preserving `id`, timestamps, and `archived`. |
| `TimeoutSeconds` | `100` | Overall request timeout. |
| `MaxPages` | `100000` | Safety limit for cursor paging. |

When `FlattenProperties` is `false`, records keep the raw HubSpot shape and only top-level properties such as `id` can be used as the source key. The search count provider runs a one-record search and returns HubSpot's `total`; HubSpot may cap that value for very large result sets.

| HubSpot JSON value | Type in the PowerShell mapping |
|---|---|
| Null, Boolean, number, string | `$null`, `Boolean`, `Int64`/`Double`, `String` |
| `properties` after flattening, nested object / array | PowerShell dictionary / array |
| CRM IDs | `System.String` |
| ISO timestamps / date properties | Usually `System.DateTime` after default PowerShell JSON parsing |
| Binary content | Not included by the CRM records API; use the document API through a custom connector if needed |

## Example

```powershell
$hubspot = [System.Net.Http.HttpClient]::new()
$hubspot.BaseAddress = [Uri]'https://api.hubapi.com/'
$hubspot.DefaultRequestHeaders.Authorization =
    [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $hubspotToken)

$steps = @(
    @{
        Id = 'Companies'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'hubspot'
            ConnectionId = 'hubspot'
            Key = 'id'
            Options = @{
                ObjectType = 'companies'
                Properties = @('name', 'domain', 'industry')
                FilterGroups = @(
                    @{
                        filters = @(
                            @{
                                propertyName = 'industry'
                                operator = 'EQ'
                                value = 'Software'
                            }
                        )
                    }
                )
            }
        }
    }
)

$connections = @{
    hubspot = $hubspot
}
```

See [source connectors](./README.md) and [migration step configuration](../../configuration.md).
