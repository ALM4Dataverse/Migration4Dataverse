using Migration4Dataverse.Engine.Connectors;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.PowerShell;

public sealed class PowerShellHierarchyRecordReaderFactory
{
    private readonly PowerShellScriptExecutor _executor;
    private readonly IReadOnlyDictionary<string, PowerShellSourceCallbackDefinition> _customSources;
    private readonly IReadOnlyDictionary<string, PowerShellDataverseSourceDefinition> _dataverseSources;

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
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(customSources);
        ArgumentNullException.ThrowIfNull(dataverseSources);

        _executor = executor;
        _customSources = new Dictionary<string, PowerShellSourceCallbackDefinition>(
            customSources,
            StringComparer.Ordinal);
        _dataverseSources = new Dictionary<string, PowerShellDataverseSourceDefinition>(
            dataverseSources,
            StringComparer.Ordinal);
    }

    public Func<HierarchySourceLevel, IHierarchyRecordReader> CreateDelegate()
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

            return HierarchyRecordReaderFactory.CreateFileReader(level);
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
}
