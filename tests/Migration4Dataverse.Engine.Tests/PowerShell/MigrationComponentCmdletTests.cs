using System.Management.Automation;
using Migration4Dataverse.Engine.PowerShell;
using PowerShellRuntime = System.Management.Automation.PowerShell;

namespace Migration4Dataverse.Engine.Tests.PowerShell;

public sealed class MigrationComponentCmdletTests
{
    [Fact]
    public void ListsDocumentedSourcesTargetsAndActionsWithHelp()
    {
        using var powershell = ImportEngineCmdlets();

        var components = InvokeAll(powershell);

        Assert.Contains(components, component =>
            component.Category == "Source" && component.Name == "file" &&
            component.Help.Contains("local file", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(components, component =>
            component.Category == "Target" && component.Name == "dataverse" &&
            component.Help.Contains("Dataverse tables", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(components, component =>
            component.Category == "Action" && component.Name == "transform" &&
            component.Help.Contains("Map input", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReturnsFullMarkdownHelpForTheSelectedComponent()
    {
        using var powershell = ImportEngineCmdlets();
        powershell.AddCommand("Get-M4dMigrationComponent")
            .AddParameter("Category", "Source")
            .AddParameter("Name", "file")
            .AddParameter("Detailed", true);

        var component = Assert.IsType<MigrationComponentHelp>(
            Assert.Single(powershell.Invoke()).BaseObject);

        Assert.Contains("# File formats", component.Help, StringComparison.Ordinal);
        Assert.Equal("docs/connectors/file-formats.md", component.Documentation);
    }

    [Fact]
    public void ExposesDescriptivePowerShellHelpForCliCommandRegistration()
    {
        using var powershell = ImportEngineCmdlets();
        powershell.AddCommand("Get-Help")
            .AddParameter("Name", "Get-M4dMigrationComponent")
            .AddParameter("Full", true);

        var help = Assert.Single(powershell.Invoke());

        Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
        Assert.Contains(
            "Lists migration sources",
            help.Properties["Synopsis"]?.Value?.ToString() ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsUnknownNamesAsErrors()
    {
        using var powershell = ImportEngineCmdlets();
        powershell.AddCommand("Get-M4dMigrationComponent")
            .AddParameter("Category", "Target")
            .AddParameter("Name", "missing");

        var exception = Assert.Throws<CmdletInvocationException>(() => powershell.Invoke());
        Assert.Contains(
            "No migration component named 'missing'",
            exception.InnerException?.Message ?? exception.Message,
            StringComparison.Ordinal);
    }

    private static PowerShellRuntime ImportEngineCmdlets()
    {
        var powershell = PowerShellRuntime.Create();
        powershell.AddCommand("Import-Module")
            .AddParameter("Name", typeof(GetMigrationComponentCommand).Assembly.Location)
            .Invoke();
        if (powershell.HadErrors)
        {
            var errors = string.Join(Environment.NewLine, powershell.Streams.Error);
            powershell.Dispose();
            throw new InvalidOperationException(errors);
        }

        powershell.Commands.Clear();
        return powershell;
    }

    private static IReadOnlyList<MigrationComponentHelp> InvokeAll(PowerShellRuntime powershell)
    {
        powershell.AddCommand("Get-M4dMigrationComponent");
        var results = powershell.Invoke();
        Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
        return results.Select(result => Assert.IsType<MigrationComponentHelp>(result.BaseObject)).ToArray();
    }
}
