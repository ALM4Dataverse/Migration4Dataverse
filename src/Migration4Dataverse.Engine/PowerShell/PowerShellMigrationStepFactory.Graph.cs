using System.Collections;
using System.Data.Common;
using System.Globalization;
using System.Management.Automation;
using Migration4Dataverse.Engine.Connectors;
using Migration4Dataverse.Engine.Scheduling;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.PowerShell;

internal static partial class PowerShellMigrationStepFactory
{
    public static bool IsMigrationStep(PSObject step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return GetProperty(step, "Type") is not null && GetProperty(step, "Id") is not null;
    }

    public static IEnumerable<string> GetRequiredModulesForGraph(IEnumerable<PSObject> steps)
    {
        var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
        {
            var type = GetRequiredString(GetPropertiesAsMap(step), "Type").ToLowerInvariant();
            var source = type == "source"
                ? GetMap(GetProperty(step, "Source"), $"Source for step '{GetString(step, "Id", true)}")!
                : null;
            var target = type == "target"
                ? GetMap(GetProperty(step, "Target"), $"Target for step '{GetString(step, "Id", true)}")!
                : null;
            var connector = source ?? target;
            if (connector is null)
            {
                continue;
            }

            var connectorType = GetRequiredString(connector, "Type").ToLowerInvariant();
            if (connectorType == "dataverse")
            {
                modules.Add("Rnwood.Dataverse.Data.PowerShell");
            }
            AddCustomModules(modules, connector, "Custom");
        }
        return modules;
    }

    public static MigrationStepSet CreateGraph(
        IEnumerable<PSObject> stepObjects,
        IDictionary connections,
        PowerShellScriptExecutor executor,
        int defaultBatchSize,
        bool captureDebugData = false,
        SqliteMigrationStore? store = null,
        int maxTargetConcurrency = 32)
    {
        ArgumentNullException.ThrowIfNull(stepObjects);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(defaultBatchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTargetConcurrency);

        var inputs = stepObjects
            .Select(step => ParseGraphStep(step, connections, defaultBatchSize))
            .ToArray();
        if (inputs.Length == 0)
        {
            throw new ArgumentException("At least one migration step must be provided.", nameof(stepObjects));
        }
        ValidateGraph(inputs);
        ValidateDatabaseSourceConnections(inputs);
        foreach (var databaseTarget in inputs.Where(input =>
                     input.Type == "target" && input.TargetType is "sqlite" or "duckdb"))
        {
            DatabaseBatchTargetWriter.PrepareTargetSchema(
                (DbConnection)databaseTarget.TargetConnection!,
                databaseTarget.TargetType!,
                databaseTarget.TableName!,
                databaseTarget.Id,
                databaseTarget.TargetOptionsJson ?? "{}");
        }

        var consumers = inputs
            .SelectMany(step => step.Inputs.Select(input => (InputId: input, Consumer: step)))
            .GroupBy(edge => edge.InputId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(edge => edge.Consumer).ToArray(),
                StringComparer.Ordinal);
        var joinConsumersByInput = inputs
            .Where(step => step.Type == "join")
            .SelectMany(join => join.InputDefinitions.Select((input, index) => (
                InputId: input.Job,
                JoinStepId: join.Id,
                KeyColumns: index == 0
                    ? join.JoinKeys.Select(key => key.Child).ToArray()
                    : join.JoinKeys.Select(key => key.Parent).ToArray())))
            .GroupBy(entry => entry.InputId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => new Hashtable(StringComparer.Ordinal)
                {
                    ["JoinStepId"] = entry.JoinStepId,
                    ["KeyColumns"] = entry.KeyColumns
                }).ToArray(),
                StringComparer.Ordinal);
        var sourceKeyParents = inputs
            .SelectMany(step => step.RowDependencies.Select(dependency => dependency.Job))
            .ToHashSet(StringComparer.Ordinal);
        SourceKeyCoordinator? sourceKeyCoordinator = null;
        if (sourceKeyParents.Count > 0)
        {
            sourceKeyCoordinator = new SourceKeyCoordinator(
                store ?? throw new InvalidOperationException(
                    "Row-linked source dependencies require the migration store."));
        }
        StepCompletionCoordinator? stepCompletionCoordinator = null;
        if (inputs.Any(input => input.Type == "join"))
        {
            stepCompletionCoordinator = new StepCompletionCoordinator();
        }
        var customSources = new Dictionary<string, PowerShellSourceCallbackDefinition>(StringComparer.Ordinal);
        var dataverseSources = new Dictionary<string, PowerShellDataverseSourceDefinition>(StringComparer.Ordinal);
        foreach (var source in inputs.Where(step => step.Type == "source" && step.SourceType == "custom"))
        {
            var custom = source.SourceCustom
                ?? throw new InvalidOperationException($"Custom source step '{source.Id}' has no Custom definition.");
            customSources.Add(
                source.Id,
                new PowerShellSourceCallbackDefinition(
                    source.SourceConnection!,
                    custom.Identity,
                    custom.CallbackText,
                    custom.PageSize));
        }
        foreach (var source in inputs.Where(step => step.Type == "source" && step.SourceType == "dataverse"))
        {
            dataverseSources.Add(
                source.Id,
                new PowerShellDataverseSourceDefinition(
                    source.SourceConnection!,
                    source.DataverseSourceOptions!,
                    source.DataverseQueryType!));
        }

        var readerFactory = new PowerShellHierarchyRecordReaderFactory(executor, customSources, dataverseSources);
        var semaphores = new Dictionary<string, (SemaphoreSlim Semaphore, int Capacity)>(StringComparer.Ordinal);
        var targetTuners = new List<AdaptiveDataverseTargetTuner>();
        var definitions = new List<MigrationStepDefinition>(inputs.Length);
        try
        {
            foreach (var input in inputs)
            {
                Func<MigrationStepCheckpoint, CancellationToken, IAsyncEnumerable<MigrationWorkBatch>>? sourceReader = null;
                HierarchySourceLevel? sourceLevel = null;
                if (input.Type == "source" && input.RowDependencies.Length == 0)
                {
                    sourceLevel = CreateGraphSourceLevel(input);
                    sourceReader = new HierarchyBatchSource(
                        sourceLevel,
                        input.BatchSize,
                        readerFactory.CreateDelegate()).CreateReader();
                }
                else if (input.Type == "source" && input.RowDependencies.Length > 0)
                {
                    sourceLevel = CreateGraphSourceLevel(input);
                    var parents = input.RowDependencies
                        .Select(dependency => new RowLinkedParentDependency(
                            dependency.Job,
                            dependency.Keys[0].Child,
                            dependency.RowsPerParent))
                        .ToArray();
                    sourceReader = new RowLinkedSourceBatchSource(
                        store!,
                        sourceKeyCoordinator!,
                        sourceLevel,
                        parents,
                        input.BatchSize,
                        readerFactory.CreateDelegate()).CreateReader();
                }
                else if (input.Type == "join")
                {
                    if (store is null)
                    {
                        throw new InvalidOperationException(
                            $"Join step '{input.Id}' requires the migration store for bounded staging.");
                    }
                    sourceReader = new IndexedStreamingJoinBatchSource(
                        store,
                        stepCompletionCoordinator!,
                        input.Id,
                        input.InputDefinitions[0].Job,
                        input.InputDefinitions[1].Job,
                        input.JoinInputAs!,
                        input.JoinParentInputAs!,
                        input.JoinType!,
                        input.BatchSize,
                        preserveInputOrder: input.JoinOrder == "stable").CreateReader();
                }
                else if (input.ReadBatchesScriptText is not null)
                {
                    sourceReader = (checkpoint, cancellationToken) => ReadCallbackBatchesAsync(
                        executor,
                        input.ReadBatchesScriptText,
                        checkpoint,
                        input.CallbackSourceConnection,
                        cancellationToken);
                }

                var targetSemaphore = GetGraphTargetSemaphore(input, semaphores);
                AdaptiveDataverseTargetTuner? targetTuner = null;
                if (input.Type == "target" && input.TargetType == "dataverse")
                {
                    targetTuner = new AdaptiveDataverseTargetTuner(
                        Math.Min(input.BatchSize, 1000),
                        maxTargetConcurrency,
                        input.TargetBatchSizeAuto,
                        input.TargetConcurrencyAuto,
                        input.BatchSize,
                        input.TargetMaxConcurrency)
                    {
                        StepId = input.Id
                    };
                    targetTuners.Add(targetTuner);
                }
                var childContexts = consumers.TryGetValue(input.Id, out var childSteps)
                    ? childSteps.Where(child =>
                            child.Type != "join" &&
                            !(child.Type == "source" && child.RowDependencies.Length > 0))
                        .Select(child => new Hashtable(StringComparer.Ordinal)
                    {
                        ["StepId"] = child.Id,
                        ["BatchSize"] = child.BatchSize,
                        ["IsRowLinked"] = child.RowDependencies.Length > 0
                    }).ToArray()
                    : Array.Empty<Hashtable>();
                var joinInputConsumers = joinConsumersByInput.TryGetValue(input.Id, out var joinSteps)
                    ? joinSteps
                    : Array.Empty<Hashtable>();
                var context = new Hashtable(StringComparer.Ordinal)
                {
                    ["StepId"] = input.Id,
                    ["Type"] = input.Type,
                    ["IdProperty"] = input.IdProperty,
                    ["ScriptText"] = input.ScriptText ?? string.Empty,
                    ["TargetType"] = input.TargetType ?? string.Empty,
                    ["TableName"] = input.TableName ?? string.Empty,
                    ["TargetConnectionId"] = input.TargetConnectionId ?? string.Empty,
                    ["TargetPath"] = input.TargetPath ?? string.Empty,
                    ["TargetOptionsJson"] = input.TargetOptionsJson ?? "{}",
                    ["TargetCallbackText"] = input.TargetCustom?.CallbackText ?? string.Empty,
                    ["TargetSemaphore"] = targetSemaphore,
                    ["TargetTuner"] = targetTuner,
                    ["TransformType"] = input.TransformType ?? string.Empty,
                    ["TransformConnectionId"] = input.TransformConnectionId ?? string.Empty,
                    ["TransformSql"] = input.TransformSql ?? string.Empty,
                    ["TransformParametersJson"] = input.TransformParametersJson ?? "{}",
                    ["Store"] = store,
                    ["BatchSize"] = input.BatchSize,
                    ["Children"] = childContexts,
                    ["JoinInputConsumers"] = joinInputConsumers,
                    ["CaptureDebugData"] = captureDebugData
                };
                var handlerWithLease = input.Type == "callback"
                    ? executor.CreateMigrationBatchHandlerWithExecutionLease(
                        "param($batch, $callbackText, $sourceConnection, $targetConnection) " +
                        "$callback = [scriptblock]::Create($callbackText); " +
                        "& $callback $batch $sourceConnection $targetConnection",
                        (_, _) => new object?[]
                        {
                            input.CallbackScriptText!,
                            input.CallbackSourceConnection,
                            input.CallbackTargetConnection
                        })
                    : executor.CreateMigrationBatchHandlerWithExecutionLease(
                        NodeHandlerScript,
                        (_, executionLease) =>
                        {
                            var invocationContext = new Hashtable(StringComparer.Ordinal);
                            foreach (DictionaryEntry entry in context)
                            {
                                invocationContext.Add(entry.Key, entry.Value);
                            }
                            if (executionLease is not null)
                            {
                                invocationContext["ExecutionLease"] = executionLease;
                            }
                            return
                            [
                                input.SourceConnection,
                                input.TargetConnection,
                                input.TransformConnection,
                                invocationContext
                            ];
                        });
                Func<IDisposable?>? tryAcquireExecutionLease = targetTuner is not null
                    ? () => targetTuner.TryAcquire()
                    : targetSemaphore is not null
                        ? () => targetSemaphore.Wait(0)
                            ? new SemaphoreExecutionLease(targetSemaphore)
                            : null
                        : null;
                Func<StoredBatch, CancellationToken, Task<MigrationBatchResult>> handler =
                    (batch, cancellationToken) => handlerWithLease(batch, null, cancellationToken);
                definitions.Add(new MigrationStepDefinition(
                    input.Id,
                    input.Inputs,
                    sourceReader,
                    handler,
                    input.Type,
                    input.CompletionDependencies)
                {
                    ExpectedRecordCount = input.ExpectedRecordCount > 0
                        ? input.ExpectedRecordCount
                        : null,
                    ResolveExpectedRecordCountAsync = CreateExpectedRecordCountResolver(
                        input,
                        executor,
                        sourceLevel),
                    WaitForInputCompletion = input.Type != "join" && input.RowDependencies.Length == 0,
                    SourceKeyCoordinator = sourceKeyCoordinator,
                    SourceKeyProperty = sourceKeyParents.Contains(input.Id) ? input.SourceKey : null,
                    StepCompletionCoordinator = stepCompletionCoordinator,
                    TryAcquireExecutionLease = tryAcquireExecutionLease,
                    ExecuteBatchWithExecutionLeaseAsync = handlerWithLease
                });
            }
        }
        catch
        {
            foreach (var (semaphore, _) in semaphores.Values)
            {
                semaphore.Dispose();
            }
            targetTuners.Clear();
            throw;
        }

        return new MigrationStepSet(
            definitions,
            semaphores.Values.Select(value => value.Semaphore).ToArray(),
            targetTuners);
    }

    private static GraphStepInput ParseGraphStep(
        PSObject step,
        IDictionary connections,
        int defaultBatchSize)
    {
        ArgumentNullException.ThrowIfNull(step);
        var id = GetString(step, "Id", required: true)!;
        var type = GetString(step, "Type", required: true)!.ToLowerInvariant();
        if (type is "sort" or "merge")
        {
            throw new NotSupportedException(
                $"Migration step type '{type}' requires coordinated stream-wide execution and is not yet supported by Invoke-Migration.");
        }
        if (type is not ("source" or "target" or "transform" or "filter" or "append" or "join" or "callback"))
        {
            throw new ArgumentException($"Step '{id}' has unsupported type '{type}'.");
        }

        var inputDefinitions = ParseInputDefinitions(GetProperty(step, "Inputs"), id);
        var inputs = inputDefinitions.Select(input => input.Job).ToArray();
        var dependencyDefinitions = ParseDependencies(GetProperty(step, "DependsOn"), id);
        var joinType = GetString(step, "JoinType", required: type == "join")?.ToLowerInvariant();
        var configuredJoinOrder = type == "join"
            ? GetString(step, "JoinOrder", required: false)
            : null;
        var joinOrder = type == "join"
            ? (string.IsNullOrWhiteSpace(configuredJoinOrder)
                ? "ready"
                : configuredJoinOrder!.ToLowerInvariant())
            : null;
        var joinInputAs = type == "join" && inputDefinitions.Length > 0
            ? inputDefinitions[0].As
            : null;
        var joinParentInput = type == "join" && inputDefinitions.Length > 1
            ? inputDefinitions[1]
            : null;
        var joinKeys = joinParentInput?.Keys ?? Array.Empty<JoinKeyInput>();
        if (GetProperty(step, "Parents") is not null)
        {
            throw new ArgumentException(
                $"Step '{id}' must declare its join relationships in Inputs instead of declaring Parents.");
        }
        if (GetProperty(step, "Relationships") is not null)
        {
            throw new ArgumentException(
                $"Join step '{id}' must declare its relationship in the second Inputs entry; Relationships is no longer supported.");
        }
        var keyedInputs = inputDefinitions
            .Where(input => input.Keys.Length > 0)
            .ToArray();
        var rowDependencies = type == "source"
            ? keyedInputs
            : Array.Empty<GraphInputDefinition>();
        var completionDependencies = dependencyDefinitions
            .Select(dependency => dependency.StepId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (completionDependencies.Length > 0 && type is not ("source" or "join"))
        {
            throw new ArgumentException(
                $"Completion dependencies can only be declared on source-driven steps; '{id}' is a '{type}' step.");
        }
        if (keyedInputs.Length > 0 && type is not ("source" or "join"))
        {
            throw new ArgumentException($"Keyed Inputs can only be declared on source steps or joins; '{id}' is a '{type}' step.");
        }
        if (type == "join" && inputDefinitions.Length != 2)
        {
            throw new ArgumentException($"Join step '{id}' must declare exactly two Inputs: the child first and its keyed parent second.");
        }
        if (type == "join" && joinType is not ("inner" or "left"))
        {
            throw new ArgumentException($"Join step '{id}' JoinType must be 'inner' or 'left'.");
        }
        if (type == "join" && joinOrder is not ("ready" or "stable"))
        {
            throw new ArgumentException($"Join step '{id}' JoinOrder must be 'ready' or 'stable'.");
        }
        if (type != "join" && GetProperty(step, "JoinOrder") is not null)
        {
            throw new ArgumentException($"JoinOrder can only be configured on join steps; '{id}' is a '{type}' step.");
        }
        if (rowDependencies.Length > 0)
        {
            if (inputDefinitions.Any(input => input.Keys.Length != 1))
            {
                throw new ArgumentException(
                    $"Each keyed Input on source step '{id}' must declare exactly one Child/Parent key pair.");
            }
        }
        if (type == "source" &&
            inputDefinitions.Any(input => input.Keys.Length == 0 && inputDefinitions.Any(keyed => keyed.Keys.Length > 0)))
        {
            throw new ArgumentException(
                $"Every Input on row-linked source step '{id}' must declare a Child/Parent key pair.");
        }
        if (type == "join" &&
            (string.IsNullOrWhiteSpace(joinInputAs) ||
             string.IsNullOrWhiteSpace(joinParentInput?.As) ||
             joinKeys.Length == 0))
        {
            throw new ArgumentException(
                $"Join step '{id}' requires As on both Inputs and Keys on its second, parent Input.");
        }
        if (type == "join" && inputDefinitions[0].Keys.Length != 0)
        {
            throw new ArgumentException(
                $"Join step '{id}' must declare its Keys on the second, parent Input.");
        }
        var batchSize = GetInt(GetPropertiesAsMap(step), "BatchSize", defaultBatchSize);
        if (batchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultBatchSize), $"BatchSize for step '{id}' must be positive.");
        }

        var sourceType = string.Empty;
        var sourcePath = string.Empty;
        var sourceKey = string.Empty;
        var idProperty = string.Empty;
        var sourceOptionsJson = "{}";
        long expectedRecordCount = 0;
        string? sourceConnectionId = null;
        object? sourceConnection = null;
        CustomConnector? sourceCustom = null;
        IDictionary? dataverseSourceOptions = null;
        string? dataverseQueryType = null;
        string? callbackScriptText = null;
        string? readBatchesScriptText = null;
        object? callbackSourceConnection = null;
        object? callbackTargetConnection = null;
        if (type == "source")
        {
            var source = GetMap(GetProperty(step, "Source"), $"Source for step '{id}'")!;
            sourceType = GetRequiredString(source, "Type").ToLowerInvariant();
            if (sourceType is not ("jsonl" or "csv" or "parquet" or "custom" or "dataverse" or "sqlite" or "duckdb"))
            {
                throw new ArgumentException($"Source connector type '{sourceType}' for step '{id}' is not supported.");
            }
            sourceConnectionId = GetRequiredString(source, "ConnectionId");
            sourceConnection = ResolveConnection(connections, sourceConnectionId, id, "source");
            if (sourceType is "sqlite" or "duckdb")
            {
                if (sourceConnection is not DbConnection databaseConnection)
                {
                    throw new ArgumentException(
                        $"Database source step '{id}' requires a DbConnection in -Connections.");
                }
                DatabaseConnectorSupport.ValidateConnection(sourceType, databaseConnection, $"source '{id}'");
                if (!string.IsNullOrWhiteSpace(GetString(source, "Path", required: false)))
                {
                    throw new ArgumentException($"Database source step '{id}' must not declare a Path.");
                }
            }
            sourcePath = ResolveConnectorPath(
                sourceConnection,
                GetString(source, "Path", required: sourceType is "jsonl" or "csv" or "parquet") ?? string.Empty,
                sourceType,
                $"Source path for step '{id}'");
            if (sourceType == "dataverse")
            {
                if (!string.IsNullOrWhiteSpace(GetString(source, "Path", required: false)))
                {
                    throw new ArgumentException($"Dataverse source step '{id}' must not declare a Path.");
                }
                dataverseSourceOptions = GetMap(
                    GetProperty(source, "Options"),
                    $"Options for Dataverse source step '{id}'",
                    required: false);
                dataverseQueryType = ValidateDataverseSourceOptions(dataverseSourceOptions, id);
            }
            sourceKey = GetRequiredString(source, "Key");
            idProperty = GetString(source, "Id", required: false) ?? string.Empty;
            sourceOptionsJson = SerializeJson(GetProperty(source, "Options"));
            if (sourceType is "sqlite" or "duckdb")
            {
                var databaseSourceOptions = DatabaseSourceOptions.FromJson(sourceOptionsJson, id);
                databaseSourceOptions.Validate(id);
                if (databaseSourceOptions.CountQuery is not null)
                {
                    ValidateReadOnlySql(
                        databaseSourceOptions.CountQuery,
                        id,
                        "Database count query for source step");
                }
            }
            expectedRecordCount = GetLong(source, "ExpectedRecordCount", 0);
            if (sourceType == "custom")
            {
                sourceCustom = ParseCustomConnector(GetProperty(source, "Custom"), id, "source");
            }
        }

        string? targetType = null;
        string? tableName = null;
        string? targetPath = null;
        string? targetOptionsJson = null;
        string? targetConnectionId = null;
        object? targetConnection = null;
        var targetMaxConcurrency = 1;
        var targetConcurrencyAuto = false;
        var targetBatchSizeAuto = false;
        var targetMatchOnGroups = Array.Empty<string[]>();
        CustomConnector? targetCustom = null;
        if (type == "target")
        {
            var target = GetMap(GetProperty(step, "Target"), $"Target for step '{id}'")!;
            targetType = GetRequiredString(target, "Type").ToLowerInvariant();
            if (targetType is not ("dataverse" or "jsonl" or "csv" or "parquet" or "custom" or "sqlite" or "duckdb"))
            {
                throw new ArgumentException($"Target connector type '{targetType}' for step '{id}' is not supported.");
            }
            if (targetType == "dataverse")
            {
                var concurrencySetting = ParseTargetTuningSetting(
                    target, "MaxConcurrency", 1, defaultAuto: true, id);
                targetMaxConcurrency = concurrencySetting.Value;
                targetConcurrencyAuto = concurrencySetting.Auto;
                if (!targetConcurrencyAuto && targetMaxConcurrency < 1)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(targetMaxConcurrency),
                        $"MaxConcurrency for Dataverse target step '{id}' must be positive.");
                }
            }
            else if (GetProperty(target, "MaxConcurrency") is not null)
            {
                throw new ArgumentException(
                    $"MaxConcurrency is only supported for Dataverse target steps; '{id}' uses '{targetType}'.");
            }
            targetConnectionId = GetRequiredString(target, "ConnectionId");
            targetConnection = ResolveConnection(connections, targetConnectionId, id, "target")
                ?? throw new ArgumentException($"Target connection '{targetConnectionId}' for step '{id}' resolved to null.");
            if (targetType is "sqlite" or "duckdb")
            {
                if (targetConnection is not DbConnection databaseConnection)
                {
                    throw new ArgumentException(
                        $"Database target step '{id}' requires a DbConnection in -Connections.");
                }
                DatabaseConnectorSupport.ValidateConnection(targetType, databaseConnection, $"target '{id}'");
                if (!string.IsNullOrWhiteSpace(GetString(target, "Path", required: false)))
                {
                    throw new ArgumentException($"Database target step '{id}' must not declare a Path.");
                }
            }
            targetPath = ResolveConnectorPath(
                targetConnection,
                GetString(target, "Path", required: targetType is "jsonl" or "csv" or "parquet") ?? string.Empty,
                targetType,
                $"Target path for step '{id}'");
            var targetOptions = GetMap(
                GetProperty(target, "Options"),
                $"Options for target step '{id}'",
                required: false);
            if (GetProperty(targetOptions, "RecordIdKeys") is not null)
            {
                throw new ArgumentException(
                    $"Target step '{id}' must not configure RecordIdKeys; parent records are captured automatically for Join relationships from MatchOn.");
            }
            targetOptionsJson = SerializeJson(targetOptions);
            targetMatchOnGroups = ParseStringGroups(GetProperty(targetOptions, "MatchOn"));
            tableName = targetType == "dataverse"
                ? GetRequiredString(target, "Table")
                : GetString(target, "Table", required: false) ?? string.Empty;
            if (targetType is "sqlite" or "duckdb" && string.IsNullOrWhiteSpace(tableName))
            {
                throw new ArgumentException($"Database target step '{id}' requires a Table.");
            }
            var operation = GetString(target, "Operation", required: false);
            var expectedOperation = targetType == "dataverse" ? "Upsert" : "Write";
            if (operation is not null && !string.Equals(operation, expectedOperation, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Target operation for step '{id}' must be '{expectedOperation}'.");
            }
            if (targetType == "dataverse")
            {
                var batchSetting = ParseTargetTuningSetting(
                    target, "BatchSize", batchSize, defaultAuto: true, id);
                batchSize = batchSetting.Value;
                targetBatchSizeAuto = batchSetting.Auto;
                if (!targetBatchSizeAuto && batchSize < 1)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(defaultBatchSize),
                        $"Target batch size for step '{id}' must be positive.");
                }
            }
            else
            {
                batchSize = GetInt(target, "BatchSize", batchSize);
                if (batchSize < 1)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(defaultBatchSize),
                        $"Target batch size for step '{id}' must be positive.");
                }
            }
            if (targetType == "custom")
            {
                targetCustom = ParseCustomConnector(GetProperty(target, "Custom"), id, "target");
            }
        }

        string? transformType = null;
        string? transformConnectionId = null;
        object? transformConnection = null;
        string? transformSql = null;
        string? transformParametersJson = null;
        string? scriptText = null;
        if (type is "transform" or "filter")
        {
            var transform = type == "transform"
                ? GetMap(GetProperty(step, "Transform"), $"Transform for step '{id}'", required: false)
                : null;
            if (transform is not null)
            {
                transformType = GetRequiredString(transform, "Type").ToLowerInvariant();
                if (transformType is not ("sqlite" or "duckdb"))
                {
                    throw new ArgumentException(
                        $"Transform connector type '{transformType}' for step '{id}' is not supported.");
                }
                var mode = GetString(transform, "Mode", required: false);
                if (mode is not null && !string.Equals(mode, "per_row", StringComparison.OrdinalIgnoreCase))
                {
                    throw new NotSupportedException(
                        $"Database transform mode '{mode}' for step '{id}' is not yet supported; use 'per_row'.");
                }
                transformConnectionId = GetRequiredString(transform, "ConnectionId");
                transformConnection = ResolveConnection(connections, transformConnectionId, id, "transform");
                if (transformConnection is not DbConnection databaseConnection)
                {
                    throw new ArgumentException(
                        $"Database transform step '{id}' requires a DbConnection in -Connections.");
                }
                DatabaseConnectorSupport.ValidateConnection(
                    transformType,
                    databaseConnection,
                    $"transform '{id}'");
                transformSql = GetString(transform, "Sql", required: false);
                var transformParameters = GetProperty(transform, "Parameters");
                if (transformSql is null && transformParameters is not null)
                {
                    throw new ArgumentException(
                        $"Transform.Parameters for step '{id}' requires Transform.Sql.");
                }
                if (transformSql is not null &&
                    transformParameters is IDictionary parameterMap &&
                    parameterMap is not System.Collections.Specialized.IOrderedDictionary)
                {
                    throw new ArgumentException(
                        $"DuckDB Transform.Parameters for step '{id}' must be an ordered hashtable.");
                }
                transformParametersJson = SerializeJson(transformParameters);
                if (transformSql is not null)
                {
                    if (transformType != "duckdb")
                    {
                        throw new ArgumentException($"Only DuckDB transforms can declare Sql; step '{id}' uses '{transformType}'.");
                    }

                    if (GetProperty(step, "Script") is not null)
                    {
                        throw new ArgumentException(
                            $"DuckDB transform step '{id}' must use either Script or Transform.Sql, not both.");
                    }
                }
                else if (GetProperty(step, "Script") is null)
                {
                    throw new ArgumentException(
                        $"Database transform step '{id}' must declare Script or a DuckDB Transform.Sql statement.");
                }
            }
            if (GetProperty(step, "Script") is { } transformScript)
            {
                scriptText = NormalizeScriptText(
                    GetScriptBlock(GetPropertiesAsMap(step), "Script", required: true));
            }
            else if (transformType is null)
            {
                throw new ArgumentException($"Transform step '{id}' must declare Script.");
            }
        }
        if (type == "callback")
        {
            callbackScriptText = NormalizeScriptText(GetScriptBlock(step, "ExecuteBatch", required: true));
            var readBatches = GetProperty(step, "ReadBatches") is null
                ? null
                : GetScriptBlock(step, "ReadBatches", required: true);
            readBatchesScriptText = readBatches is null ? null : NormalizeScriptText(readBatches);
            var callbackSourceConnectionId = GetString(step, "SourceConnectionId", required: false);
            var callbackTargetConnectionId = GetString(step, "TargetConnectionId", required: false);
            callbackSourceConnection = string.IsNullOrWhiteSpace(callbackSourceConnectionId)
                ? null
                : ResolveConnection(connections, callbackSourceConnectionId, id, "source");
            callbackTargetConnection = string.IsNullOrWhiteSpace(callbackTargetConnectionId)
                ? null
                : ResolveConnection(connections, callbackTargetConnectionId, id, "target");
            if (inputs.Length == 0 && readBatchesScriptText is null)
            {
                throw new ArgumentException($"Root callback step '{id}' must declare ReadBatches.");
            }
            if (inputs.Length > 0 && readBatchesScriptText is not null)
            {
                throw new ArgumentException($"Callback step '{id}' cannot declare ReadBatches when it has Inputs.");
            }
        }

        return new GraphStepInput(
            id,
            type,
            inputs,
            inputDefinitions,
            completionDependencies,
            joinType,
            joinOrder,
            joinInputAs,
            joinParentInput?.As,
            joinKeys,
            rowDependencies,
            batchSize,
            sourceType,
            sourcePath,
            sourceKey,
            idProperty,
            sourceOptionsJson,
            expectedRecordCount,
            sourceConnectionId,
            sourceConnection,
            sourceCustom,
            dataverseSourceOptions,
            dataverseQueryType,
            targetType,
            tableName,
            targetPath,
            targetOptionsJson,
            targetConnectionId,
            targetConnection,
            targetMaxConcurrency,
            targetConcurrencyAuto,
            targetBatchSizeAuto,
            targetMatchOnGroups,
            targetCustom,
            transformType,
            transformConnectionId,
            transformConnection,
            transformSql,
            transformParametersJson,
            scriptText,
            callbackScriptText,
            readBatchesScriptText,
            callbackSourceConnection,
            callbackTargetConnection);
    }

    private static GraphInputDefinition[] ParseInputDefinitions(object? value, string stepId)
    {
        if (value is null)
        {
            return Array.Empty<GraphInputDefinition>();
        }

        var entries = Unwrap(value) is string ||
                      GetMap(value, $"Input for step '{stepId}'", required: false) is not null
            ? new[] { value }
            : LanguagePrimitives.GetEnumerable(value)?.Cast<object?>().ToArray() ?? new[] { value };
        var definitions = new List<GraphInputDefinition>();
        foreach (var rawEntry in entries)
        {
            if (Unwrap(rawEntry) is string inputId)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(inputId);
                definitions.Add(new GraphInputDefinition(
                    inputId,
                    null,
                    Array.Empty<JoinKeyInput>(),
                    0));
                continue;
            }

            var definition = GetMap(rawEntry, $"Input for step '{stepId}'", required: true)!;
            var keysValue = GetProperty(definition, "Keys");
            var keys = keysValue is null
                ? Array.Empty<JoinKeyInput>()
                : ParseKeyPairs(keysValue, $"Input '{GetRequiredString(definition, "Job")}' on step '{stepId}'");
            var rowsPerParent = GetInt(definition, "RowsPerParent", 0);
            if (rowsPerParent < 0)
            {
                throw new ArgumentException(
                    $"RowsPerParent on Input for step '{stepId}' cannot be negative.");
            }

            definitions.Add(new GraphInputDefinition(
                GetRequiredString(definition, "Job"),
                GetString(definition, "As", required: false),
                keys,
                rowsPerParent));
        }

        return definitions.ToArray();
    }

    private static JoinKeyInput[] ParseKeyPairs(object value, string context)
    {
        var keyEntries = GetMap(value, $"Keys for {context}", required: false) is not null
            ? new[] { value }
            : LanguagePrimitives.GetEnumerable(value)?.Cast<object?>().ToArray() ?? new[] { value };
        var keyPairs = keyEntries
            .Select(rawKey =>
            {
                var key = GetMap(rawKey, $"Key pair for {context}", required: true)!;
                return new JoinKeyInput(
                    GetRequiredString(key, "Child"),
                    GetRequiredString(key, "Parent"));
            })
            .ToArray();
        if (keyPairs.Length == 0 ||
            keyPairs.Select(key => key.Child).Distinct(StringComparer.OrdinalIgnoreCase).Count() != keyPairs.Length ||
            keyPairs.Select(key => key.Parent).Distinct(StringComparer.OrdinalIgnoreCase).Count() != keyPairs.Length)
        {
            throw new ArgumentException(
                $"Keys for {context} must contain unique, non-empty Child/Parent pairs.");
        }
        return keyPairs;
    }

    private static string[][] ParseStringGroups(object? value)
    {
        if (value is null)
        {
            return Array.Empty<string[]>();
        }

        var groups = LanguagePrimitives.GetEnumerable(value)?.Cast<object?>().ToArray() ?? [value];
        return groups
            .Select(group => GetStrings(group))
            .Where(columns => columns.Length > 0)
            .ToArray();
    }

    private static void ValidateGraph(IReadOnlyList<GraphStepInput> steps)
    {
        var byId = new Dictionary<string, GraphStepInput>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (!byId.TryAdd(step.Id, step))
            {
                throw new ArgumentException($"Migration step ID '{step.Id}' is duplicated.");
            }
        }
        foreach (var step in steps)
        {
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (step.Type == "join" &&
                (!aliases.Add(step.JoinInputAs!) || !aliases.Add(step.JoinParentInputAs!)))
            {
                throw new ArgumentException($"Join input aliases on step '{step.Id}' must be distinct.");
            }
            if (step.Type == "join")
            {
                var parentInput = step.InputDefinitions[1];
                if (!byId.TryGetValue(parentInput.Job, out var parent))
                {
                    throw new ArgumentException(
                        $"The second Input on Join step '{step.Id}' references unknown step '{parentInput.Job}'.");
                }
                if (parent.Type == "target")
                {
                    if (parent.TargetType != "dataverse")
                    {
                        throw new ArgumentException(
                            $"The second Input on Join step '{step.Id}' cannot reference non-Dataverse Target '{parent.Id}'.");
                    }
                    var parentKeys = step.JoinKeys.Select(key => key.Parent).ToArray();
                    if (!parent.TargetMatchOnGroups.Any(group =>
                            group.Length == parentKeys.Length &&
                            group.SequenceEqual(parentKeys, StringComparer.OrdinalIgnoreCase)))
                    {
                        throw new ArgumentException(
                            $"Parent Target '{parent.Id}' must configure MatchOn with the same ordered key columns as the second Input on Join '{step.Id}'.");
                    }
                }
            }

            var requiredInputs = step.Type switch
            {
                "source" => step.RowDependencies.Length,
                "target" or "transform" or "filter" => 1,
                "join" => 2,
                "append" => 2,
                "callback" => 0,
                _ => throw new InvalidOperationException($"Unknown step type '{step.Type}'.")
            };
            if (step.Inputs.Distinct(StringComparer.Ordinal).Count() != step.Inputs.Length)
            {
                throw new ArgumentException($"Step '{step.Id}' references an input more than once.");
            }
            if (step.Type == "callback")
            {
                if ((step.Inputs.Length == 0) != (step.ReadBatchesScriptText is not null))
                {
                    throw new ArgumentException(
                        $"Callback step '{step.Id}' must either declare ReadBatches without Inputs or consume Inputs without ReadBatches.");
                }
            }
            else if (step.Inputs.Length < requiredInputs ||
                     (step.Type is not ("append" or "join") && step.Inputs.Length != requiredInputs))
            {
                throw new ArgumentException($"Step '{step.Id}' of type '{step.Type}' has an invalid number of Inputs.");
            }
            foreach (var inputId in step.Inputs)
            {
                if (string.Equals(step.Id, inputId, StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Step '{step.Id}' cannot consume itself.");
                }
                if (!byId.ContainsKey(inputId))
                {
                    throw new ArgumentException($"Step '{step.Id}' references unknown input step '{inputId}'.");
                }
            }
            if (step.Type == "source" && step.RowDependencies.Length == 0 && step.Inputs.Length != 0)
            {
                throw new ArgumentException($"Root source step '{step.Id}' must not declare Inputs.");
            }
            if (step.Type == "source" &&
                !step.Inputs.SequenceEqual(
                    step.RowDependencies.Select(dependency => dependency.Job),
                    StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    $"Every Input on row-linked source step '{step.Id}' must declare its keyed parent relationship.");
            }
            foreach (var dependencyId in step.CompletionDependencies)
            {
                if (string.Equals(step.Id, dependencyId, StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Step '{step.Id}' cannot depend on itself.");
                }
                if (!byId.ContainsKey(dependencyId))
                {
                    throw new ArgumentException(
                        $"Step '{step.Id}' references unknown completion dependency '{dependencyId}'.");
                }
            }
            if (step.Type == "join")
            {
                if (step.Inputs.Length != 2)
                {
                    throw new ArgumentException(
                       $"Join step '{step.Id}' must have exactly two Inputs.");
                }
            }
            if (step.Type == "target" &&
                steps.Any(consumer =>
                    consumer.Inputs.Contains(step.Id, StringComparer.Ordinal) &&
                    (consumer.Type != "join" ||
                    !string.Equals(
                        consumer.InputDefinitions[1].Job,
                        step.Id,
                        StringComparison.Ordinal))))
            {
                throw new ArgumentException(
                    $"Target step '{step.Id}' can only be consumed by Join relationship inputs.");
            }
            foreach (var rowDependency in step.RowDependencies)
            {
                if (!byId.TryGetValue(rowDependency.Job, out var parent) ||
                    parent.Type != "source" ||
                    !string.Equals(
                       parent.SourceKey,
                       rowDependency.Keys[0].Parent,
                       StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                       $"Keyed Input '{rowDependency.Job}' on source step '{step.Id}' must name an existing Source and its configured Source.Key as Parent.");
                }
            }
        }
        var hasReader = steps.Any(step => step.Type == "source" ||
                                          step.Type == "callback" &&
                                          step.Inputs.Length == 0 &&
                                          step.ReadBatchesScriptText is not null);
        var hasTerminal = steps.Any(step => step.Type == "target") ||
                          steps.Any(step => step.Type == "callback" &&
                                            !steps.Any(candidate =>
                                                candidate.Inputs.Contains(step.Id, StringComparer.Ordinal)));
        if (!hasReader || !hasTerminal)
        {
            throw new ArgumentException(
                "A migration graph must contain at least one source reader and one terminal Target or callback step.");
        }

        var visitStates = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            Visit(step);
        }

        var terminalIds = steps
            .Where(step => step.Type == "target" ||
                           step.Type == "callback" &&
                           !steps.Any(candidate => candidate.Inputs.Contains(step.Id, StringComparer.Ordinal)))
            .Select(step => step.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (!ReachesTarget(step.Id, new HashSet<string>(StringComparer.Ordinal)))
            {
                throw new ArgumentException($"Step '{step.Id}' does not contribute to a terminal Target or callback step.");
            }
        }

        void Visit(GraphStepInput step)
        {
            if (visitStates.TryGetValue(step.Id, out var state))
            {
                if (state == 1)
                {
                    throw new ArgumentException($"Migration step graph contains a cycle at '{step.Id}'.");
                }
                if (state == 2)
                {
                    return;
                }
            }
            visitStates[step.Id] = 1;
            foreach (var inputId in step.Inputs)
            {
                Visit(byId[inputId]);
            }
            foreach (var dependencyId in step.CompletionDependencies)
            {
                Visit(byId[dependencyId]);
            }
            visitStates[step.Id] = 2;
        }

        bool ReachesTarget(string stepId, HashSet<string> visited)
        {
            if (terminalIds.Contains(stepId))
            {
                return true;
            }
            if (!visited.Add(stepId))
            {
                return false;
            }
            return steps
                .Where(candidate => candidate.Inputs.Contains(stepId, StringComparer.Ordinal))
                .Any(candidate => ReachesTarget(candidate.Id, visited));
        }
    }

    private static void ValidateDatabaseSourceConnections(IReadOnlyList<GraphStepInput> steps)
    {
        foreach (var source in steps.Where(step =>
                     step.Type == "source" && step.SourceType is "sqlite" or "duckdb"))
        {
            var sourceConnection = source.SourceConnection;
            if (steps.Any(step =>
                    !ReferenceEquals(step, source) &&
                    (ReferenceEquals(step.SourceConnection, sourceConnection) ||
                     ReferenceEquals(step.TargetConnection, sourceConnection) ||
                     ReferenceEquals(step.TransformConnection, sourceConnection))))
            {
                throw new ArgumentException(
                    $"Database source connection instance for step '{source.Id}' must not be reused by another source, transform, or target step. Use separate connection instances.");
            }
        }
    }

    private static HierarchySourceLevel CreateGraphSourceLevel(GraphStepInput input)
    {
        return new HierarchySourceLevel(
            input.Id,
            input.SourceType,
            input.SourcePath,
            input.SourceKey,
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: input.ExpectedRecordCount,
            children: null,
            optionsJson: input.SourceOptionsJson,
            connectionId: input.SourceConnectionId,
            identity: input.SourceCustom?.Identity,
            databaseConnection: input.SourceConnection as DbConnection);
    }

    private static Func<CancellationToken, Task<long>>? CreateExpectedRecordCountResolver(
        GraphStepInput input,
        PowerShellScriptExecutor executor,
        HierarchySourceLevel? sourceLevel)
    {
        if (input.Type != "source" || input.ExpectedRecordCount > 0 || sourceLevel is null)
        {
            return null;
        }

        Func<CancellationToken, Task<long>>? resolver = null;
        if (input.SourceType == "parquet")
        {
            resolver = cancellationToken =>
                HierarchyRecordReaderFactory.GetMetadataRecordCountAsync(sourceLevel, cancellationToken);
        }
        else if (input.SourceType is "sqlite" or "duckdb")
        {
            var options = DatabaseSourceOptions.FromJson(input.SourceOptionsJson, input.Id);
            var connection = input.SourceConnection as DbConnection
                ?? throw new InvalidOperationException(
                    $"Database source step '{input.Id}' has no database connection for its count query.");
            resolver = cancellationToken =>
                DatabaseHierarchyRecordReader.CountRecordsAsync(
                    connection,
                    input.Id,
                    options,
                    cancellationToken);
        }
        else if (input.SourceType == "dataverse" &&
                 input.DataverseQueryType is "simple" or "fetchxml")
        {
            var definition = new PowerShellDataverseSourceDefinition(
                input.SourceConnection!,
                input.DataverseSourceOptions!,
                input.DataverseQueryType);
            resolver = cancellationToken =>
                PowerShellDataverseHierarchyRecordReader.CountRecordsAsync(
                    executor,
                    input.Id,
                    input.SourceKey,
                    definition,
                    cancellationToken);
        }
        else if (input.SourceType == "custom" && input.SourceCustom?.CountCallbackText is { } countCallbackText)
        {
            resolver = cancellationToken => ReadCustomSourceRecordCountAsync(
                executor,
                countCallbackText,
                input.SourceConnection!,
                input.Id,
                input.SourcePath,
                input.SourceOptionsJson,
                cancellationToken);
        }

        return resolver is null
            ? null
            : async cancellationToken =>
            {
                var count = await resolver(cancellationToken).ConfigureAwait(false);
                sourceLevel.ExpectedRecordCount = count;
                sourceLevel.HasExpectedRecordCount = true;
                return count;
            };
    }

    private static async Task<long> ReadCustomSourceRecordCountAsync(
        PowerShellScriptExecutor executor,
        string countCallbackText,
        object connection,
        string stepId,
        string path,
        string optionsJson,
        CancellationToken cancellationToken)
    {
        const string countScript = """
            param($callbackText, $connection, $stepId, $path, $optionsJson)
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'
            $options = ConvertFrom-Json -InputObject $optionsJson -AsHashtable -Depth 100 -ErrorAction Stop
            $callback = [scriptblock]::Create([string]$callbackText)
            & $callback $connection $stepId $path $options
            """;

        var output = await executor.InvokeAsync(
                countScript,
                new object?[] { countCallbackText, connection, stepId, path, optionsJson },
                cancellationToken)
            .ConfigureAwait(false);
        if (output.Count != 1)
        {
            throw new InvalidDataException(
                $"Custom source CountCallback for step '{stepId}' must return exactly one non-negative integer; " +
                $"it returned {output.Count} output object(s).");
        }

        var value = output[0].BaseObject;
        if (value is not (byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal))
        {
            throw new InvalidDataException(
                $"Custom source CountCallback for step '{stepId}' must return exactly one non-negative integer.");
        }

        decimal count;
        try
        {
            count = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (
            exception is InvalidCastException or FormatException or OverflowException)
        {
            throw new InvalidDataException(
                $"Custom source CountCallback for step '{stepId}' must return exactly one non-negative integer.",
                exception);
        }

        if (count < 0 || decimal.Truncate(count) != count || count > long.MaxValue)
        {
            throw new InvalidDataException(
                $"Custom source CountCallback for step '{stepId}' must return exactly one non-negative integer.");
        }

        return (long)count;
    }

    private static SemaphoreSlim? GetGraphTargetSemaphore(
        GraphStepInput step,
        IDictionary<string, (SemaphoreSlim Semaphore, int Capacity)> semaphores)
    {
        if (step.Type != "target" ||
            step.TargetType is not ("custom" or "sqlite" or "duckdb"))
        {
            return null;
        }
        var capacity = step.TargetType == "custom"
            ? step.TargetCustom?.MaxConcurrency ?? 1
            : 1;
        var key = step.TargetType is "sqlite" or "duckdb"
            ? $"database:{step.TargetConnectionId}"
            : $"{step.TargetType}:{step.TargetConnectionId}";
        if (semaphores.TryGetValue(key, out var existing))
        {
            if (existing.Capacity != capacity)
            {
                throw new ArgumentException(
                    $"Target steps using connection '{step.TargetConnectionId}' must declare consistent concurrency.");
            }
            return existing.Semaphore;
        }
        var semaphore = new SemaphoreSlim(capacity, capacity);
        semaphores.Add(key, (semaphore, capacity));
        return semaphore;
    }

    private sealed class SemaphoreExecutionLease(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }

    private static (int Value, bool Auto) ParseTargetTuningSetting(
        IDictionary target,
        string name,
        int defaultValue,
        bool defaultAuto,
        string stepId)
    {
        var raw = Unwrap(GetProperty(target, name));
        if (raw is null)
        {
            return (defaultValue, defaultAuto);
        }
        if (raw is string text && string.Equals(text, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return (defaultValue, true);
        }

        int value;
        try
        {
            value = LanguagePrimitives.ConvertTo<int>(raw);
        }
        catch (PSInvalidCastException exception)
        {
            throw new ArgumentException(
                $"Dataverse target '{stepId}' property '{name}' must be a positive integer or 'auto'.",
                name,
                exception);
        }
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(
                name,
                $"Dataverse target '{stepId}' property '{name}' must be positive.");
        }
        return (value, false);
    }

    private sealed record GraphStepInput(
        string Id,
        string Type,
        string[] Inputs,
        GraphInputDefinition[] InputDefinitions,
        string[] CompletionDependencies,
        string? JoinType,
        string? JoinOrder,
        string? JoinInputAs,
        string? JoinParentInputAs,
        JoinKeyInput[] JoinKeys,
        GraphInputDefinition[] RowDependencies,
        int BatchSize,
        string SourceType,
        string SourcePath,
        string SourceKey,
        string IdProperty,
        string SourceOptionsJson,
        long ExpectedRecordCount,
        string? SourceConnectionId,
        object? SourceConnection,
        CustomConnector? SourceCustom,
        IDictionary? DataverseSourceOptions,
        string? DataverseQueryType,
        string? TargetType,
        string? TableName,
        string? TargetPath,
        string? TargetOptionsJson,
        string? TargetConnectionId,
        object? TargetConnection,
        int TargetMaxConcurrency,
        bool TargetConcurrencyAuto,
        bool TargetBatchSizeAuto,
        string[][] TargetMatchOnGroups,
        CustomConnector? TargetCustom,
        string? TransformType,
        string? TransformConnectionId,
        object? TransformConnection,
        string? TransformSql,
        string? TransformParametersJson,
        string? ScriptText,
        string? CallbackScriptText,
        string? ReadBatchesScriptText,
        object? CallbackSourceConnection,
        object? CallbackTargetConnection);

    private sealed record GraphInputDefinition(
        string Job,
        string? As,
        JoinKeyInput[] Keys,
        int RowsPerParent);

    private sealed record JoinKeyInput(string Child, string Parent);

    private static async IAsyncEnumerable<MigrationWorkBatch> ReadCallbackBatchesAsync(
        PowerShellScriptExecutor executor,
        string scriptText,
        MigrationStepCheckpoint checkpoint,
        object? connection,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cursor = checkpoint.CursorJson;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = await executor.InvokeAsync(
                    "param($callbackText, $checkpoint, $connection, $cursor) " +
                    "$callback = [scriptblock]::Create($callbackText); " +
                    "& $callback $checkpoint $connection $cursor",
                    new object?[] { scriptText, checkpoint, connection, cursor },
                    cancellationToken)
                .ConfigureAwait(false);
            if (output.Count == 0)
            {
                yield break;
            }
            if (output.Count != 1 || output[0].BaseObject is not MigrationWorkBatch batch)
            {
                throw new InvalidOperationException(
                    "A callback step's ReadBatches script must emit at most one MigrationWorkBatch per call.");
            }

            yield return batch;
            if (batch.IsLast)
            {
                yield break;
            }
            if (string.IsNullOrWhiteSpace(batch.CursorAfterJson) ||
                string.Equals(cursor, batch.CursorAfterJson, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "A non-final source batch must advance its non-empty CursorAfterJson value.");
            }
            cursor = batch.CursorAfterJson;
        }
    }
}
