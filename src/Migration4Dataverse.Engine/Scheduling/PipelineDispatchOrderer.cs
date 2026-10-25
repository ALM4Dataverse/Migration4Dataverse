using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.Scheduling;

internal static class PipelineDispatchOrderer
{
    public static IReadOnlyList<PendingBatchStepCandidate> Order(
        IReadOnlyList<PendingBatchStepCandidate> candidates,
        IReadOnlyList<string> stepOrder,
        IReadOnlyDictionary<string, double> dispatchWeights,
        IReadOnlyDictionary<string, long> dispatchedByStep,
        int nextCandidateIndex)
    {
        if (candidates.Count < 2 || stepOrder.Count == 0)
        {
            return candidates;
        }

        var rotationOrder = stepOrder
            .Select((stepId, index) => (
                stepId,
                index: (index - nextCandidateIndex + stepOrder.Count) % stepOrder.Count))
            .ToDictionary(entry => entry.stepId, entry => entry.index, StringComparer.Ordinal);
        var ordered = new List<PendingBatchStepCandidate>(candidates.Count);
        foreach (var priorityGroup in candidates.GroupBy(candidate => candidate.Priority)
                     .OrderBy(group => group.Key))
        {
            ordered.AddRange(priorityGroup
                .OrderBy(candidate =>
                {
                    var weight = dispatchWeights.GetValueOrDefault(candidate.StepId, 1);
                    if (!double.IsFinite(weight) || weight <= 0)
                    {
                        throw new InvalidOperationException(
                            $"Dispatch weight for step '{candidate.StepId}' must be finite and greater than zero.");
                    }

                    if (candidate.PendingBatchCount <= 0)
                    {
                        throw new InvalidOperationException(
                            $"Pending batch count for step '{candidate.StepId}' must be greater than zero.");
                    }

                    var backlogWeight = Math.Log2(candidate.PendingBatchCount + 1d);
                    return dispatchedByStep.GetValueOrDefault(candidate.StepId) / (weight * backlogWeight);
                })
                .ThenBy(candidate => rotationOrder.GetValueOrDefault(candidate.StepId, int.MaxValue))
                .ThenBy(candidate => candidate.StepId, StringComparer.Ordinal));
        }

        return ordered;
    }
}
