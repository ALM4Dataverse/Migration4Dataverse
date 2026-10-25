using System.Diagnostics;

namespace Migration4Dataverse.Engine.PowerShell;

internal sealed class AdaptiveDataverseTargetTuner
{
    private static readonly int[] BatchSizeCandidates = [1, 2, 5, 10, 25, 50, 100, 250, 500, 1000];
    private static readonly int[] ConcurrencyCandidates = [1, 2, 4, 8, 16, 32];
    private const int SamplesPerCandidate = 3;
    private const double ImprovementThreshold = 1.05;

    private readonly object _sync = new();
    private readonly Func<long> _getTimestamp;
    private readonly Func<double>? _getPipelineThroughput;
    private readonly int[] _batchCandidates;
    private readonly int[] _concurrencyCandidates;
    private readonly int _maxBatchSize;
    private readonly int _maxConcurrency;
    private readonly int? _persistedConcurrency;
    private readonly bool _autoBatchSize;
    private readonly bool _autoConcurrency;
    private readonly Action<int?, int?>? _persistTunedValues;
    private int _active;
    private int _epoch;
    private int _batchIndex;
    private int _concurrencyIndex;
    private int _samples;
    private int _nonImprovingCandidates;
    private long _sampleRows;
    private long _sampleStart = long.MaxValue;
    private long _sampleEnd;
    private double _bestThroughput;
    private int _bestBatchSize;
    private int _bestConcurrency;
    private bool _transitionPending;
    private bool _profilePersisted;
    private TuningStage _stage;

    public AdaptiveDataverseTargetTuner(
        int maxBatchSize,
        int maxConcurrency,
        bool autoBatchSize,
        bool autoConcurrency,
        int fixedBatchSize,
        int fixedConcurrency,
        Func<long>? getTimestamp = null,
        Func<double>? getPipelineThroughput = null,
        int? persistedBatchSize = null,
        int? persistedConcurrency = null,
        Action<int?, int?>? persistTunedValues = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBatchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fixedBatchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fixedConcurrency);
        if (persistedBatchSize is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(persistedBatchSize));
        }
        if (persistedConcurrency is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(persistedConcurrency));
        }

        _getTimestamp = getTimestamp ?? Stopwatch.GetTimestamp;
        _getPipelineThroughput = getPipelineThroughput;
        _autoBatchSize = autoBatchSize;
        _autoConcurrency = autoConcurrency;
        _persistTunedValues = persistTunedValues;
        _maxBatchSize = maxBatchSize;
        _maxConcurrency = maxConcurrency;
        var initialBatchSize = Math.Min(persistedBatchSize ?? fixedBatchSize, maxBatchSize);
        _batchCandidates = CreateBatchCandidateOrder(
            BatchSizeCandidates.Where(value => value <= maxBatchSize),
            initialBatchSize);
        _concurrencyCandidates = ConcurrencyCandidates.Where(value => value <= maxConcurrency).ToArray();
        var tuneBatchSize = autoBatchSize;
        var tuneConcurrency = autoConcurrency && persistedConcurrency is null;
        _persistedConcurrency = autoConcurrency && persistedConcurrency.HasValue
            ? Math.Min(persistedConcurrency.Value, maxConcurrency)
            : null;
        BatchSize = tuneBatchSize ? _batchCandidates[0] : initialBatchSize;
        MaxConcurrency = tuneConcurrency || tuneBatchSize && autoConcurrency
            ? 1
            : _persistedConcurrency ?? Math.Min(fixedConcurrency, maxConcurrency);
        _stage = tuneBatchSize
            ? TuningStage.BatchSize
            : tuneConcurrency
                ? TuningStage.Concurrency
                : TuningStage.Complete;
        _profilePersisted = _stage == TuningStage.Complete;
        if (_stage == TuningStage.BatchSize)
        {
            _batchIndex = 0;
        }
        else if (_stage == TuningStage.Concurrency)
        {
            _concurrencyIndex = Array.IndexOf(_concurrencyCandidates, 1);
        }
    }

    public int BatchSize { get; private set; }

    public int MaxConcurrency { get; private set; }

    public string StepId { get; set; } = string.Empty;

    public TargetTuningLease Acquire(bool countsForTuning = true)
    {
        lock (_sync)
        {
            while (_transitionPending || _active >= MaxConcurrency)
            {
                Monitor.Wait(_sync);
            }

            _active++;
            return CreateLease(countsForTuning);
        }
    }

    public TargetTuningLease? TryAcquire(bool countsForTuning = true)
    {
        lock (_sync)
        {
            if (_transitionPending || _active >= MaxConcurrency)
            {
                return null;
            }

            _active++;
            return CreateLease(countsForTuning);
        }
    }

    public void Complete(TargetTuningLease lease, int rowsWritten, bool succeeded)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentOutOfRangeException.ThrowIfNegative(rowsWritten);
        if (!lease.TryMarkCompleted())
        {
            throw new InvalidOperationException("The target tuning lease has already been released.");
        }

        lock (_sync)
        {
            if (_active <= 0)
            {
                throw new InvalidOperationException("The target tuning lease is not active.");
            }

            if (succeeded &&
                lease.CountsForTuning &&
                lease.Epoch == _epoch &&
                _stage != TuningStage.Complete)
            {
                _samples++;
                _sampleRows += rowsWritten;
                _sampleStart = Math.Min(_sampleStart, lease.StartedAt);
                _sampleEnd = Math.Max(_sampleEnd, _getTimestamp());
                if (_samples >= SamplesPerCandidate)
                {
                    _transitionPending = true;
                }
            }

            _active--;
            if (_transitionPending && _active == 0)
            {
                FinishCandidate();
            }
            try
            {
                PersistCompletedProfile();
            }
            finally
            {
                Monitor.PulseAll(_sync);
            }
        }
    }

    internal void ReleaseUncompletedLease()
    {
        lock (_sync)
        {
            if (_active <= 0)
            {
                throw new InvalidOperationException("The target tuning lease is not active.");
            }

            _active--;
            if (_transitionPending && _active == 0)
            {
                FinishCandidate();
            }
            try
            {
                PersistCompletedProfile();
            }
            finally
            {
                Monitor.PulseAll(_sync);
            }
        }
    }

    private void PersistCompletedProfile()
    {
        if (_stage != TuningStage.Complete || _profilePersisted)
        {
            return;
        }

        _persistTunedValues?.Invoke(
            _autoBatchSize ? BatchSize : null,
            _autoConcurrency ? MaxConcurrency : null);
        _profilePersisted = true;
    }

    public string GetSummary()
    {
        lock (_sync)
        {
            var status = !_autoBatchSize && !_autoConcurrency
                ? "fixed"
                : _stage == TuningStage.Complete
                    ? "complete"
                    : "in progress";
            return $"Target '{StepId}': BatchSize={BatchSize} ({BatchSizeState()}), " +
                   $"DOP={MaxConcurrency} ({ConcurrencyState()}), tuning {status}.";
        }
    }

    public AdaptiveDataverseTargetTuningState GetDashboardState()
    {
        lock (_sync)
        {
            return new AdaptiveDataverseTargetTuningState(
                BatchSize,
                _autoBatchSize,
                BatchSizeState(),
                _maxBatchSize,
                MaxConcurrency,
                _autoConcurrency,
                ConcurrencyState(),
                _maxConcurrency,
                _stage.ToString(),
                _samples,
                SamplesPerCandidate);
        }
    }

    private string BatchSizeState() =>
        !_autoBatchSize
            ? "fixed"
            : _stage == TuningStage.BatchSize
                ? $"tuning candidate ({_samples}/{SamplesPerCandidate} samples)"
                : "complete";

    private string ConcurrencyState() =>
        !_autoConcurrency
            ? "fixed"
            : _stage switch
            {
                TuningStage.BatchSize => "waiting for batch-size tuning",
                TuningStage.Concurrency => $"tuning candidate ({_samples}/{SamplesPerCandidate} samples)",
                _ => "complete"
            };

    private void FinishCandidate()
    {
        var elapsed = Stopwatch.GetElapsedTime(_sampleStart, _sampleEnd).TotalSeconds;
        var throughput = _getPipelineThroughput is null
            ? elapsed > 0 ? _sampleRows / elapsed : 0
            : _getPipelineThroughput();
        var currentValue = _stage == TuningStage.BatchSize ? BatchSize : MaxConcurrency;
        var currentBestValue = _stage == TuningStage.BatchSize ? _bestBatchSize : _bestConcurrency;

        if (_bestThroughput == 0 || throughput >= _bestThroughput * ImprovementThreshold)
        {
            _bestThroughput = throughput;
            if (_stage == TuningStage.BatchSize)
            {
                _bestBatchSize = currentValue;
            }
            else
            {
                _bestConcurrency = currentValue;
            }
            _nonImprovingCandidates = 0;
        }
        else
        {
            _nonImprovingCandidates++;
        }

        var candidates = _stage == TuningStage.BatchSize ? _batchCandidates : _concurrencyCandidates;
        ref var candidateIndex = ref (_stage == TuningStage.BatchSize
            ? ref _batchIndex
            : ref _concurrencyIndex);
        candidateIndex++;
        var stageFinished = _nonImprovingCandidates >= 2 || candidateIndex >= candidates.Length;
        if (stageFinished)
        {
            if (_stage == TuningStage.BatchSize)
            {
                BatchSize = _bestBatchSize > 0 ? _bestBatchSize : currentBestValue;
                if (_autoConcurrency)
                {
                    StartConcurrencyStage();
                }
                else
                {
                    _stage = TuningStage.Complete;
                }
            }
            else
            {
                MaxConcurrency = _bestConcurrency > 0 ? _bestConcurrency : currentBestValue;
                _stage = TuningStage.Complete;
            }
        }
        else if (_stage == TuningStage.BatchSize)
        {
            BatchSize = candidates[candidateIndex];
        }
        else
        {
            MaxConcurrency = candidates[candidateIndex];
        }

        _samples = 0;
        _sampleRows = 0;
        _sampleStart = long.MaxValue;
        _sampleEnd = 0;
        _transitionPending = false;
        _epoch++;
    }

    private void StartConcurrencyStage()
    {
        if (_persistedConcurrency.HasValue)
        {
            MaxConcurrency = _persistedConcurrency.Value;
            _stage = TuningStage.Complete;
            return;
        }

        _stage = TuningStage.Concurrency;
        _bestThroughput = 0;
        _bestConcurrency = 0;
        _nonImprovingCandidates = 0;
        _concurrencyIndex = Array.IndexOf(_concurrencyCandidates, 1);
        MaxConcurrency = 1;
    }

    private TargetTuningLease CreateLease(bool countsForTuning) =>
        new(
            this,
            _epoch,
            BatchSize,
            _getTimestamp(),
            countsForTuning,
            _getPipelineThroughput?.Invoke() ?? 0);

    private static int[] CreateBatchCandidateOrder(IEnumerable<int> candidates, int startingBatchSize)
    {
        var values = candidates.ToHashSet();
        values.Add(startingBatchSize);
        return
        [
            startingBatchSize,
            .. values.Where(value => value > startingBatchSize).Order(),
            .. values.Where(value => value < startingBatchSize).OrderDescending()
        ];
    }

    private enum TuningStage
    {
        BatchSize,
        Concurrency,
        Complete
    }
}

internal sealed class TargetTuningLease(
    AdaptiveDataverseTargetTuner tuner,
    int epoch,
    int batchSize,
    long startedAt,
    bool countsForTuning,
    double pipelineRateAtStart) : IDisposable
{
    private int _released;

    public int Epoch { get; } = epoch;

    public int BatchSize { get; } = batchSize;

    public long StartedAt { get; } = startedAt;

    public bool CountsForTuning { get; } = countsForTuning;

    public double PipelineRateAtStart { get; } = pipelineRateAtStart;

    internal bool TryMarkCompleted() => Interlocked.Exchange(ref _released, 1) == 0;

    public void Dispose()
    {
        if (TryMarkCompleted())
        {
            tuner.ReleaseUncompletedLease();
        }
    }
}

internal sealed record AdaptiveDataverseTargetTuningState(
    int BatchSize,
    bool BatchSizeAuto,
    string BatchSizeState,
    int BatchSizeLimit,
    int MaxConcurrency,
    bool ConcurrencyAuto,
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
