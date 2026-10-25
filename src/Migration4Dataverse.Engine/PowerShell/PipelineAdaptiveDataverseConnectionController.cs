using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace Migration4Dataverse.Engine.PowerShell;

internal sealed class PipelineAdaptiveDataverseConnectionController : IDisposable
{
    private static readonly TimeSpan ConcurrencyAdjustmentInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ThrottleCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NoImprovementCooldown = TimeSpan.FromMinutes(1);

    private readonly object _sync = new();
    private readonly string _connectionId;
    private readonly object _connection;
    private readonly int _maxConcurrency;
    private bool _autoConcurrency;
    private readonly PipelineThroughputMeter _throughputMeter;
    private readonly DataverseConnectionRetryMonitor _retryMonitor;
    private readonly PipelineBatchTuningCoordinator _batchTuningCoordinator;
    private readonly Dictionary<string, TargetRegistration> _targets = new(StringComparer.Ordinal);
    private int _concurrencyBudget;
    private int _active;
    private int _totalRetries;
    private int _totalThrottles;
    private bool _capacitySaturated;
    private bool _concurrencyIncreasePending;
    private int _concurrencyBeforeIncrease;
    private double _throughputBeforeIncrease;
    private long _concurrencyIncreaseTimestamp;
    private long _cooldownUntilTimestamp;
    private bool _disposed;

    public int CurrentConcurrencyBudget
    {
        get
        {
            lock (_sync)
            {
                return _concurrencyBudget;
            }
        }
    }

    public PipelineAdaptiveDataverseConnectionController(
        string connectionId,
        object connection,
        int maxConcurrency,
        PipelineThroughputMeter throughputMeter,
        PipelineBatchTuningCoordinator batchTuningCoordinator,
        bool disableRetriesForDiagnostics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
        ArgumentNullException.ThrowIfNull(throughputMeter);
        ArgumentNullException.ThrowIfNull(batchTuningCoordinator);

        _connectionId = connectionId;
        _connection = connection;
        _maxConcurrency = maxConcurrency;
        _throughputMeter = throughputMeter;
        _batchTuningCoordinator = batchTuningCoordinator;
        _retryMonitor = new DataverseConnectionRetryMonitor(connection, disableRetriesForDiagnostics);
        _concurrencyBudget = maxConcurrency;
    }

    public void RegisterTarget(
        string stepId,
        int maxBatchSize,
        bool autoBatchSize,
        bool autoConcurrency,
        int batchSize,
        int maxStepConcurrency,
        int? persistedBatchSize,
        int? persistedConcurrency,
        Action<int?, int?>? persistTunedValues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBatchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxStepConcurrency);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_targets.ContainsKey(stepId))
            {
                throw new InvalidOperationException(
                    $"Dataverse target '{stepId}' was registered more than once for connection '{_connectionId}'.");
            }

            var tuner = new AdaptiveDataverseTargetTuner(
                Math.Min(maxBatchSize, 1000),
                _maxConcurrency,
                autoBatchSize,
                autoConcurrency: false,
                batchSize,
                _maxConcurrency,
                getPipelineThroughput: _throughputMeter.GetRowsPerSecond,
                persistedBatchSize: persistedBatchSize,
                persistTunedValues: persistTunedValues)
            {
                StepId = stepId
            };
            _targets.Add(stepId, new TargetRegistration(
                tuner,
                autoBatchSize,
                autoConcurrency,
                autoConcurrency
                    ? _maxConcurrency
                    : Math.Min(maxStepConcurrency, _maxConcurrency)));
            _targets[stepId].PersistTunedValues = persistTunedValues;
            _targets[stepId].PersistedConcurrency = autoConcurrency && persistedConcurrency is > 0
                ? Math.Min(persistedConcurrency.Value, _maxConcurrency)
                : null;

            if (autoConcurrency && !_autoConcurrency)
            {
                _autoConcurrency = true;
                _concurrencyBudget = GetRecommendedConcurrency();
            }
        }
    }

    public void CompleteRegistration()
    {
        lock (_sync)
        {
            var autoTargets = _targets.Values.Where(target => target.AutoConcurrency).ToArray();
            if (autoTargets.Length == 0 ||
                autoTargets.Any(target => target.PersistedConcurrency is null))
            {
                return;
            }

            var persistedValues = autoTargets
                .Select(target => target.PersistedConcurrency!.Value)
                .Distinct()
                .ToArray();
            if (persistedValues.Length == 1)
            {
                _concurrencyBudget = Math.Min(persistedValues[0], _maxConcurrency);
            }
        }
    }

    public PipelineDataverseTargetLease? TryAcquire(string stepId)
    {
        TargetRegistration target;
        bool countsForTuning;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_targets.TryGetValue(stepId, out target!))
            {
                throw new KeyNotFoundException(
                    $"Dataverse target '{stepId}' is not registered for connection '{_connectionId}'.");
            }

            if (_active >= _concurrencyBudget)
            {
                _capacitySaturated = true;
                return null;
            }
            if (target.Active >= target.MaxStepConcurrency)
            {
                return null;
            }

            _active++;
            target.Active++;
        }

        countsForTuning = _batchTuningCoordinator.TryAcquire(
            stepId,
            target.AutoBatchSize,
            target.Tuner.GetDashboardState().Stage == "Complete");
        TargetTuningLease? tuningLease;
        try
        {
            tuningLease = target.Tuner.TryAcquire(countsForTuning);
        }
        catch
        {
            if (countsForTuning)
            {
                _batchTuningCoordinator.Release(stepId, false);
            }
            ReleaseReservation(stepId);
            throw;
        }
        if (tuningLease is null)
        {
            if (countsForTuning)
            {
                _batchTuningCoordinator.Release(
                    stepId,
                    target.Tuner.GetDashboardState().Stage == "Complete");
            }
            ReleaseReservation(stepId);
            return null;
        }

        return new PipelineDataverseTargetLease(this, stepId, target.Tuner, tuningLease);
    }

    public void Complete(PipelineDataverseTargetLease lease, int rowsWritten, bool succeeded)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentOutOfRangeException.ThrowIfNegative(rowsWritten);
        if (!lease.TryMarkCompleted())
        {
            throw new InvalidOperationException("The Dataverse target lease has already been completed or released.");
        }

        try
        {
            lease.Tuner.Complete(lease.TuningLease, rowsWritten, succeeded);
            var observation = _retryMonitor.ReadObservations();
            lock (_sync)
            {
                _totalRetries = checked(_totalRetries + observation.RetryCount);
                _totalThrottles = checked(_totalThrottles + observation.ThrottleCount);
                AdjustConcurrency(observation.ThrottleCount);
            }
        }
        finally
        {
            var tuningComplete = lease.Tuner.GetDashboardState().Stage == "Complete";
            if (lease.TuningLease.CountsForTuning || tuningComplete)
            {
                _batchTuningCoordinator.Release(lease.StepId, tuningComplete);
            }
            ReleaseReservation(lease.StepId);
        }
    }

    public AdaptiveDataverseTargetTuningState GetDashboardState(string stepId)
    {
        lock (_sync)
        {
            if (!_targets.TryGetValue(stepId, out var target))
            {
                throw new KeyNotFoundException(
                    $"Dataverse target '{stepId}' is not registered for connection '{_connectionId}'.");
            }

            var state = target.Tuner.GetDashboardState();
            return state with
            {
                MaxConcurrency = target.AutoConcurrency ? _concurrencyBudget : target.MaxStepConcurrency,
                ConcurrencyAuto = target.AutoConcurrency,
                ConcurrencyState = target.AutoConcurrency
                    ? $"shared connection budget ({_active}/{_concurrencyBudget}; retries {_totalRetries}; throttles {_totalThrottles})"
                    : "fixed step limit",
                ConcurrencyLimit = target.AutoConcurrency ? _maxConcurrency : target.MaxStepConcurrency,
                ConnectionId = _connectionId,
                ActiveConnectionCalls = _active,
                ConnectionConcurrencyBudget = _concurrencyBudget,
                RetryCount = _totalRetries,
                ThrottleCount = _totalThrottles,
                RetryTelemetryAvailable = _retryMonitor.IsAvailable
            };
        }
    }

    public string GetSummary()
    {
        lock (_sync)
        {
            return $"Dataverse connection '{_connectionId}': shared DOP={_concurrencyBudget}/{_maxConcurrency}, " +
                   $"active={_active}, retries={_totalRetries}, throttles={_totalThrottles}.";
        }
    }

    public void PersistCurrentConcurrency()
    {
        TargetRegistration[] autoTargets;
        int concurrencyBudget;
        lock (_sync)
        {
            autoTargets = _targets.Values.Where(target => target.AutoConcurrency).ToArray();
            concurrencyBudget = _concurrencyBudget;
        }

        foreach (var target in autoTargets)
        {
            target.PersistTunedValues?.Invoke(null, concurrencyBudget);
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
        _retryMonitor.Dispose();
    }

    internal void ReleaseLease(PipelineDataverseTargetLease lease)
    {
        lease.TuningLease.Dispose();
        var tuningComplete = lease.Tuner.GetDashboardState().Stage == "Complete";
        if (lease.TuningLease.CountsForTuning || tuningComplete)
        {
            _batchTuningCoordinator.Release(lease.StepId, tuningComplete);
        }
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

    private void AdjustConcurrency(int observedThrottles)
    {
        var now = Stopwatch.GetTimestamp();
        var throughput = _throughputMeter.GetRowsPerSecond();
        if (!_autoConcurrency)
        {
            return;
        }
        if (observedThrottles > 0)
        {
            _concurrencyBudget = Math.Max(1, _concurrencyBudget / 2);
            _concurrencyIncreasePending = false;
            _capacitySaturated = false;
            _cooldownUntilTimestamp = now + (long)(ThrottleCooldown.TotalSeconds * Stopwatch.Frequency);
            return;
        }

        if (!_retryMonitor.IsAvailable)
        {
            return;
        }

        if (_concurrencyIncreasePending &&
            Stopwatch.GetElapsedTime(_concurrencyIncreaseTimestamp, now) >= ConcurrencyAdjustmentInterval)
        {
            if (_throughputBeforeIncrease == 0
                    ? throughput <= 0
                    : throughput < _throughputBeforeIncrease * 1.05)
            {
                _concurrencyBudget = _concurrencyBeforeIncrease;
                _cooldownUntilTimestamp = now + (long)(NoImprovementCooldown.TotalSeconds * Stopwatch.Frequency);
            }
            _concurrencyIncreasePending = false;
        }

        if (_capacitySaturated &&
            _concurrencyBudget < _maxConcurrency &&
            !_concurrencyIncreasePending &&
            now >= _cooldownUntilTimestamp &&
            Stopwatch.GetElapsedTime(_concurrencyIncreaseTimestamp, now) >= ConcurrencyAdjustmentInterval)
        {
            _concurrencyBeforeIncrease = _concurrencyBudget;
            _throughputBeforeIncrease = throughput;
            _concurrencyBudget++;
            _concurrencyIncreaseTimestamp = now;
            _concurrencyIncreasePending = true;
            _capacitySaturated = false;
        }
    }

    private int GetRecommendedConcurrency()
    {
        var property = _connection.GetType().GetProperty(
            "RecommendedDegreesOfParallelism",
            BindingFlags.Instance | BindingFlags.Public);
        if (property?.GetValue(_connection) is { } value)
        {
            var recommended = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (recommended > 0)
            {
                return Math.Min(recommended, _maxConcurrency);
            }
        }

        return Math.Min(4, _maxConcurrency);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class TargetRegistration(
        AdaptiveDataverseTargetTuner tuner,
        bool autoBatchSize,
        bool autoConcurrency,
        int maxStepConcurrency)
    {
        public AdaptiveDataverseTargetTuner Tuner { get; } = tuner;

        public bool AutoBatchSize { get; } = autoBatchSize;

        public bool AutoConcurrency { get; } = autoConcurrency;

        public int MaxStepConcurrency { get; } = maxStepConcurrency;

        public int? PersistedConcurrency { get; set; }

        public Action<int?, int?>? PersistTunedValues { get; set; }

        public int Active { get; set; }
    }
}

internal sealed class PipelineDataverseTargetLease(
    PipelineAdaptiveDataverseConnectionController controller,
    string stepId,
    AdaptiveDataverseTargetTuner tuner,
    TargetTuningLease tuningLease) : IDisposable
{
    private int _released;

    public string StepId { get; } = stepId;

    public AdaptiveDataverseTargetTuner Tuner { get; } = tuner;

    public TargetTuningLease TuningLease { get; } = tuningLease;

    public int BatchSize => TuningLease.BatchSize;

    internal bool TryMarkCompleted() => Interlocked.Exchange(ref _released, 1) == 0;

    public void Dispose()
    {
        if (TryMarkCompleted())
        {
            controller.ReleaseLease(this);
        }
    }
}
