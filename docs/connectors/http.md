# HTTP and OData sources

The `http` and `odata` connectors read records from JSON HTTP APIs. They are source-only. The caller may supply a `System.Net.Http.HttpClient` in `-Connections`, or supply a `Uri` or absolute URL string and let the engine create and dispose a client.

A source `Path` is appended to a base `Uri` connection. With an `HttpClient` connection, `Path` must be an absolute URL. The engine fetches every page before reading begins, sorts records by the configured parent and key properties, writes them to a temporary JSON Lines file, and streams that file through the standard JSONL reader.

```powershell
$steps = @(
    @{
        Id = 'Contacts'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'odata'
            ConnectionId = 'contactsApi'
            Path = 'contacts'
            Key = 'contactid'
            Options = @{
                PageSize = 500
            }
        }
    }
)
```

| Option | Default | Description |
|---|---:|---|
| `Method` | `GET` | `GET` or `POST`. |
| `Body` | `null` | Request body for `POST`. |
| `ContentType` | `null` | Content type for a request body. |
| `Headers` | Empty | Additional request headers. |
| `RecordsPath` | Root for `http`; `value` for `odata` | Property path to the records array. |
| `NextLinkPath` | `null`; `@odata.nextLink` for `odata` | Property path to the next-page URL. |
| `NextLinkHeader` | `null` | Response header carrying the next-page URL. |
| `TotalCountPath` | `null`; `@odata.count` for `odata` | Accepted for OData compatibility; it is not used as a source count provider. |
| `PageSizeParameter` | `null`; `$top` for `odata` | Query parameter set to `PageSize` on the first request. |
| `OffsetParameter` | `null`; `$skip` for `odata` | Query parameter advanced by `PageSize` when no next link is present. |
| `PageSize` | `1000` | Page size and offset increment. |
| `TimeoutSeconds` | `100` | Overall fetch timeout. |
| `MaxPages` | `100000` | Safety limit for pagination. |

`RecordsPath` and `NextLinkPath` accept `$`, dotted paths, slash-separated paths, and direct property names, including names such as `@odata.nextLink`. Records must be JSON objects. When a next link is present, the engine follows it. Otherwise pagination stops when `OffsetParameter` is empty or the page contains fewer than `PageSize` records. `MaxPages` prevents an unbounded next-link loop.

`odata` applies the defaults shown above. You can override any of them; for example, set `NextLinkHeader = 'Link'` for a service that uses a Link header instead of a JSON next link. `http` has no implicit records or pagination defaults, so set `RecordsPath` and the pagination options that match the API.

The source does not derive an expected count from `TotalCountPath` or by scanning the downloaded data. Set `ExpectedRecordCount` when the exact count is known. Because fetching completes before the reader starts, a very large API response can consume significant time and memory during source initialization; prefer the paged custom PowerShell source for APIs that cannot be safely fetched as one bounded dataset.

See [source connectors](./sources/README.md) and [migration step configuration](../configuration.md).
