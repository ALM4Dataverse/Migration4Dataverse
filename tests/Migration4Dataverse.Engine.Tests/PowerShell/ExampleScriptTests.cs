using System.Management.Automation.Language;

namespace Migration4Dataverse.Engine.Tests.PowerShell;

public sealed class ExampleScriptTests
{
    [Theory]
    [InlineData("examples/dataverse/Setup-Example.ps1")]
    [InlineData("examples/dataverse/example.ps1")]
    [InlineData("examples/jsonl/Setup-Example.ps1")]
    [InlineData("examples/jsonl/example.ps1")]
    [InlineData("examples/csv/Setup-Example.ps1")]
    [InlineData("examples/csv/example.ps1")]
    [InlineData("examples/parquet/Setup-Example.ps1")]
    [InlineData("examples/parquet/example.ps1")]
    [InlineData("examples/sqlite/Setup-Example.ps1")]
    [InlineData("examples/sqlite/example.ps1")]
    [InlineData("examples/duckdb/Setup-Example.ps1")]
    [InlineData("examples/duckdb/example.ps1")]
    [InlineData("examples/custom/Setup-Example.ps1")]
    [InlineData("examples/custom/example.ps1")]
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
    public void EachTargetExampleIsSelfContainedAndUsesItsOwnConnectorType()
    {
        var repositoryRoot = FindRepositoryRoot();
        var examples = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dataverse"] = "dataverse",
            ["jsonl"] = "jsonl",
            ["csv"] = "csv",
            ["parquet"] = "parquet",
            ["custom"] = "custom",
            ["sqlite"] = "sqlite",
            ["duckdb"] = "duckdb"
        };

        foreach (var (folder, targetType) in examples)
        {
            var scriptPath = Path.Combine(repositoryRoot, "examples", folder, "example.ps1");
            var script = File.ReadAllText(scriptPath);

            Assert.Contains($"Type = '{targetType}'", script, StringComparison.Ordinal);
            Assert.DoesNotContain("ExampleMigration.ps1", script, StringComparison.Ordinal);
            Assert.DoesNotContain("ExampleConnectionOptions.ps1", script, StringComparison.Ordinal);
            Assert.DoesNotContain("$PSBoundParameters", script, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("sqlite", "SQLite", "Microsoft.Data.Sqlite.SqliteConnection")]
    [InlineData("duckdb", "DuckDB", "DuckDB.NET.Data.DuckDBConnection")]
    public void DatabaseExamplesUseEngineConnectionFactory(
        string folder,
        string provider,
        string providerConnectionType)
    {
        var exampleDirectory = Path.Combine(FindRepositoryRoot(), "examples", folder);
        foreach (var fileName in new[] { "Setup-Example.ps1", "example.ps1" })
        {
            var script = File.ReadAllText(Path.Combine(exampleDirectory, fileName));
            Assert.Contains($"New-MigrationDatabaseConnection -Provider {provider}", script, StringComparison.Ordinal);
            Assert.DoesNotContain(providerConnectionType, script, StringComparison.Ordinal);
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
