using System.Management.Automation;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.PowerShell;

[Cmdlet(VerbsCommon.Get, "M4dMigrationStep")]
[OutputType(typeof(MigrationDashboardStep))]
public sealed class GetMigrationStepCommand : MigrationStoreCmdletBase
{
    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string RunId { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string StepId { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        var step = OpenStore()
            .GetDashboardRun(RunId)
            .Steps
            .FirstOrDefault(candidate => string.Equals(candidate.Id, StepId, StringComparison.Ordinal));
        if (step is null)
        {
            throw new ItemNotFoundException($"Run '{RunId}' has no step '{StepId}'.");
        }

        WriteObject(step);
    }
}
