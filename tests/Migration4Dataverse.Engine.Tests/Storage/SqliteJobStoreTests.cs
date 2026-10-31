using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Data.Sqlite;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.Tests.Storage;

public sealed class SqliteJobStoreTests
{
    [Fact]
    public void PendingBatchCandidatesIncludePerStepReadyBacklog()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun("candidate-run", "candidate-run", "hash-1");
        store.EnqueueBatch("candidate-run", "HighImpact", 0, "high-0", null, "{}", nowUtc: DateTimeOffset.UtcNow);
        store.EnqueueBatch("candidate-run", "HighImpact", 1, "high-1", null, "{}", nowUtc: DateTimeOffset.UtcNow);
        store.EnqueueBatch("candidate-run", "LowerImpact", 0, "low-0", null, "{}", nowUtc: DateTimeOffset.UtcNow);

        var candidates = store.GetPendingBatchStepCandidates("candidate-run");

        Assert.Equal(2, Assert.Single(candidates, candidate => candidate.StepId == "HighImpact").PendingBatchCount);
        Assert.Equal(1, Assert.Single(candidates, candidate => candidate.StepId == "LowerImpact").PendingBatchCount);
    }

    [Fact]
    public void CheckpointAdvancesOnlyAcrossContiguousCompletedBatchesAndOutboxSurvivesRestart()
    {
        using var database = new TemporaryDatabase();
        const string runId = "run-1";
        const string tableName = "Accounts";
        const string workerOne = "worker-1";
        const string workerTwo = "worker-2";
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun(runId, "test-job", "hash-1", now);
        store.EnqueueBatch(runId, tableName, 0, "batch-0", "{\"lastId\":10}", "[{\"id\":10}]", now);
        store.EnqueueBatch(runId, tableName, 1, "batch-1", "{\"lastId\":20}", "[{\"id\":20}]", now);

        var batchOne = store.TryLeaseNextBatch(runId, workerOne, TimeSpan.FromMinutes(5), now);
        var batchTwo = store.TryLeaseNextBatch(runId, workerTwo, TimeSpan.FromMinutes(5), now);
        Assert.NotNull(batchOne);
        Assert.NotNull(batchTwo);
        Assert.Equal("batch-0", batchOne.BatchId);
        Assert.Equal("batch-1", batchTwo.BatchId);

        var dependencyEvent = DependencyEvent.Create("batch-1", "Contacts", "{\"accountIds\":[20]}");
        store.CompleteBatch(
            "batch-1",
            workerTwo,
            "{\"succeeded\":1,\"failed\":0}",
            hasErrors: false,
            new[] { dependencyEvent },
            nowUtc: now.AddSeconds(1));

        var checkpoint = store.GetStepCheckpoint(runId, tableName);
        Assert.Equal(-1, checkpoint.LastCompletedSequence);
        Assert.Null(checkpoint.CursorJson);

        store.CompleteBatch(
            "batch-0",
            workerOne,
            "{\"succeeded\":1,\"failed\":0}",
            hasErrors: false,
            nowUtc: now.AddSeconds(2));

        checkpoint = store.GetStepCheckpoint(runId, tableName);
        Assert.Equal(1, checkpoint.LastCompletedSequence);
        Assert.Equal("{\"lastId\":20}", checkpoint.CursorJson);

        var reopenedStore = new SqliteMigrationStore(database.Path);
        var pendingEvents = reopenedStore.GetPendingDependencyEvents(runId);
        var storedEvent = Assert.Single(pendingEvents);
        Assert.Equal(dependencyEvent.EventId, storedEvent.EventId);
        Assert.Equal("Contacts", storedEvent.ChildStepId);

        reopenedStore.CompleteDependencyEvent(runId, storedEvent.EventId, now.AddSeconds(3));
        Assert.Empty(reopenedStore.GetPendingDependencyEvents(runId));
    }

    [Fact]
    public async Task ConcurrentBatchCompletionsSerializeReadModifyWriteTransactions()
    {
        using var database = new TemporaryDatabase();
        const string runId = "concurrent-completion-run";
        const int batchCount = 32;
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun(
            runId,
            "concurrent-completion-test",
            "hash",
            pipelineJson: """[{"Id":"Work","Inputs":[],"HasSource":true,"Type":"source"}]""");

        var batches = new List<StoredBatch>();
        for (var index = 0; index < batchCount; index++)
        {
            var batchId = $"concurrent-batch-{index}";
            store.EnqueueBatch(
                runId,
                "Work",
                index,
                batchId,
                null,
                """{"Records":[{"id":1}]}""");
            var batch = store.TryLeaseNextBatch(
                runId,
                $"worker-{index}",
                TimeSpan.FromMinutes(1));
            Assert.NotNull(batch);
            batches.Add(batch);
        }

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completions = batches.Select((batch, index) => Task.Run(async () =>
        {
            await start.Task;
            store.CompleteBatch(
                batch.BatchId,
                $"worker-{index}",
                """{"Succeeded":1,"Failed":0}""",
                hasErrors: false);
        })).ToArray();
        start.SetResult();

        await Task.WhenAll(completions);

        Assert.Equal(batchCount, store.GetDashboardRun(runId).SucceededBatches);
    }

    [Fact]
    public async Task ConcurrentBatchLeasesSerializeBeforeSelectingBatches()
    {
        using var database = new TemporaryDatabase();
        const string runId = "concurrent-lease-run";
        const int batchCount = 64;
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun(runId, "concurrent-lease-test", "hash");
        for (var index = 0; index < batchCount; index++)
        {
            store.EnqueueBatch(
                runId,
                "Work",
                index,
                $"concurrent-lease-batch-{index}",
                null,
                """{"Records":[{"id":1}]}""");
        }

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leases = Enumerable.Range(0, batchCount).Select(index => Task.Run(async () =>
        {
            await start.Task;
            return store.TryLeaseNextBatch(
                runId,
                $"worker-{index}",
                TimeSpan.FromMinutes(1));
        })).ToArray();
        start.SetResult();

        var leasedBatches = await Task.WhenAll(leases);

        Assert.All(leasedBatches, batch => Assert.NotNull(batch));
        Assert.Equal(batchCount, leasedBatches.Select(batch => batch!.BatchId).Distinct().Count());
    }

    [Fact]
    public async Task DatabaseWriteGateSerializesConcurrentWritersForTheSamePath()
    {
        using var database = new TemporaryDatabase();
        var activeWriters = 0;
        var maximumActiveWriters = 0;

        var writers = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            using var writeLock = SqliteDatabaseWriteGate.Enter(database.Path);
            var active = Interlocked.Increment(ref activeWriters);
            var observed = Volatile.Read(ref maximumActiveWriters);
            while (active > observed)
            {
                var previous = Interlocked.CompareExchange(ref maximumActiveWriters, active, observed);
                if (previous == observed)
                {
                    break;
                }
                observed = previous;
            }

            Thread.Sleep(10);
            Interlocked.Decrement(ref activeWriters);
        })).ToArray();

        await Task.WhenAll(writers);

        Assert.Equal(1, maximumActiveWriters);
    }

    [Fact]
    public void ExpiredLeaseCanBeRecoveredAfterStoreIsReopened()
    {
        using var database = new TemporaryDatabase();
        const string runId = "run-2";
        var start = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var firstStore = new SqliteMigrationStore(database.Path);
        firstStore.CreateRun(runId, "test-job", "hash-1", start);
        firstStore.EnqueueBatch(runId, "Contacts", 0, "batch-0", "{\"lastId\":10}", "[{\"id\":10}]", start);

        var firstLease = firstStore.TryLeaseNextBatch(runId, "worker-before-crash", TimeSpan.FromMinutes(1), start);
        Assert.NotNull(firstLease);
        Assert.Equal(1, firstLease.Attempts);

        var reopenedStore = new SqliteMigrationStore(database.Path);
        var recoveredLease = reopenedStore.TryLeaseNextBatch(
            runId,
            "worker-after-restart",
            TimeSpan.FromMinutes(1),
            start.AddMinutes(2));

        Assert.NotNull(recoveredLease);
        Assert.Equal("batch-0", recoveredLease.BatchId);
        Assert.Equal("worker-after-restart", recoveredLease.LeaseOwner);
        Assert.Equal(2, recoveredLease.Attempts);
        Assert.Equal("[{\"id\":10}]", recoveredLease.PayloadJson);
    }

    [Fact]
    public void SucceededBatchCanBeReplayedOnlyWithTheOriginalPayload()
    {
        using var database = new TemporaryDatabase();
        const string runId = "run-5";
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun(runId, "test-job", "hash-1", now);
        store.EnqueueBatch(runId, "Accounts", 0, "batch-0", "{\"lastId\":10}", "[{\"id\":10}]", now);

        var lease = store.TryLeaseNextBatch(runId, "worker-1", TimeSpan.FromMinutes(1), now);
        Assert.NotNull(lease);
        store.CompleteBatch(
            lease.BatchId,
            "worker-1",
            "{\"succeeded\":1,\"failed\":0}",
            hasErrors: false,
            nowUtc: now.AddSeconds(1));

        var completedBatch = store.GetBatch("batch-0");
        Assert.NotNull(completedBatch);
        Assert.Equal(MigrationBatchState.Succeeded, completedBatch.State);
        Assert.Null(completedBatch.PayloadJson);

        Assert.Equal(
            "batch-0",
            store.EnqueueBatch(runId, "Accounts", 0, "batch-0", "{\"lastId\":10}", "[{\"id\":10}]", now));
        Assert.Throws<InvalidOperationException>(() =>
            store.EnqueueBatch(runId, "Accounts", 0, "batch-0", "{\"lastId\":11}", "[{\"id\":11}]", now));
    }

    [Fact]
    public void DashboardReportsActiveRowsPerSecondAndCumulativeIdleTime()
    {
        using var database = new TemporaryDatabase();
        var start = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun(
            "metric-run",
            "metrics",
            "hash",
            start,
            pipelineJson: """[{"Id":"Work","Inputs":[],"CompletionDependencies":[],"HasSource":false,"Type":"target"}]""");
        store.StartStepActivity("metric-run", "Work", start);
        store.EnqueueBatch("metric-run", "Work", 0, "work-0", null, """[{"id":1}]""", start);

        var lease = store.TryLeaseNextBatch(
            "metric-run",
            "worker-1",
            TimeSpan.FromMinutes(1),
            start.AddSeconds(2));
        Assert.NotNull(lease);
        store.CompleteBatch(
            lease.BatchId,
            "worker-1",
            """{"succeeded":6,"failed":0}""",
            hasErrors: false,
            nowUtc: start.AddSeconds(6));
        store.CompleteStepActivity("metric-run", "Work", start.AddSeconds(10));

        var step = Assert.Single(store.GetDashboardRun("metric-run").Steps);

        Assert.Equal(6, step.SucceededRows);
        Assert.Equal(1.5, step.RowsPerSecond);
        Assert.Equal(4_000, step.ActiveMilliseconds);
        Assert.Equal(6_000, step.IdleMilliseconds);
        Assert.Collection(
            step.ActivityPeriods,
            period =>
            {
                Assert.Equal("Waiting", period.State);
                Assert.Equal(start, period.StartedUtc);
                Assert.Equal(start.AddSeconds(2), period.EndedUtc);
            },
            period =>
            {
                Assert.Equal("Working", period.State);
                Assert.Equal(start.AddSeconds(2), period.StartedUtc);
                Assert.Equal(start.AddSeconds(6), period.EndedUtc);
            },
            period =>
            {
                Assert.Equal("Waiting", period.State);
                Assert.Equal(start.AddSeconds(6), period.StartedUtc);
                Assert.Equal(start.AddSeconds(10), period.EndedUtc);
            });
    }

    [Fact]
    public void StepActivityPeriodsClampSubMillisecondClockRegression()
    {
        using var database = new TemporaryDatabase();
        var start = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun(
            "activity-clock-run",
            "activity clock",
            "hash",
            start,
            pipelineJson: """[{"Id":"Work","Inputs":[],"CompletionDependencies":[],"HasSource":false,"Type":"target"}]""");

        store.StartStepActivity("activity-clock-run", "Work", start);
        store.EnterStepActivity("activity-clock-run", "Work", start.AddTicks(1));
        store.ExitStepActivity("activity-clock-run", "Work", start);
        store.CompleteStepActivity("activity-clock-run", "Work", start.AddSeconds(1));

        var step = Assert.Single(store.GetDashboardRun("activity-clock-run").Steps);

        Assert.Collection(
            step.ActivityPeriods,
            period =>
            {
                Assert.Equal("Waiting", period.State);
                Assert.Equal(start, period.StartedUtc);
                Assert.Equal(start.AddTicks(1), period.EndedUtc);
            },
            period =>
            {
                Assert.Equal("Working", period.State);
                Assert.Equal(start.AddTicks(1), period.StartedUtc);
                Assert.Equal(start.AddTicks(1), period.EndedUtc);
            },
            period =>
            {
                Assert.Equal("Waiting", period.State);
                Assert.Equal(start.AddTicks(1), period.StartedUtc);
                Assert.Equal(start.AddSeconds(1), period.EndedUtc);
            });
    }

    [Fact]
    public void FailedRowsRemainDurableAndTheirCursorCanBeAcknowledged()
    {
        using var database = new TemporaryDatabase();
        const string runId = "run-3";
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun(runId, "test-job", "hash-1", now);
        store.EnqueueBatch(runId, "Projects", 0, "batch-0", "{\"lastId\":10}", "[{\"id\":10}]", now);

        var lease = store.TryLeaseNextBatch(runId, "worker-1", TimeSpan.FromMinutes(1), now);
        Assert.NotNull(lease);

        store.CompleteBatch(
            lease.BatchId,
            "worker-1",
            "{\"succeeded\":0,\"failed\":[{\"key\":10,\"message\":\"invalid\"}]}",
            hasErrors: true,
            nowUtc: now.AddSeconds(1));

        var persistedBatch = store.GetBatch(lease.BatchId);
        Assert.NotNull(persistedBatch);
        Assert.Equal(MigrationBatchState.CompletedWithErrors, persistedBatch.State);
        Assert.NotNull(persistedBatch.PayloadJson);
        Assert.Contains("\"message\":\"invalid\"", persistedBatch.OutcomesJson);
        Assert.Equal(0, store.GetStepCheckpoint(runId, "Projects").LastCompletedSequence);
    }

    [Fact]
    public void FailedBatchesCanBeQueuedForReplayWithAuditedHistory()
    {
        using var database = new TemporaryDatabase();
        const string runId = "retry-failed-batches-run";
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun(runId, "retry-test", "hash-1", now);
        store.EnqueueBatch(runId, "Accounts", 0, "failed-batch", null, """{"Records":[]}""", now);
        var lease = store.TryLeaseNextBatch(runId, "worker", TimeSpan.FromMinutes(1), now);
        Assert.NotNull(lease);
        store.CompleteBatch(
            lease.BatchId,
            "worker",
            """{"succeeded":0,"failed":[{"id":"account-1"}]}""",
            hasErrors: true,
            lastError: "Target rejected account.",
            nowUtc: now.AddSeconds(1));
        store.SetRunFailure(
            runId,
            new MigrationRunFailure(
                "Batch processing",
                "Accounts",
                "Target rejected account.",
                "InvalidOperationException",
                "Target rejected account.\n   at Example.Target()",
                now.AddSeconds(2)));
        store.SetRunStatus(runId, MigrationRunStatus.Failed, now.AddSeconds(2));

        Assert.Equal("Batch processing", store.GetDashboardRun(runId).Failure?.Phase);
        store.SetRunStatus(runId, MigrationRunStatus.Running, now.AddSeconds(3));
        Assert.Throws<InvalidOperationException>(() =>
            store.QueueFailedBatchesForRetry(runId, now.AddSeconds(4)));
        store.SetRunFailure(
            runId,
            new MigrationRunFailure(
                "Batch processing",
                "Accounts",
                "Target rejected account.",
                "InvalidOperationException",
                "Target rejected account.\n   at Example.Target()",
                now.AddSeconds(4)));
        store.SetRunStatus(runId, MigrationRunStatus.Failed, now.AddSeconds(4));

        var queued = store.QueueFailedBatchesForRetry(runId, now.AddSeconds(5));
        Assert.Equal(1, queued);
        var pending = store.GetBatch(lease.BatchId);
        Assert.NotNull(pending);
        Assert.Equal(MigrationBatchState.Pending, pending.State);
        Assert.Equal(0, pending.Attempts);
        Assert.Null(pending.OutcomesJson);
        Assert.NotNull(pending.PayloadJson);

        var retry = Assert.Single(store.GetDashboardBatchRetryHistory(runId));
        Assert.Equal(lease.BatchId, retry.BatchId);
        Assert.Equal("Accounts", retry.StepId);
        Assert.Equal(1, retry.PreviousAttempts);
        Assert.Equal("Target rejected account.", retry.PreviousLastError);
        Assert.Contains("account-1", retry.PreviousOutcomesJson);

        var run = store.GetDashboardRun(runId);
        Assert.Equal("Batch processing", run.Failure?.Phase);
        Assert.Equal("Accounts", run.Failure?.StepId);
        store.SetRunStatus(runId, MigrationRunStatus.Running, now.AddSeconds(6));
        Assert.Null(store.GetDashboardRun(runId).Failure);
    }

    [Fact]
    public void ExistingRunCannotBeResumedWithDifferentJobDefinition()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun("run-4", "test-job", "hash-1");

        Assert.Throws<InvalidOperationException>(() => store.CreateRun("run-4", "test-job", "hash-2"));
    }

    [Fact]
    public void RunLeaseExcludesOtherSchedulersUntilExpiry()
    {
        using var database = new TemporaryDatabase();
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun("leased-run", "test-job", "hash-1", now);

        Assert.True(store.TryAcquireRunLease("leased-run", "scheduler-1", TimeSpan.FromMinutes(1), now));
        Assert.False(store.TryAcquireRunLease(
            "leased-run",
            "scheduler-2",
            TimeSpan.FromMinutes(1),
            now.AddSeconds(30)));
        Assert.True(store.TryAcquireRunLease(
            "leased-run",
            "scheduler-1",
            TimeSpan.FromMinutes(1),
            now.AddSeconds(30)));
        Assert.False(store.TryAcquireRunLease(
            "leased-run",
            "scheduler-2",
            TimeSpan.FromMinutes(1),
            now.AddMinutes(1)));
        Assert.True(store.TryAcquireRunLease(
            "leased-run",
            "scheduler-2",
            TimeSpan.FromMinutes(1),
            now.AddMinutes(1).AddSeconds(31)));
        Assert.Throws<InvalidOperationException>(() => store.ReleaseRunLease("leased-run", "scheduler-1", now));
        store.ReleaseRunLease("leased-run", "scheduler-2", now);
    }

    [Fact]
    public async Task RunLeasesAllowMultipleActiveRunsPerDatabase()
    {
        using var database = new TemporaryDatabase();
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var firstStore = new SqliteMigrationStore(database.Path);
        var secondStore = new SqliteMigrationStore(database.Path);
        firstStore.CreateRun("run-one", "run-one", "hash-1", now);
        firstStore.CreateRun("run-two", "run-two", "hash-2", now);

        var attempts = await Task.WhenAll(
            Task.Run(() => firstStore.TryAcquireRunLease(
                "run-one",
                "scheduler-one",
                TimeSpan.FromMinutes(1),
                now)),
            Task.Run(() => secondStore.TryAcquireRunLease(
                "run-two",
                "scheduler-two",
                TimeSpan.FromMinutes(1),
                now)));

        Assert.All(attempts, Assert.True);
    }

    [Fact]
    public void WriteGateMeasuresWaitAndHoldDurations()
    {
        using var database = new TemporaryDatabase();
        var measurements = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Migration4Dataverse.Engine.Storage")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, _, _, _) =>
        {
            measurements.AddOrUpdate(instrument.Name, 1, static (_, count) => count + 1);
        });
        listener.Start();

        using (SqliteDatabaseWriteGate.Enter(database.Path))
        {
        }

        Assert.True(measurements["migration.sqlite.write_gate.wait"] > 0);
        Assert.True(measurements["migration.sqlite.write_gate.hold"] > 0);
    }

    [Fact]
    public void ExpiredRunLeaseDoesNotBlockAnotherRunInSameDatabase()
    {
        using var database = new TemporaryDatabase();
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun("expired-run", "expired-run", "hash-1", now);
        store.CreateRun("new-run", "new-run", "hash-2", now);

        Assert.True(store.TryAcquireRunLease(
            "expired-run",
            "scheduler-one",
            TimeSpan.FromMinutes(1),
            now));
        Assert.True(store.TryAcquireRunLease(
            "new-run",
            "scheduler-two",
            TimeSpan.FromMinutes(1),
            now.AddMinutes(1).AddSeconds(1)));
    }

    [Fact]
    public void DependencyEventWaitsForPrerequisitesAndMaterializesChildBatchAtomically()
    {
        using var database = new TemporaryDatabase();
        const string runId = "run-prerequisites";
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun(runId, "test-job", "hash-1", now);
        store.EnqueueBatch(runId, "Accounts", 0, "accounts-0", null, "[{\"id\":1}]", now);
        store.EnqueueBatch(runId, "Contacts", 0, "contacts-0", null, "[{\"id\":2}]", now);

        var accountBatch = store.TryLeaseNextBatch(runId, "worker-accounts", TimeSpan.FromMinutes(1), now);
        var contactBatch = store.TryLeaseNextBatch(runId, "worker-contacts", TimeSpan.FromMinutes(1), now);
        Assert.NotNull(accountBatch);
        Assert.NotNull(contactBatch);
        Assert.Equal("Accounts", accountBatch.StepId);
        Assert.Equal("Contacts", contactBatch.StepId);

        var dependencyEvent = DependencyEvent.Create(
            accountBatch.BatchId,
            "Projects",
            "{\"accountId\":1,\"contactId\":2}",
            new[] { contactBatch.BatchId });
        store.CompleteBatch(
            accountBatch.BatchId,
            "worker-accounts",
            "{\"succeeded\":1}",
            hasErrors: false,
            new[] { dependencyEvent },
            nowUtc: now.AddSeconds(1));

        Assert.Null(store.TryMaterializeDependencyEvent(runId, dependencyEvent.EventId, now.AddSeconds(2)));
        Assert.Single(store.GetPendingDependencyEvents(runId));

        store.CompleteBatch(
            contactBatch.BatchId,
            "worker-contacts",
            "{\"succeeded\":1}",
            hasErrors: false,
            nowUtc: now.AddSeconds(3));

        var materialization = store.TryMaterializeDependencyEvent(runId, dependencyEvent.EventId, now.AddSeconds(4));
        Assert.NotNull(materialization);
        Assert.True(materialization.WasCreated);
        Assert.Equal("Projects", materialization.Batch.StepId);
        Assert.Equal(0, materialization.Batch.Sequence);
        Assert.Equal(MigrationBatchState.Pending, materialization.Batch.State);
        Assert.Empty(store.GetPendingDependencyEvents(runId));

        var replayedMaterialization = store.TryMaterializeDependencyEvent(
            runId,
            dependencyEvent.EventId,
            now.AddSeconds(5));
        Assert.NotNull(replayedMaterialization);
        Assert.False(replayedMaterialization.WasCreated);
        Assert.Equal(materialization.Batch.BatchId, replayedMaterialization.Batch.BatchId);

        var progress = store.GetRunProgress(runId);
        Assert.Equal(1, progress.PendingBatches);
        Assert.Equal(1, progress.CompletedDependencyEvents);
        Assert.Equal(0, progress.PendingDependencyEvents);
    }

    [Fact]
    public void FreshDatabaseAppliesOrderedMigrationsAndUsesStepSchema()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun("target-id-run", "test-run", "hash-1");
        store.SetRunStatus("target-id-run", MigrationRunStatus.CompletedWithErrors);
        var reopenedStore = new SqliteMigrationStore(database.Path);
        Assert.Equal("CompletedWithErrors", reopenedStore.GetDashboardRun("target-id-run").Status);

        using var verificationConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.Path
        }.ToString());
        verificationConnection.Open();
        using var verificationCommand = verificationConnection.CreateCommand();
        verificationCommand.CommandText = "SELECT Version, Name FROM SchemaMigrations ORDER BY Version;";
        using (var migrationReader = verificationCommand.ExecuteReader())
        {
            Assert.True(migrationReader.Read());
            Assert.Equal(1, migrationReader.GetInt32(0));
            Assert.Equal("001-initial-store", migrationReader.GetString(1));
            Assert.True(migrationReader.Read());
            Assert.Equal(2, migrationReader.GetInt32(0));
            Assert.Equal("002-top-level-steps", migrationReader.GetString(1));
            Assert.True(migrationReader.Read());
            Assert.Equal(3, migrationReader.GetInt32(0));
            Assert.Equal("003-step-runtime-metrics", migrationReader.GetString(1));
            Assert.True(migrationReader.Read());
            Assert.Equal(4, migrationReader.GetInt32(0));
            Assert.Equal("004-target-record-ids", migrationReader.GetString(1));
            Assert.True(migrationReader.Read());
            Assert.Equal(5, migrationReader.GetInt32(0));
            Assert.Equal("005-target-record-contexts", migrationReader.GetString(1));
            Assert.True(migrationReader.Read());
            Assert.Equal(6, migrationReader.GetInt32(0));
            Assert.Equal("006-streaming-join-staging", migrationReader.GetString(1));
            Assert.True(migrationReader.Read());
            Assert.Equal(7, migrationReader.GetInt32(0));
            Assert.Equal("007-record-progress", migrationReader.GetString(1));
            Assert.True(migrationReader.Read());
            Assert.Equal(8, migrationReader.GetInt32(0));
            Assert.Equal("008-step-activity-periods", migrationReader.GetString(1));
            Assert.True(migrationReader.Read());
            Assert.Equal(9, migrationReader.GetInt32(0));
            Assert.Equal("009-source-key-index", migrationReader.GetString(1));
            Assert.True(migrationReader.Read());
            Assert.Equal(10, migrationReader.GetInt32(0));
            Assert.Equal("010-streaming-join-key-index", migrationReader.GetString(1));
            Assert.True(migrationReader.Read());
            Assert.Equal(11, migrationReader.GetInt32(0));
            Assert.Equal("011-debug-snapshot-retention-index", migrationReader.GetString(1));
            Assert.True(migrationReader.Read());
            Assert.Equal(12, migrationReader.GetInt32(0));
            Assert.Equal("012-dataverse-target-tuning-profiles", migrationReader.GetString(1));
            Assert.True(migrationReader.Read());
            Assert.Equal(13, migrationReader.GetInt32(0));
            Assert.Equal("013-pipeline-aware-tuning-profiles", migrationReader.GetString(1));
            Assert.True(migrationReader.Read());
            Assert.Equal(14, migrationReader.GetInt32(0));
            Assert.Equal("014-connection-scoped-tuning-profiles", migrationReader.GetString(1));
            Assert.True(migrationReader.Read());
            Assert.Equal(15, migrationReader.GetInt32(0));
            Assert.Equal("015-run-failure-recovery", migrationReader.GetString(1));
            Assert.True(migrationReader.Read());
            Assert.Equal(16, migrationReader.GetInt32(0));
            Assert.Equal("016-error-threshold-continuation", migrationReader.GetString(1));
            Assert.True(migrationReader.Read());
            Assert.Equal(17, migrationReader.GetInt32(0));
            Assert.Equal("017-row-error-retry", migrationReader.GetString(1));
            Assert.False(migrationReader.Read());
        }

        verificationCommand.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name = 'StepCheckpoints';
            """;
        Assert.Equal("StepCheckpoints", verificationCommand.ExecuteScalar()?.ToString());
        verificationCommand.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name = 'SourceKeyIndex';
            """;
        Assert.Equal("SourceKeyIndex", verificationCommand.ExecuteScalar()?.ToString());
        verificationCommand.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name = 'JoinInputKeyIndex';
            """;
        Assert.Equal("JoinInputKeyIndex", verificationCommand.ExecuteScalar()?.ToString());
        verificationCommand.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name = 'StepActivity';
            """;
        Assert.Equal("StepActivity", verificationCommand.ExecuteScalar()?.ToString());
        verificationCommand.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name = 'StepActivityPeriods';
            """;
        Assert.Equal("StepActivityPeriods", verificationCommand.ExecuteScalar()?.ToString());
        verificationCommand.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name = 'TargetRecordIds';
            """;
        Assert.Equal("TargetRecordIds", verificationCommand.ExecuteScalar()?.ToString());
        verificationCommand.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name = 'TargetRecordContexts';
            """;
        Assert.Equal("TargetRecordContexts", verificationCommand.ExecuteScalar()?.ToString());
        verificationCommand.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name = 'JoinInputRecords';
            """;
        Assert.Equal("JoinInputRecords", verificationCommand.ExecuteScalar()?.ToString());
        verificationCommand.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name = 'JoinRelationshipMatches';
            """;
        Assert.Equal("JoinRelationshipMatches", verificationCommand.ExecuteScalar()?.ToString());
        verificationCommand.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name = 'BatchRowErrors';
            """;
        Assert.Equal("BatchRowErrors", verificationCommand.ExecuteScalar()?.ToString());
        verificationCommand.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name = 'BatchRowRetryLinks';
            """;
        Assert.Equal("BatchRowRetryLinks", verificationCommand.ExecuteScalar()?.ToString());
        verificationCommand.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name = 'BatchParentReferences';
            """;
        Assert.Equal("BatchParentReferences", verificationCommand.ExecuteScalar()?.ToString());
        verificationCommand.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name = 'DataverseTargetTuningProfiles';
            """;
        Assert.Equal("DataverseTargetTuningProfiles", verificationCommand.ExecuteScalar()?.ToString());
        verificationCommand.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name = 'BatchRetryHistory';
            """;
        Assert.Equal("BatchRetryHistory", verificationCommand.ExecuteScalar()?.ToString());
        verificationCommand.CommandText = """
            EXPLAIN QUERY PLAN
            DELETE FROM DebugSnapshots
            WHERE RunId = $runId
              AND CapturedUtc < (
                SELECT strftime(
                    '%Y-%m-%dT%H:%M:%f+00:00',
                    'now',
                    '-' || DebugCaptureRetentionDays || ' days')
                FROM Runs
                WHERE RunId = $runId
              );
            """;
        verificationCommand.Parameters.AddWithValue("$runId", "target-id-run");
        using (var planReader = verificationCommand.ExecuteReader())
        {
            var planDetails = new List<string>();
            while (planReader.Read())
            {
                planDetails.Add(planReader.GetString(3));
            }

            Assert.Contains(
                planDetails,
                detail => detail.Contains("IX_DebugSnapshots_Run_Captured", StringComparison.Ordinal) &&
                          detail.Contains("CapturedUtc<?", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void VersionFifteenDatabaseRebuildsRunStatusConstraintForAcceptedErrors()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun("legacy-status-run", "legacy-status-test", "hash-1");

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.Path
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = OFF;";
            command.ExecuteNonQuery();
            command.CommandText = """
                BEGIN;
                CREATE TABLE Runs_15 (
                    RunId TEXT NOT NULL PRIMARY KEY,
                    RunName TEXT NOT NULL,
                    StepHash TEXT NOT NULL,
                    Status TEXT NOT NULL CHECK (Status IN ('Running', 'Completed', 'Failed', 'Cancelled')),
                    CreatedUtc TEXT NOT NULL,
                    UpdatedUtc TEXT NOT NULL,
                    SchedulerLeaseOwner TEXT NULL,
                    SchedulerLeaseExpiresUtc TEXT NULL,
                    PipelineJson TEXT NOT NULL DEFAULT '[]',
                    DebugCaptureEnabled INTEGER NOT NULL DEFAULT 0,
                    FinishedUtc TEXT NULL,
                    DebugCaptureRetentionDays INTEGER NOT NULL DEFAULT 30,
                    ConfigurationJson TEXT NOT NULL DEFAULT 'null',
                    ConnectionInstancesJson TEXT NOT NULL DEFAULT '[]',
                    FailureJson TEXT NULL
                );
                INSERT INTO Runs_15
                SELECT RunId, RunName, StepHash, Status, CreatedUtc, UpdatedUtc,
                       SchedulerLeaseOwner, SchedulerLeaseExpiresUtc, PipelineJson,
                       DebugCaptureEnabled, FinishedUtc, DebugCaptureRetentionDays,
                       ConfigurationJson, ConnectionInstancesJson, FailureJson
                FROM Runs;
                DROP TABLE Runs;
                ALTER TABLE Runs_15 RENAME TO Runs;
                ALTER TABLE Batches DROP COLUMN FailureAccepted;
                DROP TABLE StepFailureContinuations;
                DROP TABLE BatchRowRetryLinks;
                DROP TABLE BatchRowErrors;
                DELETE FROM SchemaMigrations WHERE Version = 17;
                DELETE FROM SchemaMigrations WHERE Version = 16;
                COMMIT;
                """;
            command.ExecuteNonQuery();
            command.CommandText = "PRAGMA foreign_keys = ON;";
            command.ExecuteNonQuery();
        }

        var upgradedStore = new SqliteMigrationStore(database.Path);
        upgradedStore.SetRunStatus("legacy-status-run", MigrationRunStatus.CompletedWithErrors);
        Assert.Equal("CompletedWithErrors", upgradedStore.GetDashboardRun("legacy-status-run").Status);
    }

    [Fact]
    public void VersionTenDatabaseGetsDebugSnapshotRetentionIndex()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun("pre-upgrade-run", "test-run", "hash-1");

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.Path
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE DataverseTargetTuningProfiles;
                DROP TABLE BatchRetryHistory;
                DROP TABLE StepFailureContinuations;
                DROP TABLE BatchRowRetryLinks;
                DROP TABLE BatchRowErrors;
                ALTER TABLE Batches DROP COLUMN FailureAccepted;
                ALTER TABLE Runs DROP COLUMN FailureJson;
                DELETE FROM SchemaMigrations WHERE Version = 17;
                DELETE FROM SchemaMigrations WHERE Version = 16;
                DELETE FROM SchemaMigrations WHERE Version = 15;
                DELETE FROM SchemaMigrations WHERE Version = 14;
                DELETE FROM SchemaMigrations WHERE Version = 13;
                DELETE FROM SchemaMigrations WHERE Version = 12;
                DROP INDEX IX_DebugSnapshots_Run_Captured;
                DELETE FROM SchemaMigrations WHERE Version = 11;
                """;
            command.ExecuteNonQuery();
        }

        var upgradedStore = new SqliteMigrationStore(database.Path);
        upgradedStore.CreateRun("post-upgrade-run", "test-run", "hash-1");

        using var verificationConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.Path
        }.ToString());
        verificationConnection.Open();
        using var verificationCommand = verificationConnection.CreateCommand();
        verificationCommand.CommandText = """
            SELECT Name FROM SchemaMigrations WHERE Version = 11;
            """;
        Assert.Equal(
            "011-debug-snapshot-retention-index",
            verificationCommand.ExecuteScalar()?.ToString());
        verificationCommand.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'index' AND name = 'IX_DebugSnapshots_Run_Captured';
            """;
        Assert.Equal(
            "IX_DebugSnapshots_Run_Captured",
            verificationCommand.ExecuteScalar()?.ToString());
        verificationCommand.CommandText = """
            SELECT Name FROM SchemaMigrations WHERE Version = 12;
            """;
        Assert.Equal(
            "012-dataverse-target-tuning-profiles",
            verificationCommand.ExecuteScalar()?.ToString());
        verificationCommand.CommandText = """
            SELECT Name FROM SchemaMigrations WHERE Version = 13;
            """;
        Assert.Equal(
            "013-pipeline-aware-tuning-profiles",
            verificationCommand.ExecuteScalar()?.ToString());
        verificationCommand.CommandText = """
            SELECT Name FROM SchemaMigrations WHERE Version = 14;
            """;
        Assert.Equal(
            "014-connection-scoped-tuning-profiles",
            verificationCommand.ExecuteScalar()?.ToString());
        verificationCommand.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name = 'DataverseTargetTuningProfiles';
            """;
        Assert.Equal(
            "DataverseTargetTuningProfiles",
            verificationCommand.ExecuteScalar()?.ToString());
        verificationCommand.CommandText = """
            SELECT AlgorithmVersion FROM DataverseTargetTuningProfiles LIMIT 1;
            """;
        Assert.Null(verificationCommand.ExecuteScalar());
    }

    [Fact]
    public void DataverseTargetTuningProfilesCanBeUpdatedAndCleared()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);

        Assert.Null(store.GetDataverseTargetTuningProfile("AccountsTarget"));
        store.SaveDataverseTargetTuningProfile("AccountsTarget", 250, 8);
        var profile = store.GetDataverseTargetTuningProfile("AccountsTarget");
        Assert.NotNull(profile);
        Assert.Equal(250, profile.BatchSize);
        Assert.Equal(8, profile.MaxConcurrency);
        Assert.Equal(1, profile.AlgorithmVersion);

        store.SaveDataverseTargetTuningProfile(
            "AccountsTarget",
            null,
            4,
            algorithmVersion: 2,
            connectionId: "crm");
        profile = store.GetDataverseTargetTuningProfile("AccountsTarget");
        Assert.Equal(250, profile?.BatchSize);
        Assert.Equal(4, profile?.MaxConcurrency);
        Assert.Equal(2, profile?.AlgorithmVersion);
        Assert.Equal("crm", profile?.ConnectionId);

        store.SaveDataverseTargetTuningProfile("ContactsTarget", 100, null);
        Assert.Equal(1, store.ClearDataverseTargetTuningProfile("AccountsTarget"));
        Assert.Null(store.GetDataverseTargetTuningProfile("AccountsTarget"));
        Assert.Equal(0, store.ClearDataverseTargetTuningProfile("AccountsTarget"));
        Assert.Equal(1, store.ClearDataverseTargetTuningProfiles());
        Assert.Null(store.GetDataverseTargetTuningProfile("ContactsTarget"));
        Assert.Throws<ArgumentException>(() =>
            store.SaveDataverseTargetTuningProfile("InvalidTarget", null, null));
    }

    [Fact]
    public void TargetRecordIdsAreRunAndConnectionScopedAndCanBeUpdated()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun("mapping-run", "test-run", "hash-1");
        var firstId = Guid.NewGuid();
        var updatedId = Guid.NewGuid();
        var mapping = new TargetRecordIdMapping(
            "crm",
            "account",
            "new_salesforceid",
            "001source",
            firstId);

        store.SaveTargetRecordIds("mapping-run", [mapping]);
        Assert.Equal(
            firstId,
            store.GetTargetRecordIds(
                "mapping-run",
                "crm",
                [new TargetRecordIdLookup("account", "new_salesforceid", "001source")])
                [new TargetRecordIdLookup("account", "new_salesforceid", "001source")]);
        Assert.Empty(store.GetTargetRecordIds(
            "mapping-run",
            "other-connection",
            [new TargetRecordIdLookup("account", "new_salesforceid", "001source")]));

        store.SaveTargetRecordIds(
            "mapping-run",
            [mapping with { RecordId = updatedId }]);
        Assert.Equal(
            updatedId,
            store.GetTargetRecordIds(
                "mapping-run",
                "crm",
                [new TargetRecordIdLookup("account", "new_salesforceid", "001source")])
                [new TargetRecordIdLookup("account", "new_salesforceid", "001source")]);
    }

    [Fact]
    public void JoinStagingIsPagedDurableAndRemovedWhenCleared()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun("join-run", "join-test", "hash");
        store.EnqueueBatch("join-run", "Child", 0, "child-batch-0", null, "{}");
        store.EnqueueBatch("join-run", "Parent", 0, "parent-batch-0", null, "{}");
        foreach (var batchId in new[] { "child-batch-0", "parent-batch-0" })
        {
            var batch = store.TryLeaseNextBatch("join-run", "join-worker", TimeSpan.FromMinutes(1));
            Assert.Equal(batchId, batch?.BatchId);
            store.CompleteBatch(
                batchId,
                "join-worker",
                """{"succeeded":1,"failed":0}""",
                hasErrors: false);
        }
        store.SaveJoinInputRecords(
            "join-run",
            [
                new JoinInputRecordMapping(
                    "Join",
                    "Child",
                    0,
                    1,
                    """{"id":"child-1"}""",
                    "child-batch-0",
                    0,
                    ["id"]),
                new JoinInputRecordMapping(
                    "Join",
                    "Parent",
                    0,
                    1,
                    """{"id":"parent-1"}""",
                    "parent-batch-0",
                    0,
                    ["id"])
            ]);
        store.SaveJoinRelationshipMatches(
            "join-run",
            "Join",
            [
                new JoinRelationshipMatch(0, 1, "Parent", "Parent", 0, 1)
            ]);

        var reopenedStore = new SqliteMigrationStore(database.Path);
        var child = Assert.Single(reopenedStore.ReadJoinInputRecordPage(
            "join-run", "Join", "Child", 0, 0, 10));
        Assert.Equal("""{"id":"child-1"}""", child.RecordJson);
        Assert.Equal("""["child-1"]""", child.KeyJson);
        var indexedParent = reopenedStore.FindJoinInputRecordByKey(
            "join-run",
            "Join",
            "Parent",
            """["parent-1"]""",
            throughSequence: 0);
        Assert.Equal("""{"id":"parent-1"}""", indexedParent?.RecordJson);
        var matches = reopenedStore.ReadJoinRelationshipMatches(
            "join-run",
            "Join",
            ["Parent"],
            new JoinInputRecordKey(0, 1),
            new JoinInputRecordKey(0, 1));
        var parentReference = matches[new JoinInputRecordKey(0, 1)]["Parent"];
        Assert.Equal("Parent", parentReference.ParentInputStepId);
        var parentRecords = reopenedStore.ReadJoinInputRecordsByKeys(
            "join-run",
            "Join",
            "Parent",
            [new JoinInputRecordKey(parentReference.ParentSequence!.Value, parentReference.ParentRecordIndex!.Value)]);
        Assert.Equal(
            """{"id":"parent-1"}""",
            parentRecords[new JoinInputRecordKey(0, 1)].RecordJson);

        reopenedStore.ClearJoinStaging("join-run");

        Assert.Empty(reopenedStore.ReadJoinInputRecordPage(
            "join-run", "Join", "Child", 0, 0, 10));
        Assert.Null(reopenedStore.FindJoinInputRecordByKey(
            "join-run",
            "Join",
            "Parent",
            """["parent-1"]""",
            throughSequence: 0));
        Assert.Empty(reopenedStore.ReadJoinRelationshipMatches(
            "join-run",
            "Join",
            ["Parent"],
            new JoinInputRecordKey(0, 1),
            new JoinInputRecordKey(0, 1)));
    }

    [Fact]
    public void TargetRecordContextsAreRunAndStepScopedAndCanBeUpdated()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun("context-run", "test-run", "hash-1");
        store.SaveTargetRecordContexts(
            "context-run",
            [
                new TargetRecordContextMapping(
                    "account-target",
                    "crm",
                    "account",
                    TargetRecordContextKey.Serialize(["new_salesforceid", "region_code"]),
                    TargetRecordContextKey.Serialize(["001source", "north"]),
                    """{"Id":"11111111-1111-1111-1111-111111111111","name":"First"}""")
            ]);

        Assert.Contains(
            "\"name\":\"First\"",
            store.GetTargetRecordContext(
                "context-run",
                "account-target",
                "crm",
                "account",
                ["new_salesforceid", "region_code"],
                ["001source", "north"]));
        Assert.Throws<KeyNotFoundException>(() =>
            store.GetTargetRecordContext(
                "context-run",
                "other-target",
                "crm",
                "account",
                ["new_salesforceid", "region_code"],
                ["001source", "north"]));
        Assert.Throws<KeyNotFoundException>(() =>
            store.GetTargetRecordContext(
                "other-run",
                "account-target",
                "crm",
                "account",
                ["new_salesforceid", "region_code"],
                ["001source", "north"]));
    }

    [Fact]
    public void StepSchemaMigrationPreservesLegacyRunAndBatchData()
    {
        using var database = new TemporaryDatabase();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.Path
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE SchemaMigrations (
                    Version INTEGER NOT NULL PRIMARY KEY,
                    Name TEXT NOT NULL UNIQUE,
                    AppliedUtc TEXT NOT NULL
                );
                INSERT INTO SchemaMigrations (Version, Name, AppliedUtc)
                VALUES (1, '001-initial-store', '2026-10-20T12:00:00.0000000+00:00');

                CREATE TABLE Runs (
                    RunId TEXT NOT NULL PRIMARY KEY,
                    JobName TEXT NOT NULL,
                    JobHash TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    CreatedUtc TEXT NOT NULL,
                    UpdatedUtc TEXT NOT NULL,
                    SchedulerLeaseOwner TEXT NULL,
                    SchedulerLeaseExpiresUtc TEXT NULL,
                    PipelineJson TEXT NOT NULL DEFAULT '[]',
                    DebugCaptureEnabled INTEGER NOT NULL DEFAULT 0,
                    FinishedUtc TEXT NULL,
                    DebugCaptureRetentionDays INTEGER NOT NULL DEFAULT 30,
                    ConfigurationJson TEXT NOT NULL DEFAULT 'null',
                    ConnectionInstancesJson TEXT NOT NULL DEFAULT '[]'
                );
                INSERT INTO Runs (RunId, JobName, JobHash, Status, CreatedUtc, UpdatedUtc)
                VALUES ('legacy-run', 'migration-run', 'legacy-hash', 'Completed',
                    '2026-10-20T12:00:00.0000000+00:00', '2026-10-20T12:00:00.0000000+00:00');

                CREATE TABLE TableCheckpoints (
                    RunId TEXT NOT NULL,
                    TableName TEXT NOT NULL,
                    LastCompletedSequence INTEGER NOT NULL DEFAULT -1,
                    CursorJson TEXT NULL,
                    UpdatedUtc TEXT NOT NULL
                );
                INSERT INTO TableCheckpoints (RunId, TableName, LastCompletedSequence, UpdatedUtc)
                VALUES ('legacy-run', 'transform-step', 0, '2026-10-20T12:00:00.0000000+00:00');

                CREATE TABLE Batches (
                    RunId TEXT NOT NULL,
                    TableName TEXT NOT NULL,
                    Sequence INTEGER NOT NULL,
                    OutcomesJson TEXT NULL
                );
                INSERT INTO Batches (RunId, TableName, Sequence, OutcomesJson)
                VALUES ('legacy-run', 'transform-step', 0, '{"succeeded":3,"failed":[{},{}]}');

                CREATE TABLE DependencyEvents (
                    RunId TEXT NOT NULL,
                    ParentBatchId TEXT NOT NULL,
                    ChildTable TEXT NOT NULL,
                    PayloadJson TEXT NOT NULL,
                    PrerequisiteBatchIdsJson TEXT NOT NULL,
                    State TEXT NOT NULL,
                    CreatedUtc TEXT NOT NULL
                );
                INSERT INTO DependencyEvents (
                    RunId, ParentBatchId, ChildTable, PayloadJson,
                    PrerequisiteBatchIdsJson, State, CreatedUtc)
                VALUES ('legacy-run', 'legacy-batch', 'target-step', '{}', '[]', 'Pending',
                    '2026-10-20T12:00:00.0000000+00:00');

                CREATE TABLE SourceFeeders (
                    RunId TEXT NOT NULL,
                    JobName TEXT NOT NULL,
                    Completed INTEGER NOT NULL,
                    UpdatedUtc TEXT NOT NULL
                );
                INSERT INTO SourceFeeders (RunId, JobName, Completed, UpdatedUtc)
                VALUES ('legacy-run', 'source-step', 1, '2026-10-20T12:00:00.0000000+00:00');

                CREATE TABLE DebugSnapshots (
                    SnapshotId INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    RunId TEXT NOT NULL,
                    BatchId TEXT NOT NULL,
                    JobName TEXT NOT NULL,
                    StageName TEXT NOT NULL,
                    RecordIndex INTEGER NOT NULL,
                    Attempt INTEGER NOT NULL,
                    LineageId TEXT NOT NULL,
                    ParentLineageIdsJson TEXT NOT NULL,
                    PayloadJson TEXT NOT NULL,
                    CapturedUtc TEXT NOT NULL
                );
                INSERT INTO DebugSnapshots (
                    RunId, BatchId, JobName, StageName, RecordIndex, Attempt,
                    LineageId, ParentLineageIdsJson, PayloadJson, CapturedUtc)
                VALUES ('legacy-run', 'legacy-batch', 'source-step', 'Input', 0, 1,
                    'lineage-1', '[]', '{}', '2026-10-20T12:00:00.0000000+00:00');
                """;
            command.ExecuteNonQuery();
        }

        _ = new SqliteMigrationStore(database.Path);
        using var verificationConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database.Path
        }.ToString());
        verificationConnection.Open();
        using var verificationCommand = verificationConnection.CreateCommand();
        verificationCommand.CommandText = "SELECT RunName, StepHash FROM Runs WHERE RunId = 'legacy-run';";
        using (var runReader = verificationCommand.ExecuteReader())
        {
            Assert.True(runReader.Read());
            Assert.Equal("migration-run", runReader.GetString(0));
            Assert.Equal("legacy-hash", runReader.GetString(1));
        }

        verificationCommand.CommandText = "SELECT StepId FROM StepCheckpoints WHERE RunId = 'legacy-run';";
        Assert.Equal("transform-step", verificationCommand.ExecuteScalar()?.ToString());
        verificationCommand.CommandText = "SELECT StepId FROM Batches WHERE RunId = 'legacy-run';";
        Assert.Equal("transform-step", verificationCommand.ExecuteScalar()?.ToString());
        verificationCommand.CommandText = "SELECT SucceededRows || ',' || FailedRows FROM Batches WHERE RunId = 'legacy-run';";
        Assert.Equal("3,2", verificationCommand.ExecuteScalar()?.ToString());
        verificationCommand.CommandText = "SELECT ChildStepId FROM DependencyEvents WHERE RunId = 'legacy-run';";
        Assert.Equal("target-step", verificationCommand.ExecuteScalar()?.ToString());
        verificationCommand.CommandText = "SELECT StepId FROM SourceFeeders WHERE RunId = 'legacy-run';";
        Assert.Equal("source-step", verificationCommand.ExecuteScalar()?.ToString());
        verificationCommand.CommandText = "SELECT StepId FROM DebugSnapshots WHERE RunId = 'legacy-run';";
        Assert.Equal("source-step", verificationCommand.ExecuteScalar()?.ToString());
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"migration4dataverse-{Guid.NewGuid():N}");

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
