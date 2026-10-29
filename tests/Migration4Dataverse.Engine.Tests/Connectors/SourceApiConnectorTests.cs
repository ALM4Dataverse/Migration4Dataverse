using System.Net;
using System.Text;
using System.Text.Json;
using Migration4Dataverse.Engine.Connectors;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.Tests.Connectors;

public sealed class SourceApiConnectorTests
{
    [Fact]
    public async Task SalesforceReadsRestPagesAndRemovesAttributes()
    {
        var requests = new List<string>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            return requests.Count == 1
                ? JsonResponse(
                    """{"totalSize":2,"done":false,"records":[{"attributes":{"type":"Account"},"Id":"a2","Name":"Two"}],"nextRecordsUrl":"/services/data/v62.0/query/01g-2000"}""")
                : JsonResponse(
                    """{"totalSize":2,"done":true,"records":[{"attributes":{"type":"Account"},"Id":"a1","Name":"One"}]}""");
        }))
        {
            BaseAddress = new Uri("https://example.test/")
        };
        var level = Source("Salesforce", "salesforce", "Id", client);
        await using var reader = new SalesforceHierarchyRecordReader(
            level,
            new SalesforceSourceOptions
            {
                Object = "Account",
                Fields = ["Id", "Name"]
            });

        var records = await ReadAllAsync(reader);

        Assert.Equal(new[] { "a1", "a2" }, records.Select(record => record.GetProperty("Id").GetString()));
        Assert.All(records, record => Assert.False(record.TryGetProperty("attributes", out _)));
        Assert.Equal(2, requests.Count);
        Assert.Contains("SELECT%20Id%2C%20Name%20FROM%20Account%20ORDER%20BY%20Id", requests[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SalesforceReadsBulkQueryCsv()
    {
        var requests = new List<string>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            var uri = request.RequestUri!.AbsolutePath;
            requests.Add($"{request.Method} {uri}");
            if (request.Method == HttpMethod.Post && uri.EndsWith("/jobs/query", StringComparison.Ordinal))
            {
                return JsonResponse("""{"id":"750","state":"UploadComplete"}""");
            }
            if (request.Method == HttpMethod.Get && uri.EndsWith("/jobs/query/750", StringComparison.Ordinal))
            {
                return JsonResponse(requests.Count < 3
                    ? """{"state":"InProgress"}"""
                    : """{"state":"JobComplete"}""");
            }
            if (request.Method == HttpMethod.Get && uri.EndsWith("/jobs/query/750/results", StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("Id,Name\n\"a2\",\"Two\"\n\"a1\",\"One\"\n", Encoding.UTF8, "text/csv")
                };
                response.Headers.TryAddWithoutValidation("Sforce-Locator", "null");
                return response;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }))
        {
            BaseAddress = new Uri("https://example.test/")
        };
        var level = Source("Salesforce", "salesforce", "Id", client);
        await using var reader = new SalesforceHierarchyRecordReader(
            level,
            new SalesforceSourceOptions
            {
                Object = "Account",
                Fields = ["Id", "Name"],
                UseBulkApi = true,
                BulkPollIntervalMilliseconds = 1
            });

        var records = await ReadAllAsync(reader);

        Assert.Equal(new[] { "a1", "a2" }, records.Select(record => record.GetProperty("Id").GetString()));
        Assert.Contains("POST /services/data/v62.0/jobs/query", requests);
        Assert.Contains("GET /services/data/v62.0/jobs/query/750/results", requests);
    }

    [Fact]
    public async Task SalesforceCountsWithoutCustomSoql()
    {
        using var client = new HttpClient(new StubHandler(_ =>
            JsonResponse("""{"totalSize":7,"done":true,"records":[]}""")))
        {
            BaseAddress = new Uri("https://example.test/")
        };
        var level = Source("Salesforce", "salesforce", "Id", client);

        var count = await SalesforceHierarchyRecordReader.CountRecordsAsync(
            level,
            new SalesforceSourceOptions { Object = "Account", Fields = ["Id"] },
            CancellationToken.None);

        Assert.Equal(7, count);
    }

    [Fact]
    public async Task SharePointFlattensFieldsAndFollowsNextLink()
    {
        var requests = new List<string>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.ToString());
            return requests.Count == 1
                ? JsonResponse(
                    """{"value":[{"id":"2","fields":{"Title":"Two"}},{"id":"1","fields":{"Title":"One"}}],"@odata.nextLink":"https://example.test/next"}""")
                : JsonResponse("""{"value":[{"id":"3","fields":{"Title":"Three"}}]}""");
        }))
        {
            BaseAddress = new Uri("https://graph.microsoft.com/v1.0/")
        };
        var level = Source("SharePoint", "sharepoint", "id", client);
        await using var reader = new SharePointHierarchyRecordReader(
            level,
            new SharePointSourceOptions { SiteId = "site", ListId = "list" });

        var records = await ReadAllAsync(reader);

        Assert.Equal(new[] { "1", "2", "3" }, records.Select(record => record.GetProperty("id").GetString()));
        Assert.Equal("One", records[0].GetProperty("Title").GetString());
        Assert.Equal(2, requests.Count);
        Assert.Contains("sites/site/lists/list/items", requests[0], StringComparison.Ordinal);
        Assert.Contains("%24expand=fields", requests[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task HubSpotFlattensPropertiesAndUsesSearchPaging()
    {
        var requests = new List<string>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri}");
            return requests.Count == 1
                ? JsonResponse(
                    """{"total":2,"results":[{"id":"2","properties":{"name":"Two"}}],"paging":{"next":{"after":"abc"}}}""")
                : JsonResponse(
                    """{"total":2,"results":[{"id":"1","properties":{"name":"One"}}]}""");
        }))
        {
            BaseAddress = new Uri("https://api.hubapi.com/")
        };
        var level = Source("HubSpot", "hubspot", "id", client);
        await using var reader = new HubSpotHierarchyRecordReader(
            level,
            new HubSpotSourceOptions
            {
                ObjectType = "contacts",
                Properties = ["name"],
                FilterGroups = JsonSerializer.SerializeToElement(new[]
                {
                    new { filters = Array.Empty<object>() }
                })
            });

        var records = await ReadAllAsync(reader);

        Assert.Equal(new[] { "1", "2" }, records.Select(record => record.GetProperty("id").GetString()));
        Assert.Equal("One", records[0].GetProperty("name").GetString());
        Assert.Equal(2, requests.Count);
        Assert.StartsWith("POST ", requests[0], StringComparison.Ordinal);
        Assert.Contains("/crm/v3/objects/contacts/search", requests[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ZendeskFollowsCursorFallback()
    {
        var requests = new List<string>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.ToString());
            return requests.Count == 1
                ? JsonResponse(
                    """{"tickets":[{"id":2},{"id":1}],"meta":{"has_more":true,"after_cursor":"abc"}}""")
                : JsonResponse("""{"tickets":[{"id":3}],"meta":{"has_more":false}}""");
        }))
        {
            BaseAddress = new Uri("https://example.zendesk.com/api/v2/")
        };
        var level = Source("Zendesk", "zendesk", "id", client);
        await using var reader = new ZendeskHierarchyRecordReader(
            level,
            new ZendeskSourceOptions { Resource = "tickets", PageSize = 2 });

        var records = await ReadAllAsync(reader);

        Assert.Equal(new long[] { 1, 2, 3 }, records.Select(record => record.GetProperty("id").GetInt64()));
        Assert.Equal(2, requests.Count);
        Assert.Contains("page%5Bafter%5D=abc", requests[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServiceNowReadsOffsetPages()
    {
        var requests = new List<string>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.ToString());
            return requests.Count == 1
                ? JsonResponse("""{"result":[{"sys_id":"2"},{"sys_id":"1"}]}""")
                : JsonResponse("""{"result":[{"sys_id":"3"}]}""");
        }))
        {
            BaseAddress = new Uri("https://example.service-now.com/")
        };
        var level = Source("ServiceNow", "servicenow", "sys_id", client);
        await using var reader = new ServiceNowHierarchyRecordReader(
            level,
            new ServiceNowSourceOptions { Table = "incident", PageSize = 2 });

        var records = await ReadAllAsync(reader);

        Assert.Equal(new[] { "1", "2", "3" }, records.Select(record => record.GetProperty("sys_id").GetString()));
        Assert.Equal(2, requests.Count);
        Assert.Contains("sysparm_offset=0", requests[0], StringComparison.Ordinal);
        Assert.Contains("sysparm_offset=2", requests[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("salesforce")]
    [InlineData("sharepoint")]
    [InlineData("hubspot")]
    [InlineData("zendesk")]
    [InlineData("servicenow")]
    public void RequiresConnectorConnectionForApiSources(string type)
    {
        Assert.Throws<ArgumentException>(() =>
            new HierarchySourceLevel(
                "Source",
                type,
                path: null,
                "id",
                parentKeyProperty: null,
                rowsPerParent: 0,
                expectedRecordCount: 0));

        var level = new HierarchySourceLevel(
            "Source",
            type,
            path: null,
            "id",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 0,
            connectorConnection: new HttpClient());
        Assert.Null(level.Path);
    }

    [Fact]
    public void ExcelRequiresAPathAndUsesAbsolutePath()
    {
        Assert.Throws<ArgumentException>(() =>
            new HierarchySourceLevel(
                "Source",
                "excel",
                path: null,
                "id",
                parentKeyProperty: null,
                rowsPerParent: 0,
                expectedRecordCount: 0));

        var level = new HierarchySourceLevel(
            "Source",
            "excel",
            "relative.xlsx",
            "id",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 0);
        Assert.NotNull(level.Path);
        Assert.True(Path.IsPathFullyQualified(level.Path!));
    }

    private static HierarchySourceLevel Source(
        string id,
        string type,
        string key,
        HttpClient client)
    {
        return new HierarchySourceLevel(
            id,
            type,
            path: null,
            key,
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 0,
            optionsJson: "{}",
            connectorConnection: client);
    }

    private static async Task<List<JsonElement>> ReadAllAsync(IHierarchyRecordReader reader)
    {
        var records = new List<JsonElement>();
        while (await reader.PeekAsync(CancellationToken.None) is JsonElement record)
        {
            records.Add(record.Clone());
            reader.Take();
        }
        return records;
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
