using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Migration4Dataverse.Engine.Dashboard;
using Migration4Dataverse.Engine.PowerShell;
using Migration4Dataverse.Engine.Scheduling;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.Tests.Storage;

public sealed class MigrationDashboardTests
{
    [Fact]
    public void RunMetadataIsAvailableWithoutOptInDebugPayloadCapture()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun(
            "metadata-run",
            "metadata-test",
            "hash",
            pipelineJson: """
                [
                  {"Id":"Customers","Inputs":[],"HasSource":true,"Type":"source"},
                  {"Id":"Contacts","Inputs":["Customers"],"HasSource":false,"Type":"target"}
                ]
                """);
        store.EnqueueBatch(
            "metadata-run",
            "Customers",
            0,
            "metadata-batch",
            "\"next\"",
            """{"Records":[{"SourceId":"customer-1"}]}""");
        var lease = store.TryLeaseNextBatch("metadata-run", "worker", TimeSpan.FromMinutes(1));
        Assert.NotNull(lease);
        store.CompleteBatch(
            lease.BatchId,
            "worker",
            """{"Succeeded":1,"Failed":0}""",
            hasErrors: false);

        var run = store.GetDashboardRun("metadata-run");
        Assert.False(run.DebugCaptureEnabled);
        Assert.Equal(30, run.DebugCaptureRetentionDays);
        Assert.Equal(1, run.SucceededRows);
        Assert.Collection(
            run.Steps,
            customers =>
            {
                Assert.Equal("Customers", customers.Id);
                Assert.True(customers.IsSource);
                Assert.Equal(1, customers.SucceededBatches);
                Assert.Equal(1, customers.SucceededRows);
            },
            contacts =>
            {
                Assert.Equal("Contacts", contacts.Id);
                Assert.Equal(new[] { "Customers" }, contacts.Inputs);
            });
        Assert.Empty(store.GetDebugSnapshots("metadata-run"));
    }

    [Fact]
    public void DashboardTracksExpectedInputRowsAndFilterCountsThroughTargetWrites()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun(
            "record-progress-run",
            "record-progress-test",
            "hash",
            pipelineJson: """
                [
                  {"Id":"Customers","Inputs":[],"HasSource":true,"Type":"source","ExpectedRecordCount":2},
                  {"Id":"CustomerMap","Inputs":["Customers"],"HasSource":false,"Type":"transform"},
                  {"Id":"CustomerTarget","Inputs":["CustomerMap"],"HasSource":false,"Type":"target"}
                ]
                """);

        store.MarkSourceFeederStarted("record-progress-run", "Customers");
        CompleteBatch(store, "record-progress-run", "Customers", "source-batch",
            """{"Records":[{"id":1},{"id":2}]}""", """{"Succeeded":2,"Failed":0}""");
        store.MarkSourceFeederCompleted("record-progress-run", "Customers");
        CompleteBatch(store, "record-progress-run", "CustomerMap", "map-batch",
            """{"Records":[{"id":1}]}""", """{"Succeeded":1,"Failed":0}""", filteredRows: 1);
        store.EnqueueBatch(
            "record-progress-run",
            "CustomerTarget",
            0,
            "target-batch",
            null,
            """{"Records":[{"id":1}]}""");

        var beforeTargetWrite = store.GetDashboardRun("record-progress-run");
        var source = Assert.Single(beforeTargetWrite.Steps, step => step.Id == "Customers");
        Assert.Equal(2, source.InputRowsProcessed);
        Assert.Equal(2, source.ExpectedRows);
        Assert.True(source.InputFinalized);

        var mapping = Assert.Single(beforeTargetWrite.Steps, step => step.Id == "CustomerMap");
        Assert.Equal(1, mapping.FilteredRows);
        Assert.True(mapping.InputFinalized);

        var target = Assert.Single(beforeTargetWrite.Steps, step => step.Id == "CustomerTarget");
        Assert.Equal(1, target.InputRowsPending);
        Assert.Equal(1, target.ExpectedRows);
        Assert.True(target.InputFinalized);

        CompleteBatch(store, "record-progress-run", "CustomerTarget", "target-batch",
            """{"Records":[{"id":1}]}""", """{"Succeeded":1,"Failed":0}""");
        target = Assert.Single(store.GetDashboardRun("record-progress-run").Steps,
            step => step.Id == "CustomerTarget");
        Assert.Equal(1, target.InputRowsProcessed);
        Assert.Equal(0, target.InputRowsPending);
        Assert.Equal(1, target.ExpectedRows);
    }

    [Fact]
    public void DashboardBuildsThroughputSamplesFromFirstWorkingPeriodThroughCompletion()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        const string runId = "throughput-history-run";
        const string stepId = "Rows";
        store.CreateRun(
            runId,
            "throughput-history-test",
            "hash",
            pipelineJson: """[{"Id":"Rows","Inputs":[],"HasSource":true,"Type":"source"}]""");

        var startedUtc = DateTimeOffset.UtcNow.AddMinutes(-2);
        store.MarkSourceFeederStarted(runId, stepId);
        store.StartStepActivity(runId, stepId, startedUtc.AddSeconds(-5));
        store.EnterStepActivity(runId, stepId, startedUtc);

        void CompleteBatchAt(long sequence, DateTimeOffset queuedUtc, DateTimeOffset completedUtc)
        {
            var batchId = $"throughput-batch-{sequence}";
            store.EnqueueBatch(
                runId,
                stepId,
                sequence,
                batchId,
                null,
                """{"Records":[{"id":1}]}""",
                queuedUtc);
            var lease = store.TryLeaseNextBatch(runId, "throughput-history-worker", TimeSpan.FromHours(1));
            Assert.NotNull(lease);
            store.CompleteBatch(
                lease.BatchId,
                "throughput-history-worker",
                """{"Succeeded":10,"Failed":0}""",
                hasErrors: false,
                nowUtc: completedUtc);
        }

        CompleteBatchAt(0, startedUtc.AddSeconds(4), startedUtc.AddSeconds(5));
        store.ExitStepActivity(runId, stepId, startedUtc.AddSeconds(10));
        store.EnterStepActivity(runId, stepId, startedUtc.AddSeconds(15));
        CompleteBatchAt(1, startedUtc.AddSeconds(15), startedUtc.AddSeconds(16));
        store.MarkSourceFeederCompleted(runId, stepId);
        store.CompleteStepActivity(runId, stepId, startedUtc.AddSeconds(20));

        var step = Assert.Single(store.GetDashboardRun(runId).Steps);
        Assert.True(step.IsComplete);
        Assert.Equal(startedUtc, step.RowsPerSecondSamples[0].PeriodStartedUtc);
        Assert.Equal(startedUtc.AddSeconds(20), step.RowsPerSecondSamples[^1].PeriodEndedUtc);
        Assert.Equal(new[] { 0d, 2d, 0d, 2d }, step.RowsPerSecondSamples.Select(sample => sample.RowsPerSecond));
        Assert.Equal(
            20d,
            step.RowsPerSecondSamples.Sum(sample =>
                sample.RowsPerSecond * (sample.PeriodEndedUtc - sample.PeriodStartedUtc).TotalSeconds),
            precision: 6);
    }

    [Fact]
    public void DashboardExposesSpecialistInfoFromLatestStepOutcome()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun(
            "specialist-info-run",
            "specialist-info-test",
            "hash",
            pipelineJson: """[{"Id":"AccountsTarget","Inputs":[],"HasSource":false,"Type":"target"}]""");
        CompleteBatch(
            store,
            "specialist-info-run",
            "AccountsTarget",
            "accounts-target-batch",
            """{"Records":[{"id":1}]}""",
            """{"Succeeded":1,"Failed":0,"SpecialistInfo":{"BatchSize":100,"BatchSizeAuto":true,"BatchSizeState":"tuning candidate (2/3 samples)","MaxConcurrency":2,"ConcurrencyAuto":true,"ConcurrencyState":"waiting for batch-size tuning"}}""");

        var target = Assert.Single(store.GetDashboardRun("specialist-info-run").Steps);
        Assert.True(target.SpecialistInfo.HasValue);
        var specialistInfo = target.SpecialistInfo.Value;
        Assert.Equal(100, specialistInfo.GetProperty("BatchSize").GetInt32());
        Assert.Equal("tuning candidate (2/3 samples)", specialistInfo.GetProperty("BatchSizeState").GetString());
        Assert.Equal(2, specialistInfo.GetProperty("MaxConcurrency").GetInt32());
        Assert.Equal("waiting for batch-size tuning", specialistInfo.GetProperty("ConcurrencyState").GetString());
    }

    [Fact]
    public async Task SchedulerPersistsResolvedZeroSourceCount()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);

        async IAsyncEnumerable<MigrationWorkBatch> ReadEmptySource(
            MigrationStepCheckpoint checkpoint,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            yield break;
        }

        var steps = new[]
        {
            new MigrationStepDefinition(
                "EmptySource",
                null,
                ReadEmptySource,
                (_, _) => Task.FromResult(new MigrationBatchResult("""{"Succeeded":0,"Failed":0}""")),
                type: "source")
            {
                ResolveExpectedRecordCountAsync = _ => Task.FromResult(0L)
            },
            new MigrationStepDefinition(
                "EmptyTarget",
                ["EmptySource"],
                null,
                (_, _) => Task.FromResult(new MigrationBatchResult("""{"Succeeded":0,"Failed":0}""")),
                type: "target")
        };

        await new DependencyScheduler(store).ExecuteAsync(
            "zero-source-count-run",
            "zero-source-count-test",
            "hash",
            steps,
            new DependencySchedulerOptions
            {
                MaxAttempts = 1,
                RetryDelay = TimeSpan.Zero,
                PollInterval = TimeSpan.FromMilliseconds(1)
            });

        var source = Assert.Single(
            store.GetDashboardRun("zero-source-count-run").Steps,
            step => step.Id == "EmptySource");
        Assert.Equal(0, source.ExpectedRows);
        Assert.True(source.InputFinalized);
    }

    [Fact]
    public async Task SchedulerResultIncludesFinalTargetRowCounts()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);

        async IAsyncEnumerable<MigrationWorkBatch> ReadSource(
            MigrationStepCheckpoint checkpoint,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new MigrationWorkBatch(
                """{"Records":[{"id":1}]}""",
                IsLast: true);
            await Task.CompletedTask;
        }

        var steps = new[]
        {
            new MigrationStepDefinition(
                "Source",
                null,
                ReadSource,
                (batch, _) => Task.FromResult(new MigrationBatchResult(
                    """{"Succeeded":1,"Failed":0}""",
                    Dependencies:
                    [
                        new MigrationDependencyOutput(
                            "Target",
                            """{"Records":[{"id":1}]}""",
                            [batch.BatchId])
                    ])),
                type: "source")
            {
                ExpectedRecordCount = 1
            },
            new MigrationStepDefinition(
                "Target",
                ["Source"],
                null,
                (_, _) => Task.FromResult(new MigrationBatchResult(
                    """{"Succeeded":1,"Failed":0}""")),
                type: "target")
        };

        var result = await new DependencyScheduler(store).ExecuteAsync(
            "target-progress-run",
            "target-progress-test",
            "hash",
            steps,
            new DependencySchedulerOptions
            {
                MaxAttempts = 1,
                RetryDelay = TimeSpan.Zero
            });

        Assert.Equal(1, result.TargetRowsProcessed);
        Assert.Equal(1, result.TargetRowsSucceeded);
        Assert.Equal(0, result.TargetRowsFailed);
        Assert.Equal(1, result.TargetRowsExpected);
    }

    [Fact]
    public void CallbackPayloadWithoutRecordsIsNotReportedAsZeroRows()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun(
            "uncounted-progress-run",
            "uncounted-progress-test",
            "hash",
            pipelineJson: """[{"Id":"CallbackSource","Inputs":[],"HasSource":true,"Type":"callback"}]""");
        store.MarkSourceFeederStarted("uncounted-progress-run", "CallbackSource");
        CompleteBatch(
            store,
            "uncounted-progress-run",
            "CallbackSource",
            "callback-batch",
            """{"CustomPayload":{"value":1}}""",
            """{"Succeeded":1,"Failed":0}""");
        store.MarkSourceFeederCompleted("uncounted-progress-run", "CallbackSource");

        var step = Assert.Single(store.GetDashboardRun("uncounted-progress-run").Steps);
        Assert.Equal(1, step.BatchesWithUnknownInputRows);
        Assert.Equal(0, step.InputRowsProcessed);
        Assert.Null(step.ExpectedRows);
        Assert.True(step.InputFinalized);
    }

    [Fact]
    public void RunConfigurationAndConnectionInstancesArePersistedWithSecretRedaction()
    {
        var step = System.Management.Automation.PSObject.AsPSObject(new
        {
            Id = "Customers",
            Type = "callback",
            Inputs = Array.Empty<string>(),
            ReadBatches = System.Management.Automation.ScriptBlock.Create("return"),
            ExecuteBatch = System.Management.Automation.ScriptBlock.Create("return"),
            Target = new { Type = "SqlServer", Connector = "SqlServer", ConnectionId = "warehouse", Table = "dbo.Customer" }
        });
        var connections = new System.Collections.Hashtable
        {
            ["warehouse"] = new System.Collections.Hashtable
            {
                ["Server"] = "db.example.test",
                ["Password"] = "must-not-be-stored",
                ["State"] = "Open"
            }
        };
        var configurationJson = MigrationConfigurationSnapshot.CreateJson([step], connections);
        var initialSnapshot = MigrationConfigurationSnapshot.AppendConnectionInstanceSnapshot(
            null,
            MigrationConfigurationSnapshot.CaptureConnectionInstances(connections, "Started"));

        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun(
            "configuration-run",
            "configuration-test",
            "hash",
            pipelineJson: """[{"Id":"Customers","Inputs":[],"HasSource":true,"Type":"source"}]""",
            configurationJson: configurationJson,
            connectionInstancesJson: initialSnapshot);
        store.UpdateConnectionInstanceSnapshots(
            "configuration-run",
            MigrationConfigurationSnapshot.AppendConnectionInstanceSnapshot(
                initialSnapshot,
                MigrationConfigurationSnapshot.CaptureConnectionInstances(connections, "Completed")));

        var run = store.GetDashboardRun("configuration-run");
        Assert.Equal("warehouse", run.Configuration!.Value.GetProperty("steps")[0]
            .GetProperty("targetConnectionId").GetString());
        Assert.Equal("SqlServer", run.Configuration.Value.GetProperty("steps")[0]
            .GetProperty("targetConnectionType").GetString());
        Assert.Equal("dbo.Customer", run.Configuration!.Value.GetProperty("steps")[0]
            .GetProperty("properties").GetProperty("Target").GetProperty("Table").GetString());
        Assert.Equal("[REDACTED]", run.Configuration.Value.GetProperty("connections")[0]
            .GetProperty("properties").GetProperty("Password").GetString());
        Assert.DoesNotContain("must-not-be-stored", run.Configuration.Value.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(2, run.ConnectionInstances!.Value.GetArrayLength());
        Assert.Equal("Started", run.ConnectionInstances.Value[0].GetProperty("phase").GetString());
        Assert.Equal("Completed", run.ConnectionInstances.Value[1].GetProperty("phase").GetString());
        Assert.DoesNotContain("must-not-be-stored", run.ConnectionInstances.Value.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DebugCaptureRedactsSecretsAndTracesRowsAcrossStagesAndGeneratedChildren()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        var payload = """
            {"Records":[{"Source":{"SourceId":"customer-1","Name":"Acme","AccessToken":"never-store-me"},
              "Children":{"Contacts":[{"SourceId":"contact-1","Name":"Sam"}]}}]}
            """;
        async IAsyncEnumerable<MigrationWorkBatch> ReadSource(
            MigrationStepCheckpoint checkpoint,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new MigrationWorkBatch(payload, IsLast: true);
            await Task.CompletedTask;
        }

        var jobs = new[]
        {
            new MigrationStepDefinition(
                "Customers",
                null,
                ReadSource,
                (batch, _) => Task.FromResult(new MigrationBatchResult(
                    """{"Succeeded":1,"Failed":0}""",
                    DebugStages:
                    [
                        new MigrationDebugStage(
                            "TargetWriteInput",
                            """{"Records":[{"Id":"customer-1","DisplayName":"ACME"}]}""")
                    ])))
        };
        await new DependencyScheduler(store).ExecuteAsync(
            "debug-run",
            "debug-test",
            "hash",
            jobs,
            new DependencySchedulerOptions
            {
                CaptureDebugData = true,
                MaxAttempts = 1,
                RetryDelay = TimeSpan.Zero
            });

        var sourceSnapshot = Assert.Single(
            store.GetDebugSnapshots("debug-run", batchId: MigrationBatchIdentity.Create("debug-run", "Customers", 0)),
            snapshot => snapshot.StageName == "SourceInput");
        var sourceLineage = sourceSnapshot.LineageId;
        var trace = store.GetDebugSnapshots("debug-run", lineageId: sourceLineage);
        Assert.Contains(trace, snapshot => snapshot.StageName == "SourceInput");
        Assert.Contains(trace, snapshot => snapshot.StageName == "TargetWriteInput");
        var child = Assert.Single(trace, snapshot => snapshot.StageName == "DependencyOutput:Contacts");
        Assert.Equal(new[] { sourceLineage }, child.ParentLineageIds);
        Assert.DoesNotContain(trace, snapshot => snapshot.PayloadJson.Contains("never-store-me", StringComparison.Ordinal));
        Assert.Contains(trace, snapshot => snapshot.PayloadJson.Contains("[REDACTED]", StringComparison.Ordinal));

        var childTrace = store.GetDebugSnapshots("debug-run", lineageId: child.LineageId);
        Assert.Contains(childTrace, snapshot => snapshot.LineageId == sourceLineage);
        var page = store.GetDebugSnapshotPage("debug-run", "Customers", "ACME", limit: 10);
        Assert.Equal(2, page.TotalCount);
        Assert.False(page.HasMore);
        Assert.Equal(1, page.StageCounts.Single(stage => stage.StageName == "TargetWriteInput").Count);
        Assert.DoesNotContain(page.StageCounts, stage => stage.StageName == "BatchOutcome");
        Assert.Contains(page.Items, snapshot => snapshot.StageName == "TargetWriteInput");
        var stagePage = store.GetDebugSnapshotPage(
            "debug-run",
            "Customers",
            "ACME",
            limit: 10,
            stageName: "TargetWriteInput");
        Assert.Equal(1, stagePage.TotalCount);
        Assert.All(stagePage.Items, snapshot => Assert.Equal("TargetWriteInput", snapshot.StageName));
        var firstSortedPage = store.GetDebugSnapshotPage(
            "debug-run",
            "Customers",
            search: "ACME",
            limit: 1,
            sortColumn: "stage",
            sortDirection: "ascending");
        var secondSortedPage = store.GetDebugSnapshotPage(
            "debug-run",
            "Customers",
            search: "ACME",
            limit: 1,
            offset: 1,
            sortColumn: "stage",
            sortDirection: "ascending");
        Assert.Equal(2, firstSortedPage.TotalCount);
        Assert.Equal(2, secondSortedPage.TotalCount);
        Assert.True(string.CompareOrdinal(firstSortedPage.Items[0].StageName, secondSortedPage.Items[0].StageName) < 0);
        Assert.Contains("DisplayName", firstSortedPage.PayloadColumns);
        var filteredPage = store.GetDebugSnapshotPage(
            "debug-run",
            "Customers",
            search: "ACME",
            columnFilters: new Dictionary<string, string> { ["payload:DisplayName"] = "ACME" });
        Assert.Equal(1, filteredPage.TotalCount);
        Assert.All(filteredPage.Items, snapshot => Assert.Equal("TargetWriteInput", snapshot.StageName));

        var port = GetAvailablePort();
        await using var dashboard = await MigrationDashboardServer.StartAsync(
            database.Path,
            $"http://127.0.0.1:{port}");
        using var client = new HttpClient();
        var apiPage = await client.GetFromJsonAsync<JsonElement>(
            new Uri(dashboard.Url, "/api/runs/debug-run/rows?stepId=Customers&search=ACME&limit=10"));
        Assert.Equal(2, apiPage.GetProperty("totalCount").GetInt32());
        Assert.Equal(page.StageCounts.Count, apiPage.GetProperty("stageCounts").GetArrayLength());
        Assert.Contains(
            apiPage.GetProperty("items").EnumerateArray(),
            snapshot => snapshot.GetProperty("stageName").GetString() == "TargetWriteInput");
        var apiStagePage = await client.GetFromJsonAsync<JsonElement>(
            new Uri(dashboard.Url, "/api/runs/debug-run/rows?stepId=Customers&search=ACME&stageName=TargetWriteInput"));
        Assert.Equal(1, apiStagePage.GetProperty("totalCount").GetInt32());
        var filtersQuery = Uri.EscapeDataString("""{"payload:DisplayName":"ACME"}""");
        var apiFilteredPage = await client.GetFromJsonAsync<JsonElement>(
            new Uri(
                dashboard.Url,
                $"/api/runs/debug-run/rows?stepId=Customers&search=ACME&filters={filtersQuery}"));
        Assert.Equal(1, apiFilteredPage.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            "TargetWriteInput",
            apiFilteredPage.GetProperty("items")[0].GetProperty("stageName").GetString());
        var apiSortedPage = await client.GetFromJsonAsync<JsonElement>(
            new Uri(
                dashboard.Url,
                "/api/runs/debug-run/rows?stepId=Customers&search=ACME&sortColumn=stage&sortDirection=ascending&limit=1"));
        Assert.Equal(2, apiSortedPage.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            firstSortedPage.Items[0].StageName,
            apiSortedPage.GetProperty("items")[0].GetProperty("stageName").GetString());
    }

    [Fact]
    public async Task DebugCaptureTracksMultipleInputRowsForMergedStageRows()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        const string inputPayload = """
            {"Records":[
              {"SourceId":"source-a","Name":"Alpha"},
              {"SourceId":"source-b","Name":"Beta"}
            ]}
            """;
        var jobs = new[]
        {
            new MigrationStepDefinition(
                "Merge",
                null,
                ReadSource,
                (_, _) => Task.FromResult(new MigrationBatchResult(
                    """{"Succeeded":1,"Failed":0}""",
                    DebugStages:
                    [
                        new MigrationDebugStage(
                            "Merged output",
                            """{"Records":[{"Id":"merged-1","Name":"Alpha Beta"}]}""",
                            [new[] { 0, 1 }])
                    ])))
        };
        await new DependencyScheduler(store).ExecuteAsync(
            "merge-lineage-run",
            "merge-lineage-test",
            "hash",
            jobs,
            new DependencySchedulerOptions
            {
                CaptureDebugData = true,
                MaxAttempts = 1,
                RetryDelay = TimeSpan.Zero
            });

        var batchId = MigrationBatchIdentity.Create("merge-lineage-run", "Merge", 0);
        var batchSnapshots = store.GetDebugSnapshots(
            "merge-lineage-run",
            batchId: batchId,
            limit: 10);
        var inputs = batchSnapshots.Where(snapshot => snapshot.StageName == "BatchInput").ToArray();
        var mergedOutput = Assert.Single(batchSnapshots, snapshot => snapshot.StageName == "Merged output");
        Assert.Equal(2, inputs.Length);
        Assert.Equal(
            inputs.Select(snapshot => snapshot.LineageId).Order(StringComparer.Ordinal),
            mergedOutput.ParentLineageIds.Order(StringComparer.Ordinal));

        var trace = store.GetDebugSnapshots("merge-lineage-run", lineageId: mergedOutput.LineageId);
        Assert.Contains(trace, snapshot => snapshot.StageName == "Merged output");
        Assert.Equal(2, trace.Count(snapshot => snapshot.StageName == "BatchInput"));

        async IAsyncEnumerable<MigrationWorkBatch> ReadSource(
            MigrationStepCheckpoint checkpoint,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new MigrationWorkBatch(inputPayload, IsLast: true);
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task DebugCaptureTracksMergedRowsAcrossDistinctParentBatches()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        const string runId = "cross-batch-lineage-run";
        var leftBatchId = MigrationBatchIdentity.Create(runId, "Left", 0);
        var rightBatchId = MigrationBatchIdentity.Create(runId, "Right", 0);
        IReadOnlyList<string> observedParentBatchIds = Array.Empty<string>();
        IReadOnlyList<string> observedParentLineageIds = Array.Empty<string>();
        var parentReferences = new IReadOnlyList<MigrationDebugParentRecordReference>[]
        {
            new[]
            {
                new MigrationDebugParentRecordReference(leftBatchId, 0),
                new MigrationDebugParentRecordReference(rightBatchId, 0)
            }
        };
        var jobs = new[]
        {
            new MigrationStepDefinition(
                "Left",
                null,
                (_, cancellationToken) => SingleBatch(
                    """{"Records":[{"SourceId":"left-row"}]}""",
                    cancellationToken),
                (_, _) => Task.FromResult(new MigrationBatchResult(
                    "{}",
                    Dependencies:
                    [
                        new MigrationDependencyOutput(
                            "Joined",
                            """{"Records":[{"Id":"joined-row"}]}""",
                            [rightBatchId])
                    ]))),
            new MigrationStepDefinition(
                "Right",
                null,
                (_, cancellationToken) => SingleBatch(
                    """{"Records":[{"SourceId":"right-row"}]}""",
                    cancellationToken),
                (_, _) => Task.FromResult(new MigrationBatchResult("{}"))),
            new MigrationStepDefinition(
                "Joined",
                ["Left", "Right"],
                null,
                (batch, _) =>
                {
                    observedParentBatchIds = batch.ParentBatchIds;
                    var leftInput = Assert.Single(
                        store.GetDebugSnapshots(runId, batchId: leftBatchId),
                        snapshot => snapshot.StageName == "BatchInput");
                    var rightInput = Assert.Single(
                        store.GetDebugSnapshots(runId, batchId: rightBatchId),
                        snapshot => snapshot.StageName == "BatchInput");
                    observedParentLineageIds = [leftInput.LineageId, rightInput.LineageId];
                    using (var connection = new SqliteConnection($"Data Source={database.Path}"))
                    {
                        connection.Open();
                        using var ageSnapshots = connection.CreateCommand();
                        ageSnapshots.CommandText = """
                            UPDATE DebugSnapshots
                            SET CapturedUtc = $capturedUtc
                            WHERE RunId = $runId AND BatchId IN ($leftBatchId, $rightBatchId);
                            """;
                        ageSnapshots.Parameters.AddWithValue(
                            "$capturedUtc",
                            DateTimeOffset.UtcNow.AddDays(-2).ToString("O"));
                        ageSnapshots.Parameters.AddWithValue("$runId", runId);
                        ageSnapshots.Parameters.AddWithValue("$leftBatchId", leftBatchId);
                        ageSnapshots.Parameters.AddWithValue("$rightBatchId", rightBatchId);
                        ageSnapshots.ExecuteNonQuery();
                    }

                    store.PurgeExpiredDebugSnapshots();
                    return Task.FromResult(new MigrationBatchResult(
                        "{}",
                        DebugStages:
                        [
                            new MigrationDebugStage(
                                "Merged output",
                                """{"Records":[{"Id":"joined-output"}]}""",
                                parentRecordIndices: null,
                                parentRecordReferences: parentReferences)
                        ]));
                })
        };

        await new DependencyScheduler(store).ExecuteAsync(
            runId,
            "cross-batch-lineage-test",
            "hash",
            jobs,
            new DependencySchedulerOptions
            {
                CaptureDebugData = true,
                DebugCaptureRetentionDays = 1,
                MaxAttempts = 1,
                RetryDelay = TimeSpan.Zero
            });

        Assert.Equal(
            new[] { leftBatchId, rightBatchId }.Order(StringComparer.Ordinal),
            observedParentBatchIds.Order(StringComparer.Ordinal));
        var mergedOutput = Assert.Single(
            store.GetDebugSnapshots(runId),
            snapshot => snapshot.StageName == "Merged output");
        Assert.Equal(
            observedParentLineageIds.Order(StringComparer.Ordinal),
            mergedOutput.ParentLineageIds.Order(StringComparer.Ordinal));

        var trace = store.GetDebugSnapshots(runId, lineageId: mergedOutput.LineageId);
        Assert.DoesNotContain(trace, snapshot => snapshot.BatchId == leftBatchId);
        Assert.DoesNotContain(trace, snapshot => snapshot.BatchId == rightBatchId);

        static async IAsyncEnumerable<MigrationWorkBatch> SingleBatch(
            string payload,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new MigrationWorkBatch(payload, IsLast: true);
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task DashboardServesEmbeddedReactAppAndReadOnlyRunApi()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun(
            "web-run",
            "web-test",
            "hash",
            pipelineJson: """[{"Id":"Customers","Inputs":[],"HasSource":true,"Type":"source"}]""");
        var port = GetAvailablePort();
        await using var dashboard = await MigrationDashboardServer.StartAsync(
            database.Path,
            $"http://127.0.0.1:{port}");
        using var client = new HttpClient();

        var html = await client.GetStringAsync(dashboard.Url);
        Assert.Contains("/assets/index-", html, StringComparison.Ordinal);
        Assert.Contains("/logo.png", html, StringComparison.Ordinal);
        Assert.NotEmpty(await client.GetByteArrayAsync(new Uri(dashboard.Url, "/logo.png")));
        var runs = await client.GetFromJsonAsync<MigrationRunSummary[]>(
            new Uri(dashboard.Url, "/api/runs"));
        var run = Assert.Single(runs!);
        Assert.Equal("web-run", run.RunId);
        Assert.Single(run.Steps);

        using var remoteRequest = new HttpRequestMessage(HttpMethod.Get, dashboard.Url);
        remoteRequest.Headers.Host = "example.test";
        using var remoteResponse = await client.SendAsync(remoteRequest);
        Assert.Contains(
            remoteResponse.StatusCode,
            new[] { HttpStatusCode.BadRequest, HttpStatusCode.NotFound });
        Assert.Equal("ok", JsonDocument.Parse(await client.GetStringAsync(new Uri(dashboard.Url, "/health")))
            .RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task DashboardServesConfigurationWithoutRunDatabase()
    {
        var port = GetAvailablePort();
        const string configuration = """{"steps":[{"id":"Customers","type":"source"}],"connections":[]}""";
        await using var dashboard = await MigrationDashboardServer.StartWithConfigurationAsync(
            null,
            $"http://127.0.0.1:{port}",
            configuration);
        using var client = new HttpClient();

        var details = await client.GetFromJsonAsync<JsonElement>(new Uri(dashboard.Url, "/api/configuration"));
        Assert.Equal("Customers", details.GetProperty("steps")[0].GetProperty("id").GetString());
        Assert.Empty(await client.GetFromJsonAsync<MigrationRunSummary[]>(new Uri(dashboard.Url, "/api/runs")) ?? []);
        using var runResponse = await client.GetAsync(new Uri(dashboard.Url, "/api/runs/none"));
        Assert.Equal(HttpStatusCode.NotFound, runResponse.StatusCode);
    }

    private static void CompleteBatch(
        SqliteMigrationStore store,
        string runId,
        string stepId,
        string batchId,
        string payloadJson,
        string outcomesJson,
        long filteredRows = 0)
    {
        store.EnqueueBatch(runId, stepId, 0, batchId, null, payloadJson);
        var batch = store.TryLeaseNextBatch(runId, "progress-test-worker", TimeSpan.FromMinutes(1));
        Assert.NotNull(batch);
        store.CompleteBatch(
            batch.BatchId,
            "progress-test-worker",
            outcomesJson,
            hasErrors: false,
            filteredRows: filteredRows);
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"migration4dataverse-dashboard-{Guid.NewGuid():N}");

        public TemporaryDatabase()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "jobs.sqlite3");
        }

        public string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
