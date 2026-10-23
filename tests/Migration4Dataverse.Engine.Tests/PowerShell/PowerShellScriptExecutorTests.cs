using Migration4Dataverse.Engine.PowerShell;
using Migration4Dataverse.Engine.Scheduling;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.Tests.PowerShell;

public sealed class PowerShellScriptExecutorTests
{
    [Fact]
    public void InvokesPowerShellScriptWithArguments()
    {
        using var executor = new PowerShellScriptExecutor(maxRunspaces: 2);

        var output = executor.Invoke("param($value) $value * 2", new object?[] { 21 });

        var result = Assert.Single(output);
        Assert.Equal(42, result.BaseObject);
    }

    [Fact]
    public void SurfacesNonTerminatingPowerShellErrors()
    {
        using var executor = new PowerShellScriptExecutor();

        var exception = Assert.Throws<PowerShellScriptException>(() => executor.Invoke("Write-Error 'script failed'"));

        Assert.Contains("script failed", exception.Message, StringComparison.Ordinal);
        Assert.NotEmpty(exception.ErrorMessages);
    }

    [Fact]
    public async Task MigrationBatchHandlerPassesTheBatchAndReturnsItsTypedResult()
    {
        using var executor = new PowerShellScriptExecutor();
        var handler = executor.CreateMigrationBatchHandler(
            """
            param($batch, $suffix)
            if ($batch.PayloadJson -ne '{"value":1}') {
                throw 'The handler received the wrong batch payload.'
            }
            [Migration4Dataverse.Engine.Scheduling.MigrationBatchResult]::new(
                ('{0}:{1}' -f $batch.BatchId, $suffix),
                $false,
                $null,
                $null)
            """,
            new object?[] { "bound" });
        var now = DateTimeOffset.UtcNow;
        var batch = new StoredBatch(
            "batch-id",
            "run-id",
            "Accounts",
            0,
            null,
            """{"value":1}""",
            MigrationBatchState.InFlight,
            1,
            "worker",
            now.AddMinutes(1),
            null,
            null,
            now,
            now);

        var result = await handler(batch, CancellationToken.None);

        Assert.Equal("batch-id:bound", result.OutcomesJson);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public async Task CancelsRunningPowerShellInvocation()
    {
        using var executor = new PowerShellScriptExecutor();
        using var started = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var invocation = executor.InvokeAsync(
            """
            param($started)
            $started.Set()
            Start-Sleep -Seconds 30
            """,
            new object?[] { started },
            cancellation.Token);

        Assert.True(started.Wait(TimeSpan.FromSeconds(10)), "PowerShell did not start the test script.");
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => invocation.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
