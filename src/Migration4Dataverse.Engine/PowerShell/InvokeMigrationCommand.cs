using System.Management.Automation;
using System.Collections;
using Microsoft.PowerShell.Commands;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Migration4Dataverse.Engine.Scheduling;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.PowerShell;

[Cmdlet(VerbsLifecycle.Invoke, "Migration")]
[OutputType(typeof(DependencySchedulerResult))]
public sealed class InvokeMigrationCommand : PSCmdlet
{
    private CancellationTokenSource? _cancellation;

    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string DatabasePath { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string RunName { get; set; } = string.Empty;

    [Parameter]
    public string? RunId { get; set; }

    [Parameter]
    public string? StepHash { get; set; }

    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public PSObject[] Steps { get; set; } = Array.Empty<PSObject>();

    [Parameter]
    public IDictionary Connections { get; set; } = new Hashtable(StringComparer.Ordinal);

    [Parameter]
    [ValidateRange(1, int.MaxValue)]
    public int MaxDegreeOfParallelism { get; set; } = 4;

    [Parameter]
    [ValidateRange(1, int.MaxValue)]
    public int MaxQueuedBatches { get; set; } = 64;

    [Parameter]
    [ValidateRange(1, int.MaxValue)]
    public int MaxQueuedBatchesPerStep { get; set; } = 32;

    [Parameter]
    [ValidateRange(1, int.MaxValue)]
    public int MaxAttempts { get; set; } = 5;

    [Parameter]
    public SwitchParameter DataverseNoRetryDiagnosticMode { get; set; }

    [Parameter]
    public string[] ModulesToImport { get; set; } = Array.Empty<string>();

    [Parameter]
    public ModuleSpecification[] RequiredModules { get; set; } = Array.Empty<ModuleSpecification>();

    [Parameter]
    public string[] ScriptsToLoad { get; set; } = Array.Empty<string>();

    [Parameter]
    public ScriptBlock[] SharedScripts { get; set; } = Array.Empty<ScriptBlock>();

    [Parameter]
    [ValidateRange(1, 1000)]
    public int BatchSize { get; set; } = 100;

    [Parameter]
    public SwitchParameter EnableDebugCapture { get; set; }

    [Parameter]
    public SwitchParameter WhatIf { get; set; }

    [Parameter]
    [ValidateRange(1, 3650)]
    public int DebugCaptureRetentionDays { get; set; } = 30;

    protected override void ProcessRecord()
    {
        if (Steps.Any(step => !PowerShellMigrationStepFactory.IsMigrationStep(step)))
        {
            throw new ArgumentException(
                "Every migration step must declare a node Type and Id. Use Source, Transform, Filter, Append, Target, or callback nodes.");
        }
        var scriptsToLoad = ScriptsToLoad
            ?? throw new ArgumentNullException(nameof(ScriptsToLoad));
        var sharedScriptBlocks = SharedScripts
            ?? throw new ArgumentNullException(nameof(SharedScripts));
        var modulesToImport = ModulesToImport
            ?? throw new ArgumentNullException(nameof(ModulesToImport));
        var helperFiles = scriptsToLoad.Select(path =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            var fullPath = Path.GetFullPath(path);
            return (Path: fullPath, Content: File.ReadAllText(fullPath));
        }).ToArray();
        var sharedScripts = sharedScriptBlocks
            .Select(script => script?.ToString()
                ?? throw new ArgumentException("SharedScripts cannot contain null entries.", nameof(SharedScripts)))
            .ToArray();
        var requiredModules = RequiredModules
            ?? throw new ArgumentNullException(nameof(RequiredModules));
        var schedulerOptions = new DependencySchedulerOptions
        {
            MaxDegreeOfParallelism = MaxDegreeOfParallelism,
            MaxQueuedBatches = MaxQueuedBatches,
            MaxQueuedBatchesPerStep = MaxQueuedBatchesPerStep,
            MaxAttempts = DataverseNoRetryDiagnosticMode.IsPresent ? 1 : MaxAttempts,
            CaptureDebugData = EnableDebugCapture.IsPresent || WhatIf.IsPresent,
            DebugCaptureRetentionDays = DebugCaptureRetentionDays
        };
        if (DataverseNoRetryDiagnosticMode.IsPresent)
        {
            WriteWarning(
                "Dataverse no-retry diagnostic mode is enabled. Dataverse and scheduler retries are disabled, " +
                "so transient failures can fail the migration.");
        }
        if (WhatIf.IsPresent)
        {
            WriteWarning(
                "WhatIf preview executes sources and transforms, captures planned target rows, and skips target writes " +
                "and batch callbacks. Caller-authored source readers and transform scripts may still have side effects.");
        }

        var modules = modulesToImport
            .Concat(PowerShellMigrationStepFactory.GetRequiredModulesForGraph(Steps))
            .Append(typeof(InvokeMigrationCommand).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        using var executor = new PowerShellScriptExecutor(
            MaxDegreeOfParallelism,
            modules,
            helperFiles.Select(file => file.Content).Concat(sharedScripts),
            requiredModules);
        var effectiveRunId = string.IsNullOrWhiteSpace(RunId) ? Guid.NewGuid().ToString("N") : RunId;
        var stepHash = string.IsNullOrWhiteSpace(StepHash)
            ? ComputeStepHash(
                Steps,
                modulesToImport,
                requiredModules,
                executor.ResolvedRequiredModules,
                helperFiles,
                sharedScripts)
            : StepHash;
        var effectiveStepHash = WhatIf.IsPresent ? $"{stepHash}:whatif-preview-v1" : stepHash;
        var databaseDirectory = Path.GetDirectoryName(Path.GetFullPath(DatabasePath));
        if (string.IsNullOrWhiteSpace(databaseDirectory))
        {
            throw new ArgumentException("The database path must have a parent directory.", nameof(DatabasePath));
        }
        Directory.CreateDirectory(databaseDirectory);
        var store = MigrationStoreFactory.Open(DatabasePath);
        var scheduler = new DependencyScheduler(store);
        using var stepSet = PowerShellMigrationStepFactory.CreateGraph(
            Steps,
            Connections,
            executor,
            BatchSize,
            EnableDebugCapture.IsPresent || WhatIf.IsPresent,
            store,
            MaxDegreeOfParallelism,
            DataverseNoRetryDiagnosticMode.IsPresent,
            WhatIf.IsPresent,
            effectiveRunId,
            DatabasePath);
        var configurationJson = MigrationConfigurationSnapshot.CreateJson(
            Steps,
            Connections,
            new MigrationSchedulerLimits(
                MaxDegreeOfParallelism,
                MaxQueuedBatches,
                MaxQueuedBatchesPerStep),
            WhatIf.IsPresent,
            modulesToImport,
            requiredModules,
            executor.ResolvedRequiredModules,
            helperFiles,
            sharedScripts);
        var connectionInstancesJson = MigrationConfigurationSnapshot.AppendConnectionInstanceSnapshot(
            null,
            MigrationConfigurationSnapshot.CaptureConnectionInstances(Connections, "Started"));
        _cancellation = new CancellationTokenSource();
        try
        {
            var execution = scheduler.ExecuteAsync(
                effectiveRunId,
                RunName,
                effectiveStepHash,
                stepSet.Steps,
                schedulerOptions,
                _cancellation.Token,
                configurationJson,
                connectionInstancesJson);
            while (!execution.IsCompleted)
            {
                Task.WhenAny(execution, Task.Delay(TimeSpan.FromMilliseconds(500)))
                    .GetAwaiter()
                    .GetResult();
                if (!execution.IsCompleted)
                {
                    WriteRunProgress(store, effectiveRunId);
                }
            }

            var result = execution.GetAwaiter().GetResult();
            stepSet.PersistTuningProfiles();
            foreach (var controller in stepSet.TargetControllers)
            {
                WriteVerbose(controller.GetSummary());
            }
            WriteProgress(new ProgressRecord(
                1,
                $"Migration: {RunName}",
                $"Completed · {result.TargetRowsSucceeded:N0} target rows succeeded · " +
                $"{result.TargetRowsFailed:N0} failed · {result.FilteredRows:N0} filtered across steps")
            {
                RecordType = ProgressRecordType.Completed,
                PercentComplete = 100
            });
            UpdateConnectionInstanceSnapshot(store, effectiveRunId, Connections, "Completed");
            WriteObject(result with { IsWhatIf = WhatIf.IsPresent });
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            UpdateConnectionInstanceSnapshot(store, effectiveRunId, Connections, "Cancelled");
            ThrowTerminatingError(new ErrorRecord(
                new OperationCanceledException("The migration was stopped."),
                "MigrationStopped",
                ErrorCategory.OperationStopped,
                effectiveRunId));
        }
        catch (Exception)
        {
            UpdateConnectionInstanceSnapshot(store, effectiveRunId, Connections, "Failed");
            throw;
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    private void UpdateConnectionInstanceSnapshot(
        IMigrationStore store,
        string runId,
        IDictionary connections,
        string phase)
    {
        try
        {
            var existing = store.GetDashboardRun(runId).ConnectionInstances?.GetRawText();
            var current = MigrationConfigurationSnapshot.CaptureConnectionInstances(connections, phase);
            store.UpdateConnectionInstanceSnapshots(
                runId,
                MigrationConfigurationSnapshot.AppendConnectionInstanceSnapshot(existing, current));
        }
        catch (Exception exception)
        {
            WriteWarning($"Connection instance details could not be saved for run '{runId}': {exception.Message}");
        }
    }

    protected override void StopProcessing()
    {
        _cancellation?.Cancel();
    }

    private void WriteRunProgress(IMigrationStore store, string runId)
    {
        MigrationRunSummary run;
        try
        {
            run = store.GetDashboardRun(runId);
        }
        catch (KeyNotFoundException)
        {
            WriteProgress(new ProgressRecord(1, $"Migration: {RunName}", "Initializing migration run")
            {
                PercentComplete = -1
            });
            return;
        }

        var targets = run.Steps
            .Where(step => string.Equals(step.Type, "target", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var processed = targets.Sum(step => step.InputRowsProcessed);
        var inFlight = targets.Sum(step => step.InputRowsInFlight);
        var pending = targets.Sum(step => step.InputRowsPending);
        var filtered = run.Steps.Sum(step => step.FilteredRows);
        var expected = targets.Length > 0 &&
                       targets.All(step => step.InputFinalized && step.ExpectedRows.HasValue)
            ? targets.Sum(step => step.ExpectedRows!.Value)
            : (long?)null;
        var percent = expected.HasValue
            ? expected.Value == 0
                ? 100
                : (int)Math.Clamp(processed * 100d / expected.Value, 0, 100)
            : -1;
        var totalDescription = targets.Length == 0
            ? "No target steps are configured"
            : expected.HasValue
                ? $"{processed:N0} of {expected.Value:N0} target rows processed"
                : $"{processed:N0} target rows processed · total " +
                  (targets.All(step => step.InputFinalized)
                      ? $"unavailable ({targets.Sum(step => step.BatchesWithUnknownInputRows):N0} uncountable batches)"
                      : "still growing");
        var activeSteps = run.Steps
            .Where(step => step.InFlightBatches > 0)
            .Select(step => step.Id)
            .Take(3)
            .ToArray();
        var operation = activeSteps.Length > 0
            ? $"Active: {string.Join(", ", activeSteps)}"
            : targets.Length == 0
                ? "Waiting for target work"
                : "Waiting for target batches";

        WriteProgress(new ProgressRecord(1, $"Migration: {RunName}", totalDescription)
        {
            PercentComplete = percent,
            CurrentOperation = $"{operation} · {inFlight:N0} in progress · {pending:N0} queued · {filtered:N0} filtered across steps"
        });
    }

    private static string ComputeStepHash(
        IEnumerable<PSObject> steps,
        IEnumerable<string> modulesToImport,
        IEnumerable<ModuleSpecification> requiredModules,
        IEnumerable<PSModuleInfo> resolvedRequiredModules,
        IEnumerable<(string Path, string Content)> helperFiles,
        IEnumerable<string> sharedScripts)
    {
        var stepArray = steps.ToArray();
        var normalizedSteps = stepArray.Select(NormalizeForHash).ToArray();
        var normalizedModules = modulesToImport.OrderBy(module => module, StringComparer.OrdinalIgnoreCase);
        var normalizedRequiredModules = requiredModules
            .OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase)
            .Select(module => new
            {
                module.Name,
                Version = module.Version?.ToString(),
                MaximumVersion = module.MaximumVersion?.ToString(),
                RequiredVersion = module.RequiredVersion?.ToString(),
                Guid = module.Guid?.ToString("D")
            })
            .ToArray();
        var normalizedResolvedModules = resolvedRequiredModules
            .OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase)
            .Select(module => new
            {
                module.Name,
                Version = module.Version.ToString(),
                Guid = module.Guid.ToString("D")
            })
            .ToArray();
        var normalizedHelperFiles = helperFiles.Select(file => new
        {
            file.Path,
            Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.Content))).ToLowerInvariant()
        }).ToArray();
        var normalizedSharedScripts = sharedScripts
            .Select(script => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script))).ToLowerInvariant())
            .ToArray();
        var hasKeyedInput = stepArray.Any(step =>
        {
            var type = GetStepProperty(step, "Type")?.ToString();
            return string.Equals(type, "join", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(type, "source", StringComparison.OrdinalIgnoreCase) &&
                   ContainsKeysDefinition(GetStepProperty(step, "Inputs"));
        });
        var shape = hasKeyedInput
            ? (object)new
            {
                KeyedInputContract = "streaming-join-order-v2",
                Steps = normalizedSteps,
                ModulesToImport = normalizedModules,
                RequiredModules = normalizedRequiredModules,
                ResolvedRequiredModules = normalizedResolvedModules,
                HelperFiles = normalizedHelperFiles,
                SharedScripts = normalizedSharedScripts
            }
            : new
            {
                Steps = normalizedSteps,
                ModulesToImport = normalizedModules,
                RequiredModules = normalizedRequiredModules,
                ResolvedRequiredModules = normalizedResolvedModules,
                HelperFiles = normalizedHelperFiles,
                SharedScripts = normalizedSharedScripts
            };
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(shape)));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool ContainsKeysDefinition(object? value)
    {
        if (value is PSObject psObject)
        {
            if (psObject.BaseObject is IDictionary baseDictionary)
            {
                return ContainsKeysDefinition(baseDictionary);
            }
            return psObject.Properties.Any(property =>
                string.Equals(property.Name, "Keys", StringComparison.OrdinalIgnoreCase) ||
                ContainsKeysDefinition(property.Value));
        }
        if (value is IDictionary dictionary)
        {
            return dictionary.Keys.Cast<object?>().Any(key =>
                       string.Equals(key?.ToString(), "Keys", StringComparison.OrdinalIgnoreCase)) ||
                   dictionary.Values.Cast<object?>().Any(ContainsKeysDefinition);
        }
        if (value is IEnumerable enumerable && value is not string)
        {
            return enumerable.Cast<object?>().Any(ContainsKeysDefinition);
        }
        return false;
    }

    private static object? GetStepProperty(PSObject step, string name)
    {
        if (step.BaseObject is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (string.Equals(entry.Key?.ToString(), name, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Value;
                }
            }
            return null;
        }

        return step.Properties[name]?.Value;
    }

    private static object? NormalizeForHash(object? value)
    {
        if (value is ScriptBlock scriptBlock)
        {
            return scriptBlock.ToString();
        }
        if (value is PSObject psObject)
        {
            if (psObject.BaseObject is IDictionary baseDictionary)
            {
                return NormalizeForHash(baseDictionary);
            }
            return psObject.Properties
                .Where(property => property.IsGettable && !property.MemberType.HasFlag(PSMemberTypes.Method))
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .ToDictionary(property => property.Name, property => NormalizeForHash(property.Value));
        }
        if (value is IDictionary dictionary)
        {
            return dictionary.Keys
                .Cast<object?>()
                .Select(key => key?.ToString() ?? string.Empty)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToDictionary(
                    key => key,
                    key => NormalizeForHash(dictionary[key]));
        }
        if (value is IEnumerable enumerable && value is not string)
        {
            return enumerable.Cast<object?>().Select(NormalizeForHash).ToArray();
        }
        return value;
    }

}
