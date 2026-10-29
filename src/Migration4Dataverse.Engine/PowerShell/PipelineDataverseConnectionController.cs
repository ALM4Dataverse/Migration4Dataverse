using System.Globalization;
using System.Reflection;

namespace Migration4Dataverse.Engine.PowerShell;

internal sealed class PipelineDataverseConnectionController : IDisposable
{
    private const int MaximumBatchSize = 1000;

    private readonly object _sync = new();
    private readonly string _connectionId;
    private readonly DataverseConnectionPool? _connectionPool;
    private readonly int _maxConcurrency;
    private readonly DataverseConnectionRetryMonitor? _retryMonitor;
    private readonly Dictionary<string, TargetRegistration> _targets = new(StringComparer.Ordinal);
    private int _active;
    private int _totalRetries;
    private int _totalThrottles;
    private bool _disposed;

    public PipelineDataverseConnectionController(
        string connectionId,
        object connection,
        int invocationMaxConcurrency,
        bool disableRetriesForDiagnostics,
        DataverseConnectionPool? connectionPool = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(invocationMaxConcurrency);

        _connectionId = connectionId;
        _connectionPool = connectionPool;
        var recommendedConcurrency = connectionPool?.RecommendedDegreesOfParallelism
            ?? GetRecommendedConcurrency(connection);
        _maxConcurrency = Math.Min(recommendedConcurrency, invocationMaxConcurrency);
        _retryMonitor = connectionPool is null
            ? new DataverseConnectionRetryMonitor(connection, disableRetriesForDiagnostics)
            : null;
    }

    public void RegisterTarget(string stepId, int batchSize, int maxStepConcurrency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        if (batchSize > MaximumBatchSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(batchSize),
                $"Dataverse target '{stepId}' batch size cannot exceed {MaximumBatchSize}.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxStepConcurrency);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_targets.TryAdd(
                    stepId,
                    new TargetRegistration(batchSize, Math.Min(maxStepConcurrency, _maxConcurrency))))
            {
                throw new InvalidOperationException(
                    $"Dataverse target '{stepId}' was registered more than once for connection '{_connectionId}'.");
            }
        }
    }

    public PipelineDataverseTargetLease? TryAcquire(string stepId)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_targets.TryGetValue(stepId, out var target))
            {
                throw new KeyNotFoundException(
                    $"Dataverse target '{stepId}' is not registered for connection '{_connectionId}'.");
            }

            if (_active >= _maxConcurrency || target.Active >= target.MaxStepConcurrency)
            {
                return null;
            }

            _active++;
            target.Active++;
            return new PipelineDataverseTargetLease(this, stepId, target.BatchSize);
        }
    }

    public void Complete(PipelineDataverseTargetLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!lease.TryMarkCompleted())
        {
            throw new InvalidOperationException("The Dataverse target lease has already been completed or released.");
        }

        try
        {
            var observation = _connectionPool?.ReadObservations()
                ?? _retryMonitor!.ReadObservations();
            lock (_sync)
            {
                _totalRetries = checked(_totalRetries + observation.RetryCount);
                _totalThrottles = checked(_totalThrottles + observation.ThrottleCount);
            }
        }
        finally
        {
            ReleaseReservation(lease.StepId);
        }
    }

    public DataverseTargetRuntimeState GetDashboardState(string stepId)
    {
        lock (_sync)
        {
            if (!_targets.TryGetValue(stepId, out var target))
            {
                throw new KeyNotFoundException(
                    $"Dataverse target '{stepId}' is not registered for connection '{_connectionId}'.");
            }

            var targetConcurrency = Math.Min(target.MaxStepConcurrency, _maxConcurrency);
            return new DataverseTargetRuntimeState(
                target.BatchSize,
                BatchSizeState: "fixed",
                BatchSizeLimit: MaximumBatchSize,
                MaxConcurrency: targetConcurrency,
                ConcurrencyState: "fixed connection budget",
                ConcurrencyLimit: targetConcurrency,
                Stage: "Fixed",
                SamplesCompleted: 0,
                SamplesPerCandidate: 0)
            {
                ConnectionId = _connectionId,
                ActiveConnectionCalls = _active,
                ConnectionConcurrencyBudget = _maxConcurrency,
                RetryCount = _totalRetries,
                ThrottleCount = _totalThrottles,
                RetryTelemetryAvailable = _connectionPool?.HasRetryTelemetry
                    ?? _retryMonitor!.IsAvailable
            };
        }
    }

    public string GetSummary()
    {
        lock (_sync)
        {
            return $"Dataverse connection '{_connectionId}': DOP budget={_maxConcurrency}, " +
                   $"active={_active}, retries={_totalRetries}, throttles={_totalThrottles}.";
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _retryMonitor?.Dispose();
    }

    internal void ReleaseLease(PipelineDataverseTargetLease lease)
    {
        ReleaseReservation(lease.StepId);
    }

    private void ReleaseReservation(string stepId)
    {
        lock (_sync)
        {
            if (_active <= 0 || !_targets.TryGetValue(stepId, out var target) || target.Active <= 0)
            {
                throw new InvalidOperationException(
                    $"The shared Dataverse execution lease counter underflowed for step '{stepId}'.");
            }

            _active--;
            target.Active--;
        }
    }

    private static int GetRecommendedConcurrency(object connection)
    {
        var property = connection.GetType().GetProperty(
            "RecommendedDegreesOfParallelism",
            BindingFlags.Instance | BindingFlags.Public);
        if (property?.GetValue(connection) is { } value)
        {
            var recommended = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (recommended > 0)
            {
                return recommended;
            }
        }

        return 4;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class TargetRegistration(int batchSize, int maxStepConcurrency)
    {
        public int BatchSize { get; } = batchSize;

        public int MaxStepConcurrency { get; } = maxStepConcurrency;

        public int Active { get; set; }
    }
}

internal sealed class PipelineDataverseTargetLease(
    PipelineDataverseConnectionController controller,
    string stepId,
    int batchSize) : IDisposable
{
    private int _released;

    public string StepId { get; } = stepId;

    public int BatchSize { get; } = batchSize;

    internal bool TryMarkCompleted() => Interlocked.Exchange(ref _released, 1) == 0;

    public void Dispose()
    {
        if (TryMarkCompleted())
        {
            controller.ReleaseLease(this);
        }
    }
}

internal sealed record DataverseTargetRuntimeState(
    int BatchSize,
    string BatchSizeState,
    int BatchSizeLimit,
    int MaxConcurrency,
    string ConcurrencyState,
    int ConcurrencyLimit,
    string Stage,
    int SamplesCompleted,
    int SamplesPerCandidate)
{
    public string? ConnectionId { get; init; }

    public int ActiveConnectionCalls { get; init; }

    public int ConnectionConcurrencyBudget { get; init; }

    public int RetryCount { get; init; }

    public int ThrottleCount { get; init; }

    public bool RetryTelemetryAvailable { get; init; }
}
