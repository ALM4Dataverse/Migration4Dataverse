using System.Management.Automation.Language;

namespace Migration4Dataverse.Engine.Tests.PowerShell;

public sealed class ExampleScriptTests
{
    [Theory]
    [InlineData("examples/dataverse/Setup-Example.ps1")]
    [InlineData("examples/dataverse/example.m4d")]
    [InlineData("examples/dataverse/example-sequential.m4d")]
    [InlineData("examples/error-recovery/Setup-Example.ps1")]
    [InlineData("examples/error-recovery/example.m4d")]
    [InlineData("examples/error-recovery/example-filter.m4d")]
    [InlineData("src/Migration4Dataverse/Migration4Dataverse.psm1")]
    [InlineData("src/Migration4Dataverse/Migration4Dataverse.psd1")]
    [InlineData("build/Stage-MigrationModule.ps1")]
    [InlineData("build/Test-MigrationModulePackage.ps1")]
    [InlineData("build/Publish-MigrationModule.ps1")]
    [InlineData("build/Update-GitHubReleaseNotes.ps1")]
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
    public void ModuleManifestDeclaresGalleryMetadataAndDependencies()
    {
        var manifestPath = Path.Combine(
            FindRepositoryRoot(),
            "src/Migration4Dataverse/Migration4Dataverse.psd1");
        var manifest = File.ReadAllText(manifestPath);

        Assert.Contains("RootModule = 'Migration4Dataverse.psm1'", manifest, StringComparison.Ordinal);
        Assert.Contains("ModuleName = 'Rnwood.Dataverse.Data.PowerShell'", manifest, StringComparison.Ordinal);
        Assert.Contains("ModuleVersion = '3.3.0'", manifest, StringComparison.Ordinal);
        Assert.Contains("ProjectUri = 'https://github.com/rnwood/Migration4Dataverse'", manifest, StringComparison.Ordinal);
        Assert.Contains("LicenseUri = 'https://github.com/rnwood/Migration4Dataverse/blob/main/LICENSE'", manifest, StringComparison.Ordinal);
        Assert.Contains("ReleaseNotes = 'https://github.com/rnwood/Migration4Dataverse/releases'", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void DataverseExamplesAreSelfContainedAndTargetDataverse()
    {
        var repositoryRoot = FindRepositoryRoot();
        var examples = new[]
        {
            "examples/dataverse/example.m4d",
            "examples/dataverse/example-sequential.m4d"
        };

        foreach (var relativePath in examples)
        {
            var scriptPath = Path.Combine(repositoryRoot, relativePath);
            var script = File.ReadAllText(scriptPath);

            Assert.Contains("Type = 'dataverse'", script, StringComparison.Ordinal);
            Assert.Contains("$Migration = @{", script, StringComparison.Ordinal);
            Assert.DoesNotContain("Invoke-M4dMigrationGraph", script, StringComparison.Ordinal);
            Assert.DoesNotContain("ExampleMigration.ps1", script, StringComparison.Ordinal);
            Assert.DoesNotContain("ExampleConnectionOptions.ps1", script, StringComparison.Ordinal);
            Assert.DoesNotContain("$PSBoundParameters", script, StringComparison.Ordinal);
            Assert.DoesNotContain("Import-Module Rnwood.Dataverse.Data.PowerShell", script, StringComparison.Ordinal);
            Assert.DoesNotContain("Start-M4dMigrationDashboard", script, StringComparison.Ordinal);
            Assert.DoesNotContain("Stop-M4dMigrationDashboard", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MigrationExamplesUseM4dFilesWithoutCallingTheRunner()
    {
        var examplesDirectory = Path.Combine(FindRepositoryRoot(), "examples");
        var migrationFiles = Directory.GetFiles(examplesDirectory, "*.m4d", SearchOption.AllDirectories);
        var scriptFiles = Directory.GetFiles(examplesDirectory, "*.ps1", SearchOption.AllDirectories);

        Assert.NotEmpty(migrationFiles);
        Assert.All(migrationFiles, file =>
        {
            var definition = File.ReadAllText(file);
            Assert.Contains("$Migration = @{", definition, StringComparison.Ordinal);
            Assert.DoesNotContain("Invoke-M4dMigrationGraph", definition, StringComparison.Ordinal);
            Assert.DoesNotContain("Invoke-M4dMigration ", definition, StringComparison.Ordinal);
        });
        Assert.All(scriptFiles, file =>
            Assert.Equal("Setup-Example.ps1", Path.GetFileName(file)));
    }

    [Theory]
    [InlineData("examples/dataverse/example.m4d")]
    [InlineData("examples/dataverse/example-sequential.m4d")]
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

        if (string.Equals(relativePath, "examples/dataverse/example.m4d", StringComparison.Ordinal))
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
            "src/Migration4Dataverse/Public/Invoke-DataverseMigration.ps1")));
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
