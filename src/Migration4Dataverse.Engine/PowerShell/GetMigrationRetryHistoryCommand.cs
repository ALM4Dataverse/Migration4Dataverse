using System.Management.Automation;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.PowerShell;

[Cmdlet(VerbsCommon.Get, "MigrationRetryHistory")]
[OutputType(typeof(MigrationBatchRetryHistory))]
public sealed class GetMigrationRetryHistoryCommand : MigrationStoreCmdletBase
{
    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string RunId { get; set; } = string.Empty;

    [Parameter]
    [ValidateRange(1, 1000)]
    public int Limit { get; set; } = 100;

    [Parameter]
    [ValidateRange(0, int.MaxValue)]
    public int Offset { get; set; }

    protected override void ProcessRecord()
    {
        WriteObject(
            OpenStore().GetDashboardBatchRetryHistory(RunId, Limit, Offset),
            enumerateCollection: true);
    }
}
