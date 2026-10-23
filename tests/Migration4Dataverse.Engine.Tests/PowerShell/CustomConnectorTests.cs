using System.Text.Json;
using Migration4Dataverse.Engine.PowerShell;
using Migration4Dataverse.Engine.Scheduling;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.Tests.PowerShell;

public sealed class CustomConnectorTests
{
    [Fact]
    public async Task CustomSourceReadsBoundedPagesUsingModulesLoadedInRunspaces()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "accounts.jsonl");
        File.WriteAllLines(
            sourcePath,
            [
                """{"SourceId":"A001"}""",
                """{"SourceId":"A002"}""",
                """{"SourceId":"A003"}"""
            ]);
        var modulePath = Path.Combine(directory.Path, "CustomSource.psm1");
        File.WriteAllText(
            modulePath,
            """
            function Get-TestSourcePage {
                param(
                    [string]$Path,
                    [object]$Cursor,
                    [int]$PageSize
                )

                $start = if ($null -eq $Cursor) { 0 } else { [int]$Cursor }
                $lines = [System.IO.File]::ReadAllLines($Path)
                $end = [Math]::Min($start + $PageSize, $lines.Length)
                $records = [System.Collections.Generic.List[object]]::new()
                for ($index = $start; $index -lt $end; $index++) {
                    $records.Add((ConvertFrom-Json -InputObject $lines[$index] -AsHashtable -ErrorAction Stop))
                }

                $done = $end -ge $lines.Length
                [pscustomobject]@{
                    Records = $records.ToArray()
                    NextCursor = if ($done) { $null } else { [string]$end }
                    Done = [bool]$done
                }
            }

            Export-ModuleMember -Function Get-TestSourcePage
            """);

        using var executor = new PowerShellScriptExecutor(
            maxRunspaces: 2,
            modulesToImport: [modulePath]);
        var callback = new PowerShellSourceCallbackDefinition(
            new object(),
            "custom-source-test-v1",
            """
            param($Connection, $Cursor, $PageSize, $StepId, $Path)
            Get-TestSourcePage -Path $Path -Cursor $Cursor -PageSize $PageSize
            """,
            pageSize: 2);
        var level = new HierarchySourceLevel(
            "Accounts",
            "custom",
            sourcePath,
            "SourceId",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 3,
            identity: callback.Identity);
        var factory = new PowerShellHierarchyRecordReaderFactory(
            executor,
            new Dictionary<string, PowerShellSourceCallbackDefinition>
            {
                ["Accounts"] = callback
            });
        var source = new HierarchyBatchSource(level, 2, factory.CreateDelegate());

        var batches = await ReadAllAsync(
            source.CreateReader()(new MigrationStepCheckpoint("run", "Accounts", -1, null, null), CancellationToken.None));

        Assert.Equal(2, batches.Count);
        using var firstPayload = JsonDocument.Parse(batches[0].PayloadJson);
        using var secondPayload = JsonDocument.Parse(batches[1].PayloadJson);
        Assert.Equal(
            new[] { "A001", "A002" },
            firstPayload.RootElement.GetProperty("Records")
                .EnumerateArray()
                .Select(record => record.GetProperty("Source").GetProperty("SourceId").GetString())
                .ToArray());
        Assert.Equal(
            "A003",
            Assert.Single(secondPayload.RootElement.GetProperty("Records").EnumerateArray().ToArray())
                .GetProperty("Source")
                .GetProperty("SourceId")
                .GetString());
    }

    private static async Task<List<MigrationWorkBatch>> ReadAllAsync(
        IAsyncEnumerable<MigrationWorkBatch> source)
    {
        var batches = new List<MigrationWorkBatch>();
        await foreach (var batch in source)
        {
            batches.Add(batch);
        }

        return batches;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"migration4dataverse-custom-connector-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
