using System.Management.Automation;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.PowerShell;

[Cmdlet(
    VerbsCommon.Clear,
    "MigrationTargetTuning",
    DefaultParameterSetName = "Step",
    SupportsShouldProcess = true,
    ConfirmImpact = ConfirmImpact.Medium)]
[OutputType(typeof(long))]
public sealed class ClearMigrationTargetTuningCommand : PSCmdlet
{
    private const string StepParameterSet = "Step";
    private const string AllParameterSet = "All";

    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string DatabasePath { get; set; } = string.Empty;

    [Parameter(Mandatory = true, ParameterSetName = StepParameterSet)]
    [ValidateNotNullOrEmpty]
    public string StepId { get; set; } = string.Empty;

    [Parameter(Mandatory = true, ParameterSetName = AllParameterSet)]
    public SwitchParameter All { get; set; }

    protected override void ProcessRecord()
    {
        var databasePath = Path.GetFullPath(DatabasePath);
        if (Directory.Exists(databasePath))
        {
            throw new ArgumentException("DatabasePath must refer to a SQLite database file.", nameof(DatabasePath));
        }
        if (!File.Exists(databasePath))
        {
            WriteObject(0L);
            return;
        }

        var target = All
            ? $"all Dataverse target tuning profiles in '{databasePath}'"
            : $"Dataverse target tuning profile for step '{StepId}' in '{databasePath}'";
        if (!ShouldProcess(target, "Clear"))
        {
            return;
        }

        var store = new SqliteMigrationStore(databasePath);
        var deletedProfiles = All
            ? store.ClearDataverseTargetTuningProfiles()
            : store.ClearDataverseTargetTuningProfile(StepId);
        WriteObject(deletedProfiles);
    }
}
