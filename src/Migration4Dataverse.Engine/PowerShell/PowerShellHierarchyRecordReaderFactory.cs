using Migration4Dataverse.Engine.Connectors;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.PowerShell;

public sealed class PowerShellHierarchyRecordReaderFactory
{
    private readonly PowerShellScriptExecutor _executor;
    private readonly IReadOnlyDictionary<string, PowerShellSourceCallbackDefinition> _customSources;
    private readonly IReadOnlyDictionary<string, PowerShellDataverseSourceDefinition> _dataverseSources;
    private readonly IReadOnlyDictionary<string, PowerShellCustomFormatSourceDefinition> _customFormatSources;

    public PowerShellHierarchyRecordReaderFactory(
        PowerShellScriptExecutor executor,
        IReadOnlyDictionary<string, PowerShellSourceCallbackDefinition> customSources)
        : this(executor, customSources, new Dictionary<string, PowerShellDataverseSourceDefinition>())
    {
    }

    public PowerShellHierarchyRecordReaderFactory(
        PowerShellScriptExecutor executor,
        IReadOnlyDictionary<string, PowerShellSourceCallbackDefinition> customSources,
        IReadOnlyDictionary<string, PowerShellDataverseSourceDefinition> dataverseSources)
        : this(
            executor,
            customSources,
            dataverseSources,
            new Dictionary<string, PowerShellCustomFormatSourceDefinition>())
    {
    }

    public PowerShellHierarchyRecordReaderFactory(
        PowerShellScriptExecutor executor,
        IReadOnlyDictionary<string, PowerShellSourceCallbackDefinition> customSources,
        IReadOnlyDictionary<string, PowerShellDataverseSourceDefinition> dataverseSources,
        IReadOnlyDictionary<string, PowerShellCustomFormatSourceDefinition> customFormatSources)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(customSources);
        ArgumentNullException.ThrowIfNull(dataverseSources);
        ArgumentNullException.ThrowIfNull(customFormatSources);

        _executor = executor;
        _customSources = new Dictionary<string, PowerShellSourceCallbackDefinition>(
            customSources,
            StringComparer.Ordinal);
        _dataverseSources = new Dictionary<string, PowerShellDataverseSourceDefinition>(
            dataverseSources,
            StringComparer.Ordinal);
        _customFormatSources = new Dictionary<string, PowerShellCustomFormatSourceDefinition>(
            customFormatSources,
            StringComparer.Ordinal);
    }

    public Func<HierarchySourceLevel, IHierarchyRecordReader> CreateDelegate()
    {
        return CreateReader;
    }

    public Func<HierarchySourceLevel, string?, IHierarchyRecordReader> CreateCheckpointAwareDelegate()
    {
        return CreateReader;
    }

    public IHierarchyRecordReader CreateReader(HierarchySourceLevel level)
    {
        ArgumentNullException.ThrowIfNull(level);
        if (level.Type != "custom")
        {
            if (level.Type == "dataverse")
            {
                if (!_dataverseSources.TryGetValue(level.StepId, out var dataverseSource))
                {
                    throw new InvalidOperationException(
                        $"Dataverse source job '{level.StepId}' has no query definition.");
                }

                return new PowerShellDataverseHierarchyRecordReader(
                    _executor,
                    level,
                    dataverseSource);
            }

            if (level.Type == "file" && level.FormatType == "custom")
            {
                if (!_customFormatSources.TryGetValue(level.StepId, out var customFormatSource))
                {
                    throw new InvalidOperationException(
                        $"Custom file format source '{level.StepId}' has no PowerShell reader.");
                }
                if (!string.Equals(customFormatSource.Identity, level.Identity, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Custom file format identity for source '{level.StepId}' changed after its hierarchy was created.");
                }

                return new PowerShellCustomFormatHierarchyRecordReader(
                    _executor,
                    level,
                    customFormatSource.ScriptText);
            }

            return HierarchyRecordReaderFactory.CreateFileReader(level, CreateReader);
        }
        if (!_customSources.TryGetValue(level.StepId, out var customSource))
        {
            throw new InvalidOperationException(
                $"Custom source job '{level.StepId}' has no PowerShell callback definition.");
        }
        if (!string.Equals(customSource.Identity, level.Identity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Custom source identity for job '{level.StepId}' changed after its hierarchy was created.");
        }

        return new PowerShellPagedHierarchyRecordReader(
            _executor,
            level,
            customSource.Connection,
            customSource.ScriptText,
            customSource.PageSize);
    }

    public IHierarchyRecordReader CreateReader(HierarchySourceLevel level, string? checkpointJson)
    {
        ArgumentNullException.ThrowIfNull(level);
        if (level.Type == "dataverse" &&
            _dataverseSources.TryGetValue(level.StepId, out var definition) &&
            definition.Partitions.Count > 0)
        {
            if (definition.Partitions.Count > _executor.MaxRunspaces)
            {
                throw new ArgumentException(
                    $"Partitioned Dataverse source '{level.StepId}' has {definition.Partitions.Count} partitions, " +
                    $"but the executor has only {_executor.MaxRunspaces} runspaces. Increase Invoke-M4dMigration -MaxDegreeOfParallelism " +
                    "or reduce the number of partitions.");
            }

            return new PowerShellDataversePartitionedHierarchyRecordReader(
                _executor,
                level,
                definition,
                checkpointJson);
        }

        return CreateReader(level);
    }
}
