using System.Collections;
using Migration4Dataverse.Engine.PowerShell;
using Migration4Dataverse.Engine.Scheduling;
using Migration4Dataverse.Engine.Storage;
using Microsoft.PowerShell.Commands;

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
    public void MakesSharedScriptFunctionsAvailableForEveryInvocation()
    {
        using var executor = new PowerShellScriptExecutor(
            maxRunspaces: 2,
            sharedScripts:
            [
                "function Get-SharedValue { param($suffix) \"shared-$suffix\" }"
            ]);

        var output = executor.Invoke("param($suffix) Get-SharedValue $suffix", ["value"]);

        Assert.Equal("shared-value", Assert.Single(output).BaseObject);
    }

    [Fact]
    public void LoadsRequiredModulesWithVersionSpecifications()
    {
        var requiredModule = new ModuleSpecification(new Hashtable
        {
            ["ModuleName"] = "Microsoft.PowerShell.Utility",
            ["ModuleVersion"] = "1.0"
        });
        using var executor = new PowerShellScriptExecutor(requiredModules: [requiredModule]);

        var output = executor.Invoke("(Get-Module Microsoft.PowerShell.Utility).Name");

        Assert.Contains(output, item => Equals(item.BaseObject, "Microsoft.PowerShell.Utility"));
    }

    [Fact]
    public void RejectsUnavailableRequiredModules()
    {
        var requiredModule = new ModuleSpecification(new Hashtable
        {
            ["ModuleName"] = "Migration4Dataverse.Missing.TestModule",
            ["ModuleVersion"] = "99.0"
        });

        Assert.Throws<InvalidOperationException>(
            () => new PowerShellScriptExecutor(requiredModules: [requiredModule]));
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
