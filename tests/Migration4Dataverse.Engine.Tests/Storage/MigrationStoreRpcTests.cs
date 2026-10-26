using System.Diagnostics;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.Tests.Storage;

public sealed class MigrationStoreRpcTests
{
    [Fact]
    public async Task OwnerHandlesConcurrentRemoteCalls()
    {
        using var database = new TemporaryDatabase();
        _ = MigrationStoreFactory.Open(database.Path);
        try
        {
            var client = MigrationStoreFactory.ConnectToOwner(database.Path);
            client.CreateRun("concurrent-run", "concurrent-run", "hash-1");
            client.StartStepActivity("concurrent-run", "shared-step");
            client.EnterStepActivity("concurrent-run", "shared-step");

            var progress = await Task.WhenAll(Enumerable.Range(0, 16).Select(index => Task.Run(() =>
            {
                if (index == 0)
                {
                    client.ExitStepActivity("concurrent-run", "shared-step");
                }

                return client.GetRunProgress("concurrent-run");
            })));
            Assert.All(progress, value => Assert.Equal(0, value.NonTerminalBatchCount));
        }
        finally
        {
            MigrationStoreFactory.StopOwnedServer(database.Path);
        }
    }

    [Fact]
    public async Task SeparatePowerShellProcessUsesTheExistingDatabaseOwner()
    {
        using var database = new TemporaryDatabase();
        _ = MigrationStoreFactory.Open(database.Path);
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(
                "$null = [System.Reflection.Assembly]::LoadFrom($env:M4D_ENGINE_ASSEMBLY); " +
                "$store = [Migration4Dataverse.Engine.Storage.MigrationStoreFactory]::Open($env:M4D_DATABASE_PATH); " +
                "if ($store.GetType().FullName -ne 'Migration4Dataverse.Engine.Storage.RemoteMigrationStoreProxy') { throw 'The client did not connect to the existing owner.' }; " +
                "$store.CreateRun('external-run', 'external-run', 'hash-1')");
            startInfo.Environment["M4D_ENGINE_ASSEMBLY"] = typeof(IMigrationStore).Assembly.Location;
            startInfo.Environment["M4D_DATABASE_PATH"] = database.Path;

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("PowerShell could not be started for the owner integration test.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("The PowerShell owner client did not finish within 30 seconds.");
            }

            Assert.True(
                process.ExitCode == 0,
                $"The PowerShell client failed with exit code {process.ExitCode}.{Environment.NewLine}" +
                $"stdout: {await standardOutput}{Environment.NewLine}stderr: {await standardError}");
            Assert.Equal("external-run", MigrationStoreFactory.Open(database.Path).GetDashboardRun("external-run").RunId);
        }
        finally
        {
            MigrationStoreFactory.StopOwnedServer(database.Path);
        }
    }

    [Fact]
    public void StoreOwnerRoundTripsComplexValuesAndAllowsParallelRunLeases()
    {
        using var database = new TemporaryDatabase();
        _ = MigrationStoreFactory.Open(database.Path);
        try
        {
            var client = MigrationStoreFactory.ConnectToOwner(database.Path);
            client.CreateRun("run-one", "run-one", "hash-1");
            client.CreateRun("run-two", "run-two", "hash-2");

            Assert.True(client.TryAcquireRunLease("run-one", "scheduler-one", TimeSpan.FromMinutes(1)));
            Assert.True(client.TryAcquireRunLease("run-two", "scheduler-two", TimeSpan.FromMinutes(1)));

            var recordId = Guid.NewGuid();
            client.SaveTargetRecordIds(
                "run-one",
                [new TargetRecordIdMapping("connection", "accounts", "id", "1", recordId)]);
            var lookup = new TargetRecordIdLookup("accounts", "id", "1");
            var targetRecordIds = client.GetTargetRecordIds("run-one", "connection", [lookup]);
            Assert.Equal(recordId, targetRecordIds[lookup]);

            client.SaveJoinInputRecords(
                "run-one",
                [new JoinInputRecordMapping("join", "parent", 0, 0, "{\"id\":1}", "source-batch", 0)]);
            client.SaveJoinRelationshipMatches(
                "run-one",
                "join",
                [new JoinRelationshipMatch(0, 0, "parent", "parent", 0, 0)]);

            var rowKey = new JoinInputRecordKey(0, 0);
            var matches = client.ReadJoinRelationshipMatches("run-one", "join", ["parent"], rowKey, rowKey);
            Assert.Equal("source-batch", client.ReadJoinInputRecordsByKeys(
                "run-one",
                "join",
                "parent",
                [rowKey])[rowKey].OriginBatchId);
            Assert.Equal("parent", matches[rowKey]["parent"].Alias);
            Assert.Equal("run-one", client.GetDashboardRun("run-one").RunId);
            Assert.Throws<KeyNotFoundException>(() => client.GetDashboardRun("missing"));
        }
        finally
        {
            MigrationStoreFactory.StopOwnedServer(database.Path);
        }
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Migration4Dataverse.Tests",
            Guid.NewGuid().ToString("N"));

        public TemporaryDatabase()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "migration.sqlite3");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
