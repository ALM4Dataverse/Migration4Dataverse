using System.Management.Automation;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.PowerShell;

[Cmdlet(VerbsLifecycle.Start, "MigrationDatabaseServer")]
[OutputType(typeof(void))]
public sealed class StartMigrationDatabaseServerCommand : PSCmdlet
{
    private CancellationTokenSource? _cancellation;

    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrEmpty]
    public string DatabasePath { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        _cancellation = new CancellationTokenSource();
        try
        {
            MigrationStoreFactory.RunServer(DatabasePath, _cancellation.Token);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    protected override void StopProcessing()
    {
        _cancellation?.Cancel();
    }
}
