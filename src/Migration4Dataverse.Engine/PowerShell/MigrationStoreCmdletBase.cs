using System.Management.Automation;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.PowerShell;

public abstract class MigrationStoreCmdletBase : PSCmdlet
{
    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string DatabasePath { get; set; } = string.Empty;

    protected string GetDatabasePath()
    {
        var path = Path.GetFullPath(DatabasePath);
        if (Directory.Exists(path))
        {
            throw new ArgumentException("DatabasePath must refer to a SQLite database file.", nameof(DatabasePath));
        }
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The migration database does not exist.", path);
        }

        return path;
    }

    protected IMigrationStore OpenStore()
    {
        return MigrationStoreFactory.Open(GetDatabasePath());
    }
}
