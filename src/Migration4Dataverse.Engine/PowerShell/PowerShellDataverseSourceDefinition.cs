using System.Collections;

namespace Migration4Dataverse.Engine.PowerShell;

public sealed class PowerShellDataverseSourceDefinition
{
    public PowerShellDataverseSourceDefinition(
        object connection,
        IDictionary options,
        string queryType)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryType);

        Connection = connection;
        Options = options;
        QueryType = queryType;
    }

    public object Connection { get; }

    public IDictionary Options { get; }

    public string QueryType { get; }
}
