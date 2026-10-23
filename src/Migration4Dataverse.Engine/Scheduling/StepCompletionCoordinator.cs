using System.Collections.Concurrent;

namespace Migration4Dataverse.Engine.Scheduling;

public sealed class StepCompletionCoordinator
{
    private readonly ConcurrentDictionary<(string RunId, string StepId), TaskCompletionSource> _completedSteps = new();

    public bool IsStepCompleted(string runId, string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        return GetCompletion(runId, stepId).Task.IsCompletedSuccessfully;
    }

    public void MarkStepCompleted(string runId, string stepId)
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
