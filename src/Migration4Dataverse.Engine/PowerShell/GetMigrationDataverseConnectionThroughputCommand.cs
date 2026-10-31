using System.Management.Automation;

namespace Migration4Dataverse.Engine.PowerShell;

[Cmdlet(VerbsCommon.Get, "M4dMigrationDataverseConnectionThroughput")]
[OutputType(typeof(DataverseConnectionPoolDashboardSnapshot))]
public sealed class GetMigrationDataverseConnectionThroughputCommand : MigrationStoreCmdletBase
{
    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string RunId { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        _ = OpenStore().GetDashboardRun(RunId);
        WriteObject(
            DataverseConnectionPool.GetDashboardSnapshots(GetDatabasePath(), RunId),
            enumerateCollection: true);
    }
}
