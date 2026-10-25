using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.Scheduling;

public sealed class DependencyScheduler
{
    private const int MaxSqliteLeaseRetries = 5;
    private readonly SqliteMigrationStore _store;

    public DependencyScheduler(SqliteMigrationStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public async Task<DependencySchedulerResult> ExecuteAsync(
        string runId,
        string runName,
        string stepHash,
        IEnumerable<MigrationStepDefinition> steps,
        DependencySchedulerOptions? options = null,
        CancellationToken cancellationToken = default,
        string? configurationJson = null,
        string? connectionInstancesJson = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runName);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepHash);
        ArgumentNullException.ThrowIfNull(steps);
        cancellationToken.ThrowIfCancellationRequested();

        var stepMap = ValidateSteps(steps);
        var effectiveOptions = options ?? new DependencySchedulerOptions();
        ValidateOptions(effectiveOptions);
        var expectedRecordCounts = new Dictionary<string, long?>(StringComparer.Ordinal);
        foreach (var step in stepMap.Values.OrderBy(step => step.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedRecordCount = step.ResolveExpectedRecordCountAsync is null
                ? step.ExpectedRecordCount
                : await step.ResolveExpectedRecordCountAsync(cancellationToken).ConfigureAwait(false);
            if (expectedRecordCount is < 0)
            {
                throw new InvalidDataException(
                    $"Source step '{step.Id}' provided a negative expected record count.");
            }
            expectedRecordCounts.Add(step.Id, expectedRecordCount);
        }
        var pipelineJson = JsonSerializer.Serialize(stepMap.Values
            .OrderBy(step => step.Id, StringComparer.Ordinal)
            .Select(step => new
            {
                step.Id,
                step.Type,
                step.Inputs,
                step.CompletionDependencies,
                step.DispatchWeight,
                HasSource = step.ReadSourceBatches is not null,
                ExpectedRecordCount = expectedRecordCounts[step.Id]
            }));
        _store.CreateRun(
            runId,
            runName,
            stepHash,
            pipelineJson: pipelineJson,
            debugCaptureEnabled: effectiveOptions.CaptureDebugData,
            debugCaptureRetentionDays: effectiveOptions.DebugCaptureRetentionDays,
            configurationJson: configurationJson ?? "null",
            connectionInstancesJson: connectionInstancesJson ?? "[]");

        var schedulerOwner = $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid():N}";
        if (!_store.TryAcquireRunLease(runId, schedulerOwner, effectiveOptions.RunLeaseDuration))
        {
            throw new InvalidOperationException(
                $"Migration run '{runId}' is already owned by another scheduler.");
        }

        Exception? runFailure = null;
        try
        {
            _store.SetRunStatus(runId, MigrationRunStatus.Running);
            var result = await ExecuteLeasedRunAsync(
                runId,
                schedulerOwner,
                stepMap,
                effectiveOptions,
                cancellationToken).ConfigureAwait(false);
            _store.SetRunStatus(runId, MigrationRunStatus.Completed);
            return result;
        }
        catch (Exception exception)
        {
            runFailure = exception;
            try
            {
                _store.SetRunStatus(
                    runId,
                    cancellationToken.IsCancellationRequested
                        ? MigrationRunStatus.Cancelled
                        : MigrationRunStatus.Failed);
            }
            catch (Exception statusException)
            {
                runFailure = new AggregateException(
                    $"Migration run '{runId}' failed, and its final status could not be recorded.",
                    exception,
                    statusException);
                throw runFailure;
            }

            throw;
        }
        finally
        {
            try
            {
                _store.ReleaseRunLease(runId, schedulerOwner);
            }
            catch (Exception releaseException)
            {
                if (runFailure is not null)
                {
                    throw new AggregateException(
                        $"Migration run '{runId}' failed, and its scheduler lease could not be released.",
                        runFailure,
                        releaseException);
                }

                throw;
            }
        }
    }

    private async Task<DependencySchedulerResult> ExecuteLeasedRunAsync(
        string runId,
        string schedulerOwner,
        IReadOnlyDictionary<string, MigrationStepDefinition> steps,
        DependencySchedulerOptions options,
        CancellationToken cancellationToken)
    {
        var initialProgress = _store.GetRunProgress(runId);
        if (initialProgress.BatchesWithErrors > 0)
        {
            throw new InvalidOperationException(
                $"Run '{runId}' contains {initialProgress.BatchesWithErrors} batches with recorded row errors. " +
                "Resolve or export those failures before resuming the run.");
        }

        var startedUtc = DateTimeOffset.UtcNow;
        var rootSteps = steps.Values.Where(step => step.ReadSourceBatches is not null).ToArray();
        var nonTerminalBatchCounts = _store.GetNonTerminalBatchCountsByStep(runId);
        var initialRootBatchCount = rootSteps.Sum(step =>
            nonTerminalBatchCounts.TryGetValue(step.Id, out var count) ? count : 0);
        var dependencyConsumersByStep = steps.Values
            .Where(step => step.ReadSourceBatches is null)
            .SelectMany(childStep => childStep.Inputs.Select(parentStepId => (parentStepId, childStep.Id)))
            .GroupBy(entry => entry.parentStepId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        var initialReadyDependencyEventsByStep = _store
            .GetPendingDependencyEventCountsWithoutPrerequisitesByStep(runId)
            .Where(entry => steps.TryGetValue(entry.Key, out var childStep) &&
                            childStep.CompletionDependencies.Count == 0)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        var initialReadyDependencyEventCount = initialReadyDependencyEventsByStep.Values.Sum();
        var runtime = new SchedulerRuntime(
            options.MaxQueuedBatches,
            options.MaxQueuedBatchesPerStep,
            initialProgress.NonTerminalBatchCount,
            initialRootBatchCount,
            initialProgress.PendingDependencyEvents,
            initialReadyDependencyEventCount,
            initialReadyDependencyEventsByStep,
            rootSteps.Length,
            rootSteps.Select(step => step.Id),
            steps.Keys,
            steps.ToDictionary(entry => entry.Key, entry => entry.Value.DispatchWeight, StringComparer.Ordinal),
            nonTerminalBatchCounts,
            hasDependentJobs: steps.Values.Any(step =>
                step.Inputs.Count > 0 || step.CompletionDependencies.Count > 0));
        var stepCompletions = steps.Keys.ToDictionary(
            stepId => stepId,
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            StringComparer.Ordinal);

        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(runCancellation.Token);

        var heartbeatTask = GuardAsync(
            token => RenewRunLeaseAsync(
                runId,
                schedulerOwner,
                options.RunLeaseDuration,
                token),
            runtime,
            heartbeatCancellation.Token);
        var eventPumpTask = GuardAsync(
            token => PumpDependencyEventsAsync(runId, steps, stepCompletions, options, runtime, token),
            runtime,
            runCancellation.Token);
        var sourceTasks = rootSteps
            .Select(step => GuardAsync(
                token => FeedSourceJobAsync(
                    runId,
                    step,
                    stepCompletions,
                    options,
                    runtime,
                    token),
                runtime,
                runCancellation.Token))
            .ToArray();
        var completionMonitorTask = GuardAsync(
            token => CompleteDependencyDrivenStepsAsync(
                runId,
                steps,
                stepCompletions,
                options,
                token),
            runtime,
            runCancellation.Token);
        var workerTasks = Enumerable.Range(0, options.MaxDegreeOfParallelism)
            .Select(workerIndex => GuardAsync(
                token => RunWorkerAsync(
                    runId,
                    schedulerOwner,
                    $"worker-{workerIndex:D2}",
                    steps,
                    dependencyConsumersByStep,
                    options,
                    runtime,
                    token),
                runtime,
                runCancellation.Token))
            .ToArray();
        var workTasks = Task.WhenAll(
            sourceTasks
                .Append(eventPumpTask)
                .Concat(workerTasks)
                .Append(completionMonitorTask));

        var completedTask = await Task.WhenAny(workTasks, runtime.FailureTask).ConfigureAwait(false);
        if (completedTask == runtime.FailureTask)
        {
            runCancellation.Cancel();
        }

        await workTasks.ConfigureAwait(false);
        heartbeatCancellation.Cancel();
        await heartbeatTask.ConfigureAwait(false);
        if (runtime.FailureTask.IsCompleted)
        {
            (await runtime.FailureTask.ConfigureAwait(false)).Throw();
        }
        cancellationToken.ThrowIfCancellationRequested();

        var finalProgress = _store.GetRunProgress(runId);
        if (finalProgress.NonTerminalBatchCount != 0 ||
            finalProgress.PendingDependencyEvents != 0 ||
            finalProgress.BatchesWithErrors != 0)
        {
            throw new InvalidOperationException(
                $"Run '{runId}' stopped with {finalProgress.NonTerminalBatchCount} unfinished batches, " +
                $"{finalProgress.PendingDependencyEvents} pending dependency events, and " +
                $"{finalProgress.BatchesWithErrors} failed batches.");
        }

        if (steps.Values.Any(step => string.Equals(step.Type, "join", StringComparison.Ordinal)))
        {
            _store.ClearJoinStaging(runId);
        }
        var dashboard = _store.GetDashboardRun(runId);
        var targets = dashboard.Steps
            .Where(step => string.Equals(step.Type, "target", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var targetRowsExpected = targets.Length > 0 &&
                                 targets.All(step => step.InputFinalized && step.ExpectedRows.HasValue)
            ? targets.Sum(step => step.ExpectedRows!.Value)
            : (long?)null;
        return new DependencySchedulerResult(
            runId,
            finalProgress.SucceededBatches,
            startedUtc,
            DateTimeOffset.UtcNow)
        {
            TargetRowsProcessed = targets.Sum(step => step.InputRowsProcessed),
            TargetRowsSucceeded = targets.Sum(step => step.SucceededRows),
            TargetRowsFailed = targets.Sum(step => step.FailedRows),
            TargetRowsExpected = targetRowsExpected,
            FilteredRows = dashboard.Steps.Sum(step => step.FilteredRows)
        };
    }

    private async Task FeedSourceJobAsync(
        string runId,
        MigrationStepDefinition step,
        IReadOnlyDictionary<string, TaskCompletionSource> stepCompletions,
        DependencySchedulerOptions options,
        SchedulerRuntime runtime,
        CancellationToken cancellationToken)
    {
        try
        {
            _store.StartStepActivity(runId, step.Id);
            _store.MarkSourceFeederStarted(runId, step.Id);
            if (step.WaitForInputCompletion)
            {
                foreach (var dependency in step.Inputs)
                {
                    await stepCompletions[dependency].Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            foreach (var dependency in step.CompletionDependencies)
            {
                await stepCompletions[dependency].Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            _store.EnterStepActivity(runId, step.Id);
            try
            {
                await FeedRootBatchesAsync(runId, step, options, runtime, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _store.ExitStepActivity(runId, step.Id);
            }

            while (_store.GetNonTerminalBatchCountsByStep(runId).TryGetValue(step.Id, out var count) &&
                   count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
            }

            _store.MarkSourceFeederCompleted(runId, step.Id);
            step.SourceKeyCoordinator?.MarkSourceCompleted(runId, step.Id);
            step.StepCompletionCoordinator?.MarkStepCompleted(runId, step.Id);
            _store.CompleteStepActivity(runId, step.Id);
            stepCompletions[step.Id].TrySetResult();
        }
        finally
        {
            runtime.SourceFeederCompleted();
        }
    }

    private async Task CompleteDependencyDrivenStepsAsync(
        string runId,
        IReadOnlyDictionary<string, MigrationStepDefinition> steps,
        IReadOnlyDictionary<string, TaskCompletionSource> stepCompletions,
        DependencySchedulerOptions options,
        CancellationToken cancellationToken)
    {
        var pendingSteps = steps.Values
            .Where(step => step.ReadSourceBatches is null)
            .ToDictionary(step => step.Id, StringComparer.Ordinal);

        while (pendingSteps.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nonTerminalBatches = _store.GetNonTerminalBatchCountsByStep(runId);
            var pendingDependencyEvents = _store.GetPendingDependencyEventCountsByStep(runId);
            var completedStepFound = true;
            while (completedStepFound)
            {
                completedStepFound = false;
                foreach (var step in pendingSteps.Values.ToArray())
                {
                    var dependencies = step.Inputs
                        .Concat(step.CompletionDependencies)
                        .Distinct(StringComparer.Ordinal);
                    if (!dependencies.All(dependency => stepCompletions[dependency].Task.IsCompletedSuccessfully))
                    {
                        continue;
                    }

                    var hasNonTerminalBatches =
                        nonTerminalBatches.TryGetValue(step.Id, out var batchCount) && batchCount > 0;
                    var hasPendingDependencyEvents =
                        pendingDependencyEvents.TryGetValue(step.Id, out var eventCount) && eventCount > 0;
                    if (hasNonTerminalBatches || hasPendingDependencyEvents)
                    {
                        continue;
                    }

                    _store.CompleteStepActivity(runId, step.Id);
                    stepCompletions[step.Id].TrySetResult();
                    step.StepCompletionCoordinator?.MarkStepCompleted(runId, step.Id);
                    pendingSteps.Remove(step.Id);
                    completedStepFound = true;
                }
            }

            if (pendingSteps.Count == 0)
            {
                return;
            }
            await Task.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FeedRootBatchesAsync(
        string runId,
        MigrationStepDefinition step,
        DependencySchedulerOptions options,
        SchedulerRuntime runtime,
        CancellationToken cancellationToken)
    {
        var source = step.ReadSourceBatches
            ?? throw new InvalidOperationException($"Root step '{step.Id}' has no batch source.");
        var checkpoint = _store.GetStepSourceCheckpoint(runId, step.Id);
        var sequence = checked(checkpoint.ResumeSequence + 1);
        var sourceKeyProperty = step.SourceKeyProperty;
        await foreach (var workBatch in source(checkpoint, cancellationToken)
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            ArgumentNullException.ThrowIfNull(workBatch);
            var batchId = MigrationBatchIdentity.Create(runId, step.Id, sequence);
            var existingBatch = _store.GetBatch(batchId);
            var sourceKeys = sourceKeyProperty is null
                ? null
                : ExtractSourceKeys(workBatch.PayloadJson, sourceKeyProperty, step.Id);
            if (existingBatch is null)
            {
                await runtime.ReserveRootBatchSlotAsync(step.Id, cancellationToken).ConfigureAwait(false);
                try
                {
                    _store.EnqueueBatch(
                        runId,
                        step.Id,
                        sequence,
                        batchId,
                        workBatch.CursorAfterJson,
                        workBatch.PayloadJson,
                        parentBatchIds: workBatch.ParentBatchIds,
                        filteredRows: workBatch.FilteredRows,
                        sourceKeys: sourceKeys);
                }
                catch
                {
                    runtime.ReleaseRootBatchSlot(step.Id);
                    throw;
                }
            }
            else
            {
                _store.EnqueueBatch(
                    runId,
                    step.Id,
                    sequence,
                    batchId,
                    workBatch.CursorAfterJson,
                    workBatch.PayloadJson,
                    parentBatchIds: workBatch.ParentBatchIds,
                    filteredRows: workBatch.FilteredRows,
                    sourceKeys: sourceKeys);
            }

            runtime.SignalWork();
            sequence = checked(sequence + 1);
        }
    }

    private static IReadOnlyList<SourceKeyEntry> ExtractSourceKeys(
        string payloadJson,
        string keyProperty,
        string stepId)
    {
        using var document = JsonDocument.Parse(payloadJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("Records", out var records) ||
            records.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"Source batch for step '{stepId}' does not contain a Records array.");
        }

        var sourceKeys = new List<SourceKeyEntry>(records.GetArrayLength());
        var distinctKeys = new HashSet<string>(StringComparer.Ordinal);
        var recordIndex = 0;
        foreach (var wrapper in records.EnumerateArray())
        {
            if (wrapper.ValueKind != JsonValueKind.Object ||
                !wrapper.TryGetProperty("Source", out var source) ||
                source.ValueKind != JsonValueKind.Object ||
                !source.TryGetProperty(keyProperty, out var keyElement))
            {
                throw new InvalidDataException(
                    $"Source batch for step '{stepId}' has a row without key property '{keyProperty}'.");
            }

            var key = keyElement.ValueKind switch
            {
                JsonValueKind.String => keyElement.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => keyElement.ToString(),
                _ => null
            };
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new InvalidDataException(
                    $"Source batch for step '{stepId}' has an invalid key in property '{keyProperty}'.");
            }
            if (!distinctKeys.Add(key))
            {
                throw new InvalidDataException(
                    $"Source batch for step '{stepId}' contains duplicate key '{key}'.");
            }

            sourceKeys.Add(new SourceKeyEntry(key, recordIndex));
            recordIndex++;
        }

        return sourceKeys;
    }

    private async Task RunWorkerAsync(
        string runId,
        string schedulerOwner,
        string workerName,
        IReadOnlyDictionary<string, MigrationStepDefinition> steps,
        IReadOnlyDictionary<string, string[]> dependencyConsumersByStep,
        DependencySchedulerOptions options,
        SchedulerRuntime runtime,
        CancellationToken cancellationToken)
    {
        var workerId = $"{schedulerOwner}-{workerName}";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (runtime.FailureTask.IsCompleted)
            {
                return;
            }
            var candidates = _store.GetPendingBatchStepCandidates(runId);
            if (runtime.ShouldWaitForDependencyMaterialization(candidates))
            {
                await runtime.WaitForWorkAsync(options.PollInterval, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }
            var batchLeased = false;
            foreach (var candidate in runtime.OrderCandidatesFairly(candidates))
            {
                steps.TryGetValue(candidate.StepId, out var step);
                var executionLease = step?.TryAcquireExecutionLease?.Invoke();
                if (step?.TryAcquireExecutionLease is not null && executionLease is null)
                {
                    continue;
                }

                var consumers = dependencyConsumersByStep.TryGetValue(candidate.StepId, out var childStepIds)
                    ? childStepIds
                    : Array.Empty<string>();
                if (!runtime.TryReserveProducerCapacity(consumers))
                {
                    executionLease?.Dispose();
                    continue;
                }

                try
                {
                    var batch = await RetrySqliteLockAsync(
                        () => _store.TryLeaseNextBatch(
                            runId,
                            workerId,
                            options.BatchLeaseDuration,
                            stepId: candidate.StepId),
                        cancellationToken).ConfigureAwait(false);
                    if (batch is null)
                    {
                        continue;
                    }

                    runtime.CandidateDispatched(candidate.StepId);
                    runtime.SignalWork();
                    if (runtime.FailureTask.IsCompleted)
                    {
                        _store.AbandonBatch(
                            batch.BatchId,
                            workerId,
                            "Batch was returned to the queue after another batch failed.");
                        return;
                    }

                    await ExecuteBatchAsync(
                        runId,
                        workerId,
                        batch,
                        steps,
                        options,
                        runtime,
                        executionLease,
                        cancellationToken).ConfigureAwait(false);
                    batchLeased = true;
                    break;
                }
                finally
                {
                    runtime.ReleaseProducerCapacity(consumers);
                    executionLease?.Dispose();
                    runtime.SignalWork();
                }
            }

            if (batchLeased)
            {
                continue;
            }
            if (runtime.IsComplete)
            {
                return;
            }

            await runtime.WaitForWorkAsync(options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task<T> RetrySqliteLockAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        for (var retry = 0; ; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return operation();
            }
            catch (SqliteException exception) when (
                (exception.SqliteErrorCode is 5 or 6) && retry < MaxSqliteLeaseRetries)
            {
                var backoffMilliseconds = Math.Min(5_000, 200 * (1 << retry));
                var jitterMilliseconds = Random.Shared.Next(0, backoffMilliseconds / 2 + 1);
                await Task.Delay(
                    TimeSpan.FromMilliseconds(backoffMilliseconds + jitterMilliseconds),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ExecuteBatchAsync(
        string runId,
        string workerId,
        StoredBatch batch,
        IReadOnlyDictionary<string, MigrationStepDefinition> steps,
        DependencySchedulerOptions options,
        SchedulerRuntime runtime,
        IDisposable? executionLease,
        CancellationToken cancellationToken)
    {
        if (batch.Attempts > options.MaxAttempts)
        {
            var exception = new InvalidOperationException(
                $"Batch '{batch.BatchId}' was recovered after exceeding the maximum of " +
                $"{options.MaxAttempts} attempts.");
            CompleteFailedBatch(
                batch,
                workerId,
                CreateFailureOutcomes(batch, exception),
                exception.Message,
                exception,
                runtime,
                options.CaptureDebugData);
            return;
        }

        if (!steps.TryGetValue(batch.StepId, out var step))
        {
            var exception = new InvalidOperationException(
                $"Stored batch '{batch.BatchId}' references unknown step '{batch.StepId}'.");
            CompleteFailedBatch(
                batch,
                workerId,
                CreateFailureOutcomes(batch, exception),
                exception.Message,
                exception,
                runtime,
                options.CaptureDebugData);
            return;
        }
        if (string.IsNullOrWhiteSpace(batch.PayloadJson))
        {
            var exception = new InvalidOperationException($"Leased batch '{batch.BatchId}' has no durable payload.");
            CompleteFailedBatch(
                batch,
                workerId,
                CreateFailureOutcomes(batch, exception),
                exception.Message,
                exception,
                runtime,
                options.CaptureDebugData);
            return;
        }

        MigrationBatchResult? result;
        try
        {
            if (options.CaptureDebugData)
            {
                _store.CaptureDebugSnapshots(MigrationDebugCapture.CaptureInput(
                    batch,
                    (parentBatchId, recordIndex) =>
                    {
                        var lineageIds = _store.GetBatchOutputLineageIds(batch.RunId, parentBatchId);
                        return recordIndex < lineageIds.Count ? lineageIds[recordIndex] : null;
                    }));
            }

            result = await ExecuteHandlerWithLeaseAsync(
                step,
                batch,
                workerId,
                options.BatchLeaseDuration,
                executionLease,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _store.AbandonBatch(
                batch.BatchId,
                workerId,
                "Batch execution was cancelled before its outcomes were committed.");
            throw;
        }
        catch (Exception exception)
        {
            if (batch.Attempts < options.MaxAttempts)
            {
                var retryDelay = GetRetryDelay(options.RetryDelay, batch.Attempts);
                try
                {
                    if (retryDelay > TimeSpan.Zero)
                    {
                        await WaitBeforeRetryWithLeaseAsync(
                            batch.BatchId,
                            workerId,
                            retryDelay,
                            options.BatchLeaseDuration,
                            cancellationToken).ConfigureAwait(false);
                    }

                    _store.AbandonBatch(batch.BatchId, workerId, exception.ToString());
                    runtime.SignalWork();
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _store.AbandonBatch(
                        batch.BatchId,
                        workerId,
                        "Batch retry delay was cancelled before the next attempt.");
                    throw;
                }
            }

            var outcomesJson = JsonSerializer.Serialize(new
            {
                succeeded = 0,
                failed = new[]
                {
                    new
                    {
                        batchId = batch.BatchId,
                        error = exception.ToString()
                    }
                }
            });
            CompleteFailedBatch(
                batch,
                workerId,
                outcomesJson,
                $"Batch '{batch.BatchId}' failed after {batch.Attempts} attempts: {exception.Message}",
                exception,
                runtime,
                options.CaptureDebugData);
            return;
        }

        if (result is null)
        {
            var exception = new InvalidOperationException(
                $"Step '{step.Id}' returned no result for batch '{batch.BatchId}'.");
            CompleteFailedBatch(
                batch,
                workerId,
                CreateFailureOutcomes(batch, exception),
                exception.Message,
                exception,
                runtime,
                options.CaptureDebugData);
            return;
        }

        try
        {
            ValidateBatchResult(result);
        }
        catch (Exception exception)
        {
            CompleteFailedBatch(
                batch,
                workerId,
                CreateFailureOutcomes(batch, exception),
                $"Step '{step.Id}' returned an invalid result for batch '{batch.BatchId}'.",
                exception,
                runtime,
                options.CaptureDebugData);
            return;
        }

        if (result.HasErrors)
        {
            CaptureDebugResult(batch, result, options.CaptureDebugData);
            var errorMessage = string.IsNullOrWhiteSpace(result.LastError)
                ? result.OutcomesJson
                : result.LastError;
            if (result.Dependencies is { Count: > 0 })
            {
                errorMessage =
                    $"Step '{step.Id}' returned dependent work for batch '{batch.BatchId}' despite row errors. " +
                    errorMessage;
            }

            _store.CompleteBatch(
                batch.BatchId,
                workerId,
                result.OutcomesJson,
                hasErrors: true,
                lastError: errorMessage,
                filteredRows: result.FilteredRows);
            runtime.BatchCompleted(batch.StepId);
            runtime.SignalDependencyWork();
            runtime.Fail(new InvalidOperationException(
                $"Step '{step.Id}' completed batch '{batch.BatchId}' with row errors: {errorMessage}"));
            return;
        }

        IReadOnlyList<DependencyEvent> dependencyEvents;
        try
        {
            dependencyEvents = CreateDependencyEvents(batch, step, result, steps);
        }
        catch (Exception exception)
        {
            CompleteFailedBatch(
                batch,
                workerId,
                CreateFailureOutcomes(batch, exception),
                $"Step '{step.Id}' returned invalid dependency output for batch '{batch.BatchId}'.",
                exception,
                runtime,
                options.CaptureDebugData);
            return;
        }

        var immediatelyReadyEvents = dependencyEvents
            .Where(dependencyEvent =>
                dependencyEvent.RequiredBatchIds.Count == 0 &&
                steps[dependencyEvent.ChildStepId].CompletionDependencies.Count == 0)
            .ToArray();
        runtime.RegisterDependencyEvents(dependencyEvents, immediatelyReadyEvents);
        try
        {
            CaptureDebugResult(batch, result, options.CaptureDebugData);
            _store.CompleteBatch(
                batch.BatchId,
                workerId,
                result.OutcomesJson,
                result.HasErrors,
                dependencyEvents,
                result.LastError,
                filteredRows: result.FilteredRows);
        }
        catch
        {
            runtime.RemoveDependencyEvents(dependencyEvents, immediatelyReadyEvents);
            throw;
        }

        runtime.BatchCompleted(batch.StepId);
        runtime.SignalDependencyWork();
    }

    private void CompleteFailedBatch(
        StoredBatch batch,
        string workerId,
        string outcomesJson,
        string failureMessage,
        Exception exception,
        SchedulerRuntime runtime,
        bool captureDebugData)
    {
        if (captureDebugData)
        {
            var failureJson = JsonSerializer.Serialize(new { error = exception.ToString() });
            _store.CaptureDebugSnapshots(
                [MigrationDebugCapture.CaptureOutcomes(batch, failureJson)]);
        }

        _store.CompleteBatch(
            batch.BatchId,
            workerId,
            outcomesJson,
            hasErrors: true,
            lastError: exception.ToString());
        runtime.BatchCompleted(batch.StepId);
        runtime.SignalDependencyWork();
        runtime.Fail(new InvalidOperationException(failureMessage, exception));
    }

    private void CaptureDebugResult(
        StoredBatch batch,
        MigrationBatchResult result,
        bool captureDebugData)
    {
        if (!captureDebugData)
        {
            return;
        }

        var snapshots = new List<MigrationDebugSnapshot>
        {
            MigrationDebugCapture.CaptureOutcomes(batch, result.OutcomesJson)
        };
        foreach (var stage in result.DebugStages ?? Array.Empty<MigrationDebugStage>())
        {
            var parentBatchLineageIds = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var reference in stage.ParentRecordReferences?
                         .Where(references => references is not null)
                         .SelectMany(references => references)
                         .Where(reference => reference is not null) ??
                     Array.Empty<MigrationDebugParentRecordReference>())
            {
                if (string.IsNullOrWhiteSpace(reference.BatchId) ||
                    string.Equals(reference.BatchId, batch.BatchId, StringComparison.Ordinal) ||
                    parentBatchLineageIds.ContainsKey(reference.BatchId))
                {
                    continue;
                }

                parentBatchLineageIds.Add(
                    reference.BatchId,
                    _store.GetBatchInputLineageIds(batch.RunId, reference.BatchId));
            }

            snapshots.AddRange(MigrationDebugCapture.CaptureStage(
                batch,
                stage,
                parentBatchLineageIds,
                _store.GetBatchInputLineageIds(batch.RunId, batch.BatchId)));
        }

        _store.CaptureDebugSnapshots(snapshots);
    }

    private static string CreateFailureOutcomes(StoredBatch batch, Exception exception)
    {
        return JsonSerializer.Serialize(new
        {
            succeeded = 0,
            failed = new[]
            {
                new
                {
                    batchId = batch.BatchId,
                    error = exception.ToString()
                }
            }
        });
    }

    private static void ValidateBatchResult(MigrationBatchResult result)
    {
        if (string.IsNullOrWhiteSpace(result.OutcomesJson))
        {
            throw new InvalidOperationException("A batch result must contain JSON outcomes.");
        }

        using var _ = JsonDocument.Parse(result.OutcomesJson);
    }

    private IReadOnlyList<DependencyEvent> CreateDependencyEvents(
        StoredBatch parentBatch,
        MigrationStepDefinition parentJob,
        MigrationBatchResult result,
        IReadOnlyDictionary<string, MigrationStepDefinition> steps)
    {
        var eventsById = new Dictionary<string, DependencyEvent>(StringComparer.Ordinal);
        foreach (var output in result.Dependencies ?? Array.Empty<MigrationDependencyOutput>())
        {
            using var payloadDocument = JsonDocument.Parse(output.PayloadJson);
            if (string.IsNullOrWhiteSpace(output.ChildStepId) ||
                !steps.TryGetValue(output.ChildStepId, out var childJob))
            {
                throw new InvalidOperationException(
                    $"Step '{parentJob.Id}' emitted work for an unknown child step '{output.ChildStepId}'.");
            }
            if (childJob.ReadSourceBatches is not null)
            {
                throw new InvalidOperationException(
                    $"Step '{childJob.Id}' has its own source and cannot also receive dependency-event work.");
            }
            if (!childJob.Inputs.Contains(parentJob.Id, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Step '{parentJob.Id}' emitted work for '{childJob.Id}', but the dependency is not declared.");
            }

            var dependencyEvent = DependencyEvent.Create(
                parentBatch.BatchId,
                childJob.Id,
                output.PayloadJson,
                output.RequiredBatchIds);
            if (eventsById.TryGetValue(dependencyEvent.EventId, out var existingEvent) &&
                (existingEvent.ChildStepId != dependencyEvent.ChildStepId ||
                 existingEvent.PayloadJson != dependencyEvent.PayloadJson ||
                 !existingEvent.RequiredBatchIds.SequenceEqual(
                     dependencyEvent.RequiredBatchIds,
                     StringComparer.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Step '{parentJob.Id}' emitted conflicting dependency event '{dependencyEvent.EventId}'.");
            }

            eventsById[dependencyEvent.EventId] = dependencyEvent;
        }

        return eventsById.Values.ToArray();
    }

    private async Task PumpDependencyEventsAsync(
        string runId,
        IReadOnlyDictionary<string, MigrationStepDefinition> steps,
        IReadOnlyDictionary<string, TaskCompletionSource> stepCompletions,
        DependencySchedulerOptions options,
        SchedulerRuntime runtime,
        CancellationToken cancellationToken)
    {
        long? stalledPendingEventCount = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completionDependencyBlocked = false;
            DateTimeOffset? afterCreatedUtc = null;
            string? afterEventId = null;
            while (true)
            {
                var events = _store.GetPendingDependencyEvents(
                    runId,
                    afterCreatedUtc: afterCreatedUtc,
                    afterEventId: afterEventId);
                if (events.Count == 0)
                {
                    break;
                }

                var materializedEventFound = false;
                foreach (var dependencyEvent in events)
                {
                    ValidateDependencyEvent(runId, dependencyEvent, steps);
                    var childStep = steps[dependencyEvent.ChildStepId];
                    if (!childStep.CompletionDependencies.All(
                            dependency => stepCompletions[dependency].Task.IsCompletedSuccessfully))
                    {
                        completionDependencyBlocked = true;
                        continue;
                    }

                    if (!runtime.TryReserveDependencyBatchSlot(childStep.Id))
                    {
                        continue;
                    }
                    DependencyBatchMaterialization? materialization;
                    try
                    {
                        materialization = _store.TryMaterializeDependencyEvent(
                            runId,
                            dependencyEvent.EventId);
                    }
                    catch
                    {
                        runtime.ReleaseBatchSlot(childStep.Id);
                        throw;
                    }

                    if (materialization is null)
                    {
                        runtime.ReleaseBatchSlot(childStep.Id);
                        continue;
                    }

                    materializedEventFound = true;
                    runtime.DependencyEventMaterialized(
                        childStep.Id,
                        dependencyEvent.RequiredBatchIds.Count == 0 &&
                        childStep.CompletionDependencies.Count == 0);
                    if (!materialization.WasCreated)
                    {
                        runtime.ReleaseBatchSlot(childStep.Id);
                    }
                    else
                    {
                        runtime.SignalWork();
                    }
                }

                if (materializedEventFound)
                {
                    afterCreatedUtc = null;
                    afterEventId = null;
                }
                else
                {
                    var lastEvent = events[^1];
                    afterCreatedUtc = lastEvent.CreatedUtc;
                    afterEventId = lastEvent.EventId;
                }
            }

            if (runtime.IsComplete)
            {
                return;
            }

            var progress = _store.GetRunProgress(runId);
            if (runtime.RootFeedersCompleted &&
                progress.NonTerminalBatchCount == 0 &&
                progress.PendingDependencyEvents > 0 &&
                !completionDependencyBlocked)
            {
                if (stalledPendingEventCount != progress.PendingDependencyEvents)
                {
                    stalledPendingEventCount = progress.PendingDependencyEvents;
                    await Task.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new InvalidOperationException(
                    $"Run '{runId}' is blocked: {progress.PendingDependencyEvents} dependency events reference " +
                    "batches that have not completed or do not exist.");
            }

            stalledPendingEventCount = null;
            await runtime.WaitForDependencyWorkAsync(options.PollInterval, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private void ValidateDependencyEvent(
        string runId,
        StoredDependencyEvent dependencyEvent,
        IReadOnlyDictionary<string, MigrationStepDefinition> steps)
    {
        if (!steps.TryGetValue(dependencyEvent.ChildStepId, out var childJob))
        {
            throw new InvalidOperationException(
                $"Dependency event '{dependencyEvent.EventId}' targets unknown step '{dependencyEvent.ChildStepId}'.");
        }
        if (childJob.ReadSourceBatches is not null)
        {
            throw new InvalidOperationException(
                $"Dependency event '{dependencyEvent.EventId}' targets source-driven step '{childJob.Id}'.");
        }

        var parentBatch = _store.GetBatch(dependencyEvent.ParentBatchId)
            ?? throw new InvalidOperationException(
                $"Dependency event '{dependencyEvent.EventId}' references missing parent batch '{dependencyEvent.ParentBatchId}'.");
        if (!string.Equals(parentBatch.RunId, runId, StringComparison.Ordinal) ||
            parentBatch.State != MigrationBatchState.Succeeded)
        {
            throw new InvalidOperationException(
                $"Dependency event '{dependencyEvent.EventId}' has a parent batch that did not succeed in this run.");
        }
        if (!childJob.Inputs.Contains(parentBatch.StepId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Step '{dependencyEvent.ChildStepId}' does not declare parent step '{parentBatch.StepId}'.");
        }

        foreach (var requiredBatchId in dependencyEvent.RequiredBatchIds)
        {
            var prerequisiteBatch = _store.GetBatch(requiredBatchId);
            if (prerequisiteBatch is not null &&
                (!string.Equals(prerequisiteBatch.RunId, runId, StringComparison.Ordinal) ||
                 !childJob.Inputs.Contains(prerequisiteBatch.StepId, StringComparer.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Dependency event '{dependencyEvent.EventId}' contains a prerequisite that is not a " +
                    $"declared parent of step '{childJob.Id}'.");
            }
        }
    }

    private async Task<MigrationBatchResult> ExecuteHandlerWithLeaseAsync(
        MigrationStepDefinition step,
        StoredBatch batch,
        string workerId,
        TimeSpan leaseDuration,
        IDisposable? executionLease,
        CancellationToken cancellationToken)
    {
        using var leaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = RenewBatchLeaseAsync(
            batch.BatchId,
            workerId,
            leaseDuration,
            leaseCancellation.Token);
        try
        {
            var handlerTask = step.ExecuteBatchWithExecutionLeaseAsync is null
                ? step.ExecuteBatchAsync(batch, leaseCancellation.Token)
                : step.ExecuteBatchWithExecutionLeaseAsync(batch, executionLease, leaseCancellation.Token);
            var completedTask = await Task.WhenAny(handlerTask, heartbeatTask).ConfigureAwait(false);
            if (completedTask == heartbeatTask)
            {
                await heartbeatTask.ConfigureAwait(false);
                throw new InvalidOperationException($"Batch lease renewal for '{batch.BatchId}' stopped unexpectedly.");
            }

            return await handlerTask.ConfigureAwait(false);
        }
        finally
        {
            leaseCancellation.Cancel();
            try
            {
                await heartbeatTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (leaseCancellation.IsCancellationRequested)
            {
            }
        }
    }

    private async Task RenewBatchLeaseAsync(
        string batchId,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var interval = GetHeartbeatInterval(leaseDuration);
        while (true)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            if (!_store.RenewBatchLease(batchId, workerId, leaseDuration))
            {
                throw new InvalidOperationException(
                    $"Worker '{workerId}' lost the lease for batch '{batchId}'.");
            }
        }
    }

    private async Task RenewRunLeaseAsync(
        string runId,
        string schedulerOwner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var interval = GetHeartbeatInterval(leaseDuration);
        while (true)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            if (!_store.TryAcquireRunLease(runId, schedulerOwner, leaseDuration))
            {
                throw new InvalidOperationException(
                    $"Scheduler '{schedulerOwner}' lost the lease for run '{runId}'.");
            }
        }
    }

    private async Task WaitBeforeRetryWithLeaseAsync(
        string batchId,
        string workerId,
        TimeSpan retryDelay,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        using var leaseCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = RenewBatchLeaseAsync(
            batchId,
            workerId,
            leaseDuration,
            leaseCancellation.Token);
        try
        {
            await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            leaseCancellation.Cancel();
            try
            {
                await heartbeatTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (leaseCancellation.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task GuardAsync(
        Func<CancellationToken, Task> action,
        SchedulerRuntime runtime,
        CancellationToken cancellationToken)
    {
        try
        {
            await action(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            runtime.Fail(exception);
        }
    }

    private static TimeSpan GetHeartbeatInterval(TimeSpan leaseDuration)
    {
        return TimeSpan.FromTicks(Math.Max(1, leaseDuration.Ticks / 3));
    }

    private static TimeSpan GetRetryDelay(TimeSpan baseDelay, int attempts)
    {
        if (baseDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var multiplier = Math.Pow(2, Math.Max(0, attempts - 1));
        var delayMilliseconds = Math.Min(TimeSpan.FromMinutes(1).TotalMilliseconds, baseDelay.TotalMilliseconds * multiplier);
        return TimeSpan.FromMilliseconds(delayMilliseconds);
    }

    private static IReadOnlyDictionary<string, MigrationStepDefinition> ValidateSteps(
        IEnumerable<MigrationStepDefinition> steps)
    {
        var stepArray = steps.ToArray();
        if (stepArray.Length == 0)
        {
            throw new ArgumentException("At least one migration step is required.", nameof(steps));
        }

        var stepMap = new Dictionary<string, MigrationStepDefinition>(StringComparer.Ordinal);
        foreach (var step in stepArray)
        {
            ArgumentNullException.ThrowIfNull(step);
            if (!double.IsFinite(step.DispatchWeight) || step.DispatchWeight <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(steps),
                    $"Dispatch weight for step '{step.Id}' must be finite and greater than zero.");
            }
            if (!stepMap.TryAdd(step.Id, step))
            {
                throw new ArgumentException($"Step name '{step.Id}' is not unique.", nameof(steps));
            }
        }

        foreach (var step in stepArray)
        {
            foreach (var dependency in step.Inputs)
            {
                if (string.Equals(step.Id, dependency, StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Step '{step.Id}' cannot depend on itself.", nameof(steps));
                }
                if (!stepMap.ContainsKey(dependency))
                {
                    throw new ArgumentException(
                        $"Step '{step.Id}' depends on unknown step '{dependency}'.",
                        nameof(steps));
                }
                if (step.ReadSourceBatches is not null &&
                    stepMap[dependency].ReadSourceBatches is null &&
                    !string.Equals(step.Type, "join", StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Source-driven step '{step.Id}' can only wait for source-driven step dependencies; " +
                        $"'{dependency}' receives dependency-event work.",
                        nameof(steps));
                }
            }
            foreach (var dependency in step.CompletionDependencies)
            {
                if (string.Equals(step.Id, dependency, StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Step '{step.Id}' cannot depend on itself.", nameof(steps));
                }
                if (!stepMap.ContainsKey(dependency))
                {
                    throw new ArgumentException(
                        $"Step '{step.Id}' depends on unknown step '{dependency}'.",
                        nameof(steps));
                }
            }
        }

        if (!stepArray.Any(step => step.Inputs.Count == 0 && step.ReadSourceBatches is not null))
        {
            throw new ArgumentException("At least one root step must provide a batch source.", nameof(steps));
        }

        var visitStates = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var step in stepArray)
        {
            Visit(step);
        }

        return stepMap;

        void Visit(MigrationStepDefinition step)
        {
            if (visitStates.TryGetValue(step.Id, out var state))
            {
                if (state == 1)
                {
                    throw new ArgumentException(
                        $"Migration step dependencies contain a cycle at '{step.Id}'.",
                        nameof(steps));
                }
                if (state == 2)
                {
                    return;
                }
            }

            visitStates[step.Id] = 1;
            foreach (var dependency in step.Inputs)
            {
                Visit(stepMap[dependency]);
            }
            foreach (var dependency in step.CompletionDependencies)
            {
                Visit(stepMap[dependency]);
            }
            visitStates[step.Id] = 2;
        }
    }

    private static void ValidateOptions(DependencySchedulerOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxDegreeOfParallelism);
        if (options.MaxDegreeOfParallelism > 32)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.MaxDegreeOfParallelism),
                "Maximum degree of parallelism cannot exceed 32.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxQueuedBatches);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxQueuedBatchesPerStep);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxAttempts);
        if (options.DebugCaptureRetentionDays is < 1 or > 3650)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.DebugCaptureRetentionDays),
                "Debug capture retention must be between 1 and 3650 days.");
        }
        if (options.BatchLeaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.BatchLeaseDuration));
        }
        if (options.RunLeaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.RunLeaseDuration));
        }
        if (options.PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.PollInterval));
        }
        if (options.RetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.RetryDelay));
        }
    }

    private sealed class SchedulerRuntime
    {
        private readonly object _stateLock = new();
        private readonly object _workSignalLock = new();
        private readonly object _dependencySignalLock = new();
        private readonly int _maxQueuedBatches;
        private readonly int _maxQueuedBatchesPerStep;
        private readonly SemaphoreSlim _workSignal = new(0, 1);
        private readonly SemaphoreSlim _dependencySignal = new(0, 1);
        private readonly TaskCompletionSource<ExceptionDispatchInfo> _failure =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly HashSet<string> _rootJobNames;
        private readonly string[] _stepOrder;
        private readonly IReadOnlyDictionary<string, double> _dispatchWeights;
        private readonly Dictionary<string, long> _outstandingByStep;
        private readonly Dictionary<string, long> _producerReservationsByStep = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _readyPendingEventsByStep;
        private readonly Dictionary<string, long> _dispatchedByStep = new(StringComparer.Ordinal);
        private int _nextCandidateIndex;
        private TaskCompletionSource _stateChanged = NewSignal();
        private long _outstandingBatches;
        private long _rootOutstandingBatches;
        private long _pendingEvents;
        private long _readyPendingEvents;
        private int _rootFeedersRemaining;
        private readonly int _maxRootQueuedBatches;

        public SchedulerRuntime(
            int maxQueuedBatches,
            int maxQueuedBatchesPerStep,
            long outstandingBatches,
            long rootOutstandingBatches,
            long pendingEvents,
            long readyPendingEvents,
            IReadOnlyDictionary<string, long> readyPendingEventsByStep,
            int rootFeedersRemaining,
            IEnumerable<string> rootJobNames,
            IEnumerable<string> stepNames,
            IReadOnlyDictionary<string, double> dispatchWeights,
            IReadOnlyDictionary<string, long> outstandingByStep,
            bool hasDependentJobs)
        {
            _maxQueuedBatches = maxQueuedBatches;
            _maxQueuedBatchesPerStep = Math.Min(maxQueuedBatchesPerStep, maxQueuedBatches);
            _outstandingBatches = outstandingBatches;
            _rootOutstandingBatches = rootOutstandingBatches;
            _pendingEvents = pendingEvents;
            _readyPendingEvents = readyPendingEvents;
            _readyPendingEventsByStep = readyPendingEventsByStep.ToDictionary(
                entry => entry.Key,
                entry => entry.Value,
                StringComparer.Ordinal);
            _rootFeedersRemaining = rootFeedersRemaining;
            _rootJobNames = new HashSet<string>(rootJobNames, StringComparer.Ordinal);
            _stepOrder = stepNames.OrderBy(stepName => stepName, StringComparer.Ordinal).ToArray();
            _dispatchWeights = dispatchWeights;
            _outstandingByStep = outstandingByStep.ToDictionary(
                entry => entry.Key,
                entry => entry.Value,
                StringComparer.Ordinal);
            var minimumRootQueue = _rootJobNames.Count;
            _maxRootQueuedBatches = hasDependentJobs && maxQueuedBatches > 1
                ? Math.Min(maxQueuedBatches, Math.Max(minimumRootQueue, maxQueuedBatches / 2))
                : maxQueuedBatches;
        }

        public Task<ExceptionDispatchInfo> FailureTask => _failure.Task;

        public bool IsComplete
        {
            get
            {
                lock (_stateLock)
                {
                    return _rootFeedersRemaining == 0 &&
                           _outstandingBatches == 0 &&
                           _pendingEvents == 0;
                }
            }
        }

        public bool RootFeedersCompleted
        {
            get
            {
                lock (_stateLock)
                {
                    return _rootFeedersRemaining == 0;
                }
            }
        }

        public IReadOnlyList<PendingBatchStepCandidate> OrderCandidatesFairly(
            IReadOnlyList<PendingBatchStepCandidate> candidates)
        {
            lock (_stateLock)
            {
                if (candidates.Count < 2 || _stepOrder.Length == 0)
                {
                    return candidates;
                }

                var ordered = PipelineDispatchOrderer.Order(
                    candidates,
                    _stepOrder,
                    _dispatchWeights,
                    _dispatchedByStep,
                    _nextCandidateIndex);
                _nextCandidateIndex = (_nextCandidateIndex + 1) % _stepOrder.Length;
                return ordered;
            }
        }

        public void CandidateDispatched(string stepId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
            lock (_stateLock)
            {
                _dispatchedByStep[stepId] = checked(_dispatchedByStep.GetValueOrDefault(stepId) + 1);
            }
        }

        public bool ShouldWaitForDependencyMaterialization(
            IReadOnlyList<PendingBatchStepCandidate> candidates)
        {
            lock (_stateLock)
            {
                return _outstandingBatches < _maxQueuedBatches &&
                       !candidates.Any(candidate => candidate.Priority == 0) &&
                       _readyPendingEventsByStep.Any(entry =>
                           GetStepQueueCount(entry.Key) < _maxQueuedBatchesPerStep);
            }
        }

        public bool TryReserveProducerCapacity(IReadOnlyList<string> childStepIds)
        {
            lock (_stateLock)
            {
                var distinctChildStepIds = childStepIds.Distinct(StringComparer.Ordinal).ToArray();
                if (distinctChildStepIds.Any(stepId =>
                        GetStepQueueCount(stepId) >= _maxQueuedBatchesPerStep))
                {
                    return false;
                }

                foreach (var stepId in distinctChildStepIds)
                {
                    _producerReservationsByStep[stepId] =
                        _producerReservationsByStep.GetValueOrDefault(stepId) + 1;
                }

                return true;
            }
        }

        public void ReleaseProducerCapacity(IReadOnlyList<string> childStepIds)
        {
            lock (_stateLock)
            {
                foreach (var stepId in childStepIds.Distinct(StringComparer.Ordinal))
                {
                    var reservations = _producerReservationsByStep.GetValueOrDefault(stepId);
                    if (reservations <= 0)
                    {
                        throw new InvalidOperationException(
                            $"The scheduler producer-capacity counter underflowed for step '{stepId}'.");
                    }

                    if (reservations == 1)
                    {
                        _producerReservationsByStep.Remove(stepId);
                    }
                    else
                    {
                        _producerReservationsByStep[stepId] = reservations - 1;
                    }
                }

                PulseStateChanged();
            }

            SignalWork();
            SignalDependencyWork();
        }

        public bool TryReserveDependencyBatchSlot(string stepId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
            lock (_stateLock)
            {
                if (_outstandingBatches >= _maxQueuedBatches ||
                    GetStepQueueCount(stepId) >= _maxQueuedBatchesPerStep)
                {
                    return false;
                }

                _outstandingBatches++;
                Increment(_outstandingByStep, stepId);
                PulseStateChanged();
                return true;
            }
        }

        public async Task ReserveRootBatchSlotAsync(string stepId, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
            while (true)
            {
                Task waitTask;
                lock (_stateLock)
                {
                    if (_outstandingBatches < _maxQueuedBatches &&
                        _rootOutstandingBatches < _maxRootQueuedBatches &&
                        GetStepQueueCount(stepId) < _maxQueuedBatchesPerStep)
                    {
                        _outstandingBatches++;
                        _rootOutstandingBatches++;
                        Increment(_outstandingByStep, stepId);
                        PulseStateChanged();
                        return;
                    }

                    waitTask = _stateChanged.Task;
                }

                await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public void ReleaseRootBatchSlot(string stepId)
        {
            lock (_stateLock)
            {
                if (_outstandingBatches <= 0 || _rootOutstandingBatches <= 0)
                {
                    throw new InvalidOperationException("The scheduler root-batch capacity counter underflowed.");
                }

                _outstandingBatches--;
                _rootOutstandingBatches--;
                Decrement(_outstandingByStep, stepId, "outstanding-batch");
                PulseStateChanged();
            }
        }

        public void ReleaseBatchSlot(string stepId)
        {
            lock (_stateLock)
            {
                if (_outstandingBatches <= 0)
                {
                    throw new InvalidOperationException("The scheduler batch capacity counter underflowed.");
                }

                _outstandingBatches--;
                Decrement(_outstandingByStep, stepId, "outstanding-batch");
                PulseStateChanged();
            }
        }

        public void RegisterDependencyEvents(
            IReadOnlyList<DependencyEvent> events,
            IReadOnlyList<DependencyEvent> readyEvents)
        {
            if (events.Count == 0)
            {
                return;
            }
            if (readyEvents.Count > events.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(readyEvents));
            }

            lock (_stateLock)
            {
                _pendingEvents = checked(_pendingEvents + events.Count);
                _readyPendingEvents = checked(_readyPendingEvents + readyEvents.Count);
                foreach (var group in readyEvents.GroupBy(
                             dependencyEvent => dependencyEvent.ChildStepId,
                             StringComparer.Ordinal))
                {
                    Increment(_readyPendingEventsByStep, group.Key, group.LongCount());
                }
                PulseStateChanged();
            }
        }

        public void RemoveDependencyEvents(
            IReadOnlyList<DependencyEvent> events,
            IReadOnlyList<DependencyEvent> readyEvents)
        {
            if (events.Count == 0)
            {
                return;
            }

            lock (_stateLock)
            {
                if (_pendingEvents < events.Count || _readyPendingEvents < readyEvents.Count)
                {
                    throw new InvalidOperationException("The scheduler dependency-event counter underflowed.");
                }

                _pendingEvents -= events.Count;
                _readyPendingEvents -= readyEvents.Count;
                foreach (var group in readyEvents.GroupBy(
                             dependencyEvent => dependencyEvent.ChildStepId,
                             StringComparer.Ordinal))
                {
                    Decrement(_readyPendingEventsByStep, group.Key, "ready-dependency-event", group.LongCount());
                }
                PulseStateChanged();
            }
        }

        public void DependencyEventMaterialized(string childStepId, bool immediatelyReady)
        {
            lock (_stateLock)
            {
                if (_pendingEvents <= 0 || (immediatelyReady && _readyPendingEvents <= 0))
                {
                    throw new InvalidOperationException("The scheduler dependency-event counter underflowed.");
                }

                _pendingEvents--;
                if (immediatelyReady)
                {
                    _readyPendingEvents--;
                    Decrement(_readyPendingEventsByStep, childStepId, "ready-dependency-event");
                }
                PulseStateChanged();
            }
        }

        public void BatchCompleted(string stepName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
            lock (_stateLock)
            {
                if (_outstandingBatches <= 0)
                {
                    throw new InvalidOperationException("The scheduler batch capacity counter underflowed.");
                }
                Decrement(_outstandingByStep, stepName, "outstanding-batch");
                if (_rootJobNames.Contains(stepName))
                {
                    if (_rootOutstandingBatches <= 0)
                    {
                        throw new InvalidOperationException("The scheduler root-batch capacity counter underflowed.");
                    }
                    _rootOutstandingBatches--;
                }

                _outstandingBatches--;
                PulseStateChanged();
            }
        }

        public void SourceFeederCompleted()
        {
            lock (_stateLock)
            {
                if (_rootFeedersRemaining <= 0)
                {
                    throw new InvalidOperationException("The scheduler root-feeder counter underflowed.");
                }

                _rootFeedersRemaining--;
                PulseStateChanged();
            }

            SignalWork();
            SignalDependencyWork();
        }

        public async Task WaitForWorkAsync(TimeSpan pollInterval, CancellationToken cancellationToken)
        {
            await _workSignal.WaitAsync(pollInterval, cancellationToken).ConfigureAwait(false);
        }

        public async Task WaitForDependencyWorkAsync(
            TimeSpan pollInterval,
            CancellationToken cancellationToken)
        {
            await _dependencySignal.WaitAsync(pollInterval, cancellationToken).ConfigureAwait(false);
        }

        public void SignalWork()
        {
            Signal(_workSignal, _workSignalLock);
        }

        public void SignalDependencyWork()
        {
            Signal(_dependencySignal, _dependencySignalLock);
            SignalWork();
        }

        public void Fail(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            if (_failure.TrySetResult(ExceptionDispatchInfo.Capture(exception)))
            {
                SignalDependencyWork();
            }
        }

        private long GetStepQueueCount(string stepId) =>
            _outstandingByStep.GetValueOrDefault(stepId) +
            _producerReservationsByStep.GetValueOrDefault(stepId);

        private static void Increment(
            Dictionary<string, long> counts,
            string stepId,
            long amount = 1)
        {
            counts[stepId] = checked(counts.GetValueOrDefault(stepId) + amount);
        }

        private static void Decrement(
            Dictionary<string, long> counts,
            string stepId,
            string counterName,
            long amount = 1)
        {
            var count = counts.GetValueOrDefault(stepId);
            if (count < amount)
            {
                throw new InvalidOperationException(
                    $"The scheduler {counterName} counter underflowed for step '{stepId}'.");
            }

            if (count == amount)
            {
                counts.Remove(stepId);
            }
            else
            {
                counts[stepId] = count - amount;
            }
        }

        private void PulseStateChanged()
        {
            var previous = _stateChanged;
            _stateChanged = NewSignal();
            previous.TrySetResult();
        }

        private static TaskCompletionSource NewSignal()
        {
            return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static void Signal(SemaphoreSlim signal, object signalLock)
        {
            lock (signalLock)
            {
                if (signal.CurrentCount == 0)
                {
                    signal.Release();
                }
            }
        }
    }
}
