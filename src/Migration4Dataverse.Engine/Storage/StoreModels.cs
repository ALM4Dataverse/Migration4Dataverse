using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Migration4Dataverse.Engine.Storage;

public enum MigrationBatchState
{
    Pending,
    InFlight,
    Succeeded,
    CompletedWithErrors
}

public enum MigrationRunStatus
{
    Running,
    Completed,
    CompletedWithErrors,
    Failed,
    Cancelled
}

public sealed record StoredBatch(
    string BatchId,
    string RunId,
    string StepId,
    long Sequence,
    string? CursorAfterJson,
    string? PayloadJson,
    MigrationBatchState State,
    int Attempts,
    string? LeaseOwner,
    DateTimeOffset? LeaseExpiresUtc,
    string? OutcomesJson,
    string? LastError,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    public IReadOnlyList<string> ParentBatchIds { get; init; } = Array.Empty<string>();

    public bool FailureAccepted { get; init; }
}

public sealed record PendingBatchStepCandidate(
    string StepId,
    int Priority,
    long PendingBatchCount = 1);

public sealed record SourceKeyBatchReference(
    string SourceKey,
    string BatchId,
    int RecordIndex);

public sealed record SourceKeyEntry(
    string SourceKey,
    int RecordIndex);

public sealed record MigrationStepCheckpoint(
    string RunId,
    string StepId,
    long LastCompletedSequence,
    string? CursorJson,
    DateTimeOffset? UpdatedUtc)
{
    public long? LastEnqueuedSequence { get; init; }

    public string? LastEnqueuedCursorJson { get; init; }

    public long ResumeSequence => LastEnqueuedSequence ?? LastCompletedSequence;

    public string? ResumeCursorJson => LastEnqueuedSequence.HasValue
        ? LastEnqueuedCursorJson
        : CursorJson;
}

public sealed record DependencyEvent(
    string EventId,
    string ChildStepId,
    string PayloadJson)
{
    public IReadOnlyList<string> RequiredBatchIds { get; init; } = Array.Empty<string>();

    public static DependencyEvent Create(
        string parentBatchId,
        string childStepId,
        string payloadJson,
        IEnumerable<string>? requiredBatchIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentBatchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(childStepId);
        ArgumentNullException.ThrowIfNull(payloadJson);

        var required = (requiredBatchIds ?? Array.Empty<string>())
            .Select(batchId =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
                return batchId;
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(batchId => batchId, StringComparer.Ordinal)
            .ToArray();
        var content = required.Length == 0
            ? $"{parentBatchId}\0{childStepId}\0{payloadJson}"
            : $"{parentBatchId}\0{childStepId}\0{payloadJson}\0{JsonSerializer.Serialize(required)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return new DependencyEvent($"dep-{Convert.ToHexString(hash).ToLowerInvariant()}", childStepId, payloadJson)
        {
            RequiredBatchIds = required
        };
    }
}

public sealed record StoredDependencyEvent(
    string EventId,
    string RunId,
    string ParentBatchId,
    string ChildStepId,
    string PayloadJson,
    IReadOnlyList<string> RequiredBatchIds,
    DateTimeOffset CreatedUtc);

public sealed record DependencyBatchMaterialization(StoredBatch Batch, bool WasCreated);

public sealed record MigrationRunProgress(
    long PendingBatches,
    long InFlightBatches,
    long SucceededBatches,
    long BatchesWithErrors,
    long PendingDependencyEvents,
    long CompletedDependencyEvents)
{
    public long NonTerminalBatchCount => PendingBatches + InFlightBatches;
}

public sealed record MigrationStepRowCounts(long SucceededRows, long FailedRows);

public sealed record MigrationRowError(int RecordIndex, string Error);

public sealed record MigrationBatchCompletion(
    MigrationStepCheckpoint Checkpoint,
    bool ErrorRateWithinThreshold,
    double? ErrorRatePercent);

public sealed record MigrationRunFailure(
    string Phase,
    string? StepId,
    string Message,
    string ExceptionType,
    string Details,
    DateTimeOffset RecordedUtc)
{
    public int Attempts { get; init; } = 1;
}

public sealed record MigrationBatchRetryHistory(
    long RetryId,
    string BatchId,
    string StepId,
    int PreviousAttempts,
    string? PreviousLastError,
    string? PreviousOutcomesJson,
    DateTimeOffset QueuedUtc);

public sealed record MigrationSchedulerLimits(
    int MaxDegreeOfParallelism,
    int MaxQueuedBatches,
    int MaxQueuedBatchesPerStep)
{
    public int MaxAttempts { get; init; } = 5;

    public int SourceReadMaxAttempts { get; init; } = 3;

    public int SourceReadRetryDelaySeconds { get; init; } = 2;

    public double MaxErrorRatePercent { get; init; }
}

public sealed record DataverseTargetTuningProfile(
    string StepId,
    int? BatchSize,
    int? MaxConcurrency,
    DateTimeOffset UpdatedUtc,
    int AlgorithmVersion = 1,
    string? ConnectionId = null);

public sealed record MigrationRunSummary(
    string RunId,
    string RunName,
    string Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    bool DebugCaptureEnabled,
    int DebugCaptureRetentionDays,
    long PendingBatches,
    long InFlightBatches,
    long SucceededBatches,
    long BatchesWithErrors,
    long SucceededRows,
    long FailedRows,
    IReadOnlyList<MigrationDashboardStep> Steps,
    JsonElement? Configuration = null,
    JsonElement? ConnectionInstances = null,
    DateTimeOffset? SchedulerLeaseExpiresUtc = null)
{
    public MigrationRunFailure? Failure { get; init; }
}

public sealed record MigrationDashboardStep(
    string Id,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> CompletionDependencies,
    bool IsSource,
    string Type,
    bool IsComplete,
    long PendingBatches,
    long InFlightBatches,
    long PendingDependencyEvents,
    long SucceededBatches,
    long BatchesWithErrors,
    long SucceededRows,
    long FailedRows,
    DateTimeOffset? FirstBatchUtc,
    DateTimeOffset? LastBatchUtc,
    double? RowsPerSecond,
    long IdleMilliseconds)
{
    public long ActiveMilliseconds { get; init; }

    public long InputRowsProcessed { get; init; }

    public long InputRowsPending { get; init; }

    public long InputRowsInFlight { get; init; }

    public long FilteredRows { get; init; }

    public long? ExpectedRows { get; init; }

    public bool InputFinalized { get; init; }

    public long BatchesWithUnknownInputRows { get; init; }

    public long UnacceptedErrorBatches { get; init; }

    public bool FailureContinuationOverride { get; init; }

    public bool FailureContinuationAllowed { get; init; }

    public JsonElement? SpecialistInfo { get; init; }

    public IReadOnlyList<MigrationDashboardRateSample> RowsPerSecondSamples { get; init; } =
        Array.Empty<MigrationDashboardRateSample>();

    public IReadOnlyList<MigrationDashboardActivityPeriod> ActivityPeriods { get; init; } =
        Array.Empty<MigrationDashboardActivityPeriod>();
}

public sealed record MigrationDashboardRateSample(
    DateTimeOffset PeriodStartedUtc,
    DateTimeOffset PeriodEndedUtc,
    double RowsPerSecond);

public sealed record MigrationDashboardActivityPeriod(
    string State,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc);

public sealed record MigrationDashboardBatch(
    string BatchId,
    string RunId,
    string StepId,
    long Sequence,
    string State,
    int Attempts,
    string? LastError,
    string? OutcomesJson,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    long SucceededRows,
    long FailedRows)
{
    public IReadOnlyList<MigrationDashboardRowError> RowErrors { get; init; } =
        Array.Empty<MigrationDashboardRowError>();
}

public sealed record MigrationDashboardRowError(
    int RecordIndex,
    string Error,
    string PayloadJson,
    bool IsResolved,
    int RetryCount,
    string? RetryState,
    bool CanRetry);

public sealed record MigrationDebugSnapshot(
    string RunId,
    string BatchId,
    string StepId,
    string StageName,
    int RecordIndex,
    int Attempt,
    string LineageId,
    IReadOnlyList<string> ParentLineageIds,
    string PayloadJson,
    DateTimeOffset CapturedUtc);

public sealed record MigrationDebugSnapshotPage(
    IReadOnlyList<MigrationDebugSnapshot> Items,
    long TotalCount,
    bool HasMore,
    IReadOnlyList<MigrationDebugSnapshotStageCount> StageCounts,
    IReadOnlyList<string> PayloadColumns);

public sealed record MigrationDebugSnapshotStageCount(string StageName, long Count);

public sealed record MigrationDebugParentRecordReference(string BatchId, int RecordIndex);

public sealed record TargetRecordIdLookup(string TableName, string KeyColumn, string KeyValue);

public sealed record TargetRecordIdMapping(
    string ConnectionId,
    string TableName,
    string KeyColumn,
    string KeyValue,
    Guid RecordId);

public sealed record TargetRecordContextMapping(
    string StepId,
    string ConnectionId,
    string TableName,
    string KeyColumnsJson,
    string KeyValuesJson,
    string RecordJson);

public sealed record JoinInputRecord(
    long Sequence,
    int RecordIndex,
    string RecordJson,
    string OriginBatchId,
    int OriginRecordIndex)
{
    public string? KeyJson { get; init; }
}

public sealed record JoinInputRecordMapping(
    string JoinStepId,
    string InputStepId,
    long Sequence,
    int RecordIndex,
    string RecordJson,
    string OriginBatchId,
    int OriginRecordIndex,
    string[]? KeyColumns = null);

public sealed record JoinRelationshipMatch(
    long Sequence,
    int RecordIndex,
    string Alias,
    string? ParentInputStepId,
    long? ParentSequence,
    int? ParentRecordIndex);

public sealed record JoinInputRecordKey(long Sequence, int RecordIndex);

public static class TargetRecordContextKey
{
    public static string Serialize(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0 || values.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Target record context keys must contain non-empty values.", nameof(values));
        }

        return JsonSerializer.Serialize(values);
    }
}

public sealed record MigrationDebugStage
{
    public MigrationDebugStage(string stageName, string recordsJson)
        : this(stageName, recordsJson, null, null)
    {
    }

    public MigrationDebugStage(
        string stageName,
        string recordsJson,
        IReadOnlyList<IReadOnlyList<int>>? parentRecordIndices)
        : this(stageName, recordsJson, parentRecordIndices, null)
    {
    }

    public MigrationDebugStage(
        string stageName,
        string recordsJson,
        IReadOnlyList<IReadOnlyList<int>>? parentRecordIndices,
        IReadOnlyList<IReadOnlyList<MigrationDebugParentRecordReference>>? parentRecordReferences)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageName);
        ArgumentNullException.ThrowIfNull(recordsJson);
        StageName = stageName;
        RecordsJson = recordsJson;
        ParentRecordIndices = parentRecordIndices?
            .Select(indices => (IReadOnlyList<int>)indices.ToArray())
            .ToArray();
        ParentRecordReferences = parentRecordReferences?
            .Select(references => (IReadOnlyList<MigrationDebugParentRecordReference>)references.ToArray())
            .ToArray();
    }

    public string StageName { get; init; }

    public string RecordsJson { get; init; }

    public IReadOnlyList<IReadOnlyList<int>>? ParentRecordIndices { get; init; }

    public IReadOnlyList<IReadOnlyList<MigrationDebugParentRecordReference>>? ParentRecordReferences { get; init; }
}
