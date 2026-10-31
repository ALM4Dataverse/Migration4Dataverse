using System.Collections;
using System.Management.Automation;
using System.Text.Json;

namespace Migration4Dataverse.Engine.PowerShell;

[Cmdlet(VerbsCommon.Get, "M4dMigrationConfigurationPreview")]
[OutputType(typeof(JsonElement))]
public sealed class GetMigrationConfigurationPreviewCommand : PSCmdlet
{
    [Parameter(Mandatory = true)]
    [ValidateNotNull]
    public PSObject[] Steps { get; set; } = [];

    [Parameter]
    public IDictionary? Connections { get; set; }

    protected override void ProcessRecord()
    {
        var connections = Connections ?? new Hashtable(StringComparer.Ordinal);
        var json = MigrationConfigurationSnapshot.CreateJson(Steps, connections);
        using var document = JsonDocument.Parse(json);
        WriteObject(document.RootElement.Clone());
    }
}
