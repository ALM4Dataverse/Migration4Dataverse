using System.Diagnostics;

namespace Migration4Dataverse.Engine.PowerShell;

internal sealed class PipelineThroughputMeter
{
    private static readonly TimeSpan Retention = TimeSpan.FromSeconds(60);
    private readonly object _sync = new();
    private readonly Queue<(long Timestamp, long Rows)> _samples = new();

    public void Record(long rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        if (rows == 0)
        {
            return;
        }

        lock (_sync)
        {
            var now = Stopwatch.GetTimestamp();
            _samples.Enqueue((now, rows));
            Prune(now);
        }
    }

    public double GetRowsPerSecond()
    {
        lock (_sync)
        {
            var now = Stopwatch.GetTimestamp();
            Prune(now);
            if (_samples.Count == 0)
            {
                return 0;
            }

            var first = _samples.Peek().Timestamp;
            var elapsedSeconds = Stopwatch.GetElapsedTime(first, now).TotalSeconds;
            return elapsedSeconds > 0
                ? _samples.Sum(sample => sample.Rows) / elapsedSeconds
                : 0;
        }
    }

    private void Prune(long now)
    {
        var oldestAllowed = now - (long)(Retention.TotalSeconds * Stopwatch.Frequency);
        while (_samples.TryPeek(out var sample) && sample.Timestamp < oldestAllowed)
        {
            _samples.Dequeue();
        }
    }
}
