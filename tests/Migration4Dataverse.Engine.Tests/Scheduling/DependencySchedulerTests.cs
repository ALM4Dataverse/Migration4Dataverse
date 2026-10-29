using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Migration4Dataverse.Engine.Scheduling;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.Tests.Scheduling;

public sealed class DependencySchedulerTests
{
    [Fact]
    public async Task SqliteBusyWhileLeasingIsRetried()
    {
        var attempts = 0;
        var result = await DependencyScheduler.RetrySqliteLockAsync(() =>
        {
            if (++attempts == 1)
            {
                throw new SqliteException("Database is locked.", 5, 5);
            }

            return "leased";
        }, CancellationToken.None);

        Assert.Equal("leased", result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task SqliteLeaseRetryDoesNotRetryUnrelatedErrors()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<SqliteException>(() =>
            DependencyScheduler.RetrySqliteLockAsync<int>(() =>
            {
                attempts++;
                throw new SqliteException("Constraint failed.", 19, 19);
            }, CancellationToken.None));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task CompletionHookFailureMarksTheRunFailed()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        var step = new MigrationStepDefinition(
            "Root",
            inputs: null,
            (checkpoint, token) => SingleBatch("{}", token),
            (_, _) => Task.FromResult(new MigrationBatchResult("{}")))
        {
            CompleteAsync = _ => Task.FromException(new IOException("file finalization failed"))
        };

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            new DependencyScheduler(store).ExecuteAsync(
                "completion-hook-run",
                "completion-hook-test",
                "hash-1",
                [step],
                CreateOptions(maxDegreeOfParallelism: 1, maxQueuedBatches: 1)));

        Assert.Equal("file finalization failed", exception.Message);
        Assert.Equal("Failed", store.GetDashboardRun("completion-hook-run").Status);
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(0, false)]
    public async Task MaxDegreeOfParallelismMustBePositiveWithoutAFixedCeiling(
        int maxDegreeOfParallelism,
        bool isPositive)
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        const string runId = "max-dop-limit-run";
        var step = new MigrationStepDefinition(
            "Root",
            inputs: null,
            (checkpoint, token) => SingleBatch("{}", token),
            (_, _) => Task.FromResult(new MigrationBatchResult("{}")));
        var runTask = new DependencyScheduler(store).ExecuteAsync(
            runId,
            "max-dop-limit-test",
            "hash-1",
            [step],
            CreateOptions(maxDegreeOfParallelism, maxQueuedBatches: 1));

        if (!isPositive)
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runTask);
            Assert.Throws<KeyNotFoundException>(() => store.GetRunProgress(runId));
            return;
        }

        await runTask;
        Assert.Equal("Completed", store.GetDashboardRun(runId).Status);
    }

    [Fact]
    public void HigherPipelineImpactGetsMoreDispatchesWithoutStarvingOtherTargets()
    {
        var candidates = new[]
        {
            new PendingBatchStepCandidate("HighImpact", 0),
            new PendingBatchStepCandidate("LowerImpact", 0)
        };
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var weights = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["HighImpact"] = 4,
            ["LowerImpact"] = 1
        };
        var highImpactDispatches = 0;
        var lowerImpactDispatches = 0;

        for (var dispatch = 0; dispatch < 40; dispatch++)
        {
            var candidate = PipelineDispatchOrderer.Order(
                candidates,
                ["HighImpact", "LowerImpact"],
                weights,
                counts,
                dispatch % 2).First();
            counts[candidate.StepId] = counts.GetValueOrDefault(candidate.StepId) + 1;
            if (candidate.StepId == "HighImpact")
            {
                highImpactDispatches++;
            }
            else
            {
                lowerImpactDispatches++;
            }
        }

        Assert.Equal(32, highImpactDispatches);
        Assert.Equal(8, lowerImpactDispatches);
    }

    [Fact]
    public void LargerReadyBacklogIncreasesDispatchPriority()
    {
        var candidates = new[]
        {
            new PendingBatchStepCandidate("Backlogged", 0, PendingBatchCount: 8),
            new PendingBatchStepCandidate("LightlyQueued", 0, PendingBatchCount: 1)
        };
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var weights = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["Backlogged"] = 1,
            ["LightlyQueued"] = 1
        };
        var backloggedDispatches = 0;

        for (var dispatch = 0; dispatch < 40; dispatch++)
        {
            var candidate = PipelineDispatchOrderer.Order(
                candidates,
                ["Backlogged", "LightlyQueued"],
                weights,
                counts,
                dispatch % 2).First();
            counts[candidate.StepId] = counts.GetValueOrDefault(candidate.StepId) + 1;
            if (candidate.StepId == "Backlogged")
            {
                backloggedDispatches++;
            }
        }

        Assert.True(backloggedDispatches > 20);
        Assert.True(backloggedDispatches < 40);
    }

    [Fact]
    public async Task FastFanOutBranchProgressesWhileSlowTargetPermitIsUnavailable()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        var allowSlowTarget = NewSignal();
        var fastBatchesCompleted = 0;
        var rootSourceFinished = 0;

        async IAsyncEnumerable<MigrationWorkBatch> ReadRoot(
            MigrationStepCheckpoint checkpoint,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var sequence = 0; sequence < 10; sequence++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new MigrationWorkBatch($"{{\"sequence\":{sequence}}}");
                await Task.Yield();
            }

            Volatile.Write(ref rootSourceFinished, 1);
        }

        var steps = new[]
        {
            new MigrationStepDefinition(
                "Root",
                inputs: null,
                ReadRoot,
                (_, _) => Task.FromResult(new MigrationBatchResult(
                    "{}",
                    Dependencies: Enumerable.Range(0, 5)
                        .SelectMany(item => new[]
                        {
                            new MigrationDependencyOutput("FastTarget", $"{{\"item\":{item}}}"),
                            new MigrationDependencyOutput("SlowTarget", $"{{\"item\":{item}}}")
                        })
                        .ToArray()))),
            new MigrationStepDefinition(
                "FastTarget",
                ["Root"],
                readSourceBatches: null,
                (_, _) =>
                {
                    Interlocked.Increment(ref fastBatchesCompleted);
                    return Task.FromResult(new MigrationBatchResult("{}"));
                },
                type: "target"),
            new MigrationStepDefinition(
                "SlowTarget",
                ["Root"],
                readSourceBatches: null,
                (_, _) => Task.FromResult(new MigrationBatchResult("{}")),
                type: "target")
            {
                TryAcquireExecutionLease = () =>
                    allowSlowTarget.Task.IsCompleted ? new TestExecutionLease() : null
            }
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = new DependencyScheduler(store).ExecuteAsync(
            "branch-fairness-run",
            "branch-fairness-test",
            "hash-1",
            steps,
            CreateOptions(maxDegreeOfParallelism: 4, maxQueuedBatches: 8) with
            {
                MaxQueuedBatchesPerStep = 2
            },
            timeout.Token);

        try
        {
            for (var attempt = 0; attempt < 500 &&
                 Volatile.Read(ref fastBatchesCompleted) < 5; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
            }

            Assert.InRange(Volatile.Read(ref fastBatchesCompleted), 5, 49);
            Assert.Equal(0, store.GetDashboardRun("branch-fairness-run")
                .Steps.Single(step => step.Id == "SlowTarget").SucceededBatches);
            Assert.Equal(0, Volatile.Read(ref rootSourceFinished));
        }
        finally
        {
            allowSlowTarget.TrySetResult();
        }

        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(110, result.SucceededBatches);
        Assert.Equal(50, Volatile.Read(ref fastBatchesCompleted));
    }

    [Fact]
    public void BatchIdentitiesAreStableAndDoNotCollideAcrossRunAndJobBoundaries()
    {
        Assert.Equal(
            MigrationBatchIdentity.Create("run-a", "b", 0),
            MigrationBatchIdentity.Create("run-a", "b", 0));
        Assert.NotEqual(
            MigrationBatchIdentity.Create("run-a", "b", 0),
            MigrationBatchIdentity.Create("run", "a-b", 0));
    }

    [Fact]
    public async Task ChildBatchStartsBeforeRootSourceHasFinished()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        var childStarted = NewSignal();
        var allowRootSourceToFinish = NewSignal();
        var rootSourceFinished = false;

        async IAsyncEnumerable<MigrationWorkBatch> ReadAccounts(
            MigrationStepCheckpoint checkpoint,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Assert.Equal(-1, checkpoint.LastCompletedSequence);
            yield return new MigrationWorkBatch("{\"sequence\":0}");
            await childStarted.Task.WaitAsync(cancellationToken);
            await allowRootSourceToFinish.Task.WaitAsync(cancellationToken);
            yield return new MigrationWorkBatch("{\"sequence\":1}");
            rootSourceFinished = true;
        }

        var jobs = new[]
        {
            new MigrationStepDefinition(
                "Accounts",
                inputs: null,
                ReadAccounts,
                (batch, _) => Task.FromResult(new MigrationBatchResult(
                    "{}",
                    Dependencies: batch.Sequence == 0
                        ? new[] { new MigrationDependencyOutput("Contacts", "{\"account\":1}") }
                        : null))),
            new MigrationStepDefinition(
                "Contacts",
                new[] { "Accounts" },
                readSourceBatches: null,
                (_, _) =>
                {
                    childStarted.TrySetResult();
                    return Task.FromResult(new MigrationBatchResult("{}"));
                })
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = new DependencyScheduler(store).ExecuteAsync(
            "streaming-run",
            "streaming-test",
            "hash-1",
            jobs,
            CreateOptions(maxDegreeOfParallelism: 1, maxQueuedBatches: 1),
            timeout.Token);

        try
        {
            await childStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(rootSourceFinished);
            MigrationRunSummary activeRun = store.GetDashboardRun("streaming-run");
            for (var attempt = 0; attempt < 100 &&
                 activeRun.Steps.Single(job => job.Id == "Contacts").SucceededBatches == 0; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10));
                activeRun = store.GetDashboardRun("streaming-run");
            }

            Assert.Equal(1, activeRun.Steps.Single(job => job.Id == "Contacts").SucceededBatches);
            Assert.All(activeRun.Steps, job => Assert.False(job.IsComplete));
        }
        finally
        {
            allowRootSourceToFinish.TrySetResult();
        }

        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, result.SucceededBatches);
        Assert.True(rootSourceFinished);
        Assert.All(store.GetDashboardRun("streaming-run").Steps, job => Assert.True(job.IsComplete));
    }

    [Fact]
    public async Task ReadyDependencyBatchRunsBeforeQueuedRootBacklog()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        var executionOrder = new System.Collections.Concurrent.ConcurrentQueue<string>();

        async IAsyncEnumerable<MigrationWorkBatch> ReadAccounts(
            MigrationStepCheckpoint checkpoint,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var sequence = 0; sequence < 4; sequence++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new MigrationWorkBatch($"{{\"sequence\":{sequence}}}");
                await Task.Yield();
            }
        }

        var jobs = new[]
        {
            new MigrationStepDefinition(
                "Accounts",
                inputs: null,
                ReadAccounts,
                (batch, _) =>
                {
                    executionOrder.Enqueue($"Accounts:{batch.Sequence}");
                    return Task.FromResult(new MigrationBatchResult(
                        "{}",
                        Dependencies: batch.Sequence == 0
                            ? new[] { new MigrationDependencyOutput("Contacts", "{\"account\":0}") }
                            : null));
                }),
            new MigrationStepDefinition(
                "Contacts",
                new[] { "Accounts" },
                readSourceBatches: null,
                (_, _) =>
                {
                    executionOrder.Enqueue("Contacts");
                    return Task.FromResult(new MigrationBatchResult("{}"));
                })
        };

        var result = await new DependencyScheduler(store).ExecuteAsync(
            "dependency-priority-run",
            "dependency-priority-test",
            "hash-1",
            jobs,
            CreateOptions(maxDegreeOfParallelism: 1, maxQueuedBatches: 4));

        Assert.Equal(5, result.SucceededBatches);
        Assert.Equal(
            new[] { "Accounts:0", "Contacts", "Accounts:1", "Accounts:2", "Accounts:3" },
            executionOrder.ToArray());
    }

    [Fact]
    public async Task RowLinkedDependencyEventWaitsForCompletionDependencies()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        var executionOrder = new System.Collections.Concurrent.ConcurrentQueue<string>();

        async IAsyncEnumerable<MigrationWorkBatch> ReadRoot(
            MigrationStepCheckpoint checkpoint,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var sequence = 0; sequence < 2; sequence++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new MigrationWorkBatch($"{{\"sequence\":{sequence}}}");
                await Task.Yield();
            }
            await Task.CompletedTask;
        }

        var steps = new[]
        {
            new MigrationStepDefinition(
                "ParentSource",
                inputs: null,
                ReadRoot,
                (_, _) => Task.FromResult(new MigrationBatchResult(
                    "{}",
                    Dependencies: new[]
                    {
                        new MigrationDependencyOutput("ParentTarget", "{}"),
                        new MigrationDependencyOutput("ChildSource", "{}")
                    })),
                type: "source"),
            new MigrationStepDefinition(
                "ParentTarget",
                new[] { "ParentSource" },
                readSourceBatches: null,
                (_, _) =>
                {
                    executionOrder.Enqueue("ParentTarget");
                    return Task.FromResult(new MigrationBatchResult("{}"));
                },
                type: "target"),
            new MigrationStepDefinition(
                "ChildSource",
                new[] { "ParentSource" },
                readSourceBatches: null,
                (_, _) =>
                {
                    executionOrder.Enqueue("ChildSource");
                    return Task.FromResult(new MigrationBatchResult("{}"));
                },
                type: "source",
                completionDependencies: new[] { "ParentTarget" })
        };

        var result = await new DependencyScheduler(store).ExecuteAsync(
            "row-linked-completion-run",
            "row-linked-completion-test",
            "hash-1",
            steps,
            CreateOptions(maxDegreeOfParallelism: 2, maxQueuedBatches: 2));

        Assert.Equal(6, result.SucceededBatches);
        Assert.Equal(
            new[] { "ParentTarget", "ParentTarget", "ChildSource", "ChildSource" },
            executionOrder.ToArray());
    }

    [Fact]
    public async Task SourceBackedJobWaitsForItsPrerequisiteJobToFinish()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        var firstRootBatchCompleted = NewSignal();
        var allowRootSourceToFinish = NewSignal();
        var finalRootBatchCompleted = NewSignal();
        var dependentSourceStarted = NewSignal();
        var independentSourceCompleted = NewSignal();

        async IAsyncEnumerable<MigrationWorkBatch> ReadRoot(
            MigrationStepCheckpoint checkpoint,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Assert.Equal(-1, checkpoint.LastCompletedSequence);
            yield return new MigrationWorkBatch("{\"sequence\":0}");
            await firstRootBatchCompleted.Task.WaitAsync(cancellationToken);
            await allowRootSourceToFinish.Task.WaitAsync(cancellationToken);
            yield return new MigrationWorkBatch("{\"sequence\":1}");
        }

        async IAsyncEnumerable<MigrationWorkBatch> ReadIndependent(
            MigrationStepCheckpoint checkpoint,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new MigrationWorkBatch("{\"independent\":true}");
            independentSourceCompleted.TrySetResult();
            await Task.CompletedTask;
        }

        async IAsyncEnumerable<MigrationWorkBatch> ReadDependent(
            MigrationStepCheckpoint checkpoint,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            dependentSourceStarted.TrySetResult();
            yield return new MigrationWorkBatch("{\"after\":\"Root\"}");
            await Task.CompletedTask;
        }

        var jobs = new[]
        {
            new MigrationStepDefinition(
                "Root",
                inputs: null,
                ReadRoot,
                (batch, _) =>
                {
                    if (batch.Sequence == 0)
                    {
                        firstRootBatchCompleted.TrySetResult();
                    }
                    else
                    {
                        finalRootBatchCompleted.TrySetResult();
                    }
                    return Task.FromResult(new MigrationBatchResult("{}"));
                }),
            new MigrationStepDefinition(
                "Independent",
                inputs: null,
                ReadIndependent,
                (_, _) => Task.FromResult(new MigrationBatchResult("{}"))),
            new MigrationStepDefinition(
                "AfterRoot",
                new[] { "Root" },
                ReadDependent,
                (_, _) => Task.FromResult(new MigrationBatchResult("{}")))
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = new DependencyScheduler(store).ExecuteAsync(
            "job-gate-run",
            "job-gate-test",
            "hash-1",
            jobs,
            CreateOptions(maxDegreeOfParallelism: 2, maxQueuedBatches: 4),
            timeout.Token);

        try
        {
            await independentSourceCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await firstRootBatchCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(dependentSourceStarted.Task.IsCompleted);
        }
        finally
        {
            allowRootSourceToFinish.TrySetResult();
        }

        await finalRootBatchCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await dependentSourceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, result.SucceededBatches);
    }

    [Fact]
    public async Task CompletionDependencyWaitsForTheWholeParentPipeline()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        var firstParentBatchCompleted = NewSignal();
        var allowRootSourceToFinish = NewSignal();
        var finalParentBatchCompleted = NewSignal();
        var dependentSourceStarted = NewSignal();

        async IAsyncEnumerable<MigrationWorkBatch> ReadRoot(
            MigrationStepCheckpoint checkpoint,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new MigrationWorkBatch("{\"sequence\":0}");
            await firstParentBatchCompleted.Task.WaitAsync(cancellationToken);
            await allowRootSourceToFinish.Task.WaitAsync(cancellationToken);
            yield return new MigrationWorkBatch("{\"sequence\":1}");
        }

        async IAsyncEnumerable<MigrationWorkBatch> ReadDependent(
            MigrationStepCheckpoint checkpoint,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            dependentSourceStarted.TrySetResult();
            yield return new MigrationWorkBatch("{\"dependent\":true}");
            await Task.CompletedTask;
        }

        var steps = new[]
        {
            new MigrationStepDefinition(
                "Root",
                inputs: null,
                ReadRoot,
                (batch, _) => Task.FromResult(new MigrationBatchResult(
                    "{}",
                    Dependencies: new[]
                    {
                        new MigrationDependencyOutput(
                            "ParentTarget",
                            batch.PayloadJson ?? throw new InvalidOperationException("Missing batch payload."))
                    }))),
            new MigrationStepDefinition(
                "ParentTarget",
                new[] { "Root" },
                readSourceBatches: null,
                (batch, _) =>
                {
                    if (batch.Sequence == 0)
                    {
                        firstParentBatchCompleted.TrySetResult();
                    }
                    else
                    {
                        finalParentBatchCompleted.TrySetResult();
                    }
                    return Task.FromResult(new MigrationBatchResult("{}"));
                },
                type: "target"),
            new MigrationStepDefinition(
                "DependentSource",
                inputs: null,
                ReadDependent,
                (_, _) => Task.FromResult(new MigrationBatchResult("{}")),
                type: "source",
                completionDependencies: new[] { "ParentTarget" })
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = new DependencyScheduler(store).ExecuteAsync(
            "completion-dependency-run",
            "completion-dependency-test",
            "hash-1",
            steps,
            CreateOptions(maxDegreeOfParallelism: 2, maxQueuedBatches: 2),
            timeout.Token);

        try
        {
            await firstParentBatchCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(dependentSourceStarted.Task.IsCompleted);
        }
        finally
        {
            allowRootSourceToFinish.TrySetResult();
        }

        await finalParentBatchCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await dependentSourceStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(5, result.SucceededBatches);
    }

    [Fact]
    public async Task ChildBatchWaitsForEveryRequiredParentBatch()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        var rightStarted = NewSignal();
        var releaseRight = NewSignal();
        var leftProducedDependency = NewSignal();
        var childStarted = NewSignal();
        const string runId = "multi-parent-run";
        var rightBatchId = MigrationBatchIdentity.Create(runId, "Right", 0);

        var jobs = new[]
        {
            new MigrationStepDefinition(
                "Left",
                inputs: null,
                (_, cancellationToken) => SingleBatch("{\"left\":1}", cancellationToken),
                async (_, cancellationToken) =>
                {
                    await rightStarted.Task.WaitAsync(cancellationToken);
                    leftProducedDependency.TrySetResult();
                    return new MigrationBatchResult(
                        "{}",
                        Dependencies: new[]
                        {
                            new MigrationDependencyOutput(
                                "Joined",
                                "{\"left\":1,\"right\":1}",
                                new[] { rightBatchId })
                        });
                }),
            new MigrationStepDefinition(
                "Right",
                inputs: null,
                (_, cancellationToken) => SingleBatch("{\"right\":1}", cancellationToken),
                async (_, cancellationToken) =>
                {
                    rightStarted.TrySetResult();
                    await releaseRight.Task.WaitAsync(cancellationToken);
                    return new MigrationBatchResult("{}");
                }),
            new MigrationStepDefinition(
                "Joined",
                new[] { "Left", "Right" },
                readSourceBatches: null,
                (_, _) =>
                {
                    childStarted.TrySetResult();
                    return Task.FromResult(new MigrationBatchResult("{}"));
                })
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = new DependencyScheduler(store).ExecuteAsync(
            runId,
            "multi-parent-test",
            "hash-1",
            jobs,
            CreateOptions(maxDegreeOfParallelism: 2, maxQueuedBatches: 3),
            timeout.Token);

        try
        {
            await leftProducedDependency.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForPendingDependencyEventsAsync(store, runId, timeout.Token);
            Assert.False(childStarted.Task.IsCompleted);
        }
        finally
        {
            releaseRight.TrySetResult();
        }

        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, result.SucceededBatches);
        Assert.True(childStarted.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task HandlerFailureIsRetriedAndSuccessfulResultIsCommitted()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        var attempts = 0;
        var jobs = new[]
        {
            new MigrationStepDefinition(
                "Accounts",
                inputs: null,
                (_, cancellationToken) => SingleBatch("{}", cancellationToken),
                (_, _) =>
                {
                    if (Interlocked.Increment(ref attempts) == 1)
                    {
                        return Task.FromException<MigrationBatchResult>(
                            new InvalidOperationException("Transient failure."));
                    }

                    return Task.FromResult(new MigrationBatchResult("{}"));
                })
        };

        var result = await new DependencyScheduler(store).ExecuteAsync(
            "retry-run",
            "retry-test",
            "hash-1",
            jobs,
            CreateOptions(maxDegreeOfParallelism: 1, maxQueuedBatches: 1));

        var batch = store.GetBatch(MigrationBatchIdentity.Create("retry-run", "Accounts", 0));
        Assert.NotNull(batch);
        Assert.Equal(2, attempts);
        Assert.Equal(2, batch.Attempts);
        Assert.Equal(MigrationBatchState.Succeeded, batch.State);
        Assert.Equal(1, result.SucceededBatches);
    }

    [Fact]
    public async Task WorkerCountBoundsConcurrentBatchExecution()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        var activeBatches = 0;
        var maximumActiveBatches = 0;

        async IAsyncEnumerable<MigrationWorkBatch> ReadBatches(
            MigrationStepCheckpoint checkpoint,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Assert.Equal(-1, checkpoint.LastCompletedSequence);
            for (var index = 0; index < 8; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new MigrationWorkBatch($"{{\"id\":{index}}}");
                await Task.Yield();
            }
        }

        var jobs = new[]
        {
            new MigrationStepDefinition(
                "Accounts",
                inputs: null,
                ReadBatches,
                async (_, cancellationToken) =>
                {
                    var active = Interlocked.Increment(ref activeBatches);
                    UpdateMaximum(ref maximumActiveBatches, active);
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
                        return new MigrationBatchResult("{}");
                    }
                    finally
                    {
                        Interlocked.Decrement(ref activeBatches);
                    }
                })
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await new DependencyScheduler(store).ExecuteAsync(
            "parallel-run",
            "parallel-test",
            "hash-1",
            jobs,
            CreateOptions(maxDegreeOfParallelism: 2, maxQueuedBatches: 3),
            timeout.Token);

        Assert.Equal(8, result.SucceededBatches);
        Assert.Equal(2, maximumActiveBatches);
    }

    [Fact]
    public async Task BatchIsPersistentlyFailedAfterMaximumAttempts()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        var attempts = 0;

        async IAsyncEnumerable<MigrationWorkBatch> ReadBatches(
            MigrationStepCheckpoint checkpoint,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Assert.Equal(-1, checkpoint.LastCompletedSequence);
            yield return new MigrationWorkBatch("{}");
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new MigrationWorkBatch("{\"id\":2}");
        }

        var jobs = new[]
        {
            new MigrationStepDefinition(
                "Accounts",
                inputs: null,
                ReadBatches,
                (_, _) =>
                {
                    Interlocked.Increment(ref attempts);
                    return Task.FromException<MigrationBatchResult>(
                        new InvalidOperationException("Permanent failure."));
                })
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DependencyScheduler(store).ExecuteAsync(
                "permanent-failure-run",
                "permanent-failure-test",
                "hash-1",
                jobs,
                CreateOptions(maxDegreeOfParallelism: 1, maxQueuedBatches: 1) with
                {
                    MaxAttempts = 2
                }));

        var batch = store.GetBatch(MigrationBatchIdentity.Create(
            "permanent-failure-run",
            "Accounts",
            0));
        Assert.NotNull(batch);
        Assert.Contains("failed after 2 attempts", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, attempts);
        Assert.Equal(2, batch.Attempts);
        Assert.Equal(MigrationBatchState.CompletedWithErrors, batch.State);
        Assert.Equal(0, store.GetRunProgress("permanent-failure-run").InFlightBatches);
        var nextBatch = store.GetBatch(MigrationBatchIdentity.Create(
            "permanent-failure-run",
            "Accounts",
            1));
        Assert.True(nextBatch is null || nextBatch.State == MigrationBatchState.Pending);
    }

    [Fact]
    public async Task SchedulerPreservesStackTraceForBackgroundFailure()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);

        async IAsyncEnumerable<MigrationWorkBatch> ReadBatches(
            MigrationStepCheckpoint checkpoint,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new MigrationWorkBatch("{}");
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Source read failed.");
        }

        var jobs = new[]
        {
            new MigrationStepDefinition(
                "Accounts",
                inputs: null,
                ReadBatches,
                (_, _) => Task.FromResult(new MigrationBatchResult("{}")))
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DependencyScheduler(store).ExecuteAsync(
                "background-failure-run",
                "background-failure-test",
                "hash-1",
                jobs,
                CreateOptions(maxDegreeOfParallelism: 1, maxQueuedBatches: 1)));

        Assert.Contains(nameof(ReadBatches), exception.StackTrace, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiredBatchIsNotExecutedAgainAfterMaximumAttempts()
    {
        using var database = new TemporaryDatabase();
        const string runId = "exhausted-recovery-run";
        const string runName = "exhausted-recovery-test";
        const string jobHash = "hash-1";
        var store = new SqliteMigrationStore(database.Path);
        var expiredTime = DateTimeOffset.UtcNow.AddHours(-1);
        store.CreateRun(runId, runName, jobHash, expiredTime);
        store.EnqueueBatch(
            runId,
            "Accounts",
            0,
            MigrationBatchIdentity.Create(runId, "Accounts", 0),
            null,
            "{}",
            expiredTime);
        var abandonedLease = store.TryLeaseNextBatch(
            runId,
            "crashed-worker",
            TimeSpan.FromSeconds(1),
            expiredTime);
        Assert.NotNull(abandonedLease);

        var handlerCalls = 0;
        var jobs = new[]
        {
            new MigrationStepDefinition(
                "Accounts",
                inputs: null,
                (_, cancellationToken) => SingleBatch("{}", cancellationToken),
                (_, _) =>
                {
                    Interlocked.Increment(ref handlerCalls);
                    return Task.FromResult(new MigrationBatchResult("{}"));
                })
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DependencyScheduler(store).ExecuteAsync(
                runId,
                runName,
                jobHash,
                jobs,
                CreateOptions(maxDegreeOfParallelism: 1, maxQueuedBatches: 1) with
                {
                    MaxAttempts = 1
                }));

        var batch = store.GetBatch(MigrationBatchIdentity.Create(runId, "Accounts", 0));
        Assert.NotNull(batch);
        Assert.Equal(0, handlerCalls);
        Assert.Equal(2, batch.Attempts);
        Assert.Equal(MigrationBatchState.CompletedWithErrors, batch.State);
    }

    [Fact]
    public async Task RootSourceResumesAfterItsPersistedBatches()
    {
        using var database = new TemporaryDatabase();
        const string runId = "cursor-resume-run";
        const string runName = "cursor-resume-test";
        const string jobHash = "hash-1";
        var store = new SqliteMigrationStore(database.Path);
        store.CreateRun(runId, runName, jobHash);
        store.EnqueueBatch(
            runId,
            "Accounts",
            0,
            MigrationBatchIdentity.Create(runId, "Accounts", 0),
            "{\"next\":1}",
            "{\"id\":0}");
        store.EnqueueBatch(
            runId,
            "Accounts",
            1,
            MigrationBatchIdentity.Create(runId, "Accounts", 1),
            "{\"next\":2}",
            "{\"id\":1}");

        var completedBatch = store.TryLeaseNextBatch(runId, "bootstrap-worker", TimeSpan.FromMinutes(1));
        Assert.NotNull(completedBatch);
        Assert.Equal(0, completedBatch.Sequence);
        store.CompleteBatch(
            completedBatch.BatchId,
            "bootstrap-worker",
            "{}",
            hasErrors: false);

        MigrationStepCheckpoint? sourceCheckpoint = null;
        var executedSequences = new List<long>();
        var executedPayloads = new List<string>();
        async IAsyncEnumerable<MigrationWorkBatch> ReadRemainingBatches(
            MigrationStepCheckpoint checkpoint,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            sourceCheckpoint = checkpoint;
            Assert.Equal(0, checkpoint.LastCompletedSequence);
            Assert.Equal("{\"next\":1}", checkpoint.CursorJson);
            Assert.Equal(1, checkpoint.ResumeSequence);
            Assert.Equal("{\"next\":2}", checkpoint.ResumeCursorJson);
            yield return new MigrationWorkBatch("{\"id\":2}", "{\"next\":3}");
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }

        var jobs = new[]
        {
            new MigrationStepDefinition(
                "Accounts",
                inputs: null,
                ReadRemainingBatches,
                (batch, _) =>
                {
                    executedSequences.Add(batch.Sequence);
                    Assert.NotNull(batch.PayloadJson);
                    executedPayloads.Add(batch.PayloadJson);
                    return Task.FromResult(new MigrationBatchResult("{}"));
                })
        };

        var result = await new DependencyScheduler(store).ExecuteAsync(
            runId,
            runName,
            jobHash,
            jobs,
            CreateOptions(maxDegreeOfParallelism: 1, maxQueuedBatches: 1));

        Assert.NotNull(sourceCheckpoint);
        Assert.Equal(new long[] { 1, 2 }, executedSequences);
        Assert.Equal(new[] { "{\"id\":1}", "{\"id\":2}" }, executedPayloads);
        Assert.Equal(3, result.SucceededBatches);
        var checkpoint = store.GetStepCheckpoint(runId, "Accounts");
        Assert.Equal(2, checkpoint.LastCompletedSequence);
        Assert.Equal("{\"next\":3}", checkpoint.CursorJson);
    }

    [Fact]
    public async Task SchedulerScansPastBlockedDependencyEventsToFindReadyWork()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        const string runId = "paged-dependency-run";
        const int accountBatchCount = 100;
        var joinedBatchCount = 0;
        var releaseRootBatchId = MigrationBatchIdentity.Create(runId, "ReleaseRoot", 0);
        var releaseChildBatchId = DependencyEvent.Create(
            releaseRootBatchId,
            "ReleaseChild",
            "{\"release\":true}").EventId;

        async IAsyncEnumerable<MigrationWorkBatch> ReadAccountBatches(
            MigrationStepCheckpoint checkpoint,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Assert.Equal(-1, checkpoint.LastCompletedSequence);
            for (var index = 0; index < accountBatchCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new MigrationWorkBatch($"{{\"account\":{index}}}");
                await Task.Yield();
            }
        }

        var jobs = new[]
        {
            new MigrationStepDefinition(
                "Accounts",
                inputs: null,
                ReadAccountBatches,
                (batch, _) => Task.FromResult(new MigrationBatchResult(
                    "{}",
                    Dependencies: new[]
                    {
                        new MigrationDependencyOutput(
                            "Joined",
                            $"{{\"account\":{batch.Sequence}}}",
                            new[] { releaseChildBatchId })
                    }))),
            new MigrationStepDefinition(
                "ReleaseRoot",
                inputs: null,
                (_, cancellationToken) => SingleBatch("{}", cancellationToken),
                async (_, cancellationToken) =>
                {
                    while (store.GetRunProgress(runId).SucceededBatches < accountBatchCount)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
                    }

                    return new MigrationBatchResult(
                        "{}",
                        Dependencies: new[]
                        {
                            new MigrationDependencyOutput("ReleaseChild", "{\"release\":true}")
                        });
                }),
            new MigrationStepDefinition(
                "Joined",
                new[] { "Accounts", "ReleaseChild" },
                readSourceBatches: null,
                (_, _) =>
                {
                    Interlocked.Increment(ref joinedBatchCount);
                    return Task.FromResult(new MigrationBatchResult("{}"));
                }),
            new MigrationStepDefinition(
                "ReleaseChild",
                new[] { "ReleaseRoot" },
                readSourceBatches: null,
                (_, _) => Task.FromResult(new MigrationBatchResult("{}")))
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await new DependencyScheduler(store).ExecuteAsync(
            runId,
            "paged-dependency-test",
            "hash-1",
            jobs,
            CreateOptions(maxDegreeOfParallelism: 4, maxQueuedBatches: 8),
            timeout.Token);

        Assert.Equal(accountBatchCount + 2 + accountBatchCount, result.SucceededBatches);
        Assert.Equal(accountBatchCount, joinedBatchCount);
        Assert.Equal(0, store.GetRunProgress(runId).PendingDependencyEvents);
    }

    [Fact]
    public async Task InvalidDependencyOutputIsRecordedAsFailedInsteadOfRemainingInFlight()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        var jobs = new[]
        {
            new MigrationStepDefinition(
                "Accounts",
                inputs: null,
                (_, cancellationToken) => SingleBatch("{}", cancellationToken),
                (_, _) => Task.FromResult(new MigrationBatchResult(
                    "{}",
                    Dependencies: new[]
                    {
                        new MigrationDependencyOutput("MissingJob", "{}")
                    })))
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DependencyScheduler(store).ExecuteAsync(
                "invalid-result-run",
                "invalid-result-test",
                "hash-1",
                jobs,
                CreateOptions(maxDegreeOfParallelism: 1, maxQueuedBatches: 1)));

        var batch = store.GetBatch(MigrationBatchIdentity.Create("invalid-result-run", "Accounts", 0));
        Assert.NotNull(batch);
        Assert.Equal(MigrationBatchState.CompletedWithErrors, batch.State);
        Assert.Equal(1, batch.Attempts);
        Assert.Contains("unknown child step", batch.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, store.GetRunProgress("invalid-result-run").InFlightBatches);
    }

    [Fact]
    public async Task CancelledRunCanResumeItsPendingBatch()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        var firstAttemptStarted = NewSignal();
        var attempts = 0;
        async IAsyncEnumerable<MigrationWorkBatch> ReadAccountBatches(
            MigrationStepCheckpoint checkpoint,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (checkpoint.ResumeSequence >= 0)
            {
                yield break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            yield return new MigrationWorkBatch("{\"id\":1}");
            await Task.Yield();
        }

        var jobs = new[]
        {
            new MigrationStepDefinition(
                "Accounts",
                inputs: null,
                ReadAccountBatches,
                async (_, cancellationToken) =>
                {
                    if (Interlocked.Increment(ref attempts) == 1)
                    {
                        firstAttemptStarted.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }

                    return new MigrationBatchResult("{}");
                })
        };

        using var cancellation = new CancellationTokenSource();
        var firstRun = new DependencyScheduler(store).ExecuteAsync(
            "resume-run",
            "resume-test",
            "hash-1",
            jobs,
            CreateOptions(maxDegreeOfParallelism: 1, maxQueuedBatches: 1),
            cancellation.Token);

        await firstAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstRun);

        var pendingBatch = store.GetBatch(MigrationBatchIdentity.Create("resume-run", "Accounts", 0));
        Assert.NotNull(pendingBatch);
        Assert.Equal(MigrationBatchState.Pending, pendingBatch.State);
        Assert.Equal(1, pendingBatch.Attempts);

        var resumedResult = await new DependencyScheduler(store).ExecuteAsync(
            "resume-run",
            "resume-test",
            "hash-1",
            jobs,
            CreateOptions(maxDegreeOfParallelism: 1, maxQueuedBatches: 1));

        var succeededBatch = store.GetBatch(MigrationBatchIdentity.Create("resume-run", "Accounts", 0));
        Assert.NotNull(succeededBatch);
        Assert.Equal(2, attempts);
        Assert.Equal(MigrationBatchState.Succeeded, succeededBatch.State);
        Assert.Equal(1, resumedResult.SucceededBatches);
    }

    [Fact]
    public async Task LostBatchLeaseDuringRetryDoesNotFailRun()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        var attempts = 0;
        var jobs = new[]
        {
            new MigrationStepDefinition(
                "Accounts",
                inputs: null,
                (_, cancellationToken) => SingleBatch("{}", cancellationToken),
                (batch, _) =>
                {
                    if (Interlocked.Increment(ref attempts) == 1)
                    {
                        store.AbandonBatch(
                            batch.BatchId,
                            Assert.IsType<string>(batch.LeaseOwner),
                            "Lease was reclaimed.");
                        return Task.FromException<MigrationBatchResult>(
                            new InvalidOperationException("Retryable failure."));
                    }

                    return Task.FromResult(new MigrationBatchResult("{}"));
                })
        };

        var result = await new DependencyScheduler(store).ExecuteAsync(
            "lost-lease-retry-run",
            "lost-lease-retry-test",
            "hash-1",
            jobs,
            CreateOptions(maxDegreeOfParallelism: 1, maxQueuedBatches: 1));

        var batch = store.GetBatch(MigrationBatchIdentity.Create("lost-lease-retry-run", "Accounts", 0));
        Assert.NotNull(batch);
        Assert.Equal(2, attempts);
        Assert.Equal(MigrationBatchState.Succeeded, batch.State);
        Assert.Equal(1, result.SucceededBatches);
    }

    [Fact]
    public async Task LostBatchLeaseBeforeCompletionDoesNotFailRun()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        var attempts = 0;
        var jobs = new[]
        {
            new MigrationStepDefinition(
                "Accounts",
                inputs: null,
                (_, cancellationToken) => SingleBatch("{}", cancellationToken),
                (batch, _) =>
                {
                    if (Interlocked.Increment(ref attempts) == 1)
                    {
                        store.AbandonBatch(
                            batch.BatchId,
                            Assert.IsType<string>(batch.LeaseOwner),
                            "Lease was reclaimed.");
                    }

                    return Task.FromResult(new MigrationBatchResult("{}"));
                })
        };

        var result = await new DependencyScheduler(store).ExecuteAsync(
            "lost-lease-complete-run",
            "lost-lease-complete-test",
            "hash-1",
            jobs,
            CreateOptions(maxDegreeOfParallelism: 1, maxQueuedBatches: 1));

        var batch = store.GetBatch(MigrationBatchIdentity.Create("lost-lease-complete-run", "Accounts", 0));
        Assert.NotNull(batch);
        Assert.Equal(2, attempts);
        Assert.Equal(MigrationBatchState.Succeeded, batch.State);
        Assert.Equal(1, result.SucceededBatches);
    }

    [Fact]
    public async Task CancellationDuringRetryDelayReturnsBatchToPending()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        var firstAttemptFailed = NewSignal();
        var jobs = new[]
        {
            new MigrationStepDefinition(
                "Accounts",
                inputs: null,
                (_, cancellationToken) => SingleBatch("{}", cancellationToken),
                (_, _) =>
                {
                    firstAttemptFailed.TrySetResult();
                    return Task.FromException<MigrationBatchResult>(
                        new InvalidOperationException("Retryable failure."));
                })
        };

        using var cancellation = new CancellationTokenSource();
        var runTask = new DependencyScheduler(store).ExecuteAsync(
            "retry-cancel-run",
            "retry-cancel-test",
            "hash-1",
            jobs,
            CreateOptions(maxDegreeOfParallelism: 1, maxQueuedBatches: 1) with
            {
                RetryDelay = TimeSpan.FromSeconds(10)
            },
            cancellation.Token);

        await firstAttemptFailed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);

        var batch = store.GetBatch(MigrationBatchIdentity.Create("retry-cancel-run", "Accounts", 0));
        Assert.NotNull(batch);
        Assert.Equal(1, batch.Attempts);
        Assert.Equal(MigrationBatchState.Pending, batch.State);
    }

    [Fact]
    public async Task DependencyGraphCyclesAreRejectedBeforeCreatingRun()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        var jobs = new[]
        {
            new MigrationStepDefinition(
                "Accounts",
                new[] { "Contacts" },
                readSourceBatches: null,
                (_, _) => Task.FromResult(new MigrationBatchResult("{}"))),
            new MigrationStepDefinition(
                "Contacts",
                new[] { "Accounts" },
                readSourceBatches: null,
                (_, _) => Task.FromResult(new MigrationBatchResult("{}")))
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new DependencyScheduler(store).ExecuteAsync(
                "cycle-run",
                "cycle-test",
                "hash-1",
                jobs));
        Assert.Throws<KeyNotFoundException>(() => store.GetRunProgress("cycle-run"));
    }

    private static DependencySchedulerOptions CreateOptions(
        int maxDegreeOfParallelism,
        int maxQueuedBatches)
    {
        return new DependencySchedulerOptions
        {
            MaxDegreeOfParallelism = maxDegreeOfParallelism,
            MaxQueuedBatches = maxQueuedBatches,
            MaxAttempts = 3,
            BatchLeaseDuration = TimeSpan.FromSeconds(3),
            RunLeaseDuration = TimeSpan.FromSeconds(3),
            PollInterval = TimeSpan.FromMilliseconds(10),
            RetryDelay = TimeSpan.Zero
        };
    }

    private static async IAsyncEnumerable<MigrationWorkBatch> SingleBatch(
        string payloadJson,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new MigrationWorkBatch(payloadJson);
    }

    private static async Task WaitForPendingDependencyEventsAsync(
        SqliteMigrationStore store,
        string runId,
        CancellationToken cancellationToken)
    {
        while (store.GetRunProgress(runId).PendingDependencyEvents == 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    private static TaskCompletionSource NewSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class TestExecutionLease : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        var observed = Volatile.Read(ref maximum);
        while (value > observed)
        {
            var previous = Interlocked.CompareExchange(ref maximum, value, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
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
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
