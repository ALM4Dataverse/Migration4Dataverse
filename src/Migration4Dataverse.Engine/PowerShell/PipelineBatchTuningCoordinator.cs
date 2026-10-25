using System.Diagnostics;

namespace Migration4Dataverse.Engine.PowerShell;

internal sealed class PipelineBatchTuningCoordinator
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);
    private readonly object _sync = new();
    private string? _activeStepId;
    private bool _leaseActive;
    private long _lastActivityTimestamp;

    public bool TryAcquire(string stepId, bool autoBatchSize, bool tuningComplete)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        if (!autoBatchSize || tuningComplete)
        {
            return false;
        }

        lock (_sync)
        {
            if (_activeStepId is not null &&
                !_leaseActive &&
                Stopwatch.GetElapsedTime(_lastActivityTimestamp) >= IdleTimeout)
            {
                _activeStepId = null;
            }

            if (_activeStepId is null)
            {
                _activeStepId = stepId;
            }
            if (_activeStepId != stepId || _leaseActive)
            {
                return false;
            }

            _leaseActive = true;
            _lastActivityTimestamp = Stopwatch.GetTimestamp();
            return true;
        }
    }

    public void Release(string stepId, bool tuningComplete)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        lock (_sync)
        {
            if (_activeStepId != stepId)
            {
                return;
            }

            _leaseActive = false;
            _lastActivityTimestamp = Stopwatch.GetTimestamp();
            if (tuningComplete)
            {
                _activeStepId = null;
            }
        }
    }
}
