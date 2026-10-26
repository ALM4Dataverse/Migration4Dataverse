using System.Management.Automation;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.PowerShell;

[Cmdlet(VerbsCommon.Get, "MigrationRun")]
[OutputType(typeof(MigrationRunSummary))]
public sealed class GetMigrationRunCommand : PSCmdlet
{
    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string DatabasePath { get; set; } = string.Empty;

    [Parameter]
    [ValidateNotNullOrEmpty]
    public string? RunId { get; set; }

    [Parameter]
    [ValidateRange(1, 1000)]
    public int Limit { get; set; } = 50;

    protected override void ProcessRecord()
    {
        var store = MigrationStoreFactory.Open(DatabasePath);
        if (!string.IsNullOrWhiteSpace(RunId))
        {
            WriteObject(store.GetDashboardRun(RunId));
            return;
        }

        WriteObject(store.GetDashboardRuns(Limit), enumerateCollection: true);
    }
}
