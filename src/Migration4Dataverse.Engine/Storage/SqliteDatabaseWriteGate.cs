using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Migration4Dataverse.Engine.Storage;

internal static class SqliteDatabaseWriteGate
{
    // SQLite allows one writer per database, so serialize engine mutations before opening write transactions.
    private static readonly Meter Meter = new("Migration4Dataverse.Engine.Storage");
    private static readonly Histogram<double> WaitDuration = Meter.CreateHistogram<double>(
        "migration.sqlite.write_gate.wait",
        "ms");
    private static readonly Histogram<double> HoldDuration = Meter.CreateHistogram<double>(
        "migration.sqlite.write_gate.hold",
        "ms");
    private static readonly ConcurrentDictionary<string, object> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static IDisposable Enter(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var path = Path.GetFullPath(databasePath);
        var gate = Gates.GetOrAdd(path, static _ => new object());
        var waitStarted = Stopwatch.GetTimestamp();
        Monitor.Enter(gate);
        var acquiredAt = Stopwatch.GetTimestamp();
        return new GateLease(
            gate,
            acquiredAt,
            Stopwatch.GetElapsedTime(waitStarted, acquiredAt).TotalMilliseconds);
    }

    private sealed class GateLease(object gate, long acquiredAt, double waitDurationMilliseconds) : IDisposable
    {
        private object? _gate = gate;

        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            if (gate is not null)
            {
                var holdDurationMilliseconds = Stopwatch.GetElapsedTime(acquiredAt).TotalMilliseconds;
                Monitor.Exit(gate);
                WaitDuration.Record(waitDurationMilliseconds);
                HoldDuration.Record(holdDurationMilliseconds);
            }
        }
    }
}
