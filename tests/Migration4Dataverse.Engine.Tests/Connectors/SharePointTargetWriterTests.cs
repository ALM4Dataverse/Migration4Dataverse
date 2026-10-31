using System.Net;
using System.Text;
using System.Text.Json;
using Migration4Dataverse.Engine.Connectors;

namespace Migration4Dataverse.Engine.Tests.Connectors;

public sealed class SharePointTargetWriterTests
{
    [Fact]
    public async Task UploadsStagedDocumentsAndRecognizesIdenticalReplays()
    {
        using var files = new TemporaryDirectory();
        var content = Encoding.ASCII.GetBytes("document content");
        var value = MigrationBinaryValue.FromBytes(
            content,
            files.Root,
            "report.txt",
            "text/plain");
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["FileName"] = "report.txt",
            ["ContentType"] = "text/plain",
            ["Content"] = value
        };
        var recordsJson = JsonSerializer.Serialize(
            MigrationBinaryValue.PrepareForTransport(new[] { record }, files.Root));
        var options = new SharePointTargetOptions { DriveId = "drive" };
        var putCount = 0;
        var downloadedCount = 0;
        byte[]? uploadedContent = null;
        using var client = new HttpClient(new StubHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.EndsWith("root:/report.txt", StringComparison.Ordinal))
            {
                return putCount == 0
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"id":"drive-item"}""", Encoding.UTF8, "application/json")
                    };
            }
            if (request.Method == HttpMethod.Get && path.EndsWith("/items/drive-item/content", StringComparison.Ordinal))
            {
                downloadedCount++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(content)
                };
            }
            if (request.Method == HttpMethod.Put && path.EndsWith("root:/report.txt:/content", StringComparison.Ordinal))
            {
                putCount++;
                uploadedContent = await request.Content!.ReadAsByteArrayAsync();
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("""{"id":"drive-item"}""", Encoding.UTF8, "application/json")
                };
            }
            throw new InvalidOperationException($"Unexpected SharePoint request: {request.Method} {path}");
        }))
        {
            BaseAddress = new Uri("https://graph.microsoft.com/v1.0/")
        };

        SharePointTargetWriter.WriteBatch(
            client,
            "Documents",
            "run",
            "batch-1",
            0,
            recordsJson,
            JsonSerializer.Serialize(options),
            files.Root);
        await SharePointTargetWriter.FinalizeAsync(
            client,
            "Documents",
            "run",
            JsonSerializer.Serialize(options),
            files.Root,
            files.Root,
            CancellationToken.None);
        await SharePointTargetWriter.FinalizeAsync(
            client,
            "Documents",
            "run",
            JsonSerializer.Serialize(options),
            files.Root,
            files.Root,
            CancellationToken.None);

        Assert.Equal(1, putCount);
        Assert.Equal(1, downloadedCount);
        Assert.Equal(content, uploadedContent);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return handler(request);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), $"migration4dataverse-sharepoint-target-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
