using System.Text.Json;
using Migration4Dataverse.Engine.Scheduling;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.Tests.Scheduling;

public sealed class JsonLinesHierarchyBatchSourceTests
{
    [Fact]
    public async Task StreamsNestedRowsInBatchesAndResumesFromContiguousCheckpoint()
    {
        using var sourceFiles = new TemporarySourceFiles();
        sourceFiles.Write(
            "accounts.jsonl",
            """{"SourceId":"A001","Name":"Account 1"}""",
            """{"SourceId":"A002","Name":"Account 2"}""");
        sourceFiles.Write(
            "contacts.jsonl",
            """{"SourceId":"C001","AccountSourceId":"A001"}""",
            """{"SourceId":"C002","AccountSourceId":"A001"}""",
            """{"SourceId":"C003","AccountSourceId":"A002"}""",
            """{"SourceId":"C004","AccountSourceId":"A002"}""");
        sourceFiles.Write(
            "projects.jsonl",
            """{"SourceId":"P001","ContactSourceId":"C001"}""",
            """{"SourceId":"P002","ContactSourceId":"C002"}""",
            """{"SourceId":"P003","ContactSourceId":"C003"}""",
            """{"SourceId":"P004","ContactSourceId":"C004"}""");

        var source = CreateSource(sourceFiles, batchSize: 1);
        var initialCheckpoint = new MigrationStepCheckpoint("run", "Accounts", -1, null, null);
        var batches = await ReadAllAsync(source.CreateReader()(initialCheckpoint, CancellationToken.None));

        Assert.Equal(2, batches.Count);
        using (var firstPayload = JsonDocument.Parse(batches[0].PayloadJson))
        {
            var root = Assert.Single(firstPayload.RootElement.GetProperty("Records").EnumerateArray().ToArray());
            Assert.Equal("A001", root.GetProperty("Source").GetProperty("SourceId").GetString());
            var contacts = root.GetProperty("Children").GetProperty("Contacts").EnumerateArray().ToArray();
            Assert.Equal(2, contacts.Length);
            Assert.Single(contacts[0].GetProperty("Children").GetProperty("Projects").EnumerateArray());
        }

        using var firstCursor = JsonDocument.Parse(batches[0].CursorAfterJson!);
        Assert.Equal("A001", firstCursor.RootElement.GetProperty("LastKey").GetString());

        var resumedCheckpoint = new MigrationStepCheckpoint("run", "Accounts", 0, batches[0].CursorAfterJson, null);
        var resumedBatches = await ReadAllAsync(
            source.CreateReader()(resumedCheckpoint, CancellationToken.None));
        Assert.Single(resumedBatches);
        using var resumedPayload = JsonDocument.Parse(resumedBatches[0].PayloadJson);
        var resumedRoot = Assert.Single(
            resumedPayload.RootElement.GetProperty("Records").EnumerateArray().ToArray());
        Assert.Equal("A002", resumedRoot.GetProperty("Source").GetProperty("SourceId").GetString());
    }

    [Fact]
    public async Task RejectsDependentRowsThatAreNotGroupedByParentKey()
    {
        using var sourceFiles = new TemporarySourceFiles();
        sourceFiles.Write(
            "accounts.jsonl",
            """{"SourceId":"A001"}""",
            """{"SourceId":"A002"}""");
        sourceFiles.Write(
            "contacts.jsonl",
            """{"SourceId":"C001","AccountSourceId":"A002"}""",
            """{"SourceId":"C002","AccountSourceId":"A001"}""");
        sourceFiles.Write(
            "projects.jsonl",
            """{"SourceId":"P001","ContactSourceId":"C001"}""",
            """{"SourceId":"P002","ContactSourceId":"C002"}""");

        var source = CreateSource(sourceFiles, batchSize: 2);
        var checkpoint = new MigrationStepCheckpoint("run", "Accounts", -1, null, null);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadAllAsync(source.CreateReader()(checkpoint, CancellationToken.None)));
    }

    [Fact]
    public async Task RejectsChildRowsNotOrderedByKeyWithinParent()
    {
        using var sourceFiles = new TemporarySourceFiles();
        sourceFiles.Write("accounts.jsonl", """{"SourceId":"A001"}""");
        sourceFiles.Write(
            "contacts.jsonl",
            """{"SourceId":"C002","AccountSourceId":"A001"}""",
            """{"SourceId":"C001","AccountSourceId":"A001"}""");

        var contacts = new JsonLinesHierarchyLevel(
            "Contacts",
            sourceFiles.GetPath("contacts.jsonl"),
            "SourceId",
            "AccountSourceId",
            rowsPerParent: 0,
            expectedRecordCount: 2);
        var accounts = new JsonLinesHierarchyLevel(
            "Accounts",
            sourceFiles.GetPath("accounts.jsonl"),
            "SourceId",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 1,
            children: new[] { contacts });
        var source = new JsonLinesHierarchyBatchSource(accounts, batchSize: 1);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadAllAsync(source.CreateReader()(
                new MigrationStepCheckpoint("run", "Accounts", -1, null, null),
                CancellationToken.None)));
    }

    [Fact]
    public async Task StreamsVariableChildCountsIncludingParentsWithoutChildren()
    {
        using var sourceFiles = new TemporarySourceFiles();
        sourceFiles.Write(
            "accounts.jsonl",
            """{"SourceId":"A001"}""",
            """{"SourceId":"A002"}""",
            """{"SourceId":"A003"}""");
        sourceFiles.Write(
            "contacts.jsonl",
            """{"SourceId":"C001","AccountSourceId":"A001"}""",
            """{"SourceId":"C002","AccountSourceId":"A001"}""",
            """{"SourceId":"C003","AccountSourceId":"A003"}""");

        var contacts = new JsonLinesHierarchyLevel(
            "Contacts",
            sourceFiles.GetPath("contacts.jsonl"),
            "SourceId",
            "AccountSourceId",
            rowsPerParent: 0,
            expectedRecordCount: 3);
        var accounts = new JsonLinesHierarchyLevel(
            "Accounts",
            sourceFiles.GetPath("accounts.jsonl"),
            "SourceId",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 3,
            children: new[] { contacts });
        var source = new JsonLinesHierarchyBatchSource(accounts, batchSize: 3);
        var checkpoint = new MigrationStepCheckpoint("run", "Accounts", -1, null, null);

        var batches = await ReadAllAsync(source.CreateReader()(checkpoint, CancellationToken.None));

        using var payload = JsonDocument.Parse(Assert.Single(batches).PayloadJson);
        var records = payload.RootElement.GetProperty("Records").EnumerateArray().ToArray();
        Assert.Equal(3, records.Length);
        Assert.Equal(2, records[0].GetProperty("Children").GetProperty("Contacts").GetArrayLength());
        Assert.Empty(records[1].GetProperty("Children").GetProperty("Contacts").EnumerateArray());
        Assert.Single(records[2].GetProperty("Children").GetProperty("Contacts").EnumerateArray());
    }

    [Fact]
    public async Task RejectsMissingCheckpointKeyInsteadOfRestartingFromTheBeginning()
    {
        using var sourceFiles = new TemporarySourceFiles();
        sourceFiles.Write("accounts.jsonl", """{"SourceId":"A001"}""");
        sourceFiles.Write(
            "contacts.jsonl",
            """{"SourceId":"C001","AccountSourceId":"A001"}""");
        sourceFiles.Write("projects.jsonl", """{"SourceId":"P001","ContactSourceId":"C001"}""");

        var source = CreateSource(sourceFiles, batchSize: 1);
        var checkpoint = new MigrationStepCheckpoint(
            "run",
            "Accounts",
            0,
            """{"LastKey":"A000"}""",
            null);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadAllAsync(source.CreateReader()(checkpoint, CancellationToken.None)));
    }

    private static JsonLinesHierarchyBatchSource CreateSource(
        TemporarySourceFiles files,
        int batchSize)
    {
        var projects = new JsonLinesHierarchyLevel(
            "Projects",
            files.GetPath("projects.jsonl"),
            "SourceId",
            "ContactSourceId",
            rowsPerParent: 1,
            expectedRecordCount: files.Count("projects.jsonl"));
        var contacts = new JsonLinesHierarchyLevel(
            "Contacts",
            files.GetPath("contacts.jsonl"),
            "SourceId",
            "AccountSourceId",
            rowsPerParent: 2,
            expectedRecordCount: files.Count("contacts.jsonl"),
            children: new[] { projects });
        var accounts = new JsonLinesHierarchyLevel(
            "Accounts",
            files.GetPath("accounts.jsonl"),
            "SourceId",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: files.Count("accounts.jsonl"),
            children: new[] { contacts });
        return new JsonLinesHierarchyBatchSource(accounts, batchSize);
    }

    private static async Task<List<MigrationWorkBatch>> ReadAllAsync(
        IAsyncEnumerable<MigrationWorkBatch> source)
    {
        var batches = new List<MigrationWorkBatch>();
        await foreach (var batch in source)
        {
            batches.Add(batch);
        }

        return batches;
    }

    private sealed class TemporarySourceFiles : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            $"migration4dataverse-jsonl-source-{Guid.NewGuid():N}");

        public TemporarySourceFiles()
        {
            Directory.CreateDirectory(_directory);
        }

        public string GetPath(string fileName)
        {
            return Path.Combine(_directory, fileName);
        }

        public void Write(string fileName, params string[] lines)
        {
            File.WriteAllLines(GetPath(fileName), lines);
        }

        public long Count(string fileName)
        {
            return File.ReadLines(GetPath(fileName)).LongCount();
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
