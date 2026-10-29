# SharePoint list source

The `sharepoint` source reads SharePoint list items through Microsoft Graph. It is source-only. The caller supplies an authenticated `HttpClient` in `-Connections` with `BaseAddress` set to `https://graph.microsoft.com/v1.0/` and a token that can read the target site and list.

## Configuration

| Property | Required | Description |
|---|---:|---|
| `Type` | Yes | `sharepoint`. |
| `ConnectionId` | Yes | ID whose runtime value is an authenticated Graph `HttpClient`. |
| `Key` | Yes | Source property used as the row key. When `FlattenFields` is `true`, list field names are available at the top level. |
| `Id` | No | Optional source property copied to the row's `Id`. |
| `ExpectedRecordCount` | No | Positive expected number of records. No automatic count provider is used. |
| `Path` | No | Omit. Use `Options.SiteId` and `Options.ListId`. |

| Option | Default | Description |
|---|---:|---|
| `SiteId` | Required | Microsoft Graph site ID. |
| `ListId` | Required | Microsoft Graph list ID. |
| `Fields` | All fields | Optional list field internal names. When set, the request uses `$expand=fields($select=...)`. |
| `Filter` | `null` | Optional Graph `$filter` expression. Use list-field paths such as `fields/Status eq 'Open'`. |
| `Expand` | `fields` | Overrides the `$expand` value when you need a different Graph projection. |
| `PageSize` | `100` | Graph page size, from 1 to 100. |
| `FlattenFields` | `true` | Merge each item's `fields` object into the item's top-level properties. |
| `TimeoutSeconds` | `100` | Overall request timeout. |
| `MaxPages` | `100000` | Safety limit for `@odata.nextLink` paging. |

The engine follows Graph `@odata.nextLink` values until paging ends. With `FlattenFields = $true`, the Graph item `id` and other item metadata are preserved unless a list field has the same name. Set `FlattenFields = $false` if you need the raw Graph item shape and a nested key path is not available to the source contract.

## Example

```powershell
$graph = [System.Net.Http.HttpClient]::new()
$graph.BaseAddress = [Uri]'https://graph.microsoft.com/v1.0/'
$graph.DefaultRequestHeaders.Authorization =
    [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $graphToken)

$steps = @(
    @{
        Id = 'Projects'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'sharepoint'
            ConnectionId = 'graph'
            Key = 'id'
            Options = @{
                SiteId = 'contoso.sharepoint.com,00000000-0000-0000-0000-000000000000,11111111-1111-1111-1111-111111111111'
                ListId = '22222222-2222-2222-2222-222222222222'
                Fields = @('Title', 'Status', 'Owner')
                Filter = "fields/Status eq 'Active'"
            }
        }
    }
)

$connections = @{
    graph = $graph
}
```

See [source connectors](./README.md) and [migration step configuration](../../configuration.md).
