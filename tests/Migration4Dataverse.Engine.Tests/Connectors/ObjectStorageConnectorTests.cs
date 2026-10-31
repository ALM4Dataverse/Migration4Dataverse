using System.Text.Json;
using System.Collections;
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
    [InlineData("data/document.pdf", "binary", true)]
    [InlineData("data/folder/", "binary", false)]
    public void MatchesObjectStorageFormat(string key, string format, bool expected)
    {
        Assert.Equal(
            expected,
            ObjectStorageConnectorSupport.MatchesFormat(key, format, includeAllExtensions: false));
        Assert.Equal(
            format == "binary" ? expected : true,
            ObjectStorageConnectorSupport.MatchesFormat(key, format, includeAllExtensions: true));
    }

    [Fact]
    public void MatchesCustomObjectFormatExtensions()
    {
        Assert.True(ObjectStorageConnectorSupport.MatchesFormat(
            "incoming/records.RECORDS",
            "custom",
            includeAllExtensions: false,
            customExtensions: [".records"]));
        Assert.False(ObjectStorageConnectorSupport.MatchesFormat(
            "incoming/records.txt",
            "custom",
            includeAllExtensions: false,
            customExtensions: [".records"]));
        Assert.True(ObjectStorageConnectorSupport.MatchesFormat(
            "incoming/records.txt",
            "custom",
            includeAllExtensions: true,
            customExtensions: [".records"]));
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
            connectorConnection: new object(),
            formatType: "jsonl");
        await using var reader = new ObjectStorageHierarchyRecordReader(
            level,
            items,
            new ObjectStorageSourceOptions());

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
            connectorConnection: new object(),
            formatType: "csv",
            formatOptionsJson: JsonSerializer.Serialize(new CsvConnectorOptions()));
        await using var reader = new ObjectStorageHierarchyRecordReader(
            level,
            items,
            new ObjectStorageSourceOptions());

        var record = await reader.PeekAsync(CancellationToken.None);

        Assert.True(record.HasValue);
        Assert.Equal("1", record.Value.GetProperty("id").GetString());
        Assert.Equal("one", record.Value.GetProperty("name").GetString());
    }

    [Fact]
    public async Task ReadsBinaryObjectsDirectlyIntoRunScopedStaging()
    {
        using var files = new TemporaryDirectory();
        var stagingRoot = files.GetPath("staging");
        var content = new byte[] { 0, 1, 2, 255 };
        var item = new ObjectStorageItem(
            "folder/document.bin",
            (_, _) => throw new InvalidOperationException("The binary source should use its stream delegate."))
        {
            ContentType = "application/octet-stream",
            DownloadToStreamAsync = (stream, _) =>
            {
                stream.Write(content);
                return Task.CompletedTask;
            }
        };
        var level = new HierarchySourceLevel(
            "BinaryObjects",
            "azureblob",
            "folder/",
            "Key",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 0,
            connectorConnection: new object(),
            binaryStagingRoot: stagingRoot,
            formatType: "binary");
        await using var reader = new ObjectStorageHierarchyRecordReader(
            level,
            [item],
            new ObjectStorageSourceOptions());

        var record = await reader.PeekAsync(CancellationToken.None)
            ?? throw new InvalidDataException("The binary object source returned no record.");
        var fields = Assert.IsAssignableFrom<IDictionary>(
            MigrationBinaryValue.RestoreFromTransport(record, stagingRoot));
        var value = Assert.IsType<MigrationBinaryValue>(fields["Content"]);
        await using var stream = value.OpenRead();
        using var actual = new MemoryStream();
        await stream.CopyToAsync(actual);

        Assert.Equal("folder/document.bin", record.GetProperty("Key").GetString());
        Assert.Equal("document.bin", record.GetProperty("FileName").GetString());
        Assert.Equal(content, actual.ToArray());
    }

    [Fact]
    public async Task PassesCustomObjectsToTheSelectedFormatReader()
    {
        var item = new ObjectStorageItem("incoming/records.records", (path, _) =>
        {
            File.WriteAllText(path, "custom-format-content");
            return Task.CompletedTask;
        });
        var level = new HierarchySourceLevel(
            "CustomObjects",
            "s3",
            "incoming/",
            "Id",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 0,
            connectorConnection: new object(),
            formatType: "custom",
            formatExtensions: [".records"]);
        await using var reader = new ObjectStorageHierarchyRecordReader(
            level,
            [item],
            new ObjectStorageSourceOptions(),
            fileLevel =>
            {
                Assert.Equal("custom", fileLevel.FormatType);
                Assert.Equal("incoming/records.records", fileLevel.FormatObjectKey);
                return new CapturingFormatReader(
                    fileLevel.FormatObjectKey!,
                    File.ReadAllText(fileLevel.Path!));
            });

        var record = await reader.PeekAsync(CancellationToken.None)
            ?? throw new InvalidDataException("The custom object reader returned no record.");

        Assert.Equal("incoming/records.records", record.GetProperty("ObjectKey").GetString());
        Assert.Equal("custom-format-content", record.GetProperty("Content").GetString());
        reader.Take();
        Assert.Null(await reader.PeekAsync(CancellationToken.None));
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

    private sealed class CapturingFormatReader(string objectKey, string content) : IHierarchyRecordReader
    {
        private JsonElement? _lookahead;

        public long RecordCount { get; private set; }

        public ValueTask<JsonElement?> PeekAsync(CancellationToken cancellationToken)
        {
            if (!_lookahead.HasValue && RecordCount == 0)
            {
                _lookahead = JsonSerializer.SerializeToElement(new
                {
                    ObjectKey = objectKey,
                    Content = content
                });
                RecordCount = 1;
            }

            return ValueTask.FromResult(_lookahead);
        }

        public JsonElement Take()
        {
            var record = _lookahead
                ?? throw new InvalidOperationException("The custom format reader has no buffered record.");
            _lookahead = null;
            return record;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
