using Azure.Storage.Files.DataLake;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.Connectors;

internal static class DataLakeConnectionSupport
{
    public static DataLakeFileSystemClient GetFileSystemClient(
        object connection,
        string? fileSystemName,
        string description)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection is DataLakeFileSystemClient fileSystem)
        {
            return fileSystem;
        }
        if (connection is DataLakeServiceClient service)
        {
            if (string.IsNullOrWhiteSpace(fileSystemName))
            {
                throw new ArgumentException(
                    $"Azure Data Lake Storage Gen2 {description} requires Options.FileSystem " +
                    "when its connection is a DataLakeServiceClient.");
            }
            return service.GetFileSystemClient(fileSystemName);
        }

        throw new ArgumentException(
            $"Azure Data Lake Storage Gen2 {description} requires a DataLakeFileSystemClient " +
            $"or DataLakeServiceClient connection; received '{connection.GetType().FullName}'.");
    }

    public static void ValidateConnection(object connection, string description)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection is not (DataLakeFileSystemClient or DataLakeServiceClient))
        {
            throw new ArgumentException(
                $"Azure Data Lake Storage Gen2 {description} requires a DataLakeFileSystemClient " +
                $"or DataLakeServiceClient connection; received '{connection.GetType().FullName}'.");
        }
    }

    public static async Task<IReadOnlyList<ObjectStorageItem>> ListItemsAsync(
        DataLakeFileSystemClient fileSystem,
        ObjectStorageSourceOptions options,
        string? prefix,
        CancellationToken cancellationToken)
    {
        var items = new List<ObjectStorageItem>();
        await foreach (var path in fileSystem.GetPathsAsync(
                           prefix,
                           recursive: true,
                           userPrincipalName: false,
                           cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (path.IsDirectory == true ||
                !ObjectStorageConnectorSupport.MatchesFormat(
                    path.Name,
                    options.Format,
                    options.IncludeAllExtensions))
            {
                continue;
            }
            var file = fileSystem.GetFileClient(path.Name);
            items.Add(new ObjectStorageItem(
                path.Name,
                async (localPath, token) =>
                {
                    var response = await file.ReadAsync(token).ConfigureAwait(false);
                    await using var local = File.Create(localPath);
                    await response.Value.Content.CopyToAsync(local, token).ConfigureAwait(false);
                }));
        }
        items.Sort((left, right) => string.Compare(left.Key, right.Key, StringComparison.Ordinal));
        return items;
    }
}
