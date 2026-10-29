# Zendesk source

The `zendesk` source reads Zendesk Support resources or search results. It is source-only. The caller supplies an authenticated `HttpClient` in `-Connections` with `BaseAddress` set to the API root, such as `https://example.zendesk.com/api/v2/`.

## Configuration

| Property | Required | Description |
|---|---:|---|
| `Type` | Yes | `zendesk`. |
| `ConnectionId` | Yes | ID whose runtime value is an authenticated Zendesk `HttpClient`. |
| `Key` | Yes | Source property used as the row key, normally `id`. |
| `Id` | No | Optional source property copied to the row's `Id`. |
| `ExpectedRecordCount` | No | Positive expected number of records. Search reads can use `/search/count.json`; resource reads have no automatic count. |
| `Path` | No | Omit. Use `Options.Resource` or `Options.Query`. |

| Option | Default | Description |
|---|---:|---|
| `Resource` | `tickets` | Resource name used as `<Resource>.json`, such as `tickets`, `users`, or `organizations`. |
| `Query` | `null` | Zendesk search query. When set, the connector calls `search.json` and defaults to `results` records. |
| `RecordsPath` | `Resource`, or `results` for search | Property containing the records array. |
| `NextLinkPath` | `links.next`, then `next_page` | Property containing the next-page URL. |
| `PageSizeParameter` | `page[size]`, or `per_page` for search | Query parameter used for page size. |
| `PageSize` | `100` | Records per page, from 1 to 100. |
| `TimeoutSeconds` | `100` | Overall request timeout. |
| `MaxPages` | `100000` | Safety limit for paging. |

The connector follows a next-page URL when present. If a cursor endpoint reports `meta.has_more = true` and provides `meta.after_cursor`, it adds or replaces `page[after]` and requests the next page. Records are sorted by the configured key before the migration reader starts.

## Example

```powershell
$zendesk = [System.Net.Http.HttpClient]::new()
$zendesk.BaseAddress = [Uri]'https://example.zendesk.com/api/v2/'
$zendesk.DefaultRequestHeaders.Authorization =
    [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $zendeskToken)

$steps = @(
    @{
        Id = 'OpenTickets'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'zendesk'
            ConnectionId = 'zendesk'
            Key = 'id'
            Options = @{
                Query = 'type:ticket status:open'
                PageSize = 100
            }
        }
    }
)

$connections = @{
    zendesk = $zendesk
}
```

See [source connectors](./README.md) and [migration step configuration](../../configuration.md).
