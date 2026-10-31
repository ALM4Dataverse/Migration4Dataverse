using System.Management.Automation.Language;

namespace Migration4Dataverse.Engine.Tests.PowerShell;

public sealed class ExampleScriptTests
{
    [Theory]
    [InlineData("examples/dataverse/Setup-Example.ps1")]
    [InlineData("examples/dataverse/example.ps1")]
    [InlineData("examples/dataverse/example-sequential.ps1")]
    [InlineData("src/Migration4Dataverse.PowerShell/Migration4Dataverse.PowerShell.psm1")]
    [InlineData("src/Migration4Dataverse.PowerShell/Migration4Dataverse.PowerShell.psd1")]
    public void PowerShellFilesHaveNoParseErrorsOrScriptParameters(string relativePath)
    {
        var repositoryRoot = FindRepositoryRoot();
        var filePath = Path.Combine(repositoryRoot, relativePath);

        Assert.True(File.Exists(filePath), $"Expected PowerShell file '{filePath}' to exist.");
        var syntax = Parser.ParseFile(filePath, out _, out var errors);

        Assert.Empty(errors);
        if (relativePath.StartsWith("examples/", StringComparison.Ordinal))
        {
            Assert.Null(syntax.ParamBlock);
        }
    }

    [Fact]
    public void DataverseExamplesAreSelfContainedAndTargetDataverse()
    {
        var repositoryRoot = FindRepositoryRoot();
        var examples = new[]
        {
            "examples/dataverse/example.ps1",
            "examples/dataverse/example-sequential.ps1"
        };

        foreach (var relativePath in examples)
        {
            var scriptPath = Path.Combine(repositoryRoot, relativePath);
            var script = File.ReadAllText(scriptPath);

            Assert.Contains("Type = 'dataverse'", script, StringComparison.Ordinal);
            Assert.DoesNotContain("ExampleMigration.ps1", script, StringComparison.Ordinal);
            Assert.DoesNotContain("ExampleConnectionOptions.ps1", script, StringComparison.Ordinal);
            Assert.DoesNotContain("$PSBoundParameters", script, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("examples/dataverse/example.ps1")]
    [InlineData("examples/dataverse/example-sequential.ps1")]
    public void DataverseExamplesShowSidecarBackedFileUploads(string relativePath)
    {
        var repositoryRoot = FindRepositoryRoot();
        var migrationScript = File.ReadAllText(Path.Combine(repositoryRoot, relativePath));
        var setupScript = File.ReadAllText(Path.Combine(repositoryRoot, "examples/dataverse/Setup-Example.ps1"));

        Assert.Contains("BinaryColumns = @('Content')", migrationScript, StringComparison.Ordinal);
        Assert.Contains("new_m4dexamplefile", migrationScript, StringComparison.Ordinal);
        Assert.Contains("AccountAttachmentTarget", migrationScript, StringComparison.Ordinal);
        Assert.Contains("account-attachments.jsonl", migrationScript, StringComparison.Ordinal);
        Assert.Contains("Type = 'File'", setupScript, StringComparison.Ordinal);
        Assert.Contains("$attachmentSidecarName/$attachmentFileName", setupScript, StringComparison.Ordinal);

        if (string.Equals(relativePath, "examples/dataverse/example.ps1", StringComparison.Ordinal))
        {
            Assert.Contains(".CreateWriter(", migrationScript, StringComparison.Ordinal);
            Assert.Contains(".DetectMimeType()", migrationScript, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("new_m4dexamplefile = $attachment", migrationScript, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ExampleConfigurationFilesAndSharedScriptsHaveBeenRemoved()
    {
        var examplesDirectory = Path.Combine(FindRepositoryRoot(), "examples");

        Assert.False(File.Exists(Path.Combine(examplesDirectory, "example-config.json")));
        Assert.False(File.Exists(Path.Combine(examplesDirectory, "example-config.psd1")));
        Assert.False(File.Exists(Path.Combine(examplesDirectory, "ExampleConfig.ps1")));
        Assert.False(File.Exists(Path.Combine(examplesDirectory, "ExampleMigration.ps1")));
        Assert.False(File.Exists(Path.Combine(examplesDirectory, "ExampleConnectionOptions.ps1")));
        Assert.False(File.Exists(Path.Combine(examplesDirectory, "Setup-Example.ps1")));
        Assert.False(File.Exists(Path.Combine(examplesDirectory, "example.ps1")));
        Assert.False(File.Exists(Path.Combine(
            FindRepositoryRoot(),
            "src/Migration4Dataverse.PowerShell/Public/Invoke-DataverseMigration.ps1")));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Migration4Dataverse.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
