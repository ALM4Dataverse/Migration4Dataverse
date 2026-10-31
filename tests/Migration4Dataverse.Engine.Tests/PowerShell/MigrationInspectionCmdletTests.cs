using System.Collections;
using System.Management.Automation;
using System.Text.Json;
using Migration4Dataverse.Engine.Connectors;
using Migration4Dataverse.Engine.PowerShell;
using Migration4Dataverse.Engine.Storage;
using PowerShellRuntime = System.Management.Automation.PowerShell;

namespace Migration4Dataverse.Engine.Tests.PowerShell;

public sealed class MigrationInspectionCmdletTests
{
    [Fact]
    public void InspectionCmdletsExposeRunStepBatchErrorsRowsAndRetryHistory()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = directory.GetPath("migration.sqlite3");
        const string runId = "inspection-run";
        const string batchId = "inspection-batch";
        const string stepId = "Transform";
        var store = new SqliteMigrationStore(databasePath);
        store.CreateRun(
            runId,
            "inspection-test",
            "hash",
            pipelineJson: """
                [{"Id":"Transform","Inputs":[],"CompletionDependencies":[],"HasSource":false,"Type":"transform"}]
                """,
            debugCaptureEnabled: true);
        store.EnqueueBatch(
            runId,
            stepId,
            0,
            batchId,
            null,
            """{"Records":[{"Name":"before"}],"__LineageReferences":[[]]}""");
        Assert.NotNull(store.TryLeaseNextBatch(runId, "worker", TimeSpan.FromMinutes(1)));
        store.TryCompleteBatchWithErrorPolicy(
            batchId,
            "worker",
            """{"succeeded":0,"failed":[{"recordIndex":0,"error":"invalid name"}]}""",
            dependencyEvents: null,
            lastError: "invalid name",
            maxErrorRatePercent: 0,
            rowErrors: [new MigrationRowError(0, "invalid name")]);
        store.SetRunStatus(runId, MigrationRunStatus.Failed);
        store.CaptureDebugSnapshots(
        [
            new MigrationDebugSnapshot(
                runId,
                batchId,
                stepId,
                "TransformInput",
                0,
                1,
                "inspection-lineage",
                Array.Empty<string>(),
                """{"Name":"before"}""",
                DateTimeOffset.UtcNow)
        ]);

        using var powershell = ImportEngineCmdlets();
        var run = InvokeSingle<MigrationRunSummary>(
            powershell,
            "Get-MigrationRun",
            ("DatabasePath", databasePath),
            ("RunId", runId));
        Assert.Equal("Failed", run.Status);
        Assert.Equal(stepId, Assert.Single(run.Steps).Id);

        var step = InvokeSingle<MigrationDashboardStep>(
            powershell,
            "Get-MigrationStep",
            ("DatabasePath", databasePath),
            ("RunId", runId),
            ("StepId", stepId));
        Assert.Equal(1, step.BatchesWithErrors);

        var batch = InvokeSingle<MigrationDashboardBatch>(
            powershell,
            "Get-MigrationBatch",
            ("DatabasePath", databasePath),
            ("RunId", runId),
            ("StepId", stepId));
        Assert.Equal(batchId, batch.BatchId);

        var error = InvokeSingle<MigrationErrorDetail>(
            powershell,
            "Get-MigrationError",
            ("DatabasePath", databasePath),
            ("RunId", runId));
        Assert.Equal("Batch", error.Scope);
        Assert.Equal("invalid name", Assert.Single(error.RowErrors).Error);
        Assert.Contains("\"Name\":\"before\"", error.RowErrors[0].PayloadJson, StringComparison.Ordinal);

        var retryHistory = InvokeAll<MigrationBatchRetryHistory>(
            powershell,
            "Get-MigrationRetryHistory",
            ("DatabasePath", databasePath),
            ("RunId", runId));
        Assert.Empty(retryHistory);

        var page = InvokeSingle<MigrationDebugSnapshotPage>(
            powershell,
            "Get-MigrationDebugRow",
            ("DatabasePath", databasePath),
            ("RunId", runId),
            ("StepId", stepId),
            ("StageName", "TransformInput"));
        Assert.Equal(1, page.TotalCount);
        var trace = InvokeAll<MigrationDebugSnapshot>(
            powershell,
            "Get-MigrationDebugRow",
            ("DatabasePath", databasePath),
            ("RunId", runId),
            ("Trace", true),
            ("LineageId", "inspection-lineage"));
        Assert.Single(trace);

        var recovery = InvokeSingle<MigrationRecoveryResult>(
            powershell,
            "Invoke-MigrationRecovery",
            ("DatabasePath", databasePath),
            ("RunId", runId),
            ("StepId", stepId),
            ("BatchId", batchId),
            ("RecordIndex", 0),
            ("PayloadJson", """{"Name":"after"}"""));
        Assert.Equal(1, recovery.QueuedBatchCount);
        Assert.NotNull(recovery.QueuedBatchId);
        Assert.Contains(
            "\"Name\":\"after\"",
            store.GetBatch(recovery.QueuedBatchId)!.PayloadJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveryCmdletQueuesBatchReplayAndApprovesSourceContinuation()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = directory.GetPath("migration.sqlite3");
        var store = new SqliteMigrationStore(databasePath);
        CreateFailedBatch(store, "replay-run", "Target", "replay-batch");

        using var powershell = ImportEngineCmdlets();
        var replay = InvokeSingle<MigrationRecoveryResult>(
            powershell,
            "Invoke-MigrationRecovery",
            ("DatabasePath", databasePath),
            ("RunId", "replay-run"),
            ("RetryFailedBatches", true));
        Assert.Equal(1, replay.QueuedBatchCount);
        Assert.Equal("Pending", store.GetBatch("replay-batch")!.State.ToString());
        var history = InvokeAll<MigrationBatchRetryHistory>(
            powershell,
            "Get-MigrationRetryHistory",
            ("DatabasePath", databasePath),
            ("RunId", "replay-run"));
        Assert.Single(history);

        const string sourceRunId = "source-continuation-run";
        store.CreateRun(
            sourceRunId,
            "source-continuation-test",
            "hash",
            pipelineJson: """
                [{"Id":"Source","Inputs":[],"CompletionDependencies":[],"HasSource":true,"Type":"source"}]
                """);
        store.SetRunFailure(
            sourceRunId,
            new MigrationRunFailure(
                "Source reader",
                "Source",
                "The source could not read the next page.",
                "System.IO.IOException",
                "The source could not read the next page.",
                DateTimeOffset.UtcNow));
        store.SetRunStatus(sourceRunId, MigrationRunStatus.Failed);

        var continuation = InvokeSingle<MigrationRecoveryResult>(
            powershell,
            "Invoke-MigrationRecovery",
            ("DatabasePath", databasePath),
            ("RunId", sourceRunId),
            ("StepId", "Source"),
            ("ContinueAfterFailure", true));
        Assert.True(continuation.ContinuationAllowed);
        Assert.True(store.IsStepFailureContinuationAllowed(sourceRunId, "Source"));

        var sourceError = InvokeSingle<MigrationErrorDetail>(
            powershell,
            "Get-MigrationError",
            ("DatabasePath", databasePath),
            ("RunId", sourceRunId));
        Assert.Equal("Run", sourceError.Scope);
        Assert.Equal("Source reader", sourceError.RunFailure?.Phase);
    }

    [Fact]
    public void PreviewAndRuntimeMetricCmdletsReturnDashboardData()
    {
        var step = PSObject.AsPSObject(new Hashtable(StringComparer.Ordinal)
        {
            ["Id"] = "Source",
            ["Type"] = "source",
            ["Inputs"] = Array.Empty<string>()
        });
        using var powershell = ImportEngineCmdlets();

        var preview = InvokeSingle<System.Text.Json.JsonElement>(
            powershell,
            "Get-MigrationConfigurationPreview",
            ("Steps", new[] { step }),
            ("Connections", new Hashtable(StringComparer.Ordinal)));
        Assert.Equal("Source", preview.GetProperty("steps")[0].GetProperty("id").GetString());

        var metrics = InvokeSingle<MigrationProcessMetricSnapshot>(
            powershell,
            "Get-MigrationProcessMetric");
        Assert.True(metrics.LogicalProcessorCount > 0);
        Assert.True(metrics.WorkingSetBytes > 0);
    }

    [Fact]
    public void DebugBinaryCmdletRestoresCapturedBinaryArtifact()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = directory.GetPath("migration.sqlite3");
        const string runId = "binary-inspection-run";
        const string batchId = "binary-inspection-batch";
        const string stepId = "Documents";
        const string stageName = "TargetWriteInput";
        const string lineageId = "binary-inspection-lineage";
        var capturedUtc = DateTimeOffset.UtcNow;
        var bytes = new byte[] { 0x25, 0x50, 0x44, 0x46 };
        var stagingRoot = MigrationBinaryValue.GetRunStagingRoot(databasePath, runId);
        var binaryValue = MigrationBinaryValue.FromBytes(bytes, stagingRoot, "report.pdf", "application/pdf");
        var payload = MigrationBinaryValue.PrepareForTransport(
            new Dictionary<string, object?> { ["Attachment"] = binaryValue },
            stagingRoot);
        var store = new SqliteMigrationStore(databasePath);
        store.CreateRun(runId, "binary-inspection-test", "hash", debugCaptureEnabled: true);
        store.EnqueueBatch(runId, stepId, 0, batchId, null, "{}");
        store.CaptureDebugSnapshots(
        [
            new MigrationDebugSnapshot(
                runId,
                batchId,
                stepId,
                stageName,
                0,
                1,
                lineageId,
                Array.Empty<string>(),
                JsonSerializer.Serialize(payload),
                capturedUtc)
        ]);

        using var powershell = ImportEngineCmdlets();
        var restored = InvokeSingle<MigrationBinaryValue>(
            powershell,
            "Get-MigrationDebugRowBinary",
            ("DatabasePath", databasePath),
            ("RunId", runId),
            ("StepId", stepId),
            ("BatchId", batchId),
            ("StageName", stageName),
            ("RecordIndex", 0),
            ("Attempt", 1),
            ("LineageId", lineageId),
            ("CapturedUtc", capturedUtc),
            ("PropertyName", "Attachment"));

        Assert.Equal("report.pdf", restored.FileName);
        Assert.Equal(bytes, ReadAllBytes(restored));
    }

    [Fact]
    public void DataverseThroughputCmdletReturnsNoSnapshotsWhenTheRunOwnsNoActivePools()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = directory.GetPath("migration.sqlite3");
        const string runId = "throughput-inspection-run";
        new SqliteMigrationStore(databasePath).CreateRun(runId, "throughput-inspection-test", "hash");
        using var powershell = ImportEngineCmdlets();

        var snapshots = InvokeAll<DataverseConnectionPoolDashboardSnapshot>(
            powershell,
            "Get-MigrationDataverseConnectionThroughput",
            ("DatabasePath", databasePath),
            ("RunId", runId));

        Assert.Empty(snapshots);
    }

    private static void CreateFailedBatch(
        SqliteMigrationStore store,
        string runId,
        string stepId,
        string batchId)
    {
        store.CreateRun(
            runId,
            runId,
            "hash",
            pipelineJson: $$"""
                [{"Id":"{{stepId}}","Inputs":[],"CompletionDependencies":[],"HasSource":false,"Type":"target"}]
                """);
        store.EnqueueBatch(runId, stepId, 0, batchId, null, """{"Records":[{"id":"failed"}]}""");
        Assert.NotNull(store.TryLeaseNextBatch(runId, "worker", TimeSpan.FromMinutes(1)));
        store.TryCompleteBatchWithErrorPolicy(
            batchId,
            "worker",
            """{"succeeded":0,"failed":1}""",
            dependencyEvents: null,
            lastError: "simulated target failure",
            maxErrorRatePercent: 0);
        store.SetRunStatus(runId, MigrationRunStatus.Failed);
    }

    private static PowerShellRuntime ImportEngineCmdlets()
    {
        var powershell = PowerShellRuntime.Create();
        powershell.AddCommand("Import-Module")
            .AddParameter("Name", typeof(InvokeMigrationCommand).Assembly.Location)
            .Invoke();
        if (powershell.HadErrors)
        {
            var errors = string.Join(Environment.NewLine, powershell.Streams.Error);
            powershell.Dispose();
            throw new InvalidOperationException(errors);
        }

        powershell.Commands.Clear();
        return powershell;
    }

    private static T InvokeSingle<T>(
        PowerShellRuntime powershell,
        string commandName,
        params (string Name, object Value)[] parameters)
    {
        var results = InvokeAll<T>(powershell, commandName, parameters);
        return Assert.Single(results);
    }

    private static IReadOnlyList<T> InvokeAll<T>(
        PowerShellRuntime powershell,
        string commandName,
        params (string Name, object Value)[] parameters)
    {
        powershell.Commands.Clear();
        var command = powershell.AddCommand(commandName);
        foreach (var (name, value) in parameters)
        {
            command.AddParameter(name, value);
        }

        var results = command.Invoke();
        Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
        return results.Select(result => Assert.IsType<T>(result.BaseObject)).ToArray();
    }

    private static byte[] ReadAllBytes(MigrationBinaryValue value)
    {
        using var stream = value.OpenRead();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            $"migration4dataverse-cmdlet-tests-{Guid.NewGuid():N}");

        public TemporaryDirectory()
        {
            Directory.CreateDirectory(_path);
        }

        public string GetPath(string fileName) => Path.Combine(_path, fileName);

        public void Dispose()
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }
    }
}
