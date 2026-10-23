namespace Migration4Dataverse.Engine.PowerShell;

public sealed class PowerShellSourceCallbackDefinition
{
    public PowerShellSourceCallbackDefinition(
        object connection,
        string identity,
        string scriptText,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptText);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        Connection = connection;
        Identity = identity;
        ScriptText = scriptText;
        PageSize = pageSize;
    }

    public object Connection { get; }

    public string Identity { get; }

    public string ScriptText { get; }

    public int PageSize { get; }
}
