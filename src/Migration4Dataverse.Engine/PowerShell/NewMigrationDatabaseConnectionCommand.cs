using System.Data.Common;
using System.Management.Automation;
using Migration4Dataverse.Engine.Connectors;

namespace Migration4Dataverse.Engine.PowerShell;

[Cmdlet(VerbsCommon.New, "MigrationDatabaseConnection")]
[OutputType(typeof(DbConnection))]
public sealed class NewMigrationDatabaseConnectionCommand : PSCmdlet
{
    [Parameter(Mandatory = true)]
    [ValidateSet("SQLite", "DuckDB")]
    public string Provider { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    [AllowEmptyString]
    public string ConnectionString { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        WriteObject(MigrationDatabaseConnectionFactory.Create(Provider, ConnectionString));
    }
}
