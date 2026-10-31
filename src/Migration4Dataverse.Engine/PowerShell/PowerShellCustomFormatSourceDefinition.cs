namespace Migration4Dataverse.Engine.PowerShell;

public sealed class PowerShellCustomFormatSourceDefinition
{
    public PowerShellCustomFormatSourceDefinition(string identity, string scriptText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptText);
        Identity = identity;
        ScriptText = scriptText;
    }

    public string Identity { get; }

    public string ScriptText { get; }
}
