using System.Net;
using System.Text;
using System.Text.Json;
using Migration4Dataverse.Engine.Connectors;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.Tests.Connectors;

public sealed class HttpConnectorTests
{
    [Fact]
    public async Task ReadsAndSortsJsonRecords()
    {
        var requests = new List<string>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.ToString());
            return JsonResponse("""[{"id":2},{"id":1}]""");
        }));
        var level = new HierarchySourceLevel(
            "Source",
            "http",
            "https://example.test/records",
            "id",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 0,
            optionsJson: "{}",
            connectorConnection: client);
        await using var reader = new HttpHierarchyRecordReader(
            level,
            new HttpSourceOptions(),
            "http");

        Assert.Equal(1, (await reader.PeekAsync(CancellationToken.None))!.Value.GetProperty("id").GetInt32());
        reader.Take();
        Assert.Equal(2, (await reader.PeekAsync(CancellationToken.None))!.Value.GetProperty("id").GetInt32());
        reader.Take();
        Assert.Null(await reader.PeekAsync(CancellationToken.None));
        Assert.Equal(2, reader.RecordCount);
        Assert.Single(requests);
    }

    [Fact]
    public async Task FollowsODataNextLinkWithoutAddingOffset()
    {
        var requests = new List<string>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.ToString());
            return requests.Count == 1
                ? JsonResponse(
                    """{"value":[{"id":1},{"id":2}],"@odata.nextLink":"https://example.test/odata?$skiptoken=abc"}""")
                : JsonResponse("""{"value":[{"id":3}]}""");
        }));
        var level = new HierarchySourceLevel(
            "Source",
            "odata",
            "https://example.test/odata",
            "id",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 0,
            optionsJson: "{}",
            connectorConnection: client);
        await using var reader = new HttpHierarchyRecordReader(
            level,
            HttpSourceOptions.ApplyODataDefaults(new HttpSourceOptions { PageSize = 2 }),
            "odata");

        var ids = new List<int>();
        while (await reader.PeekAsync(CancellationToken.None) is JsonElement record)
        {
            ids.Add(record.GetProperty("id").GetInt32());
            reader.Take();
        }

        Assert.Equal(new[] { 1, 2, 3 }, ids);
        Assert.Equal(2, requests.Count);
        Assert.Contains("$top=2", requests[0], StringComparison.Ordinal);
        Assert.Contains("$skip=0", requests[0], StringComparison.Ordinal);
        Assert.Equal("https://example.test/odata?$skiptoken=abc", requests[1]);
    }

    [Fact]
    public async Task FollowsLinkHeader()
    {
        var requests = new List<string>();
        using var client = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.ToString());
            if (requests.Count == 1)
            {
                var response = JsonResponse("""[{"id":1}]""");
                response.Headers.TryAddWithoutValidation(
                    "Link",
                    "<https://example.test/records?page=2>; rel=\"next\"");
                return response;
            }
            return JsonResponse("""[{"id":2}]""");
        }));
        var level = new HierarchySourceLevel(
            "Source",
            "http",
            "https://example.test/records",
            "id",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 0,
            optionsJson: "{}",
            connectorConnection: client);
        await using var reader = new HttpHierarchyRecordReader(
            level,
            new HttpSourceOptions { NextLinkHeader = "Link" },
            "http");

        Assert.Equal(1, (await reader.PeekAsync(CancellationToken.None))!.Value.GetProperty("id").GetInt32());
        reader.Take();
        Assert.Equal(2, (await reader.PeekAsync(CancellationToken.None))!.Value.GetProperty("id").GetInt32());
        reader.Take();
        Assert.Null(await reader.PeekAsync(CancellationToken.None));
        Assert.Equal(2, requests.Count);
    }

    [Theory]
    [InlineData("value", true)]
    [InlineData("data.items", true)]
    [InlineData("missing", false)]
    public void FindsNestedJsonElements(string path, bool found)
    {
        using var document = JsonDocument.Parse("""{"value":[],"data":{"items":[]}}""");

        var element = HttpConnectorSupport.FindElement(document.RootElement, path);

        Assert.Equal(found, element.HasValue);
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
