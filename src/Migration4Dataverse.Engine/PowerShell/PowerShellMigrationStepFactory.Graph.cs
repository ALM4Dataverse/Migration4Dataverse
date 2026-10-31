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
    private const int DefaultDataverseTargetBatchSize = 10;

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
            var transform = type == "transform"
                ? GetMap(GetProperty(step, "Transform"), $"Transform for step '{GetString(step, "Id", true)}", required: false)
                : null;
            var connector = source ?? target;
            if (connector is not null)
            {
                var connectorType = GetRequiredString(connector, "Type").ToLowerInvariant();
                if (connectorType == "dataverse")
                {
                    modules.Add("Rnwood.Dataverse.Data.PowerShell");
                }
                AddCustomModules(modules, connector, "Custom");
            }
            if (transform is not null &&
                string.Equals(
                    GetString(transform, "Type", required: false),
                    "dataverse",
                    StringComparison.OrdinalIgnoreCase))
            {
                modules.Add("Rnwood.Dataverse.Data.PowerShell");
            }
        }
        return modules;
    }

    public static MigrationStepSet CreateGraph(
        IEnumerable<PSObject> stepObjects,
        IDictionary connections,
        PowerShellScriptExecutor executor,
        int defaultBatchSize,
        bool captureDebugData = false,
        IMigrationStore? store = null,
        int maxTargetConcurrency = 64,
        bool disableDataverseRetriesForDiagnostics = false,
        bool whatIf = false,
        string? runId = null,
        string? migrationDatabasePath = null)
    {
        var ownedConnections = new List<DbConnection>();
        var ownedDataversePools = new List<DataverseConnectionPool>();
        try
        {
            return CreateGraphCore(
                stepObjects,
                connections,
                executor,
                defaultBatchSize,
                captureDebugData,
                store,
                maxTargetConcurrency,
                disableDataverseRetriesForDiagnostics,
                whatIf,
                runId,
                migrationDatabasePath,
                ownedConnections,
                ownedDataversePools);
        }
        catch
        {
            foreach (var pool in ownedDataversePools)
            {
                pool.Dispose();
            }
            foreach (var connection in ownedConnections)
            {
                connection.Dispose();
            }
            throw;
        }
    }

    private static MigrationStepSet CreateGraphCore(
        IEnumerable<PSObject> stepObjects,
        IDictionary connections,
        PowerShellScriptExecutor executor,
        int defaultBatchSize,
        bool captureDebugData,
        IMigrationStore? store,
        int maxTargetConcurrency,
        bool disableDataverseRetriesForDiagnostics,
        bool whatIf,
        string? runId,
        string? migrationDatabasePath,
        List<DbConnection> ownedConnections,
        List<DataverseConnectionPool> ownedDataversePools)
    {
        ArgumentNullException.ThrowIfNull(stepObjects);
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(defaultBatchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTargetConcurrency);

        var graphSteps = stepObjects.ToArray();
        var objectStagingRoot = GetObjectStagingRoot(migrationDatabasePath, runId);
        var binaryStagingRoot = GetBinaryStagingRoot(migrationDatabasePath, runId);
        var wholeDatasetStageTargetIds = GetWholeDatasetStageTargetIds(graphSteps);
        var inputs = graphSteps
            .Select(step => ParseGraphStep(
                step,
                connections,
                defaultBatchSize,
                maxTargetConcurrency,
                wholeDatasetStageTargetIds))
            .ToArray();
        if (inputs.Length == 0)
        {
            throw new ArgumentException("At least one migration step must be provided.", nameof(stepObjects));
        }
        ValidateGraph(inputs);
        ValidateFileTargetPaths(inputs);
        inputs = ConfigureDatabaseConnectorConnections(inputs, ownedConnections);
        inputs = ConfigureManagedTransformConnections(
            inputs,
            runId,
            migrationDatabasePath,
            ownedConnections);
        ValidateGraph(inputs);
        ValidateDatabaseSourceConnections(inputs);
        var dataversePools = CreateDataverseConnectionPools(
            inputs,
            runId,
            migrationDatabasePath,
            disableDataverseRetriesForDiagnostics,
            ownedDataversePools);
        var dispatchWeights = CalculatePipelineDispatchWeights(inputs);
        foreach (var databaseTarget in inputs.Where(input =>
                     !whatIf && input.Type == "target" &&
                     DatabaseConnectorSupport.IsDatabaseConnectorType(input.TargetType!)))
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
        var customFormatSources = new Dictionary<string, PowerShellCustomFormatSourceDefinition>(StringComparer.Ordinal);
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
                    GetDataverseConnection(source, dataversePools),
                    source.DataverseSourceOptions!,
                    source.DataverseQueryType!,
                    source.DataverseSourcePartitions));
        }

        foreach (var source in inputs.Where(step =>
                     step.Type == "source" &&
                     step.SourceFormat?.Type == "custom"))
        {
            var format = source.SourceFormat
                ?? throw new InvalidOperationException($"Custom format source '{source.Id}' has no format definition.");
            customFormatSources.Add(
                source.Id,
                new PowerShellCustomFormatSourceDefinition(
                    format.Identity!,
                    format.ReaderScriptText!));
        }

        var readerFactory = new PowerShellHierarchyRecordReaderFactory(
            executor,
            customSources,
            dataverseSources,
            customFormatSources);
        var semaphores = new Dictionary<string, (SemaphoreSlim Semaphore, int Capacity)>(StringComparer.Ordinal);
        var definitions = new List<MigrationStepDefinition>(inputs.Length);
        var throughputMeter = new PipelineThroughputMeter();
        var targetControllers = new List<PipelineDataverseConnectionController>();
        var targetControllersByConnection = new Dictionary<string, PipelineDataverseConnectionController>(
            StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var targetGroup in inputs
                         .Where(input => input.Type == "target" && input.TargetType == "dataverse")
                         .GroupBy(input => input.TargetConnectionId!, StringComparer.OrdinalIgnoreCase))
            {
                var targetConnection = targetGroup.First().TargetConnection
                    ?? throw new InvalidOperationException(
                        $"Dataverse connection '{targetGroup.Key}' resolved to null.");
                var targetPool = dataversePools.GetValueOrDefault(targetGroup.Key);
                targetConnection = targetPool?.Connection ?? targetConnection;
                var controller = new PipelineDataverseConnectionController(
                    targetGroup.Key,
                    targetConnection,
                    maxTargetConcurrency,
                    disableDataverseRetriesForDiagnostics,
                    targetPool);
                targetControllers.Add(controller);
                targetControllersByConnection.Add(targetGroup.Key, controller);
                foreach (var input in targetGroup)
                {
                    controller.RegisterTarget(
                        input.Id,
                        input.BatchSize,
                        input.TargetMaxConcurrency);
                }
            }

            foreach (var input in inputs)
            {
                Func<MigrationStepCheckpoint, CancellationToken, IAsyncEnumerable<MigrationWorkBatch>>? sourceReader = null;
                HierarchySourceLevel? sourceLevel = null;
                if (input.Type == "source" && input.RowDependencies.Length == 0)
                {
                    sourceLevel = CreateGraphSourceLevel(input, binaryStagingRoot);
                    sourceReader = new HierarchyBatchSource(
                        sourceLevel,
                        input.BatchSize,
                        readerFactory.CreateDelegate(),
                        readerFactory.CreateCheckpointAwareDelegate()).CreateReader();
                }
                else if (input.Type == "source" && input.RowDependencies.Length > 0)
                {
                    sourceLevel = CreateGraphSourceLevel(input, binaryStagingRoot);
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
                else if (input.Type == "transform" && input.TransformMode == "whole_dataset")
                {
                    sourceLevel = CreateGraphWholeDatasetTransformSourceLevel(input);
                    sourceReader = new HierarchyBatchSource(
                        sourceLevel,
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
                var targetController = input.Type == "target" && input.TargetType == "dataverse"
                    ? targetControllersByConnection[input.TargetConnectionId!]
                    : null;
                var childContexts = consumers.TryGetValue(input.Id, out var childSteps)
                    ? childSteps.Where(child =>
                            child.Type != "join" &&
                            !(child.Type == "transform" && child.TransformMode == "whole_dataset") &&
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
                    ["TargetOperation"] = input.TargetOperation ?? string.Empty,
                    ["TableName"] = input.TableName ?? string.Empty,
                    ["TargetConnectionId"] = input.TargetConnectionId ?? string.Empty,
                    ["TargetPath"] = input.TargetPath ?? string.Empty,
                    ["TargetOptionsJson"] = input.TargetOptionsJson ?? "{}",
                    ["TargetFormatType"] = input.TargetFormat?.Type ?? string.Empty,
                    ["TargetFormatOptionsJson"] = input.TargetFormat?.OptionsJson ?? "{}",
                    ["TargetFormatExtensions"] = input.TargetFormat?.Extensions ?? Array.Empty<string>(),
                    ["TargetFormatWriterText"] = input.TargetFormat?.WriterScriptText ?? string.Empty,
                    ["TargetFormatContentType"] = input.TargetFormat?.ContentType ?? string.Empty,
                    ["TargetCallbackText"] = input.TargetCustom?.CallbackText ?? string.Empty,
                    ["TargetBinaryReferenceText"] = input.TargetBinaryReferenceScriptText ?? string.Empty,
                    ["SourcePath"] = input.SourcePath,
                    ["SourceOptionsJson"] = input.SourceOptionsJson,
                    ["SourceFormatType"] = input.SourceFormat?.Type ?? string.Empty,
                    ["SourceFormatOptionsJson"] = input.SourceFormat?.OptionsJson ?? "{}",
                    ["SourceBinaryResolverText"] = input.SourceBinaryResolverScriptText ?? string.Empty,
                    ["TargetSemaphore"] = targetSemaphore,
                    ["TargetController"] = targetController,
                    ["PipelineThroughputMeter"] = throughputMeter,
                    ["DisableDataverseRetriesForDiagnostics"] = disableDataverseRetriesForDiagnostics,
                    ["TransformType"] = input.TransformType ?? string.Empty,
                    ["TransformMode"] = input.TransformMode ?? string.Empty,
                    ["TransformConnectionId"] = input.TransformConnectionId ?? string.Empty,
                    ["TransformSql"] = input.TransformSql ?? string.Empty,
                    ["TransformParametersJson"] = input.TransformParametersJson ?? "{}",
                    ["TransformCommandTimeoutSeconds"] = input.TransformCommandTimeoutSeconds,
                    ["Store"] = store,
                    ["BatchSize"] = input.BatchSize,
                    ["Children"] = childContexts,
                    ["JoinInputConsumers"] = joinInputConsumers,
                    ["CaptureDebugData"] = captureDebugData,
                    ["StagingRoot"] = objectStagingRoot,
                    ["BinaryStagingRoot"] = binaryStagingRoot,
                    ["WhatIf"] = whatIf
                };
                var handlerWithLease = input.Type == "callback"
                    ? executor.CreateMigrationBatchHandlerWithExecutionLease(
                        """
                        param($batch, $callbackText, $sourceConnection, $targetConnection, $whatIf)
                        if ($whatIf) {
                            [Migration4Dataverse.Engine.Scheduling.MigrationBatchResult]::new(
                                '{"Succeeded":0,"Failed":0}', $false, $null, $null)
                        } else {
                            $callback = [scriptblock]::Create($callbackText)
                            & $callback $batch $sourceConnection $targetConnection
                        }
                        """,
                        (_, _) => new object?[]
                        {
                            input.CallbackScriptText!,
                            input.CallbackSourceConnection,
                            input.CallbackTargetConnection,
                            whatIf
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
                                input.TargetType == "dataverse"
                                    ? dataversePools.GetValueOrDefault(input.TargetConnectionId!)?.Connection
                                        ?? input.TargetConnection
                                    : input.TargetConnection,
                                input.TransformConnection,
                                invocationContext
                            ];
                        });
                Func<IDisposable?>? tryAcquireExecutionLease = targetController is not null
                    ? () => targetController.TryAcquire(input.Id)
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
                        sourceLevel,
                        input.SourceType == "dataverse"
                            ? GetDataverseConnection(input, dataversePools)
                            : input.SourceConnection),
                    WaitForInputCompletion = input.Type != "join" && input.RowDependencies.Length == 0,
                    SourceKeyCoordinator = sourceKeyCoordinator,
                    SourceKeyProperty = sourceKeyParents.Contains(input.Id) ? input.SourceKey : null,
                    StepCompletionCoordinator = stepCompletionCoordinator,
                    ErrorRateThresholdPercent = input.ErrorRateThresholdPercent,
                    TryAcquireExecutionLease = tryAcquireExecutionLease,
                    ExecuteBatchWithExecutionLeaseAsync = handlerWithLease,
                    CompleteAsync = CreateTargetFinalizer(
                        input,
                        executor,
                        whatIf,
                        runId,
                        objectStagingRoot,
                        binaryStagingRoot),
                    DispatchWeight = dispatchWeights[input.Id]
                });
            }
        }
        catch
        {
            foreach (var (semaphore, _) in semaphores.Values)
            {
                semaphore.Dispose();
            }
            foreach (var controller in targetControllers)
            {
                controller.Dispose();
            }
            foreach (var pool in ownedDataversePools)
            {
                pool.Dispose();
            }
            throw;
        }

        return new MigrationStepSet(
            definitions,
            semaphores.Values.Select(value => value.Semaphore).ToArray(),
            targetControllers,
            throughputMeter,
            ownedConnections,
            ownedDataversePools);
    }

    private static GraphStepInput[] ConfigureDatabaseConnectorConnections(
        IReadOnlyList<GraphStepInput> inputs,
        ICollection<DbConnection> ownedConnections)
    {
        var connectionsById = new Dictionary<string, (string Provider, string Path, DbConnection Connection)>(
            StringComparer.OrdinalIgnoreCase);

        DbConnection GetConnection(
            string connectionId,
            object? pathConnection,
            string databasePath,
            string provider,
            string stepId,
            string role)
        {
            if (connectionsById.TryGetValue(connectionId, out var existing))
            {
                if (!string.Equals(existing.Provider, provider, StringComparison.Ordinal) ||
                    !string.Equals(existing.Path, databasePath, OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Database {role} steps using connection ID '{connectionId}' must use the same provider and database path.");
                }
                return existing.Connection;
            }

            if (pathConnection is not (FileInfo or DirectoryInfo))
            {
                throw new ArgumentException(
                    $"Database {role} step '{stepId}' requires a FileInfo or DirectoryInfo in -Connections.");
            }
            var connection = MigrationDatabaseConnectionFactory.CreateForPath(provider, databasePath);
            DatabaseConnectorSupport.ValidateConnection(provider, connection, $"{role} '{stepId}'");
            ownedConnections.Add(connection);
            connectionsById.Add(connectionId, (provider, databasePath, connection));
            return connection;
        }

        return inputs.Select(input =>
        {
            if (input.Type == "source" &&
                DatabaseConnectorSupport.IsFileDatabaseConnectorType(input.SourceType))
            {
                var connection = GetConnection(
                    input.SourceConnectionId!,
                    input.SourceConnection,
                    input.SourcePath,
                    input.SourceType,
                    input.Id,
                    "source");
                return input with { SourceConnection = connection };
            }
            if (input.Type == "target" &&
                DatabaseConnectorSupport.IsFileDatabaseConnectorType(input.TargetType!) &&
                input.TargetConnection is not null)
            {
                var connection = GetConnection(
                    input.TargetConnectionId!,
                    input.TargetConnection,
                    input.TargetPath!,
                    input.TargetType!,
                    input.Id,
                    "target");
                return input with { TargetConnection = connection };
            }
            return input;
        }).ToArray();
    }

    private static Dictionary<string, DataverseConnectionPool> CreateDataverseConnectionPools(
        IReadOnlyList<GraphStepInput> inputs,
        string? runId,
        string? migrationDatabasePath,
        bool disableRetriesForDiagnostics,
        ICollection<DataverseConnectionPool> ownedPools)
    {
        var pools = new Dictionary<string, DataverseConnectionPool>(StringComparer.OrdinalIgnoreCase);
        var connectionGroups = inputs
            .Where(input =>
                input.Type == "source" && input.SourceType == "dataverse" ||
                input.Type == "target" && input.TargetType == "dataverse")
            .GroupBy(
                input => input.Type == "source" ? input.SourceConnectionId! : input.TargetConnectionId!,
                StringComparer.OrdinalIgnoreCase);
        foreach (var group in connectionGroups)
        {
            var slots = group
                .Select(input => input.Type == "source" ? input.SourceConnection : input.TargetConnection)
                .Select(connection => connection is object[] values ? values : [connection!])
                .ToArray();
            var connections = slots[0];
            if (slots.Any(slot =>
                    slot.Length != connections.Length ||
                    !slot.SequenceEqual(connections, ReferenceEqualityComparer.Instance)))
            {
                throw new ArgumentException(
                    $"Dataverse connection slot '{group.Key}' resolves to inconsistent connection arrays.");
            }

            if (connections.Length > 1)
            {
                var pool = DataverseConnectionPool.Create(
                    group.Key,
                    connections,
                    disableRetriesForDiagnostics,
                    runId,
                    migrationDatabasePath);
                pools.Add(group.Key, pool);
                ownedPools.Add(pool);
            }
        }

        return pools;
    }

    private static object GetDataverseConnection(
        GraphStepInput input,
        IReadOnlyDictionary<string, DataverseConnectionPool> pools)
    {
        var connectionId = input.Type == "source" ? input.SourceConnectionId : input.TargetConnectionId;
        var connection = input.Type == "source" ? input.SourceConnection : input.TargetConnection;
        return connectionId is not null && pools.TryGetValue(connectionId, out var pool)
            ? pool.Connection
            : connection ?? throw new InvalidOperationException(
                $"Dataverse connection for step '{input.Id}' resolved to null.");
    }

    private static object NormalizeDataverseConnectionSlot(
        object connection,
        string connectionId,
        string stepId)
    {
        var unwrapped = Unwrap(connection)
            ?? throw new ArgumentException(
                $"Dataverse connection slot '{connectionId}' for step '{stepId}' resolved to null.");
        if (unwrapped is IDictionary or string)
        {
            return unwrapped;
        }

        var enumerable = LanguagePrimitives.GetEnumerable(unwrapped);
        if (enumerable is null)
        {
            return unwrapped;
        }

        var values = enumerable.Cast<object?>()
            .Select(Unwrap)
            .ToArray();
        if (values.Length == 0 || values.Any(value => value is null))
        {
            throw new ArgumentException(
                $"Dataverse connection slot '{connectionId}' for step '{stepId}' must contain one or more non-null connections.");
        }
        if (values.Distinct(ReferenceEqualityComparer.Instance).Count() != values.Length)
        {
            throw new ArgumentException(
                $"Dataverse connection slot '{connectionId}' for step '{stepId}' contains the same connection instance more than once.");
        }

        return values.Length == 1
            ? values[0]!
            : values.Cast<object>().ToArray();
    }

    private static DataverseSourcePartition[]? ParseDataverseSourcePartitions(
        object? value,
        IDictionary sourceOptions,
        string queryType,
        string stepId)
    {
        if (value is null)
        {
            return null;
        }

        var entries = GetMap(value, $"Partitions for Dataverse source step '{stepId}", required: false) is not null
            ? new[] { value }
            : LanguagePrimitives.GetEnumerable(value)?.Cast<object?>().ToArray() ?? [value];
        if (entries.Length < 2)
        {
            throw new ArgumentException(
                $"Dataverse source step '{stepId}' must declare at least two query partitions.");
        }

        var partitions = new List<DataverseSourcePartition>(entries.Length);
        var partitionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var definition = GetMap(entry, $"Partition for Dataverse source step '{stepId}", required: true)!;
            foreach (var key in definition.Keys)
            {
                var keyName = key?.ToString() ?? string.Empty;
                if (!string.Equals(keyName, "Id", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(keyName, "Options", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        $"Partition for Dataverse source step '{stepId}' has unsupported property '{keyName}'.");
                }
            }

            var partitionId = GetRequiredString(definition, "Id");
            if (!partitionIds.Add(partitionId))
            {
                throw new ArgumentException(
                    $"Dataverse source step '{stepId}' contains duplicate partition ID '{partitionId}'.");
            }

            var mergedOptions = new Hashtable(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry option in sourceOptions)
            {
                mergedOptions[option.Key] = Unwrap(option.Value);
            }
            var rawPartitionOptions = GetProperty(definition, "Options");
            var partitionOptions = GetMap(
                rawPartitionOptions,
                $"Options for partition '{partitionId}' on Dataverse source step '{stepId}",
                required: false);
            if (rawPartitionOptions is not null && partitionOptions is null)
            {
                throw new ArgumentException(
                    $"Options for partition '{partitionId}' on Dataverse source step '{stepId}' must be a dictionary or object with named properties.");
            }
            if (partitionOptions is not null)
            {
                foreach (DictionaryEntry option in partitionOptions)
                {
                    mergedOptions[option.Key] = Unwrap(option.Value);
                }
            }

            var partitionQueryType = GetRequiredString(mergedOptions, "QueryType").Trim().ToLowerInvariant();
            if (!string.Equals(partitionQueryType, queryType, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Partition '{partitionId}' on Dataverse source step '{stepId}' must use the same QueryType as its source.");
            }
            _ = ValidateDataverseSourceOptions(mergedOptions, $"{stepId}/{partitionId}");
            if (partitionQueryType == "cds4sql")
            {
                var maxDegreeOfParallelism = GetInt(mergedOptions, "MaxDegreeOfParallelism", 1);
                if (maxDegreeOfParallelism != 1)
                {
                    throw new ArgumentException(
                        $"Cds4Sql partition '{partitionId}' on Dataverse source step '{stepId}' must use MaxDegreeOfParallelism = 1.");
                }
                mergedOptions["MaxDegreeOfParallelism"] = 1;
            }

            partitions.Add(new DataverseSourcePartition(partitionId, mergedOptions));
        }

        return partitions.ToArray();
    }

    private static HashSet<string> GetWholeDatasetStageTargetIds(IReadOnlyList<PSObject> steps)
    {
        var stageTargetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (!string.Equals(
                    GetString(step, "Type", required: false),
                    "transform",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var transform = GetMap(GetProperty(step, "Transform"), "Transform", required: false);
            if (transform is null || !string.Equals(
                    GetString(transform, "Mode", required: false),
                    "whole_dataset",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var stepId = GetString(step, "Id", required: true)!;
            var inputs = ParseInputDefinitions(GetProperty(step, "Inputs"), stepId);
            if (inputs.Length == 1)
            {
                stageTargetIds.Add(inputs[0].Job);
            }
        }
        return stageTargetIds;
    }

    private static string GetObjectStagingRoot(string? migrationDatabasePath, string? runId)
    {
        var runDirectory = string.IsNullOrWhiteSpace(runId) ? "adhoc" : runId;
        if (!string.IsNullOrWhiteSpace(migrationDatabasePath))
        {
            var databaseDirectory = Path.GetDirectoryName(Path.GetFullPath(migrationDatabasePath));
            if (!string.IsNullOrWhiteSpace(databaseDirectory))
            {
                return Path.Combine(databaseDirectory, ".migration-object-staging", runDirectory);
            }
        }
        return Path.Combine(Path.GetTempPath(), "migration4dataverse-staging", runDirectory);
    }

    private static string GetBinaryStagingRoot(string? migrationDatabasePath, string? runId)
    {
        return MigrationBinaryValue.GetRunStagingRoot(migrationDatabasePath, runId);
    }

    private static void ValidateFileTargetPaths(IReadOnlyList<GraphStepInput> inputs)
    {
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var duplicatePath = inputs
            .Where(input => input.Type == "target" && input.TargetType == "file")
            .GroupBy(input => Path.GetFullPath(input.TargetPath!), pathComparer)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicatePath is not null)
        {
            throw new ArgumentException(
                $"File target steps cannot share output path '{duplicatePath.Key}'.");
        }

        var duplicateObjectPath = inputs
            .Where(input => input.Type == "target" &&
                            input.TargetType is "azureblob" or "s3" or "adlsgen2" or "gcs")
            .GroupBy(
                input => $"{input.TargetType}\0{input.TargetConnectionId}\0{input.TargetPath}",
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateObjectPath is not null)
        {
            throw new ArgumentException(
                $"Object storage target steps cannot share the same connection and object path.");
        }
    }

    private static GraphStepInput[] ConfigureManagedTransformConnections(
        IReadOnlyList<GraphStepInput> inputs,
        string? runId,
        string? migrationDatabasePath,
        ICollection<DbConnection> ownedConnections)
    {
        var transforms = inputs
            .Where(input => input.Type == "transform" && input.TransformType is "sqlite" or "duckdb")
            .ToArray();
        if (transforms.Length == 0)
        {
            return inputs.ToArray();
        }
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(migrationDatabasePath))
        {
            throw new ArgumentException(
                "Database transforms require a migration run ID and -DatabasePath so the engine can manage their database files.");
        }

        var byId = inputs.ToDictionary(input => input.Id, StringComparer.Ordinal);
        var managedStageTargetIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var transform in transforms)
        {
            string databaseStepId;
            if (transform.TransformMode == "whole_dataset")
            {
                if (transform.Inputs.Length != 1 ||
                    !byId.TryGetValue(transform.Inputs[0], out var stageTarget) ||
                    stageTarget.Type != "target" ||
                    stageTarget.TargetType != transform.TransformType)
                {
                    throw new ArgumentException(
                        $"Whole-dataset {transform.TransformType} transform '{transform.Id}' must depend on one matching database staging Target.");
                }
                if (!managedStageTargetIds.Add(stageTarget.Id))
                {
                    throw new ArgumentException(
                        $"Database staging Target '{stageTarget.Id}' cannot be shared by multiple whole-dataset transforms.");
                }
                databaseStepId = transform.Id;
            }
            else
            {
                databaseStepId = transform.Id;
            }

            var connection = MigrationDatabaseConnectionFactory.CreateManagedTransformConnection(
                transform.TransformType!,
                migrationDatabasePath,
                runId,
                databaseStepId);
            ownedConnections.Add(connection);
            if (!string.IsNullOrWhiteSpace(transform.TransformInitializeSql))
            {
                var openedConnection = connection.State != System.Data.ConnectionState.Open;
                if (openedConnection)
                {
                    connection.Open();
                }
                try
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = transform.TransformInitializeSql;
                    command.CommandTimeout = transform.TransformCommandTimeoutSeconds;
                    command.ExecuteNonQuery();
                }
                finally
                {
                    if (openedConnection)
                    {
                        connection.Close();
                    }
                }
            }
            DatabaseConnectorSupport.ValidateConnection(
                transform.TransformType!,
                connection,
                $"transform '{transform.Id}'");

            if (transform.TransformMode == "whole_dataset")
            {
                var stageTarget = byId[transform.Inputs[0]];
                byId[stageTarget.Id] = stageTarget with { TargetConnection = connection };
            }
            byId[transform.Id] = transform with { TransformConnection = connection };
        }

        return inputs.Select(input => byId[input.Id]).ToArray();
    }

    private static GraphStepInput ParseGraphStep(
        PSObject step,
        IDictionary connections,
        int defaultBatchSize,
        int maxTargetConcurrency,
        IReadOnlySet<string> wholeDatasetStageTargetIds)
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
        var errorRateThresholdPercent = GetDouble(
            GetPropertiesAsMap(step),
            "ErrorRateThresholdPercent",
            defaultValue: null);
        if (errorRateThresholdPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                "ErrorRateThresholdPercent",
                $"ErrorRateThresholdPercent for step '{id}' must be between 0 and 100.");
        }

        var sourceType = string.Empty;
        var sourcePath = string.Empty;
        GraphFileFormatInput? sourceFormat = null;
        var sourceKey = string.Empty;
        var idProperty = string.Empty;
        var sourceOptionsJson = "{}";
        string? sourceBinaryResolverScriptText = null;
        long expectedRecordCount = 0;
        string? sourceConnectionId = null;
        object? sourceConnection = null;
        CustomConnector? sourceCustom = null;
        IDictionary? dataverseSourceOptions = null;
        DataverseSourcePartition[]? dataverseSourcePartitions = null;
        string? dataverseQueryType = null;
        string? callbackScriptText = null;
        string? readBatchesScriptText = null;
        object? callbackSourceConnection = null;
        object? callbackTargetConnection = null;
        if (type == "source")
        {
            var source = GetMap(GetProperty(step, "Source"), $"Source for step '{id}'")!;
            sourceType = GetRequiredString(source, "Type").ToLowerInvariant();
            if (sourceType is not ("file" or "custom" or "dataverse" or
                "sqlite" or "duckdb" or "sqlserver" or "postgresql" or "mysql" or "oracle" or
                "azureblob" or "s3" or "adlsgen2" or "gcs" or "http" or "odata" or "mongodb" or
                "salesforce" or "sharepoint" or "hubspot" or "zendesk" or "servicenow"))
            {
                throw new ArgumentException($"Source connector type '{sourceType}' for step '{id}' is not supported.");
            }
            if (IsFileOrObjectStorageType(sourceType))
            {
                sourceFormat = ParseFileFormat(GetProperty(source, "Format"), id, "source");
            }
            else if (GetProperty(source, "Format") is not null)
            {
                throw new ArgumentException(
                    $"Source step '{id}' can configure Format only for file and object-storage sources.");
            }
            sourceConnectionId = GetRequiredString(source, "ConnectionId");
            sourceConnection = ResolveConnection(connections, sourceConnectionId, id, "source");
            if (sourceType == "file" && sourceConnection is not (FileInfo or DirectoryInfo))
            {
                throw new ArgumentException(
                    $"File source step '{id}' requires a FileInfo or DirectoryInfo in -Connections.");
            }
            if (sourceType == "dataverse")
            {
                sourceConnection = NormalizeDataverseConnectionSlot(
                    sourceConnection
                        ?? throw new ArgumentException(
                            $"Dataverse connection '{sourceConnectionId}' for step '{id}' resolved to null."),
                    sourceConnectionId,
                    id);
            }
            if (sourceType is "sqlite" or "duckdb" &&
                sourceConnection is not (FileInfo or DirectoryInfo))
            {
                throw new ArgumentException(
                    $"Database source step '{id}' requires a FileInfo or DirectoryInfo in -Connections.");
            }
            if (sourceType is "sqlserver" or "postgresql" or "mysql" or "oracle")
            {
                if (sourceConnection is not DbConnection databaseConnection)
                {
                    throw new ArgumentException(
                        $"Database source step '{id}' requires a {sourceType} DbConnection in -Connections.");
                }
                DatabaseConnectorSupport.ValidateConnection(
                    sourceType,
                    databaseConnection,
                    $"source '{id}'");
            }
            if (sourceType is "azureblob" or "s3" or "adlsgen2" or "gcs")
            {
                if (sourceConnection is null)
                {
                    throw new ArgumentException(
                        $"Object storage source step '{id}' requires a connection in -Connections.");
                }
                ObjectStorageConnectionSupport.ValidateConnection(
                    sourceType,
                    sourceConnection,
                    $"source '{id}'");
            }
            if (sourceType == "mongodb")
            {
                if (sourceConnection is null)
                {
                    throw new ArgumentException(
                        $"MongoDB source step '{id}' requires an IMongoClient or IMongoDatabase in -Connections.");
                }
                MongoConnectionSupport.ValidateConnection(sourceConnection, $"source '{id}'");
            }
            if (sourceType is "http" or "odata" &&
                sourceConnection is not (Uri or string or HttpClient))
            {
                throw new ArgumentException(
                    $"HTTP source step '{id}' requires a Uri, absolute URL string, or HttpClient in -Connections.");
            }
            if (sourceType is "salesforce" or "sharepoint" or "hubspot" or "zendesk" or "servicenow")
            {
                RestJsonConnectorSupport.GetHttpClient(sourceConnection, sourceType, $"source '{id}'");
            }
            var configuredSourcePath = GetString(
                source,
                "Path",
                required: sourceConnection is DirectoryInfo &&
                          (sourceType is "file" or "sqlite" or "duckdb"));
            sourcePath = ResolveConnectorPath(
                sourceConnection,
                configuredSourcePath ?? string.Empty,
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
                if (sourceConnection is object[] && dataverseQueryType == "cds4sql")
                {
                    var maxDegreeOfParallelism = GetInt(
                        dataverseSourceOptions!,
                        "MaxDegreeOfParallelism",
                        1);
                    if (maxDegreeOfParallelism != 1)
                    {
                        throw new ArgumentException(
                            $"Cds4Sql source step '{id}' must use MaxDegreeOfParallelism = 1 when its ConnectionId contains multiple connections.");
                    }
                    dataverseSourceOptions!["MaxDegreeOfParallelism"] = 1;
                }
                dataverseSourcePartitions = ParseDataverseSourcePartitions(
                    GetProperty(source, "Partitions"),
                    dataverseSourceOptions!,
                    dataverseQueryType,
                    id);
            }
            else if (GetProperty(source, "Partitions") is not null)
            {
                throw new ArgumentException(
                    $"Source partitions are only supported for Dataverse source step '{id}'.");
            }
            sourceKey = GetRequiredString(source, "Key");
            idProperty = GetString(source, "Id", required: false) ?? string.Empty;
            if (sourceType == "dataverse" &&
                GetProperty(dataverseSourceOptions, "FileColumns") is not null &&
                string.IsNullOrWhiteSpace(idProperty))
            {
                throw new ArgumentException(
                    $"Dataverse source step '{id}' must configure Source.Id when Options.FileColumns is used.");
            }
            var sourceOptions = GetMap(
                GetProperty(source, "Options"),
                $"Options for source step '{id}'",
                required: false);
            if (sourceType == "file" && sourceOptions is { Count: > 0 })
            {
                throw new ArgumentException(
                    $"File source step '{id}' has no storage Options; put format settings in Format.Options.");
            }
            sourceOptionsJson = SerializeJson(sourceOptions);
            var sourceFormatOptionsJson = sourceFormat?.OptionsJson ?? "{}";
            var sourceBinaryColumns = BinaryFileReferenceSupport.GetConfiguredBinaryColumns(
                sourceFormatOptionsJson,
                id);
            if (sourceBinaryColumns.Length > 0 &&
                (sourceType != "file" || sourceFormat!.Type is not ("csv" or "jsonl" or "parquet")))
            {
                throw new ArgumentException(
                    $"Source step '{id}' can configure Format.Options.BinaryColumns only for CSV, JSON Lines, or Parquet file sources.");
            }
            if (GetProperty(source, "ResolveBinaryReference") is not null)
            {
                if (sourceType != "file" || sourceFormat!.Type is not ("csv" or "jsonl" or "parquet"))
                {
                    throw new ArgumentException(
                        $"Source step '{id}' can configure ResolveBinaryReference only for CSV, JSON Lines, or Parquet file formats.");
                }
                if (sourceBinaryColumns.Length == 0)
                {
                    throw new ArgumentException(
                        $"Source step '{id}' must configure Format.Options.BinaryColumns when ResolveBinaryReference is provided.");
                }
                sourceBinaryResolverScriptText = NormalizeScriptText(
                    GetScriptBlock(source, "ResolveBinaryReference", required: true));
            }
            if (sourceFormat?.Type == "excel")
            {
                ExcelSourceOptions.FromJson(sourceFormatOptionsJson, id).Validate(id);
            }
            else if (sourceType == "salesforce")
            {
                SalesforceSourceOptions.FromJson(sourceOptionsJson, id).Validate(id);
            }
            else if (sourceType == "sharepoint")
            {
                SharePointSourceOptions.FromJson(sourceOptionsJson, id).Validate(id);
            }
            else if (sourceType == "hubspot")
            {
                HubSpotSourceOptions.FromJson(sourceOptionsJson, id).Validate(id);
            }
            else if (sourceType == "zendesk")
            {
                ZendeskSourceOptions.FromJson(sourceOptionsJson, id).Validate(id);
            }
            else if (sourceType == "servicenow")
            {
                ServiceNowSourceOptions.FromJson(sourceOptionsJson, id).Validate(id);
            }
            if (DatabaseConnectorSupport.IsDatabaseConnectorType(sourceType))
            {
                var databaseSourceOptions = DatabaseSourceOptions.FromJson(sourceOptionsJson, id);
                databaseSourceOptions.Validate(id);
                ReadOnlySqlValidator.ValidateSelect(
                    databaseSourceOptions.Query,
                    $"Database source query for step '{id}'");
                if (databaseSourceOptions.CountQuery is not null)
                {
                    ReadOnlySqlValidator.ValidateSelect(
                        databaseSourceOptions.CountQuery,
                        $"Database count query for source step '{id}'");
                }
            }
            expectedRecordCount = GetLong(source, "ExpectedRecordCount", 0);
            if (sourceType == "custom")
            {
                sourceCustom = ParseCustomConnector(GetProperty(source, "Custom"), id, "source");
            }
        }

        string? targetType = null;
        string? targetOperation = null;
        string? tableName = null;
        string? targetPath = null;
        GraphFileFormatInput? targetFormat = null;
        string? targetOptionsJson = null;
        string? targetBinaryReferenceScriptText = null;
        string? targetConnectionId = null;
        object? targetConnection = null;
        var targetMaxConcurrency = maxTargetConcurrency;
        var targetMatchOnGroups = Array.Empty<string[]>();
        CustomConnector? targetCustom = null;
        if (type == "target")
        {
            var target = GetMap(GetProperty(step, "Target"), $"Target for step '{id}'")!;
            targetType = GetRequiredString(target, "Type").ToLowerInvariant();
            if (targetType is not ("dataverse" or "file" or "custom" or
                "sqlite" or "duckdb" or "sqlserver" or "postgresql" or "mysql" or "oracle" or
                "azureblob" or "s3" or "adlsgen2" or "gcs" or "mongodb" or "sharepoint"))
            {
                throw new ArgumentException($"Target connector type '{targetType}' for step '{id}' is not supported.");
            }
            if (IsFileOrObjectStorageType(targetType))
            {
                targetFormat = ParseFileFormat(GetProperty(target, "Format"), id, "target");
            }
            else if (GetProperty(target, "Format") is not null)
            {
                throw new ArgumentException(
                    $"Target step '{id}' can configure Format only for file and object-storage targets.");
            }
            if (targetType == "dataverse")
            {
                targetMaxConcurrency = ParsePositiveTargetInteger(
                    target,
                    "MaxConcurrency",
                    maxTargetConcurrency,
                    id);
            }
            else if (GetProperty(target, "MaxConcurrency") is not null)
            {
                throw new ArgumentException(
                    $"MaxConcurrency is only supported for Dataverse target steps; '{id}' uses '{targetType}'.");
            }
            var managedStageTarget = wholeDatasetStageTargetIds.Contains(id);
            var configuredTargetConnectionId = GetString(
                target,
                "ConnectionId",
                required: !managedStageTarget);
            if (managedStageTarget)
            {
                if (!string.IsNullOrWhiteSpace(configuredTargetConnectionId))
                {
                    throw new ArgumentException(
                        $"Database staging Target '{id}' for a whole-dataset transform must omit ConnectionId; its database is managed by the engine.");
                }
                if (GetProperty(target, "Path") is not null)
                {
                    throw new ArgumentException(
                        $"Database staging Target '{id}' for a whole-dataset transform must omit Path; its database is managed by the engine.");
                }
                targetConnectionId = $"managed-stage:{id}";
            }
            else
            {
                targetConnectionId = configuredTargetConnectionId!;
                targetConnection = ResolveConnection(connections, targetConnectionId, id, "target")
                    ?? throw new ArgumentException(
                        $"Target connection '{targetConnectionId}' for step '{id}' resolved to null.");
                if (targetType == "file" && targetConnection is not (FileInfo or DirectoryInfo))
                {
                    throw new ArgumentException(
                        $"File target step '{id}' requires a FileInfo or DirectoryInfo in -Connections.");
                }
                if (targetType == "dataverse")
                {
                    targetConnection = NormalizeDataverseConnectionSlot(
                        targetConnection,
                        targetConnectionId,
                        id);
                }
            }
            if (targetType is "sqlite" or "duckdb")
            {
                if (!managedStageTarget && targetConnection is not (FileInfo or DirectoryInfo))
                {
                    throw new ArgumentException(
                        $"Database target step '{id}' requires a FileInfo or DirectoryInfo in -Connections.");
                }
            }
            if (targetType is "sqlserver" or "postgresql" or "mysql" or "oracle")
            {
                if (targetConnection is not DbConnection databaseConnection)
                {
                    throw new ArgumentException(
                        $"Database target step '{id}' requires a {targetType} DbConnection in -Connections.");
                }
                DatabaseConnectorSupport.ValidateConnection(
                    targetType,
                    databaseConnection,
                    $"target '{id}'");
            }
            if (targetType == "mongodb")
            {
                if (targetConnection is null)
                {
                    throw new ArgumentException(
                        $"MongoDB target step '{id}' requires an IMongoClient or IMongoDatabase in -Connections.");
                }
                MongoConnectionSupport.ValidateConnection(targetConnection, $"target '{id}'");
            }
            if (targetType is "azureblob" or "s3" or "adlsgen2" or "gcs")
            {
                if (targetConnection is null)
                {
                    throw new ArgumentException(
                        $"Object storage target step '{id}' requires a connection in -Connections.");
                }
                ObjectStorageConnectionSupport.ValidateConnection(
                    targetType,
                    targetConnection,
                    $"target '{id}'");
            }
            if (targetType == "sharepoint")
            {
                RestJsonConnectorSupport.GetHttpClient(targetConnection, "SharePoint", $"target '{id}'");
            }
            var configuredTargetPath = GetString(
                target,
                "Path",
                required: !managedStageTarget &&
                          targetConnection is DirectoryInfo &&
                          (targetType is "file" or "sqlite" or "duckdb"));
            targetPath = managedStageTarget
                ? string.Empty
                : ResolveConnectorPath(
                    targetConnection,
                    configuredTargetPath ?? string.Empty,
                    targetType,
                    $"Target path for step '{id}'");
            var targetOptions = GetMap(
                GetProperty(target, "Options"),
                $"Options for target step '{id}'",
                required: false);
            if (targetType == "file" && targetOptions is { Count: > 0 })
            {
                throw new ArgumentException(
                    $"File target step '{id}' has no storage Options; put format settings in Format.Options.");
            }
            if (GetProperty(targetOptions, "RecordIdKeys") is not null)
            {
                throw new ArgumentException(
                    $"Target step '{id}' must not configure RecordIdKeys; parent records are captured automatically for Join relationships from MatchOn.");
            }
            targetOptionsJson = SerializeJson(targetOptions);
            var targetFormatOptionsJson = targetFormat?.OptionsJson ?? "{}";
            var targetBinaryColumns = BinaryFileReferenceSupport.GetConfiguredBinaryColumns(
                targetFormatOptionsJson,
                id);
            if (targetBinaryColumns.Length > 0 &&
                (targetType != "file" || targetFormat!.Type is not ("csv" or "jsonl" or "parquet")))
            {
                throw new ArgumentException(
                    $"Target step '{id}' can configure Format.Options.BinaryColumns only for CSV, JSON Lines, or Parquet file formats.");
            }
            if (GetProperty(target, "CreateBinaryReference") is not null)
            {
                if (targetType != "file" || targetFormat!.Type is not ("csv" or "jsonl" or "parquet"))
                {
                    throw new ArgumentException(
                        $"Target step '{id}' can configure CreateBinaryReference only for CSV, JSON Lines, or Parquet file formats.");
                }
                targetBinaryReferenceScriptText = NormalizeScriptText(
                    GetScriptBlock(target, "CreateBinaryReference", required: true));
            }
            targetMatchOnGroups = ParseStringGroups(GetProperty(targetOptions, "MatchOn"));
            tableName = targetType == "dataverse"
                ? GetRequiredString(target, "Table")
                : GetString(target, "Table", required: false) ?? string.Empty;
            if (targetType is "azureblob" or "s3" or "adlsgen2" or "gcs" && string.IsNullOrWhiteSpace(targetPath))
            {
                throw new ArgumentException($"Object storage target step '{id}' requires a Path.");
            }
            if ((DatabaseConnectorSupport.IsDatabaseConnectorType(targetType) ||
                 targetType == "mongodb") &&
                string.IsNullOrWhiteSpace(tableName))
            {
                throw new ArgumentException($"Database target step '{id}' requires a Table.");
            }
            var operation = GetString(target, "Operation", required: false);
            targetOperation = targetType == "dataverse"
                ? operation ?? "Upsert"
                : operation ?? "Write";
            var supportedDataverseOperations = new[]
            {
                "Upsert",
                "Create",
                "Update",
                "Delete",
                "CreateOrUpdateIfNeeded"
            };
            if (targetType == "dataverse" &&
                !supportedDataverseOperations.Contains(targetOperation, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Dataverse target operation for step '{id}' must be one of: {string.Join(", ", supportedDataverseOperations)}.");
            }
            if (targetType != "dataverse" &&
                !string.Equals(targetOperation, "Write", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Target operation for step '{id}' must be 'Write'.");
            }
            if (targetType == "dataverse")
            {
                targetOperation = supportedDataverseOperations.Single(
                    supportedOperation => string.Equals(
                        supportedOperation,
                        targetOperation,
                        StringComparison.OrdinalIgnoreCase));
                var setOnlyOptions = new List<string>();
                var noLookup = GetProperty(targetOptions, "NoLookup");
                if (noLookup is not null && LanguagePrimitives.IsTrue(noLookup))
                {
                    setOnlyOptions.Add("NoLookup");
                }
                setOnlyOptions.AddRange(new[]
                {
                    "BypassBusinessLogicExecution",
                    "BypassBusinessLogicExecutionStepIds"
                });
                var deleteOptions = setOnlyOptions
                    .Where(option => GetProperty(targetOptions, option) is not null)
                    .ToArray();
                if (targetOperation == "Delete" && deleteOptions.Length > 0)
                {
                    throw new ArgumentException(
                        $"Dataverse Delete target step '{id}' does not support Set-DataverseRecord options: " +
                        $"{string.Join(", ", deleteOptions)}.");
                }
                if (targetOperation == "Create" &&
                    GetProperty(targetOptions, "MatchOn") is not null)
                {
                    throw new ArgumentException(
                        $"Dataverse target operation '{targetOperation}' for step '{id}' cannot configure Options.MatchOn.");
                }
            }
            if (targetType == "dataverse")
            {
                batchSize = ParsePositiveTargetInteger(
                    target,
                    "BatchSize",
                    DefaultDataverseTargetBatchSize,
                    id);
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
        string? transformInitializeSql = null;
        string? transformMode = null;
        string transformKey = string.Empty;
        string? transformParametersJson = null;
        string? transformSourceOptionsJson = null;
        var transformCommandTimeoutSeconds = 30;
        string? scriptText = null;
        if (type is "transform" or "filter")
        {
            var transform = type == "transform"
                ? GetMap(GetProperty(step, "Transform"), $"Transform for step '{id}'", required: false)
                : null;
            if (transform is not null)
            {
                transformType = GetRequiredString(transform, "Type").ToLowerInvariant();
                if (transformType is not ("sqlite" or "duckdb" or "dataverse"))
                {
                    throw new ArgumentException(
                        $"Transform connector type '{transformType}' for step '{id}' is not supported.");
                }
                transformSql = GetString(transform, "Sql", required: false);
                transformInitializeSql = GetString(transform, "InitializeSql", required: false);
                if (transformInitializeSql is not null && string.IsNullOrWhiteSpace(transformInitializeSql))
                {
                    throw new ArgumentException(
                        $"Transform.InitializeSql for step '{id}' must not be empty.");
                }
                var configuredMode = GetString(transform, "Mode", required: false);
                transformMode = (configuredMode ??
                    (transformType == "sqlite" && transformSql is not null ? "per_batch" : "per_row"))
                    .ToLowerInvariant();
                if (transformMode is not ("per_row" or "per_batch" or "whole_dataset"))
                {
                    throw new ArgumentException(
                        $"Transform.Mode for step '{id}' must be 'per_row', 'per_batch', or 'whole_dataset'.");
                }
                if (transformType is not ("sqlite" or "duckdb") && transformMode != "per_row")
                {
                    throw new ArgumentException(
                        $"Transform.Mode '{transformMode}' for step '{id}' is supported only by SQLite and DuckDB.");
                }
                if (transformType is "sqlite" or "duckdb")
                {
                    if (GetProperty(transform, "ConnectionId") is not null)
                    {
                        throw new ArgumentException(
                            $"Database Transform step '{id}' must not declare ConnectionId; its database is managed by the engine.");
                    }
                }
                else
                {
                    transformConnectionId = GetRequiredString(transform, "ConnectionId");
                    transformConnection = ResolveConnection(connections, transformConnectionId, id, "transform");
                }
                if (transformType == "dataverse" && transformConnection is null)
                {
                    throw new ArgumentException(
                        $"Dataverse transform step '{id}' requires a non-null connection in -Connections.");
                }
                transformCommandTimeoutSeconds = GetInt(transform, "CommandTimeoutSeconds", 30);
                if (transformCommandTimeoutSeconds < 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(transformCommandTimeoutSeconds),
                        $"Transform.CommandTimeoutSeconds for step '{id}' cannot be negative.");
                }
                var transformParameters = GetProperty(transform, "Parameters");
                if (transformSql is null && transformParameters is not null)
                {
                    throw new ArgumentException(
                        $"Transform.Parameters for step '{id}' requires Transform.Sql.");
                }
                if (transformType == "duckdb" &&
                    transformSql is not null &&
                    transformParameters is IDictionary parameterMap &&
                    parameterMap is not System.Collections.Specialized.IOrderedDictionary)
                {
                    throw new ArgumentException(
                        $"DuckDB Transform.Parameters for step '{id}' must be an ordered hashtable.");
                }
                transformParametersJson = SerializeJson(
                    transformSql is not null && transformParameters is null
                        ? new Hashtable()
                        : transformParameters);
                if (transformType == "dataverse" &&
                    (transformSql is not null ||
                     transformParameters is not null ||
                     transformInitializeSql is not null))
                {
                    throw new ArgumentException(
                        $"Dataverse transform step '{id}' supports PowerShell Script only; database SQL options are not supported.");
                }
                if (transformSql is not null)
                {
                    if (transformType == "dataverse")
                    {
                        throw new ArgumentException(
                            $"Dataverse transform step '{id}' supports PowerShell Script only.");
                    }
                    if (GetProperty(step, "Script") is not null)
                    {
                        throw new ArgumentException(
                            $"Database transform step '{id}' must use either Script or Transform.Sql, not both.");
                    }
                    if (transformType == "sqlite" && transformMode == "per_row")
                    {
                        throw new ArgumentException(
                            $"SQLite Transform.Sql for step '{id}' requires Mode 'per_batch' or 'whole_dataset'.");
                    }
                    if (transformMode != "per_row")
                    {
                        ReadOnlySqlValidator.ValidateSelect(
                            transformSql,
                            $"{transformType} Transform.Sql for step '{id}'");
                    }
                }
                else if (transformMode is "per_batch" or "whole_dataset")
                {
                    throw new ArgumentException(
                        $"{transformType} Transform.Mode '{transformMode}' for step '{id}' requires Transform.Sql.");
                }
                else if (GetProperty(step, "Script") is null)
                {
                    throw new ArgumentException(
                        $"Database transform step '{id}' must declare Script or a database Transform.Sql statement.");
                }

                if (transformMode == "whole_dataset")
                {
                    transformKey = GetRequiredString(transform, "Key");
                    var transformSourceOptions = new Hashtable(StringComparer.Ordinal)
                    {
                        ["Query"] = transformSql!,
                        ["Parameters"] = transformParameters ?? new Hashtable(),
                        ["CommandTimeoutSeconds"] = transformCommandTimeoutSeconds
                    };
                    var countQuery = GetString(transform, "CountQuery", required: false);
                    if (countQuery is not null)
                    {
                        throw new ArgumentException(
                            $"Whole-dataset transform '{id}' cannot use CountQuery because its staged input is not complete during run initialization; use ExpectedRecordCount if known.");
                    }
                    transformSourceOptionsJson = SerializeJson(transformSourceOptions);
                    var databaseOptions = DatabaseSourceOptions.FromJson(transformSourceOptionsJson, id);
                    databaseOptions.Validate(id);
                    expectedRecordCount = GetLong(transform, "ExpectedRecordCount", 0);
                }
                else if (GetProperty(transform, "Key") is not null ||
                         GetProperty(transform, "CountQuery") is not null ||
                         GetProperty(transform, "ExpectedRecordCount") is not null)
                {
                    throw new ArgumentException(
                        $"Transform.Key, CountQuery, and ExpectedRecordCount are only supported with Mode 'whole_dataset' on step '{id}'.");
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
            sourceFormat,
            sourceKey,
            idProperty,
            sourceOptionsJson,
            sourceBinaryResolverScriptText,
            expectedRecordCount,
            sourceConnectionId,
            sourceConnection,
            sourceCustom,
            dataverseSourceOptions,
            dataverseSourcePartitions,
            dataverseQueryType,
            targetType,
            targetOperation,
            tableName,
            targetPath,
            targetFormat,
            targetOptionsJson,
            targetBinaryReferenceScriptText,
            targetConnectionId,
            targetConnection,
            targetMaxConcurrency,
            targetMatchOnGroups,
            targetCustom,
            transformType,
            transformConnectionId,
            transformConnection,
            transformSql,
            transformInitializeSql,
            transformMode,
            transformKey,
            transformParametersJson,
            transformSourceOptionsJson,
            transformCommandTimeoutSeconds,
            scriptText,
            callbackScriptText,
            readBatchesScriptText,
            callbackSourceConnection,
            callbackTargetConnection)
        {
            ErrorRateThresholdPercent = errorRateThresholdPercent
        };
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

    private static bool IsFileOrObjectStorageType(string connectorType)
    {
        return connectorType == "file" ||
            connectorType is "azureblob" or "s3" or "adlsgen2" or "gcs";
    }

    private static GraphFileFormatInput ParseFileFormat(
        object? value,
        string stepId,
        string role)
    {
        var format = GetMap(value, $"Format for {role} step '{stepId}'", required: true)!;
        var formatType = GetRequiredString(format, "Type").Trim().ToLowerInvariant();
        var options = GetMap(
            GetProperty(format, "Options"),
            $"Format.Options for {role} step '{stepId}'",
            required: false);
        var optionsJson = SerializeJson(options);
        var extensions = ParseFormatExtensions(GetProperty(format, "Extensions"), stepId, role);
        var contentType = GetString(format, "ContentType", required: false);
        string? identity = null;
        string? readerScriptText = null;
        string? writerScriptText = null;

        if (formatType == "custom")
        {
            if (role == "source")
            {
                readerScriptText = NormalizeScriptText(GetScriptBlock(format, "Read", required: true));
                identity = GetRequiredString(format, "Identity");
                if (GetProperty(format, "Write") is not null)
                {
                    throw new ArgumentException(
                        $"Custom source format for step '{stepId}' must not configure Write.");
                }
            }
            else
            {
                writerScriptText = NormalizeScriptText(GetScriptBlock(format, "Write", required: true));
                if (GetProperty(format, "Read") is not null)
                {
                    throw new ArgumentException(
                        $"Custom target format for step '{stepId}' must not configure Read.");
                }
            }

            if (contentType is not null &&
                (!contentType.Contains('/', StringComparison.Ordinal) ||
                 contentType.Any(char.IsWhiteSpace)))
            {
                throw new ArgumentException(
                    $"Format.ContentType for custom format on step '{stepId}' must be a MIME type.");
            }
        }
        else
        {
            if (formatType is not ("jsonl" or "csv" or "parquet" or "excel" or "binary"))
            {
                throw new ArgumentException(
                    $"File format '{formatType}' for {role} step '{stepId}' is not supported.");
            }

            var handler = RecordFileFormatRegistry.GetRequired(formatType);
            if (role == "source" && !handler.SupportsSource)
            {
                throw new ArgumentException(
                    $"File format '{formatType}' does not support sources.");
            }
            if (role == "target" && !handler.SupportsTarget)
            {
                throw new ArgumentException(
                    $"File format '{formatType}' does not support targets.");
            }
            if (extensions.Length > 0 || contentType is not null ||
                GetProperty(format, "Read") is not null ||
                GetProperty(format, "Write") is not null ||
                GetProperty(format, "Identity") is not null)
            {
                throw new ArgumentException(
                    $"Format.Extensions, Format.ContentType, Format.Identity, Read, and Write are only supported by custom formats on step '{stepId}'.");
            }
        }

        return new GraphFileFormatInput(
            formatType,
            optionsJson,
            extensions,
            contentType,
            identity,
            readerScriptText,
            writerScriptText);
    }

    private static string[] ParseFormatExtensions(object? value, string stepId, string role)
    {
        if (value is null)
        {
            return Array.Empty<string>();
        }

        if (value is string)
        {
            throw new ArgumentException(
                $"Format.Extensions for {role} step '{stepId}' must be an array of extensions.");
        }

        var items = LanguagePrimitives.GetEnumerable(value)?.Cast<object?>().ToArray()
            ?? throw new ArgumentException(
                $"Format.Extensions for {role} step '{stepId}' must be an array of extensions.");
        var extensions = items.Select(item =>
        {
            var extension = Unwrap(item) as string;
            if (string.IsNullOrWhiteSpace(extension) ||
                extension[0] != '.' ||
                extension.Contains('/') ||
                extension.Contains('\\'))
            {
                throw new ArgumentException(
                    $"Format.Extensions for {role} step '{stepId}' must contain simple extensions beginning with '.'.");
            }
            return extension.Trim();
        }).ToArray();
        if (extensions.Distinct(StringComparer.OrdinalIgnoreCase).Count() != extensions.Length)
        {
            throw new ArgumentException(
                $"Format.Extensions for {role} step '{stepId}' must be unique.");
        }
        return extensions;
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
            if (step.Type == "transform" && step.TransformMode == "whole_dataset")
            {
                if (step.Inputs.Length != 1)
                {
                    throw new ArgumentException(
                        $"Whole-dataset {step.TransformType} transform '{step.Id}' must declare one staged Target input.");
                }
                var stagedInput = byId[step.Inputs[0]];
                if (stagedInput.Type != "target" ||
                    stagedInput.TargetType != step.TransformType ||
                    (!ReferenceEquals(stagedInput.TargetConnection, step.TransformConnection) &&
                     (stagedInput.TargetConnection is not null || step.TransformConnection is not null)))
                {
                    throw new ArgumentException(
                        $"Whole-dataset {step.TransformType} transform '{step.Id}' must depend on a matching database Target using its managed database.");
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
            if (step.DataverseSourcePartitions is not null && step.RowDependencies.Length > 0)
            {
                throw new ArgumentException(
                    $"Partitioned Dataverse source step '{step.Id}' must be a root source without keyed parent inputs.");
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
                         StringComparison.Ordinal)) &&
                     !(consumer.Type == "transform" && consumer.TransformMode == "whole_dataset")))
            {
                throw new ArgumentException(
                    $"Target step '{step.Id}' can only be consumed by Join relationship inputs or whole-dataset database transforms.");
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
                     step.Type == "source" &&
                     DatabaseConnectorSupport.IsDatabaseConnectorType(step.SourceType)))
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

    private static HierarchySourceLevel CreateGraphSourceLevel(
        GraphStepInput input,
        string binaryStagingRoot)
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
            identity: input.SourceCustom?.Identity ?? input.SourceFormat?.Identity,
            databaseConnection: input.SourceConnection as DbConnection,
            connectorConnection: input.SourceConnection,
            binaryStagingRoot: binaryStagingRoot,
            idProperty: input.IdProperty,
            formatType: input.SourceFormat?.Type,
            formatOptionsJson: input.SourceFormat?.OptionsJson,
            formatExtensions: input.SourceFormat?.Extensions);
    }

    private static HierarchySourceLevel CreateGraphWholeDatasetTransformSourceLevel(GraphStepInput input)
    {
        return new HierarchySourceLevel(
            input.Id,
            input.TransformType!,
            path: null,
            keyProperty: input.TransformKey,
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: input.ExpectedRecordCount,
            children: null,
            optionsJson: input.TransformSourceOptionsJson
                ?? throw new InvalidOperationException(
                    $"Whole-dataset {input.TransformType} transform '{input.Id}' has no source options."),
            connectionId: input.TransformConnectionId,
            databaseConnection: input.TransformConnection as DbConnection);
    }

    private static Func<CancellationToken, Task>? CreateTargetFinalizer(
        GraphStepInput input,
        PowerShellScriptExecutor executor,
        bool whatIf,
        string? runId,
        string objectStagingRoot,
        string binaryStagingRoot)
    {
        if (whatIf || input.Type != "target")
        {
            return null;
        }

        if (input.TargetType == "file")
        {
            var format = input.TargetFormat
                ?? throw new InvalidOperationException($"File target '{input.Id}' has no format definition.");
            var targetRunId = runId
                ?? throw new InvalidOperationException($"File target '{input.Id}' requires a run ID for finalization.");
            if (format.Type == "custom")
            {
                return cancellationToken =>
                {
                    PowerShellCustomFormatTargetWriter.FinalizeSingleFileTarget(
                        executor,
                        input.TargetPath!,
                        targetRunId,
                        input.Id,
                        format.WriterScriptText!,
                        format.OptionsJson,
                        binaryStagingRoot,
                        objectKey: null,
                        cancellationToken);
                    return Task.CompletedTask;
                };
            }

            return cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileBatchTargetWriter.FinalizeSingleFileTarget(
                    input.TargetPath!,
                    targetRunId,
                    input.Id,
                    format.Type,
                    format.OptionsJson,
                    binaryStagingRoot);
                return Task.CompletedTask;
            };
        }

        if (input.TargetType is "azureblob" or "s3" or "adlsgen2" or "gcs")
        {
            var format = input.TargetFormat
                ?? throw new InvalidOperationException(
                    $"Object storage target '{input.Id}' has no format definition.");
            var targetRunId = runId
                ?? throw new InvalidOperationException(
                    $"Object storage target '{input.Id}' requires a run ID for finalization.");
            if (format.Type == "custom")
            {
                return async cancellationToken =>
                {
                    var stagingPath = ObjectStorageTargetWriter.GetStagingPath(
                        objectStagingRoot,
                        targetRunId,
                        input.Id,
                        format.Type,
                        format.Extensions);
                    PowerShellCustomFormatTargetWriter.FinalizeSingleFileTarget(
                        executor,
                        stagingPath,
                        targetRunId,
                        input.Id,
                        format.WriterScriptText!,
                        format.OptionsJson,
                        binaryStagingRoot,
                        input.TargetPath,
                        cancellationToken);
                    await ObjectStorageTargetWriter.UploadFinalizedFileAsync(
                            input.TargetConnection!,
                            input.TargetType,
                            input.TargetPath!,
                            input.Id,
                            stagingPath,
                            input.TargetOptionsJson ?? "{}",
                            format.ContentType,
                            cancellationToken)
                        .ConfigureAwait(false);
                };
            }

            return cancellationToken => ObjectStorageTargetWriter.FinalizeAsync(
                input.TargetConnection!,
                input.TargetType,
                input.TargetPath!,
                input.Id,
                targetRunId,
                input.TargetOptionsJson ?? "{}",
                format.Type,
                format.Extensions,
                format.OptionsJson,
                format.ContentType,
                objectStagingRoot,
                binaryStagingRoot,
                cancellationToken);
        }

        if (input.TargetType == "sharepoint")
        {
            return cancellationToken =>
                SharePointTargetWriter.FinalizeAsync(
                    (HttpClient)input.TargetConnection!,
                    input.Id,
                    runId ?? throw new InvalidOperationException(
                        $"SharePoint target '{input.Id}' requires a run ID for finalization."),
                    input.TargetOptionsJson ?? "{}",
                    objectStagingRoot,
                    binaryStagingRoot,
                    cancellationToken);
        }

        return null;
    }

    private static Func<CancellationToken, Task<long>>? CreateExpectedRecordCountResolver(
        GraphStepInput input,
        PowerShellScriptExecutor executor,
        HierarchySourceLevel? sourceLevel,
        object? dataverseSourceConnection)
    {
        if (input.Type != "source" ||
            input.ExpectedRecordCount > 0 ||
            sourceLevel is null)
        {
            return null;
        }

        Func<CancellationToken, Task<long>>? resolver = null;
        if (input.SourceType == "file" && input.SourceFormat?.Type == "parquet")
        {
            resolver = cancellationToken =>
                HierarchyRecordReaderFactory.GetMetadataRecordCountAsync(sourceLevel, cancellationToken);
        }
        else if (input.SourceType == "file" && input.SourceFormat?.Type == "excel")
        {
            var options = ExcelSourceOptions.FromJson(input.SourceFormat.OptionsJson, input.Id);
            resolver = cancellationToken =>
                ExcelHierarchyRecordReader.CountRecordsAsync(
                    sourceLevel,
                    options,
                    cancellationToken);
        }
        else if (input.SourceType == "salesforce")
        {
            var options = SalesforceSourceOptions.FromJson(input.SourceOptionsJson, input.Id);
            if (!string.IsNullOrWhiteSpace(options.Soql))
            {
                return null;
            }
            resolver = cancellationToken =>
                SalesforceHierarchyRecordReader.CountRecordsAsync(
                    sourceLevel,
                    options,
                    cancellationToken);
        }
        else if (input.SourceType == "hubspot")
        {
            var options = HubSpotSourceOptions.FromJson(input.SourceOptionsJson, input.Id);
            if (!options.UsesSearch)
            {
                return null;
            }
            resolver = cancellationToken =>
                HubSpotHierarchyRecordReader.CountRecordsAsync(
                    sourceLevel,
                    options,
                    cancellationToken);
        }
        else if (input.SourceType == "zendesk")
        {
            var options = ZendeskSourceOptions.FromJson(input.SourceOptionsJson, input.Id);
            if (string.IsNullOrWhiteSpace(options.Query))
            {
                return null;
            }
            resolver = cancellationToken =>
                ZendeskHierarchyRecordReader.CountRecordsAsync(
                    sourceLevel,
                    options,
                    cancellationToken);
        }
        else if (input.SourceType == "mongodb")
        {
            var options = MongoSourceOptions.FromJson(input.SourceOptionsJson, input.Id);
            var connection = input.SourceConnection
                ?? throw new InvalidOperationException(
                    $"MongoDB source step '{input.Id}' has no connection for its count query.");
            var database = MongoConnectionSupport.GetDatabase(
                connection,
                options.Database,
                $"source '{input.Id}'");
            resolver = cancellationToken =>
                MongoHierarchyRecordReader.CountRecordsAsync(
                    database,
                    input.Id,
                    options,
                    cancellationToken);
        }
        else if (DatabaseConnectorSupport.IsDatabaseConnectorType(input.SourceType))
        {
            var options = DatabaseSourceOptions.FromJson(input.SourceOptionsJson, input.Id);
            if (input.SourceType == "sqlserver" && string.IsNullOrWhiteSpace(options.CountQuery))
            {
                return null;
            }
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
                dataverseSourceConnection
                    ?? throw new InvalidOperationException(
                        $"Dataverse source step '{input.Id}' has no effective connection."),
                input.DataverseSourceOptions!,
                input.DataverseQueryType,
                input.DataverseSourcePartitions);
            resolver = async cancellationToken =>
            {
                if (definition.Partitions.Count == 0)
                {
                    return await PowerShellDataverseHierarchyRecordReader.CountRecordsAsync(
                        executor,
                        input.Id,
                        input.SourceKey,
                        definition,
                        cancellationToken).ConfigureAwait(false);
                }

                long total = 0;
                foreach (var partition in definition.Partitions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var partitionDefinition = new PowerShellDataverseSourceDefinition(
                        definition.Connection,
                        partition.Options,
                        definition.QueryType);
                    total = checked(total + await PowerShellDataverseHierarchyRecordReader.CountRecordsAsync(
                        executor,
                        $"{input.Id}/{partition.Id}",
                        input.SourceKey,
                        partitionDefinition,
                        cancellationToken).ConfigureAwait(false));
                }

                return total;
            };
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
            (step.TargetType is not "custom" &&
             !DatabaseConnectorSupport.IsDatabaseConnectorType(step.TargetType!) &&
             step.TargetType is not ("azureblob" or "s3" or "adlsgen2" or "gcs" or "mongodb")))
        {
            return null;
        }
        var capacity = step.TargetType == "custom"
            ? step.TargetCustom?.MaxConcurrency ?? 1
            : 1;
        var key = DatabaseConnectorSupport.IsDatabaseConnectorType(step.TargetType!)
            ? $"database:{step.TargetConnectionId}"
            : step.TargetType == "mongodb"
                ? $"mongodb:{step.TargetConnectionId}"
            : step.TargetType is "azureblob" or "s3" or "adlsgen2" or "gcs"
                ? $"object:{step.TargetType}:{step.TargetConnectionId}"
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

    private static int ParsePositiveTargetInteger(
        IDictionary target,
        string name,
        int defaultValue,
        string stepId)
    {
        var raw = Unwrap(GetProperty(target, name));
        if (raw is null)
        {
            return defaultValue;
        }

        int value;
        try
        {
            value = LanguagePrimitives.ConvertTo<int>(raw);
        }
        catch (PSInvalidCastException exception)
        {
            throw new ArgumentException(
                $"Dataverse target '{stepId}' property '{name}' must be a positive integer.",
                name,
                exception);
        }
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException(
                name,
                $"Dataverse target '{stepId}' property '{name}' must be positive.");
        }
        if (name.Equals("BatchSize", StringComparison.OrdinalIgnoreCase) && value > 1000)
        {
            throw new ArgumentOutOfRangeException(
                name,
                $"Dataverse target '{stepId}' batch size cannot exceed 1000.");
        }

        return value;
    }

    private static IReadOnlyDictionary<string, double> CalculatePipelineDispatchWeights(
        IReadOnlyList<GraphStepInput> inputs)
    {
        var parentsByStep = inputs.ToDictionary(
            step => step.Id,
            step => step.Inputs
                .Concat(step.CompletionDependencies)
                .Concat(step.RowDependencies.Select(dependency => dependency.Job))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            StringComparer.Ordinal);
        var weights = inputs.ToDictionary(step => step.Id, _ => 1d, StringComparer.Ordinal);

        foreach (var target in inputs.Where(step => step.Type == "target"))
        {
            var ancestorDistances = new Dictionary<string, int>(StringComparer.Ordinal);
            var pending = new Queue<(string StepId, int Distance)>();
            pending.Enqueue((target.Id, 0));
            while (pending.TryDequeue(out var current))
            {
                foreach (var parentId in parentsByStep[current.StepId])
                {
                    var distance = current.Distance + 1;
                    if (ancestorDistances.TryGetValue(parentId, out var previousDistance) &&
                        previousDistance <= distance)
                    {
                        continue;
                    }

                    ancestorDistances[parentId] = distance;
                    pending.Enqueue((parentId, distance));
                }
            }

            var targetImportance = 1 + Math.Log2(ancestorDistances.Count + 1);
            weights[target.Id] += targetImportance;
            foreach (var (ancestorId, distance) in ancestorDistances)
            {
                weights[ancestorId] += targetImportance / (distance + 1);
            }
        }

        return weights;
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
        GraphFileFormatInput? SourceFormat,
        string SourceKey,
        string IdProperty,
        string SourceOptionsJson,
        string? SourceBinaryResolverScriptText,
        long ExpectedRecordCount,
        string? SourceConnectionId,
        object? SourceConnection,
        CustomConnector? SourceCustom,
        IDictionary? DataverseSourceOptions,
        DataverseSourcePartition[]? DataverseSourcePartitions,
        string? DataverseQueryType,
        string? TargetType,
        string? TargetOperation,
        string? TableName,
        string? TargetPath,
        GraphFileFormatInput? TargetFormat,
        string? TargetOptionsJson,
        string? TargetBinaryReferenceScriptText,
        string? TargetConnectionId,
        object? TargetConnection,
        int TargetMaxConcurrency,
        string[][] TargetMatchOnGroups,
        CustomConnector? TargetCustom,
        string? TransformType,
        string? TransformConnectionId,
        object? TransformConnection,
        string? TransformSql,
        string? TransformInitializeSql,
        string? TransformMode,
        string TransformKey,
        string? TransformParametersJson,
        string? TransformSourceOptionsJson,
        int TransformCommandTimeoutSeconds,
        string? ScriptText,
        string? CallbackScriptText,
        string? ReadBatchesScriptText,
        object? CallbackSourceConnection,
        object? CallbackTargetConnection)
    {
        public double? ErrorRateThresholdPercent { get; init; }
    }

    private sealed record GraphFileFormatInput(
        string Type,
        string OptionsJson,
        string[] Extensions,
        string? ContentType,
        string? Identity,
        string? ReaderScriptText,
        string? WriterScriptText);

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
        var cursor = checkpoint.ResumeCursorJson;
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
