using System.Data.Common;
using System.Runtime.CompilerServices;

namespace Migration4Dataverse.Engine.Connectors;

public static class DatabaseConnectionGate
{
    private static readonly ConditionalWeakTable<DbConnection, SemaphoreSlim> Gates = new();

    public static IDisposable Acquire(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var gate = Gates.GetValue(connection, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        return new Lease(gate);
    }

    public static async ValueTask<IDisposable> AcquireAsync(
        DbConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var gate = Gates.GetValue(connection, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}
