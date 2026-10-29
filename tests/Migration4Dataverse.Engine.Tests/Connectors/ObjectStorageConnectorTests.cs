using System.Text.Json;
using Azure.Storage.Files.DataLake;
using Migration4Dataverse.Engine.Connectors;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.Tests.Connectors;

public sealed class ObjectStorageConnectorTests
{
    [Theory]
    [InlineData("data/records.jsonl", "jsonl", true)]
    [InlineData("data/records.ndjson", "jsonl", true)]
    [InlineData("data/records.json", "jsonl", false)]
    [InlineData("data/records.csv", "csv", true)]
    [InlineData("data/records.parquet", "parquet", true)]
    [InlineData("data/records.txt", "csv", false)]
    public void MatchesObjectStorageFormat(string key, string format, bool expected)
    {
        Assert.Equal(
            expected,
            ObjectStorageConnectorSupport.MatchesFormat(key, format, includeAllExtensions: false));
        Assert.True(ObjectStorageConnectorSupport.MatchesFormat(key, format, includeAllExtensions: true));
    }

    [Fact]
    public async Task ReadsJsonLinesObjectsInSortedOrder()
    {
        using var files = new TemporaryDirectory();
        var first = files.GetPath("first.jsonl");
        var second = files.GetPath("second.jsonl");
        await File.WriteAllLinesAsync(first, new[] { """{"id":1}""", """{"id":2}""" });
        await File.WriteAllLinesAsync(second, new[] { """{"id":3}""" });
        var items = new[]
        {
            new ObjectStorageItem("first.jsonl", (path, _) =>
            {
                File.Copy(first, path);
                return Task.CompletedTask;
            }),
            new ObjectStorageItem("second.jsonl", (path, _) =>
            {
                File.Copy(second, path);
                return Task.CompletedTask;
            })
        };
        var level = new HierarchySourceLevel(
            "Source",
            "azureblob",
            "data/",
            "id",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 0,
            optionsJson: "{}",
            connectorConnection: new object());
        await using var reader = new ObjectStorageHierarchyRecordReader(
            level,
            items,
            new ObjectStorageSourceOptions { Format = "jsonl" });

        var ids = new List<long>();
        while (await reader.PeekAsync(CancellationToken.None) is JsonElement record)
        {
            ids.Add(record.GetProperty("id").GetInt64());
            reader.Take();
        }

        Assert.Equal(new long[] { 1, 2, 3 }, ids);
        Assert.Equal(3, reader.RecordCount);
    }

    [Fact]
    public async Task ReadsCsvObjectsWithNestedOptions()
    {
        using var files = new TemporaryDirectory();
        var source = files.GetPath("records.csv");
        await File.WriteAllLinesAsync(source, new[] { "id,name", "1,one" });
        var items = new[]
        {
            new ObjectStorageItem("records.csv", (path, _) =>
            {
                File.Copy(source, path);
                return Task.CompletedTask;
            })
        };
        var level = new HierarchySourceLevel(
            "Source",
            "s3",
            "data/",
            "id",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 0,
            optionsJson: "{}",
            connectorConnection: new object());
        await using var reader = new ObjectStorageHierarchyRecordReader(
            level,
            items,
            new ObjectStorageSourceOptions
            {
                Format = "csv",
                Csv = JsonSerializer.SerializeToElement(new CsvConnectorOptions())
            });

        var record = await reader.PeekAsync(CancellationToken.None);

        Assert.True(record.HasValue);
        Assert.Equal("1", record.Value.GetProperty("id").GetString());
        Assert.Equal("one", record.Value.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("jsonl", ".jsonl")]
    [InlineData("csv", ".csv")]
    [InlineData("parquet", ".parquet")]
    public void BuildsObjectTargetStagingPath(string format, string extension)
    {
        var path = ObjectStorageTargetWriter.GetStagingPath(
            "/tmp/staging",
            "run",
            "Target Step",
            format);

        Assert.EndsWith($"object-targets/run/Target-Step{extension}", path, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatesNewObjectStorageConnections()
    {
        Assert.Throws<ArgumentException>(() =>
            ObjectStorageConnectionSupport.ValidateConnection("adlsgen2", new object(), "source"));
        Assert.Throws<ArgumentException>(() =>
            ObjectStorageConnectionSupport.ValidateConnection("gcs", new object(), "source"));
        Assert.Throws<ArgumentException>(() =>
            ObjectStorageConnectionSupport.GetGcsBucket(null, "source"));
    }

    [Theory]
    [InlineData("adlsgen2", "data/records/")]
    [InlineData("gcs", "data/records/")]
    public void KeepsRemoteSourcePathsRaw(string type, string path)
    {
        var level = new HierarchySourceLevel(
            "Source",
            type,
            path,
            "id",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 0,
            connectorConnection: new object());

        Assert.Equal(path, level.Path);
    }

    [Fact]
    public void RequiresConnectorConnectionsForNewRemoteSources()
    {
        foreach (var type in new[] { "adlsgen2", "gcs", "mongodb" })
        {
            Assert.Throws<ArgumentException>(() =>
                new HierarchySourceLevel(
                    "Source",
                    type,
                    null,
                    "id",
                    parentKeyProperty: null,
                    rowsPerParent: 0,
                    expectedRecordCount: 0));
        }

        var mongo = new HierarchySourceLevel(
            "Source",
            "mongodb",
            path: "ignored",
            "id",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 0,
            connectorConnection: new object());
        Assert.Null(mongo.Path);
    }

    [Fact]
    public void ResolvesDataLakeConnections()
    {
        var fileSystem = new DataLakeFileSystemClient(
            new Uri("https://example.dfs.core.windows.net/records"));
        Assert.Same(
            fileSystem,
            DataLakeConnectionSupport.GetFileSystemClient(fileSystem, null, "source"));

        var service = new DataLakeServiceClient(
            new Uri("https://example.dfs.core.windows.net"));
        var resolved = DataLakeConnectionSupport.GetFileSystemClient(service, "records", "source");
        Assert.Equal("records", resolved.Name);
        Assert.Throws<ArgumentException>(() =>
            DataLakeConnectionSupport.GetFileSystemClient(service, null, "source"));
    }

    [Fact]
    public void ParsesMongoOptions()
    {
        var options = MongoSourceOptions.FromJson(
            """{"Database":"db","Collection":"records","Sort":"_id"}""",
            "Source");
        Assert.Equal("records", options.Collection);
        Assert.Equal("db", options.Database);
        Assert.Throws<ArgumentException>(() => MongoSourceOptions.FromJson("{}", "Source"));

        var target = new MongoTargetOptions { KeyColumns = ["_id"] };
        target.Validate("Target");
        Assert.Throws<ArgumentException>(() =>
            new MongoTargetOptions { KeyColumns = [""] }.Validate("Target"));
    }

    [Fact]
    public void MongoBatchMarkerIsStableAndDistinct()
    {
        var first = MongoConnectionSupport.GetBatchMarkerId("run", "step", "batch");
        var second = MongoConnectionSupport.GetBatchMarkerId("run", "step", "batch");
        var other = MongoConnectionSupport.GetBatchMarkerId("run", "step", "other");

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            $"migration4dataverse-tests-{Guid.NewGuid():N}");

        public TemporaryDirectory()
        {
            Directory.CreateDirectory(_path);
        }

        public string GetPath(string name)
        {
            return Path.Combine(_path, name);
        }

        public void Dispose()
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }
    }
}
