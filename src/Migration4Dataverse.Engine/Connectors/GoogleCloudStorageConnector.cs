using Google.Cloud.Storage.V1;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.Connectors;

internal static class GoogleCloudStorageConnectorSupport
{
    public static async Task<IReadOnlyList<ObjectStorageItem>> ListItemsAsync(
        StorageClient client,
        string bucket,
        ObjectStorageSourceOptions options,
        string formatType,
        IReadOnlyList<string> formatExtensions,
        string? prefix,
        CancellationToken cancellationToken)
    {
        var items = new List<ObjectStorageItem>();
        await foreach (var item in client.ListObjectsAsync(bucket, prefix).ConfigureAwait(false))
        {
            if (item.Name is null ||
                !ObjectStorageConnectorSupport.MatchesFormat(
                    item.Name,
                    formatType,
                    options.IncludeAllExtensions,
                    formatExtensions))
            {
                continue;
            }
            var key = item.Name;
            items.Add(new ObjectStorageItem(
                key,
                async (localPath, token) =>
                {
                    await using var file = File.Create(localPath);
                    await client.DownloadObjectAsync(
                        bucket,
                        key,
                        file,
                        cancellationToken: token).ConfigureAwait(false);
                })
            {
                ContentType = item.ContentType,
                DownloadToStreamAsync = (stream, token) => client.DownloadObjectAsync(
                    bucket,
                    key,
                    stream,
                    cancellationToken: token)
            });
        }
        items.Sort((left, right) => string.Compare(left.Key, right.Key, StringComparison.Ordinal));
        return items;
    }
}
