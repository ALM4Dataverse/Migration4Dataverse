using Migration4Dataverse.Engine.Scheduling;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.Tests.Scheduling;

public sealed class SourceKeyCoordinatorTests
{
    [Fact]
    public async Task ReleasesAKeyWhenItsParentBatchIsIndexed()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun("run", "run", "hash");
        var coordinator = new SourceKeyCoordinator(store);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var wait = coordinator.WaitForSourceKeyAsync(
            "run",
            "Parents",
            "P001",
            cancellation.Token);
        store.EnqueueBatch(
            "run",
            "Parents",
            0,
            "parents-0",
            null,
            """{"Records":[{"Source":{"Id":"P001"}}]}""",
            sourceKeys: [new SourceKeyEntry("P001", 0)]);

        var reference = await wait;

        Assert.Equal("parents-0", reference.BatchId);
        Assert.Equal(0, reference.RecordIndex);
    }

    [Fact]
    public async Task RejectsAKeyThatIsAbsentWhenItsParentSourceCompletes()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun("run", "run", "hash");
        var coordinator = new SourceKeyCoordinator(store);
        coordinator.MarkSourceCompleted("run", "Parents");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => coordinator.WaitForSourceKeyAsync(
                "run",
                "Parents",
                "MISSING",
                CancellationToken.None));

        Assert.Contains("No source row", exception.Message, StringComparison.Ordinal);
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"migration4dataverse-source-key-index-{Guid.NewGuid():N}");

        public TemporaryDatabase()
        {
            Directory.CreateDirectory(_directory);
        }

        public string Path => System.IO.Path.Combine(_directory, "migration.sqlite3");

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
