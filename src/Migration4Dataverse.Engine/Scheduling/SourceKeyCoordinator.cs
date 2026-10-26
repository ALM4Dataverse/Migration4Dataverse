using System.Collections.Concurrent;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.Scheduling;

public sealed class SourceKeyCoordinator
{
    private const int MaximumCachedSourceKeys = 8192;
    private readonly IMigrationStore _store;
    private readonly ConcurrentDictionary<(string RunId, string StepId), TaskCompletionSource> _completedSteps = new();
    private readonly ConcurrentDictionary<(string RunId, string StepId, string Key), SourceKeyBatchReference> _sourceKeys = new();

    public SourceKeyCoordinator(IMigrationStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public async Task<SourceKeyBatchReference> WaitForSourceKeyAsync(
        string runId,
        string stepId,
        string sourceKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_sourceKeys.TryGetValue((runId, stepId, sourceKey), out var cachedReference))
            {
                return cachedReference;
            }

            var reference = _store.GetSourceKeyBatchReference(runId, stepId, sourceKey);
            if (reference is not null)
            {
                if (_sourceKeys.Count < MaximumCachedSourceKeys)
                {
                    _sourceKeys.TryAdd((runId, stepId, sourceKey), reference);
                }
                return reference;
            }

            var completion = GetCompletion(runId, stepId);
            if (completion.Task.IsCompleted)
            {
                reference = _store.GetSourceKeyBatchReference(runId, stepId, sourceKey);
                if (reference is null)
                {
                    throw new InvalidDataException(
                        $"No source row in parent step '{stepId}' has key '{sourceKey}'.");
                }

                if (_sourceKeys.Count < MaximumCachedSourceKeys)
                {
                    _sourceKeys.TryAdd((runId, stepId, sourceKey), reference);
                }
                return reference;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task WaitForSourceCompletionAsync(
        string runId,
        string stepId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        await GetCompletion(runId, stepId).Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void MarkSourceCompleted(string runId, string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        GetCompletion(runId, stepId).TrySetResult();
    }

    private TaskCompletionSource GetCompletion(string runId, string stepId)
    {
        return _completedSteps.GetOrAdd(
            (runId, stepId),
            static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
