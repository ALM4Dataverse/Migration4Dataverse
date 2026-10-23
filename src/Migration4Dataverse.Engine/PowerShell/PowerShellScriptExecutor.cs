using System.Runtime.CompilerServices;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Threading.Channels;
using Migration4Dataverse.Engine.Scheduling;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.PowerShell;

public sealed class PowerShellScriptExecutor : IDisposable
{
    private readonly RunspacePool _runspacePool;
    private bool _disposed;

    public PowerShellScriptExecutor(int maxRunspaces = 1, IEnumerable<string>? modulesToImport = null)
    {
        if (maxRunspaces < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRunspaces), "At least one runspace is required.");
        }

        var initialSessionState = InitialSessionState.CreateDefault2();
        var modules = modulesToImport?.Where(module => !string.IsNullOrWhiteSpace(module)).ToArray();
        if (modules is { Length: > 0 })
        {
            initialSessionState.ImportPSModule(modules);
        }

        _runspacePool = RunspaceFactory.CreateRunspacePool(initialSessionState);
        if (!_runspacePool.SetMaxRunspaces(maxRunspaces))
        {
            _runspacePool.Dispose();
            throw new InvalidOperationException($"PowerShell could not set the runspace limit to {maxRunspaces}.");
        }

        _runspacePool.Open();
    }

    public IReadOnlyList<PSObject> Invoke(string script, IReadOnlyList<object?>? arguments = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(script);

        using var powerShell = System.Management.Automation.PowerShell.Create();
        powerShell.RunspacePool = _runspacePool;
        powerShell.AddScript(script, useLocalScope: true);
        if (arguments is not null)
        {
            foreach (var argument in arguments)
            {
                powerShell.AddArgument(argument);
            }
        }

        var output = powerShell.Invoke();
        if (powerShell.HadErrors)
        {
            var errors = powerShell.Streams.Error.Select(error => error.ToString()).ToArray();
            throw new PowerShellScriptException(errors);
        }

        return output.ToArray();
    }

    public Func<StoredBatch, CancellationToken, Task<MigrationBatchResult>> CreateMigrationBatchHandler(
        string script,
        object?[]? boundArguments = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(script);

        var arguments = boundArguments?.ToArray() ?? Array.Empty<object?>();
        return async (batch, cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(batch);
            var invocationArguments = new object?[arguments.Length + 1];
            invocationArguments[0] = batch;
            Array.Copy(arguments, 0, invocationArguments, 1, arguments.Length);
            var output = await InvokeAsync(script, invocationArguments, cancellationToken).ConfigureAwait(false);

            if (output.Count != 1 || output[0].BaseObject is not MigrationBatchResult result)
            {
                throw new InvalidOperationException(
                    $"PowerShell batch handler must return exactly one {nameof(MigrationBatchResult)} object; " +
                    $"it returned {output.Count} output object(s).");
            }

            return result;
        };
    }

    public Func<StoredBatch, IDisposable?, CancellationToken, Task<MigrationBatchResult>>
        CreateMigrationBatchHandlerWithExecutionLease(
            string script,
            Func<StoredBatch, IDisposable?, object?[]> boundArgumentsFactory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        ArgumentNullException.ThrowIfNull(boundArgumentsFactory);

        return async (batch, executionLease, cancellationToken) =>
        {
            ArgumentNullException.ThrowIfNull(batch);
            var arguments = boundArgumentsFactory(batch, executionLease)
                ?? throw new InvalidOperationException("The batch argument factory returned null.");
            var invocationArguments = new object?[arguments.Length + 1];
            invocationArguments[0] = batch;
            Array.Copy(arguments, 0, invocationArguments, 1, arguments.Length);
            var output = await InvokeAsync(script, invocationArguments, cancellationToken).ConfigureAwait(false);

            if (output.Count != 1 || output[0].BaseObject is not MigrationBatchResult result)
            {
                throw new InvalidOperationException(
                    $"PowerShell batch handler must return exactly one {nameof(MigrationBatchResult)} object; " +
                    $"it returned {output.Count} output object(s).");
            }

            return result;
        };
    }

    public Task<IReadOnlyList<PSObject>> InvokeAsync(
        string script,
        IReadOnlyList<object?>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(script);

        var invocationArguments = arguments?.ToArray() ?? Array.Empty<object?>();
        return Task.Run<IReadOnlyList<PSObject>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var powerShell = System.Management.Automation.PowerShell.Create();
            powerShell.RunspacePool = _runspacePool;
            powerShell.AddScript(script, useLocalScope: true);
            foreach (var argument in invocationArguments)
            {
                powerShell.AddArgument(argument);
            }

            var asyncResult = powerShell.BeginInvoke();
            using var cancellationRegistration = cancellationToken.Register(
                static state => ((System.Management.Automation.PowerShell)state!).Stop(),
                powerShell);
            PSDataCollection<PSObject> output;
            try
            {
                output = powerShell.EndInvoke(asyncResult);
            }
            catch (PipelineStoppedException exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "PowerShell batch execution was cancelled.",
                    exception,
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (powerShell.HadErrors)
            {
                var errors = powerShell.Streams.Error.Select(error => error.ToString()).ToArray();
                throw new PowerShellScriptException(errors);
            }

            return output.ToArray();
        });
    }

    public async IAsyncEnumerable<PSObject> InvokeStreamingAsync(
        string script,
        IReadOnlyList<object?>? arguments = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(script);

        var channel = Channel.CreateBounded<PSObject>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
        using var powerShell = System.Management.Automation.PowerShell.Create();
        powerShell.RunspacePool = _runspacePool;
        powerShell.AddScript(script, useLocalScope: true);

        var invocationArguments = arguments?.ToList() ?? new List<object?>();
        invocationArguments.Add(new Action<PSObject>(record =>
            channel.Writer.WriteAsync(record, cancellationToken).AsTask().GetAwaiter().GetResult()));
        foreach (var argument in invocationArguments)
        {
            powerShell.AddArgument(argument);
        }

        var asyncResult = powerShell.BeginInvoke();
        using var cancellationRegistration = cancellationToken.Register(
            static state => ((System.Management.Automation.PowerShell)state!).Stop(),
            powerShell);
        var completion = Task.Run(() =>
        {
            try
            {
                powerShell.EndInvoke(asyncResult);
                cancellationToken.ThrowIfCancellationRequested();
                if (powerShell.HadErrors)
                {
                    throw new PowerShellScriptException(
                        powerShell.Streams.Error.Select(error => error.ToString()).ToArray());
                }
                channel.Writer.TryComplete();
            }
            catch (Exception exception)
            {
                channel.Writer.TryComplete(exception);
            }
        });

        try
        {
            await foreach (var record in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return record;
            }
        }
        finally
        {
            channel.Writer.TryComplete();
            if (!completion.IsCompleted)
            {
                powerShell.Stop();
            }
            await completion.ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _runspacePool.Close();
        _runspacePool.Dispose();
        _disposed = true;
    }
}

public sealed class PowerShellScriptException : Exception
{
    public PowerShellScriptException(IReadOnlyList<string> errorMessages)
        : base(string.Join(Environment.NewLine, errorMessages))
    {
        ErrorMessages = errorMessages;
    }

    public IReadOnlyList<string> ErrorMessages { get; }
}
