using System.Management.Automation;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.PowerShell;

[Cmdlet(VerbsCommon.Get, "M4dMigrationBatch")]
[OutputType(typeof(MigrationDashboardBatch))]
public sealed class GetMigrationBatchCommand : MigrationStoreCmdletBase
{
    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string RunId { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string StepId { get; set; } = string.Empty;

    [Parameter]
    [ValidateRange(1, 1000)]
    public int Limit { get; set; } = 100;

    protected override void ProcessRecord()
    {
        WriteObject(
            OpenStore().GetDashboardBatches(RunId, StepId, Limit),
            enumerateCollection: true);
    }
}
