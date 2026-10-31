using System.Management.Automation;
using Migration4Dataverse.Engine.PowerShell;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.Tests.PowerShell;

public sealed class ClearMigrationTargetTuningCommandTests
{
    [Fact]
    public void ClearCmdletSupportsWhatIfStepAndAllProfiles()
    {
        using var database = new TemporaryDatabase();
        var store = new SqliteMigrationStore(database.Path);
        store.SaveDataverseTargetTuningProfile("AccountsTarget", 250, 8);
        store.SaveDataverseTargetTuningProfile("ContactsTarget", 100, 4);
        using var powershell = System.Management.Automation.PowerShell.Create();
        powershell.AddCommand("Import-Module")
            .AddParameter("Name", typeof(InvokeMigrationCommand).Assembly.Location)
            .Invoke();
        Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));

        powershell.Commands.Clear();
        var whatIfResult = powershell.AddCommand("Clear-M4dMigrationTargetTuning")
            .AddParameter("DatabasePath", database.Path)
            .AddParameter("StepId", "AccountsTarget")
            .AddParameter("WhatIf", true)
            .Invoke();
        Assert.Empty(whatIfResult);
        Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
        Assert.NotNull(store.GetDataverseTargetTuningProfile("AccountsTarget"));

        powershell.Commands.Clear();
        var stepClearResult = powershell.AddCommand("Clear-M4dMigrationTargetTuning")
            .AddParameter("DatabasePath", database.Path)
            .AddParameter("StepId", "AccountsTarget")
            .AddParameter("Confirm", false)
            .Invoke();
        Assert.Equal(1L, Assert.Single(stepClearResult).BaseObject);
        Assert.Null(store.GetDataverseTargetTuningProfile("AccountsTarget"));
        Assert.NotNull(store.GetDataverseTargetTuningProfile("ContactsTarget"));

        powershell.Commands.Clear();
        var allClearResult = powershell.AddCommand("Clear-M4dMigrationTargetTuning")
            .AddParameter("DatabasePath", database.Path)
            .AddParameter("All", true)
            .AddParameter("Confirm", false)
            .Invoke();
        Assert.Equal(1L, Assert.Single(allClearResult).BaseObject);
        Assert.Null(store.GetDataverseTargetTuningProfile("ContactsTarget"));
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"migration4dataverse-clear-tuning-{Guid.NewGuid():N}");

        public TemporaryDatabase()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "migration.sqlite3");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
