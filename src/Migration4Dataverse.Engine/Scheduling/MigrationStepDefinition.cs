using System.Security.Cryptography;
using System.Text;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.Scheduling;

public sealed class MigrationStepDefinition
{
    public MigrationStepDefinition(
        string id,
        IEnumerable<string>? inputs,
        Func<MigrationStepCheckpoint, CancellationToken, IAsyncEnumerable<MigrationWorkBatch>>? readSourceBatches,
        Func<StoredBatch, CancellationToken, Task<MigrationBatchResult>> executeBatchAsync,
        string type = "callback",
        IEnumerable<string>? completionDependencies = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(executeBatchAsync);

        Id = id;
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        Type = type;
        Inputs = (inputs ?? Array.Empty<string>()).ToArray();
        if (Inputs.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Input step IDs must not be empty.", nameof(inputs));
        }
        if (Inputs.Distinct(StringComparer.Ordinal).Count() != Inputs.Count)
        {
            throw new ArgumentException("Input step IDs must be unique.", nameof(inputs));
        }
        ReadSourceBatches = readSourceBatches;
        ExecuteBatchAsync = executeBatchAsync;
        CompletionDependencies = (completionDependencies ?? Array.Empty<string>()).ToArray();
        if (CompletionDependencies.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Completion dependency step IDs must not be empty.", nameof(completionDependencies));
        }
        if (CompletionDependencies.Distinct(StringComparer.Ordinal).Count() != CompletionDependencies.Count)
        {
            throw new ArgumentException("Completion dependency step IDs must be unique.", nameof(completionDependencies));
        }
    }

    public string Id { get; }

    public string Type { get; }

    public IReadOnlyList<string> Inputs { get; }

    public IReadOnlyList<string> CompletionDependencies { get; }

    public Func<MigrationStepCheckpoint, CancellationToken, IAsyncEnumerable<MigrationWorkBatch>>? ReadSourceBatches { get; }

    public Func<StoredBatch, CancellationToken, Task<MigrationBatchResult>> ExecuteBatchAsync { get; }

    public Func<IDisposable?>? TryAcquireExecutionLease { get; init; }

    public Func<StoredBatch, IDisposable?, CancellationToken, Task<MigrationBatchResult>>?
        ExecuteBatchWithExecutionLeaseAsync { get; init; }

    public double DispatchWeight { get; init; } = 1;

    public long? ExpectedRecordCount { get; init; }

    public Func<CancellationToken, Task<long>>? ResolveExpectedRecordCountAsync { get; init; }

    public Func<CancellationToken, Task>? CompleteAsync { get; init; }

    public bool WaitForInputCompletion { get; init; } = true;

    public SourceKeyCoordinator? SourceKeyCoordinator { get; init; }

    public string? SourceKeyProperty { get; init; }

    public StepCompletionCoordinator? StepCompletionCoordinator { get; init; }
}

public sealed record MigrationWorkBatch(
    string PayloadJson,
    string? CursorAfterJson = null,
    bool IsLast = false)
{
    public IReadOnlyList<string> ParentBatchIds { get; init; } = Array.Empty<string>();

    public long FilteredRows { get; init; }
}

public sealed record MigrationDependencyOutput(
    string ChildStepId,
    string PayloadJson,
    IReadOnlyList<string>? RequiredBatchIds = null);

public sealed record MigrationBatchResult(
    string OutcomesJson,
    bool HasErrors = false,
    IReadOnlyList<MigrationDependencyOutput>? Dependencies = null,
    string? LastError = null,
    IReadOnlyList<MigrationDebugStage>? DebugStages = null)
{
    public MigrationBatchResult(
        string outcomesJson,
        bool hasErrors,
        IReadOnlyList<MigrationDependencyOutput>? dependencies,
        string? lastError,
        IReadOnlyList<MigrationDebugStage>? debugStages,
        long filteredRows)
        : this(outcomesJson, hasErrors, dependencies, lastError, debugStages)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(filteredRows);
        FilteredRows = filteredRows;
    }

    public long FilteredRows { get; init; }
}

public sealed record DependencySchedulerOptions
{
    public int MaxDegreeOfParallelism { get; init; } = 4;

    public int MaxQueuedBatches { get; init; } = 64;

    public int MaxQueuedBatchesPerStep { get; init; } = 32;

    public int MaxAttempts { get; init; } = 5;

    public TimeSpan BatchLeaseDuration { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan RunLeaseDuration { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public bool CaptureDebugData { get; init; }

    public int DebugCaptureRetentionDays { get; init; } = 30;
}

public sealed record DependencySchedulerResult(
    string RunId,
    long SucceededBatches,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc)
{
    public bool IsWhatIf { get; init; }

    public long TargetRowsProcessed { get; init; }

    public long TargetRowsSucceeded { get; init; }

    public long TargetRowsFailed { get; init; }

    public long? TargetRowsExpected { get; init; }

    public long FilteredRows { get; init; }
}

public static class MigrationBatchIdentity
{
    public static string Create(string runId, string stepId, long sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Batch sequence must be zero or greater.");
        }

        var identity = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{runId.Length}:{runId}{stepId.Length}:{stepId}{sequence}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"batch-{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
