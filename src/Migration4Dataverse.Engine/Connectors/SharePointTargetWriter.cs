using System.Collections;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.Connectors;

public sealed class SharePointTargetOptions
{
    public string? DriveId { get; init; }

    public string ParentItemId { get; init; } = "root";

    public string FileNameColumn { get; init; } = "FileName";

    public string ContentColumn { get; init; } = "Content";

    public string ContentTypeColumn { get; init; } = "ContentType";

    public string? ContentType { get; init; }

    public int TimeoutSeconds { get; init; } = 100;

    public void Validate(string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(DriveId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ParentItemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(FileNameColumn);
        ArgumentException.ThrowIfNullOrWhiteSpace(ContentColumn);
        ArgumentException.ThrowIfNullOrWhiteSpace(ContentTypeColumn);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TimeoutSeconds);
    }

    public static SharePointTargetOptions FromJson(string optionsJson, string stepId)
    {
        return RestJsonConnectorSupport.DeserializeOptions<SharePointTargetOptions>(optionsJson, stepId);
    }
}

public static class SharePointTargetWriter
{
    private const long MaximumSimpleUploadLength = 250L * 1024 * 1024;

    public static void WriteBatch(
        HttpClient client,
        string stepId,
        string runId,
        string batchId,
        long sequence,
        string recordsJson,
        string optionsJson,
        string stagingRoot)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        var options = SharePointTargetOptions.FromJson(optionsJson, stepId);
        options.Validate(stepId);
        ValidateClient(client, stepId);
        FileBatchTargetWriter.WriteSingleFileTargetBatch(
            GetStagingPath(stagingRoot, runId, stepId),
            runId,
            stepId,
            batchId,
            sequence,
            recordsJson);
    }

    public static async Task FinalizeAsync(
        HttpClient client,
        string stepId,
        string runId,
        string optionsJson,
        string stagingRoot,
        string binaryStagingRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryStagingRoot);
        var options = SharePointTargetOptions.FromJson(optionsJson, stepId);
        options.Validate(stepId);
        ValidateClient(client, stepId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        var stagedFile = GetStagingPath(stagingRoot, runId, stepId);
        foreach (var record in FileBatchTargetWriter.ReadSingleFileTargetRecords(
                     stagedFile,
                     runId,
                     stepId))
        {
            timeout.Token.ThrowIfCancellationRequested();
            var restored = MigrationBinaryValue.RestoreFromTransport(record, binaryStagingRoot);
            if (restored is not IDictionary fields ||
                fields[options.ContentColumn] is not MigrationBinaryValue content)
            {
                throw new InvalidDataException(
                    $"SharePoint target '{stepId}' requires a MigrationBinaryValue in field '{options.ContentColumn}'.");
            }
            if (fields[options.FileNameColumn] is not string fileName ||
                string.IsNullOrWhiteSpace(fileName) ||
                fileName.IndexOfAny(['/', '\\']) >= 0 ||
                fileName is "." or "..")
            {
                throw new InvalidDataException(
                    $"SharePoint target '{stepId}' requires a single file name in field '{options.FileNameColumn}'.");
            }
            var rowContentType = fields[options.ContentTypeColumn] as string;
            var contentType = !string.IsNullOrWhiteSpace(options.ContentType)
                ? options.ContentType
                : !string.IsNullOrWhiteSpace(rowContentType)
                    ? rowContentType
                    : !string.IsNullOrWhiteSpace(content.ContentType)
                        ? content.ContentType
                        : content.DetectMimeType();
            await UploadDocumentAsync(
                client,
                options,
                stepId,
                fileName,
                content,
                contentType,
                timeout.Token).ConfigureAwait(false);
        }
    }

    public static string GetStagingPath(string stagingRoot, string runId, string stepId)
    {
        var safeStepId = new string(stepId
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-')
            .ToArray());
        if (string.IsNullOrWhiteSpace(safeStepId))
        {
            safeStepId = "step";
        }
        var runHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId))).ToLowerInvariant();
        return Path.Combine(
            Path.GetFullPath(stagingRoot),
            "sharepoint-targets",
            runHash,
            $"{safeStepId}.jsonl");
    }

    private static async Task UploadDocumentAsync(
        HttpClient client,
        SharePointTargetOptions options,
        string stepId,
        string fileName,
        MigrationBinaryValue content,
        string contentType,
        CancellationToken cancellationToken)
    {
        if (content.Length > MaximumSimpleUploadLength)
        {
            throw new InvalidDataException(
                $"SharePoint target '{stepId}' cannot upload '{fileName}' because its {content.Length}-byte " +
                "content exceeds the 250 MiB simple-upload limit.");
        }

        var itemUri = BuildItemUri(client, options, fileName);
        using (var existingResponse = await client.GetAsync(
                   itemUri,
                   HttpCompletionOption.ResponseHeadersRead,
                   cancellationToken).ConfigureAwait(false))
        {
            if (existingResponse.IsSuccessStatusCode)
            {
                var existingId = await ReadDriveItemIdAsync(existingResponse, stepId, fileName, cancellationToken)
                    .ConfigureAwait(false);
                var existingHash = await ComputeRemoteHashAsync(
                    client,
                    options.DriveId!,
                    existingId,
                    cancellationToken).ConfigureAwait(false);
                if (string.Equals(existingHash, content.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                throw new InvalidDataException(
                    $"SharePoint target '{fileName}' already exists with different content.");
            }
            if (existingResponse.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                existingResponse.EnsureSuccessStatusCode();
            }
        }

        var uploadUri = new Uri(itemUri.AbsoluteUri + ":/content", UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Put, uploadUri);
        request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        await using var stream = content.OpenRead();
        request.Content = new StreamContent(stream);
        request.Content.Headers.ContentLength = content.Length;
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        using var uploadResponse = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (uploadResponse.StatusCode is System.Net.HttpStatusCode.Conflict or
            System.Net.HttpStatusCode.PreconditionFailed)
        {
            var concurrentHash = await GetExistingHashAsync(
                client,
                itemUri,
                options.DriveId!,
                stepId,
                fileName,
                cancellationToken).ConfigureAwait(false);
            if (string.Equals(concurrentHash, content.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            throw new InvalidDataException(
                $"SharePoint target '{fileName}' was created concurrently with different content.");
        }
        uploadResponse.EnsureSuccessStatusCode();
    }

    private static async Task<string?> GetExistingHashAsync(
        HttpClient client,
        Uri itemUri,
        string driveId,
        string stepId,
        string fileName,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            itemUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        var id = await ReadDriveItemIdAsync(response, stepId, fileName, cancellationToken).ConfigureAwait(false);
        return await ComputeRemoteHashAsync(client, driveId, id, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadDriveItemIdAsync(
        HttpResponseMessage response,
        string stepId,
        string fileName,
        CancellationToken cancellationToken)
    {
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("id", out var id) ||
            id.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(id.GetString()))
        {
            throw new InvalidDataException(
                $"SharePoint target '{stepId}' received an existing file '{fileName}' without an item ID.");
        }
        return id.GetString()!;
    }

    private static async Task<string> ComputeRemoteHashAsync(
        HttpClient client,
        string driveId,
        string itemId,
        CancellationToken cancellationToken)
    {
        var uri = RestJsonConnectorSupport.BuildUri(
            client,
            $"drives/{Uri.EscapeDataString(driveId)}/items/{Uri.EscapeDataString(itemId)}/content");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static Uri BuildItemUri(
        HttpClient client,
        SharePointTargetOptions options,
        string fileName)
    {
        var drive = Uri.EscapeDataString(options.DriveId!);
        var parent = string.Equals(options.ParentItemId, "root", StringComparison.OrdinalIgnoreCase)
            ? "root"
            : $"items/{Uri.EscapeDataString(options.ParentItemId)}";
        var name = Uri.EscapeDataString(fileName);
        return RestJsonConnectorSupport.BuildUri(
            client,
            $"drives/{drive}/{parent}:/{name}");
    }

    private static void ValidateClient(HttpClient client, string stepId)
    {
        if (client.BaseAddress is null || !client.BaseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException(
                $"SharePoint target '{stepId}' requires an HttpClient with an absolute BaseAddress.");
        }
    }
}
