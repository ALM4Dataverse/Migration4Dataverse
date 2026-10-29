using System.Collections;

namespace Migration4Dataverse.Engine.PowerShell;

public sealed class PowerShellDataverseSourceDefinition
{
    public PowerShellDataverseSourceDefinition(
        object connection,
        IDictionary options,
        string queryType,
        IReadOnlyList<DataverseSourcePartition>? partitions = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryType);

        Connection = connection;
        Options = options;
        QueryType = queryType;
        Partitions = partitions?.ToArray() ?? Array.Empty<DataverseSourcePartition>();
    }

    public object Connection { get; }

    public IDictionary Options { get; }

    public string QueryType { get; }

    public IReadOnlyList<DataverseSourcePartition> Partitions { get; }
}

public sealed record DataverseSourcePartition(string Id, IDictionary Options);
