using System.Collections;
using System.Management.Automation;
using Microsoft.Data.Sqlite;
using Migration4Dataverse.Engine.PowerShell;
using Migration4Dataverse.Engine.Scheduling;
using Migration4Dataverse.Engine.Storage;
using System.Text;
using System.Text.Json;

namespace Migration4Dataverse.Engine.Tests.PowerShell;

public sealed class InvokeMigrationCommandTests
{
    [Fact]
    public void QueueLimitsDefaultTo64GloballyAnd32PerStep()
    {
        Assert.Equal(64, new InvokeMigrationCommand().MaxQueuedBatches);
        Assert.Equal(64, new DependencySchedulerOptions().MaxQueuedBatches);
        Assert.Equal(32, new InvokeMigrationCommand().MaxQueuedBatchesPerStep);
        Assert.Equal(32, new DependencySchedulerOptions().MaxQueuedBatchesPerStep);
    }

    [Fact]
    public void NoRetryDiagnosticModeCapsSchedulerAttemptsAtOne()
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"migration4dataverse-no-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var attemptsPath = Path.Combine(temporaryDirectory, "attempts.txt");
        var escapedAttemptsPath = attemptsPath.Replace("'", "''", StringComparison.Ordinal);
        var step = PSObject.AsPSObject(new
        {
            Id = "FailingCallback",
            Type = "callback",
            Inputs = Array.Empty<string>(),
            ReadBatches = ScriptBlock.Create("""
                param($checkpoint, $connection, $cursor)
                if ($null -eq $cursor) {
                    [Migration4Dataverse.Engine.Scheduling.MigrationWorkBatch]::new(
                        '{"Records":[{"id":1}]}',
                        '"done"',
                        $true)
                }
                """),
            ExecuteBatch = ScriptBlock.Create(
                $"[System.IO.File]::AppendAllText('{escapedAttemptsPath}', 'x'); throw 'intentional test failure'")
        });

        try
        {
            using var powershell = System.Management.Automation.PowerShell.Create();
            powershell.AddCommand("Import-Module")
                .AddParameter("Name", typeof(InvokeMigrationCommand).Assembly.Location);
            powershell.Invoke();
            Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));

            powershell.Commands.Clear();
            var exception = Assert.Throws<CmdletInvocationException>(() =>
                powershell.AddCommand("Invoke-Migration")
                .AddParameter("DatabasePath", Path.Combine(temporaryDirectory, "migration.sqlite3"))
                .AddParameter("RunName", "NoRetryDiagnosticTest")
                .AddParameter("RunId", "no-retry-diagnostic-test")
                .AddParameter("Steps", new[] { step })
                .AddParameter("MaxAttempts", 4)
                .AddParameter("DataverseNoRetryDiagnosticMode", true)
                .Invoke());

            Assert.Contains("failed after 1 attempts", exception.Message, StringComparison.Ordinal);
            Assert.Equal("x", File.ReadAllText(attemptsPath));
            Assert.Contains(powershell.Streams.Warning, warning =>
                warning.Message.Contains("no-retry diagnostic mode is enabled", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void BinaryCmdletWritesLivePowerShellProgressForLongRunningWork()
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"migration4dataverse-progress-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            var step = PSObject.AsPSObject(new
            {
                Id = "SlowSource",
                Type = "callback",
                Inputs = Array.Empty<string>(),
                ReadBatches = ScriptBlock.Create("""
                    param($checkpoint, $connection, $cursor)
                    if ($null -eq $cursor) {
                        [System.Threading.Thread]::Sleep(800)
                        [Migration4Dataverse.Engine.Scheduling.MigrationWorkBatch]::new(
                            '{"Records":[{"id":1}]}',
                            '"done"',
                            $true)
                    }
                    """),
                ExecuteBatch = ScriptBlock.Create("""
                    [Migration4Dataverse.Engine.Scheduling.MigrationBatchResult]::new(
                        '{"Succeeded":1,"Failed":0}',
                        $false,
                        $null,
                        $null)
                    """)
            });

            using var powershell = System.Management.Automation.PowerShell.Create();
            powershell.AddCommand("Import-Module")
                .AddParameter("Name", typeof(InvokeMigrationCommand).Assembly.Location);
            powershell.Invoke();
            Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));

            powershell.Commands.Clear();
            var output = powershell.AddCommand("Invoke-Migration")
                .AddParameter("DatabasePath", Path.Combine(temporaryDirectory, "migration.sqlite3"))
                .AddParameter("RunName", "ProgressOutputTest")
                .AddParameter("RunId", "progress-output-test")
                .AddParameter("Steps", new[] { step })
                .AddParameter("MaxAttempts", 1)
                .Invoke();

            Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
            Assert.IsType<DependencySchedulerResult>(Assert.Single(output).BaseObject);
            Assert.Contains(powershell.Streams.Progress, record =>
                record.Activity == "Migration: ProgressOutputTest" &&
                record.RecordType == ProgressRecordType.Processing);
            Assert.Contains(powershell.Streams.Progress, record =>
                record.Activity == "Migration: ProgressOutputTest" &&
                record.RecordType == ProgressRecordType.Completed);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void BinaryCmdletAcceptsHashtableStepDefinitions()
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"migration4dataverse-hashtable-steps-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(temporaryDirectory, "source");
        var targetDirectory = Path.Combine(temporaryDirectory, "target");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(targetDirectory);
        WriteJsonLines(Path.Combine(sourceDirectory, "accounts.jsonl"), """{"Id":"1","Name":"Ada"}""");

        try
        {
            var steps = new Hashtable[]
            {
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["Id"] = "AccountsSource",
                    ["Type"] = "source",
                    ["Inputs"] = Array.Empty<string>(),
                    ["Source"] = new Hashtable(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Type"] = "jsonl",
                        ["ConnectionId"] = "source",
                        ["Path"] = "accounts.jsonl",
                        ["Key"] = "Id",
                        ["Options"] = new Hashtable()
                    }
                },
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["Id"] = "AccountFields",
                    ["Type"] = "transform",
                    ["Inputs"] = new[] { "AccountsSource" },
                    ["Script"] = ScriptBlock.Create("param($row) @{ name = [string]$row.Name }")
                },
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["Id"] = "AccountsTarget",
                    ["Type"] = "target",
                    ["Inputs"] = new[] { "AccountFields" },
                    ["Target"] = new Hashtable(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Type"] = "jsonl",
                        ["ConnectionId"] = "target",
                        ["Path"] = "accounts",
                        ["Operation"] = "Write",
                        ["BatchSize"] = 10,
                        ["Options"] = new Hashtable()
                    }
                }
            };

            using var powershell = System.Management.Automation.PowerShell.Create();
            powershell.AddCommand("Import-Module")
                .AddParameter("Name", typeof(InvokeMigrationCommand).Assembly.Location);
            powershell.Invoke();
            Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));

            powershell.Commands.Clear();
            var output = powershell.AddCommand("Invoke-Migration")
                .AddParameter("DatabasePath", Path.Combine(temporaryDirectory, "migration.sqlite3"))
                .AddParameter("RunName", "HashtableStepDefinitions")
                .AddParameter("RunId", "hashtable-step-definitions")
                .AddParameter("Steps", steps)
                .AddParameter("Connections", new Hashtable(StringComparer.OrdinalIgnoreCase)
                {
                    ["source"] = new DirectoryInfo(sourceDirectory),
                    ["target"] = new DirectoryInfo(targetDirectory)
                })
                .Invoke();

            Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
            Assert.IsType<DependencySchedulerResult>(Assert.Single(output).BaseObject);
            var rows = ReadJsonLines(Path.Combine(targetDirectory, "accounts", "hashtable-step-definitions"));
            Assert.Equal("Ada", Assert.Single(rows).RootElement.GetProperty("name").GetString());
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void BinaryCmdletDerivesDatabaseCountFromSourceQuery()
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"migration4dataverse-database-count-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var targetDirectory = Path.Combine(temporaryDirectory, "target");
        Directory.CreateDirectory(targetDirectory);
        using var sourceConnection = new SqliteConnection(
            $"Data Source={Path.Combine(temporaryDirectory, "source.sqlite")}");
        sourceConnection.Open();
        using (var command = sourceConnection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE records (Id INTEGER NOT NULL, Name TEXT NOT NULL);
                INSERT INTO records VALUES (1, 'Ada'), (2, 'Grace');
                """;
            command.ExecuteNonQuery();
        }

        try
        {
            var steps = new Hashtable[]
            {
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["Id"] = "DatabaseSource",
                    ["Type"] = "source",
                    ["Inputs"] = Array.Empty<string>(),
                    ["Source"] = new Hashtable(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Type"] = "sqlite",
                        ["ConnectionId"] = "source-db",
                        ["Key"] = "Id",
                        ["Options"] = new Hashtable(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Query"] = "SELECT Id, Name FROM records ORDER BY Id"
                        }
                    }
                },
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["Id"] = "DatabaseFields",
                    ["Type"] = "transform",
                    ["Inputs"] = new[] { "DatabaseSource" },
                    ["Script"] = ScriptBlock.Create("param($row) @{ name = [string]$row.Name }")
                },
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["Id"] = "DatabaseTarget",
                    ["Type"] = "target",
                    ["Inputs"] = new[] { "DatabaseFields" },
                    ["Target"] = new Hashtable(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Type"] = "jsonl",
                        ["ConnectionId"] = "target-files",
                        ["Path"] = "records",
                        ["Operation"] = "Write",
                        ["BatchSize"] = 10,
                        ["Options"] = new Hashtable()
                    }
                }
            };

            using var powershell = System.Management.Automation.PowerShell.Create();
            powershell.AddCommand("Import-Module")
                .AddParameter("Name", typeof(InvokeMigrationCommand).Assembly.Location);
            powershell.Invoke();
            Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));

            powershell.Commands.Clear();
            var databasePath = Path.Combine(temporaryDirectory, "migration.sqlite");
            var output = powershell.AddCommand("Invoke-Migration")
                .AddParameter("DatabasePath", databasePath)
                .AddParameter("RunName", "DatabaseCountQuery")
                .AddParameter("RunId", "database-count-query")
                .AddParameter("Steps", steps)
                .AddParameter("Connections", new Hashtable(StringComparer.OrdinalIgnoreCase)
                {
                    ["source-db"] = sourceConnection,
                    ["target-files"] = new DirectoryInfo(targetDirectory)
                })
                .Invoke();

            Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
            Assert.IsType<DependencySchedulerResult>(Assert.Single(output).BaseObject);
            var migrationStore = new SqliteMigrationStore(databasePath);
            var source = Assert.Single(
                migrationStore.GetDashboardRun("database-count-query").Steps,
                step => step.Id == "DatabaseSource");
            Assert.Equal(2, source.ExpectedRows);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void BinaryCmdletUsesCustomSourceCountCallback()
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"migration4dataverse-custom-count-{Guid.NewGuid():N}");
        var targetDirectory = Path.Combine(temporaryDirectory, "target");
        Directory.CreateDirectory(targetDirectory);
        var customConnection = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["TotalRecordCount"] = 1L,
            ["CountCalls"] = 0
        };

        try
        {
            var steps = new Hashtable[]
            {
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["Id"] = "CustomSource",
                    ["Type"] = "source",
                    ["Inputs"] = Array.Empty<string>(),
                    ["Source"] = new Hashtable(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Type"] = "custom",
                        ["ConnectionId"] = "custom-service",
                        ["Path"] = "customers",
                        ["Key"] = "Id",
                        ["Options"] = new Hashtable(StringComparer.OrdinalIgnoreCase)
                        {
                            ["QueryName"] = "Customers"
                        },
                        ["Custom"] = new Hashtable(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Identity"] = "custom-count-callback-test-v1",
                            ["PageSize"] = 10,
                            ["Callback"] = ScriptBlock.Create("""
                                param($Connection, $Cursor, $PageSize, $StepId, $Path)
                                @{
                                    Records = @(@{ Id = 1; Name = 'Ada' })
                                    Done = $true
                                }
                                """),
                            ["CountCallback"] = ScriptBlock.Create("""
                                param($Connection, $StepId, $Path, $Options)
                                if ($Options.QueryName -ne 'Customers') {
                                    throw 'Source options were not passed to CountCallback.'
                                }
                                $Connection.CountCalls = [int]$Connection.CountCalls + 1
                                [long]$Connection.TotalRecordCount
                                """)
                        }
                    }
                },
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["Id"] = "CustomFields",
                    ["Type"] = "transform",
                    ["Inputs"] = new[] { "CustomSource" },
                    ["Script"] = ScriptBlock.Create("param($row) @{ name = [string]$row.Name }")
                },
                new(StringComparer.OrdinalIgnoreCase)
                {
                    ["Id"] = "CustomTarget",
                    ["Type"] = "target",
                    ["Inputs"] = new[] { "CustomFields" },
                    ["Target"] = new Hashtable(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Type"] = "jsonl",
                        ["ConnectionId"] = "target-files",
                        ["Path"] = "customers",
                        ["Operation"] = "Write",
                        ["BatchSize"] = 10,
                        ["Options"] = new Hashtable()
                    }
                }
            };

            using var powershell = System.Management.Automation.PowerShell.Create();
            powershell.AddCommand("Import-Module")
                .AddParameter("Name", typeof(InvokeMigrationCommand).Assembly.Location);
            powershell.Invoke();
            Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));

            powershell.Commands.Clear();
            var databasePath = Path.Combine(temporaryDirectory, "migration.sqlite");
            var output = powershell.AddCommand("Invoke-Migration")
                .AddParameter("DatabasePath", databasePath)
                .AddParameter("RunName", "CustomSourceCount")
                .AddParameter("RunId", "custom-source-count")
                .AddParameter("Steps", steps)
                .AddParameter("Connections", new Hashtable(StringComparer.OrdinalIgnoreCase)
                {
                    ["custom-service"] = customConnection,
                    ["target-files"] = new DirectoryInfo(targetDirectory)
                })
                .Invoke();

            Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
            Assert.IsType<DependencySchedulerResult>(Assert.Single(output).BaseObject);
            Assert.Equal(1, customConnection["CountCalls"]);
            var migrationStore = new SqliteMigrationStore(databasePath);
            var source = Assert.Single(
                migrationStore.GetDashboardRun("custom-source-count").Steps,
                step => step.Id == "CustomSource");
            Assert.Equal(1, source.ExpectedRows);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void BinaryCmdletRunsCallbackStepsWithMultiParentBatchPrerequisites()
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"migration4dataverse-cmdlet-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            var rootStep = PSObject.AsPSObject(new
            {
                Id = "Root",
                Type = "callback",
                Inputs = Array.Empty<string>(),
                SourceConnectionId = "source",
                TargetConnectionId = "target",
                ReadBatches = ScriptBlock.Create("""
                    param($checkpoint, $connection, $cursor)
                    if ($connection -ne 'source-connection') { throw 'Source connection was not resolved.' }
                    if ($null -eq $cursor) {
                        [Migration4Dataverse.Engine.Scheduling.MigrationWorkBatch]::new(
                            '{"Records":[{"id":1}]}',
                            '"cursor-1"',
                            $false)
                    }
                    else {
                        [Migration4Dataverse.Engine.Scheduling.MigrationWorkBatch]::new(
                            '{"Records":[{"id":2}]}',
                            '"cursor-2"',
                            $true)
                    }
                    """),
                ExecuteBatch = ScriptBlock.Create("""
                    param($batch, $sourceConnection, $targetConnection)
                    if ($sourceConnection -ne 'source-connection' -or $targetConnection -ne 'target-connection') {
                        throw 'Named connections were not passed to the batch handler.'
                    }
                    $dependencies = [Migration4Dataverse.Engine.Scheduling.MigrationDependencyOutput[]]@()
                    if ($batch.Sequence -eq 0) {
                        $output = [Migration4Dataverse.Engine.Scheduling.MigrationDependencyOutput]::new(
                            'Leaf',
                            '{"Records":[{"id":1}]}',
                            [string[]]@(
                                $batch.BatchId,
                                [Migration4Dataverse.Engine.Scheduling.MigrationBatchIdentity]::Create(
                                    'generic-multi-parent-test',
                                    'OtherRoot',
                                    0)))
                        $dependencies = [Migration4Dataverse.Engine.Scheduling.MigrationDependencyOutput[]]@($output)
                    }
                    [Migration4Dataverse.Engine.Scheduling.MigrationBatchResult]::new(
                        '{"Succeeded":1}',
                        $false,
                        $dependencies,
                        $null)
                    """)
            });
            var leafStep = PSObject.AsPSObject(new
            {
                Id = "Leaf",
                Type = "callback",
                Inputs = new[] { "Root", "OtherRoot" },
                ExecuteBatch = ScriptBlock.Create("""
                    param($batch)
                    [Migration4Dataverse.Engine.Scheduling.MigrationBatchResult]::new(
                        '{"Succeeded":1}',
                        $false,
                        $null,
                        $null)
                    """)
            });
            var otherRootStep = PSObject.AsPSObject(new
            {
                Id = "OtherRoot",
                Type = "callback",
                Inputs = Array.Empty<string>(),
                ReadBatches = ScriptBlock.Create(
                    "param($checkpoint, $connection, $cursor) [Migration4Dataverse.Engine.Scheduling.MigrationWorkBatch]::new('{\"Records\":[{\"id\":2}]}', 'null', $true)"),
                ExecuteBatch = ScriptBlock.Create(
                    "[Migration4Dataverse.Engine.Scheduling.MigrationBatchResult]::new('{\"Succeeded\":1}', $false, $null, $null)")
            });

            using var powershell = System.Management.Automation.PowerShell.Create();
            powershell.AddCommand("Import-Module")
                .AddParameter("Name", typeof(InvokeMigrationCommand).Assembly.Location);
            powershell.Invoke();
            Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));

            powershell.Commands.Clear();
            powershell.AddCommand("Invoke-Migration")
                .AddParameter("DatabasePath", Path.Combine(temporaryDirectory, "migration.sqlite3"))
                .AddParameter("RunName", "GenericMultiParentTest")
                .AddParameter("RunId", "generic-multi-parent-test")
                .AddParameter("Steps", new[] { rootStep, otherRootStep, leafStep })
                .AddParameter("Connections", new System.Collections.Hashtable
                {
                    ["source"] = "source-connection",
                    ["target"] = "target-connection"
                })
                .AddParameter("MaxDegreeOfParallelism", 2)
                .AddParameter("MaxAttempts", 1);
            var output = powershell.Invoke();

            Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
            var result = Assert.IsType<DependencySchedulerResult>(Assert.Single(output).BaseObject);
            Assert.Equal("generic-multi-parent-test", result.RunId);
            Assert.Equal(4, result.SucceededBatches);

            powershell.Commands.Clear();
            var runOutput = powershell.AddCommand("Get-MigrationRun")
                .AddParameter("DatabasePath", Path.Combine(temporaryDirectory, "migration.sqlite3"))
                .Invoke();
            Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
            var run = Assert.IsType<MigrationRunSummary>(Assert.Single(runOutput).BaseObject);
            Assert.Equal("generic-multi-parent-test", run.RunId);
            Assert.Equal("Root", run.Configuration!.Value.GetProperty("steps")[0]
                .GetProperty("properties").GetProperty("Id").GetString());
            Assert.Equal(2, run.ConnectionInstances!.Value.GetArrayLength());

            powershell.Commands.Clear();
            var specificRun = powershell.AddCommand("Get-MigrationRun")
                .AddParameter("DatabasePath", Path.Combine(temporaryDirectory, "migration.sqlite3"))
                .AddParameter("RunId", "generic-multi-parent-test")
                .Invoke();
            Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
            Assert.Equal(
                "generic-multi-parent-test",
                Assert.IsType<MigrationRunSummary>(Assert.Single(specificRun).BaseObject).RunId);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void BinaryCmdletRejectsLegacyTypedJobShape()
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"migration4dataverse-legacy-mapping-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var legacyJob = PSObject.AsPSObject(new
            {
                Name = "Legacy",
                Target = new
                {
                    Type = "jsonl",
                    ConnectionId = "output",
                    Path = "legacy"
                },
                Mapping = ScriptBlock.Create("param($row) @{ name = $row.Name }")
            });

            using var powershell = System.Management.Automation.PowerShell.Create();
            powershell.AddCommand("Import-Module")
                .AddParameter("Name", typeof(InvokeMigrationCommand).Assembly.Location);
            powershell.Invoke();
            Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));

            powershell.Commands.Clear();
            var exception = Assert.Throws<CmdletInvocationException>(() =>
                powershell.AddCommand("Invoke-Migration")
                    .AddParameter("DatabasePath", Path.Combine(temporaryDirectory, "migration.sqlite3"))
                    .AddParameter("RunName", "LegacyMappingTest")
                    .AddParameter("Steps", new[] { legacyJob })
                    .Invoke());

            Assert.Contains("node Type and Id", exception.Message);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void BinaryCmdletRejectsStructuredDependsOnAndInvalidJoinInputs()
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"migration4dataverse-invalid-inputs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var invalidSteps = new[]
            {
                (
                    Steps: new[]
                    {
                        PSObject.AsPSObject(new
                        {
                            Id = "ChildSource",
                            Type = "source",
                            Inputs = Array.Empty<string>(),
                            Source = new
                            {
                                Type = "jsonl",
                                ConnectionId = "source",
                                Path = "source.jsonl",
                                Key = "Id"
                            },
                            DependsOn = new object[]
                            {
                                new Hashtable(StringComparer.OrdinalIgnoreCase)
                                {
                                    ["Job"] = "ParentSource",
                                    ["Keys"] = new Hashtable(StringComparer.OrdinalIgnoreCase)
                                    {
                                        ["Child"] = "ParentId",
                                        ["Parent"] = "Id"
                                    }
                                }
                            }
                        })
                    },
                    ExpectedMessage: "accepts only step IDs"),
                (
                    Steps: new[]
                    {
                        PSObject.AsPSObject(new
                        {
                            Id = "TooManyInputs",
                            Type = "join",
                            JoinType = "left",
                            Inputs = new[] { "Left", "ParentA", "ParentB" }
                        })
                    },
                    ExpectedMessage: "exactly two Inputs"),
                (
                    Steps: new[]
                    {
                        PSObject.AsPSObject(new
                        {
                            Id = "MissingParentKey",
                            Type = "join",
                            JoinType = "left",
                            Inputs = new object[]
                            {
                                new Hashtable(StringComparer.OrdinalIgnoreCase)
                                {
                                    ["Job"] = "Left",
                                    ["As"] = "Child"
                                },
                                new Hashtable(StringComparer.OrdinalIgnoreCase)
                                {
                                    ["Job"] = "Parent",
                                    ["As"] = "Parent",
                                    ["Keys"] = new Hashtable(StringComparer.OrdinalIgnoreCase)
                                    {
                                        ["Child"] = "ParentId"
                                    }
                                }
                            }
                        })
                    },
                    ExpectedMessage: "property 'Parent'")
            };

            foreach (var invalid in invalidSteps)
            {
                using var powershell = System.Management.Automation.PowerShell.Create();
                powershell.AddCommand("Import-Module")
                    .AddParameter("Name", typeof(InvokeMigrationCommand).Assembly.Location);
                powershell.Invoke();
                Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));

                powershell.Commands.Clear();
                var exception = Assert.Throws<CmdletInvocationException>(() =>
                    powershell.AddCommand("Invoke-Migration")
                        .AddParameter("DatabasePath", Path.Combine(temporaryDirectory, "migration.sqlite3"))
                        .AddParameter("RunName", "InvalidInputTest")
                        .AddParameter("Steps", invalid.Steps)
                        .AddParameter("Connections", new Hashtable
                        {
                            ["source"] = new DirectoryInfo(temporaryDirectory)
                        })
                        .Invoke());
                Assert.Contains(invalid.ExpectedMessage, exception.Message);
            }
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void BinaryCmdletRunsStepGraphWithFanOutAndRowLineage()
    {
            var temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                $"migration4dataverse-typed-cmdlet-{Guid.NewGuid():N}");
            var sourceDirectory = Path.Combine(temporaryDirectory, "source");
            var targetDirectory = Path.Combine(temporaryDirectory, "target");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(targetDirectory);

            try
            {
                WriteJsonLines(
                    Path.Combine(sourceDirectory, "accounts.jsonl"),
                    """
                    {"SourceId":"A001","DataverseId":"00000000-0000-0000-0000-000000000001","Name":"Account 1"}
                    {"SourceId":"A002","DataverseId":"00000000-0000-0000-0000-000000000002","Name":"Account 2"}
                    """);
                WriteJsonLines(
                    Path.Combine(sourceDirectory, "contacts.jsonl"),
                    """
                    {"SourceId":"C001","DataverseId":"10000000-0000-0000-0000-000000000001","AccountSourceId":"A001","Name":"Contact 1"}
                    {"SourceId":"C002","DataverseId":"10000000-0000-0000-0000-000000000002","AccountSourceId":"A002","Name":"Contact 2"}
                    """);
                WriteJsonLines(
                    Path.Combine(sourceDirectory, "products.jsonl"),
                    """
                    {"SourceId":"P001","DataverseId":"20000000-0000-0000-0000-000000000001","Name":"Product 1"}
                    {"SourceId":"P002","DataverseId":"20000000-0000-0000-0000-000000000002","Name":"Product 2"}
                    """);
                WriteJsonLines(
                    Path.Combine(sourceDirectory, "summary.jsonl"),
                    """{"SourceId":"S001","DataverseId":"30000000-0000-0000-0000-000000000001","Name":"Product summary"}""");

                var steps = new[]
                {
                    SourceStep("AccountsSource", "accounts.jsonl"),
                    Step("filter-accounts", "filter", ["AccountsSource"],
                        ScriptBlock.Create("param($row) $row.Name -eq 'Account 1'")),
                    Step("map-accounts", "transform", ["filter-accounts"],
                        ScriptBlock.Create("param($row) @{ new_name = [string]$row.Name }")),
                    TargetStep("Accounts", "map-accounts", "Accounts"),
                    TargetStep("AccountAudit", "AccountsSource", "AccountAudit"),

                    SourceStep("ContactsSource", "contacts.jsonl", ["Accounts"]),
                    Step("map-contacts", "transform", ["ContactsSource"],
                        ScriptBlock.Create("param($row) @{ new_name = [string]$row.Name }")),
                    TargetStep("Contacts", "map-contacts", "Contacts"),

                    SourceStep("ProductsSource", "products.jsonl"),
                    Step("map-products", "transform", ["ProductsSource"],
                        ScriptBlock.Create("param($row) if ($row.Name -eq 'Product 1') { return }; @(@{ Id = [guid]'20000000-0000-0000-0000-000000000011'; new_name = 'Product A' }, @{ Id = [guid]'20000000-0000-0000-0000-000000000012'; new_name = 'Product B' })")),
                    TargetStep("Products", "map-products", "Products"),

                    SourceStep("SummarySource", "summary.jsonl"),
                    Step("map-summary", "transform", ["SummarySource"],
                        ScriptBlock.Create("param($row) @{ new_name = [string]$row.Name }")),
                    TargetStep("ProductSummary", "map-summary", "ProductSummary")
                };

                using var powershell = System.Management.Automation.PowerShell.Create();
                var databasePath = Path.Combine(temporaryDirectory, "migration.sqlite3");
                powershell.AddCommand("Import-Module")
                    .AddParameter("Name", typeof(InvokeMigrationCommand).Assembly.Location);
                powershell.Invoke();
                Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));

                powershell.Commands.Clear();
                powershell.AddCommand("Invoke-Migration")
                    .AddParameter("DatabasePath", databasePath)
                    .AddParameter("RunName", "TypedConnectorTest")
                    .AddParameter("RunId", "typed-connector-test")
                    .AddParameter("BatchSize", 1)
                    .AddParameter("Steps", steps)
                    .AddParameter("Connections", new System.Collections.Hashtable
                    {
                        ["source"] = new DirectoryInfo(sourceDirectory),
                        ["target"] = new DirectoryInfo(targetDirectory)
                    })
                    .AddParameter("MaxDegreeOfParallelism", 2)
                    .AddParameter("EnableDebugCapture", true)
                    .AddParameter("MaxAttempts", 1);
                var output = powershell.Invoke();

                Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
                var result = Assert.IsType<DependencySchedulerResult>(Assert.Single(output).BaseObject);
                Assert.Equal(23, result.SucceededBatches);
                Assert.Equal(8, result.TargetRowsProcessed);
                Assert.Equal(8, result.TargetRowsSucceeded);
                Assert.Equal(8, result.TargetRowsExpected);
                Assert.Equal(2, result.FilteredRows);
                Assert.Single(ReadJsonLines(Path.Combine(targetDirectory, "Accounts", "typed-connector-test")));
                Assert.Equal(2, ReadJsonLines(Path.Combine(targetDirectory, "Contacts", "typed-connector-test")).Count);
                Assert.Equal(2, ReadJsonLines(Path.Combine(targetDirectory, "Products", "typed-connector-test")).Count);
                Assert.Single(ReadJsonLines(Path.Combine(targetDirectory, "ProductSummary", "typed-connector-test")));
                Assert.Equal(2, ReadJsonLines(Path.Combine(targetDirectory, "AccountAudit", "typed-connector-test")).Count);

                var debugStore = new SqliteMigrationStore(databasePath);
                Assert.Equal(
                    new[] { "Accounts" },
                    Assert.Single(
                        debugStore.GetDashboardRun("typed-connector-test").Steps,
                        step => step.Id == "ContactsSource").CompletionDependencies);
                var accountSnapshots = debugStore.GetDebugSnapshots("typed-connector-test");
                var loadedAccount = Assert.Single(
                    accountSnapshots,
                    snapshot => snapshot.StepId == "Accounts" &&
                                snapshot.StageName == "Accounts" &&
                                snapshot.PayloadJson.Contains("Account 1", StringComparison.Ordinal));
                var accountLineage = debugStore.GetDebugSnapshots(
                    "typed-connector-test",
                    lineageId: loadedAccount.LineageId);
                Assert.Contains(
                    accountLineage,
                    snapshot => snapshot.StageName == "map-accounts");
                Assert.Contains(accountLineage, snapshot => snapshot.StageName == "AccountsSource");
            }
            finally
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }

        [Fact]
        public void BinaryCmdletRunsVariableRowLinkedSourceAfterParentTargetCompletes()
        {
            var temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                $"migration4dataverse-row-dependency-{Guid.NewGuid():N}");
            var sourceDirectory = Path.Combine(temporaryDirectory, "source");
            var targetDirectory = Path.Combine(temporaryDirectory, "target");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(targetDirectory);

            try
            {
                WriteJsonLines(
                    Path.Combine(sourceDirectory, "parents.jsonl"),
                    """
                    {"SourceId":"A001","Name":"Parent 1"}
                    {"SourceId":"A002","Name":"Parent 2"}
                    """);
                WriteJsonLines(
                    Path.Combine(sourceDirectory, "children.jsonl"),
                    """
                    {"SourceId":"C001","ParentSourceId":"A001","Name":"Child 1"}
                    """);

                var childSource = PSObject.AsPSObject(new
                {
                    Id = "ChildSource",
                    Type = "source",
                    Inputs = new object[]
                    {
                        new System.Collections.Hashtable
                        {
                            ["Job"] = "ParentSource",
                            ["Keys"] = new System.Collections.Hashtable
                            {
                                ["Child"] = "ParentSourceId",
                                ["Parent"] = "SourceId"
                            }
                        }
                    },
                    DependsOn = new object[]
                    {
                        "ParentTarget"
                    },
                    Source = new
                    {
                        Type = "jsonl",
                        ConnectionId = "source",
                        Path = "children.jsonl",
                        Key = "SourceId",
                        Options = new System.Collections.Hashtable()
                    }
                });
                var steps = new[]
                {
                    SourceStep("ParentSource", "parents.jsonl"),
                    Step("ParentMap", "transform", ["ParentSource"],
                        ScriptBlock.Create("param($row) @{ name = [string]$row.Name }")),
                    TargetStep("ParentTarget", "ParentMap", "Parents"),
                    childSource,
                    Step("ChildMap", "transform", ["ChildSource"],
                        ScriptBlock.Create("param($row) @{ name = [string]$row.Name }")),
                    TargetStep("ChildTarget", "ChildMap", "Children")
                };

                using var powershell = System.Management.Automation.PowerShell.Create();
                powershell.AddCommand("Import-Module")
                    .AddParameter("Name", typeof(InvokeMigrationCommand).Assembly.Location);
                powershell.Invoke();
                Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));

                powershell.Commands.Clear();
                var output = powershell.AddCommand("Invoke-Migration")
                    .AddParameter("DatabasePath", Path.Combine(temporaryDirectory, "migration.sqlite3"))
                    .AddParameter("RunName", "RowLinkedSourceTest")
                    .AddParameter("RunId", "row-linked-source-test")
                    .AddParameter("BatchSize", 1)
                    .AddParameter("Steps", steps)
                    .AddParameter("Connections", new System.Collections.Hashtable
                    {
                        ["source"] = new DirectoryInfo(sourceDirectory),
                        ["target"] = new DirectoryInfo(targetDirectory)
                    })
                    .AddParameter("MaxDegreeOfParallelism", 2)
                    .AddParameter("MaxAttempts", 1)
                    .Invoke();

                Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
                Assert.IsType<DependencySchedulerResult>(Assert.Single(output).BaseObject);
                var childRows = ReadJsonLines(
                    Path.Combine(targetDirectory, "Children", "row-linked-source-test"));
                Assert.Single(childRows);
                Assert.Contains(childRows, row => row.RootElement.GetProperty("name").GetString() == "Child 1");
                Assert.Equal(
                    2,
                    ReadJsonLines(Path.Combine(targetDirectory, "Parents", "row-linked-source-test")).Count);
            }
            finally
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }

        [Fact]
        public void BinaryCmdletStreamsMultiParentRowsAndEmitsNullRelationshipsFirst()
        {
            var temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                $"migration4dataverse-multi-parent-{Guid.NewGuid():N}");
            var sourceDirectory = Path.Combine(temporaryDirectory, "source");
            var targetDirectory = Path.Combine(temporaryDirectory, "target");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(targetDirectory);

            try
            {
                WriteJsonLines(
                    Path.Combine(sourceDirectory, "parents-a.jsonl"),
                    """
                    {"SourceId":"A001","Name":"Parent A1"}
                    {"SourceId":"A002","Name":"Parent A2"}
                    """);
                WriteJsonLines(
                    Path.Combine(sourceDirectory, "parents-b.jsonl"),
                    """
                    {"SourceId":"B001","Name":"Parent B1"}
                    {"SourceId":"B002","Name":"Parent B2"}
                    """);
                WriteJsonLines(
                    Path.Combine(sourceDirectory, "children.jsonl"),
                    """
                    {"SourceId":"C004","ParentAId":"A002","ParentBId":"B002","Name":"Fully linked"}
                    {"SourceId":"C002","ParentAId":"A001","ParentBId":null,"Name":"A only"}
                    {"SourceId":"C001","ParentAId":null,"ParentBId":"B001","Name":"B only"}
                    {"SourceId":"C003","ParentAId":null,"ParentBId":null,"Name":"No parents"}
                    """);

                var childSource = PSObject.AsPSObject(new
                {
                    Id = "ChildSource",
                    Type = "source",
                    Inputs = new object[]
                    {
                        new System.Collections.Hashtable
                        {
                            ["Job"] = "ParentASource",
                            ["Keys"] = new System.Collections.Hashtable
                            {
                                ["Child"] = "ParentAId",
                                ["Parent"] = "SourceId"
                            },
                            ["RowsPerParent"] = 1
                        },
                        new System.Collections.Hashtable
                        {
                            ["Job"] = "ParentBSource",
                            ["Keys"] = new System.Collections.Hashtable
                            {
                                ["Child"] = "ParentBId",
                                ["Parent"] = "SourceId"
                            },
                            ["RowsPerParent"] = 1
                        }
                    },
                    Source = new
                    {
                        Type = "jsonl",
                        ConnectionId = "source",
                        Path = "children.jsonl",
                        Key = "SourceId",
                        Options = new System.Collections.Hashtable()
                    }
                });
                var steps = new[]
                {
                    SourceStep("ParentASource", "parents-a.jsonl"),
                    TargetStep("ParentATarget", "ParentASource", "ParentsA"),
                    SourceStep("ParentBSource", "parents-b.jsonl"),
                    TargetStep("ParentBTarget", "ParentBSource", "ParentsB"),
                    childSource,
                    TargetStep("ChildTarget", "ChildSource", "Children")
                };

                using var powershell = System.Management.Automation.PowerShell.Create();
                powershell.AddCommand("Import-Module")
                    .AddParameter("Name", typeof(InvokeMigrationCommand).Assembly.Location);
                powershell.Invoke();
                Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));

                powershell.Commands.Clear();
                var output = powershell.AddCommand("Invoke-Migration")
                    .AddParameter("DatabasePath", Path.Combine(temporaryDirectory, "migration.sqlite3"))
                    .AddParameter("RunName", "MultiParentSourceTest")
                    .AddParameter("RunId", "multi-parent-source-test")
                    .AddParameter("BatchSize", 1)
                    .AddParameter("Steps", steps)
                    .AddParameter("Connections", new System.Collections.Hashtable
                    {
                        ["source"] = new DirectoryInfo(sourceDirectory),
                        ["target"] = new DirectoryInfo(targetDirectory)
                    })
                    .AddParameter("MaxDegreeOfParallelism", 1)
                    .AddParameter("MaxAttempts", 1)
                    .Invoke();

                Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
                Assert.IsType<DependencySchedulerResult>(Assert.Single(output).BaseObject);

                var childOutputDirectory = Path.Combine(targetDirectory, "Children", "multi-parent-source-test");
                var childRows = Directory.EnumerateFiles(childOutputDirectory, "*.jsonl")
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .SelectMany(File.ReadLines)
                    .Select(line => JsonDocument.Parse(line))
                    .ToArray();
                try
                {
                    Assert.Equal(
                        new[] { "C001", "C002", "C003", "C004" },
                        childRows.Select(row => row.RootElement.GetProperty("SourceId").GetString()));
                    Assert.Null(childRows[0].RootElement.GetProperty("ParentAId").GetString());
                    Assert.Null(childRows[1].RootElement.GetProperty("ParentBId").GetString());
                    Assert.Null(childRows[2].RootElement.GetProperty("ParentAId").GetString());
                    Assert.Null(childRows[2].RootElement.GetProperty("ParentBId").GetString());
                }
                finally
                {
                    foreach (var row in childRows)
                    {
                        row.Dispose();
                    }
                }
            }
            finally
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }

        private static PSObject SourceStep(string id, string sourcePath, string[]? dependsOn = null)
        {
            return PSObject.AsPSObject(new
            {
                Id = id,
                Type = "source",
                Inputs = Array.Empty<string>(),
                DependsOn = dependsOn ?? Array.Empty<string>(),
                Source = new
                {
                    Type = "jsonl",
                    ConnectionId = "source",
                    Path = sourcePath,
                    Key = "SourceId",
                    Id = "DataverseId",
                    Options = new System.Collections.Hashtable(),
                    Custom = (object?)null
                }
            });
        }

        [Fact]
        public void BinaryCmdletStagesSingleOutputJoinRecordsWithoutCollapsingArray()
        {
            var temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                $"migration4dataverse-single-join-output-{Guid.NewGuid():N}");
            var sourceDirectory = Path.Combine(temporaryDirectory, "source");
            var targetDirectory = Path.Combine(temporaryDirectory, "target");
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(targetDirectory);

            try
            {
                WriteJsonLines(
                    Path.Combine(sourceDirectory, "children.jsonl"),
                    """
                    {"SourceId":"C1","JoinKey":"A"}
                    {"SourceId":"C2","JoinKey":"B"}
                    """);
                WriteJsonLines(
                    Path.Combine(sourceDirectory, "parents.jsonl"),
                    """
                    {"SourceId":"A"}
                    {"SourceId":"X"}
                    """);
                WriteJsonLines(
                    Path.Combine(sourceDirectory, "lookups.jsonl"),
                    """
                    {"SourceId":"L1","LookupKey":"A"}
                    {"SourceId":"L2","LookupKey":"Z"}
                    """);

                var firstJoin = PSObject.AsPSObject(new
                {
                    Id = "FirstJoin",
                    Type = "join",
                    JoinType = "inner",
                    Inputs = new object[]
                    {
                        new System.Collections.Hashtable(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Job"] = "ChildrenSource",
                            ["As"] = "Left"
                        },
                        new System.Collections.Hashtable(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Job"] = "ParentsSource",
                            ["As"] = "Right",
                            ["Keys"] = new System.Collections.Hashtable(StringComparer.OrdinalIgnoreCase)
                            {
                                ["Child"] = "JoinKey",
                                ["Parent"] = "SourceId"
                            }
                        }
                    }
                });
                var secondJoin = PSObject.AsPSObject(new
                {
                    Id = "SecondJoin",
                    Type = "join",
                    JoinType = "left",
                    Inputs = new object[]
                    {
                        new System.Collections.Hashtable(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Job"] = "FirstJoin",
                            ["As"] = "First"
                        },
                        new System.Collections.Hashtable(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Job"] = "LookupSource",
                            ["As"] = "Lookup",
                            ["Keys"] = new System.Collections.Hashtable(StringComparer.OrdinalIgnoreCase)
                            {
                                ["Child"] = "Left.JoinKey",
                                ["Parent"] = "LookupKey"
                            }
                        }
                    }
                });
                var steps = new[]
                {
                    SourceStep("ChildrenSource", "children.jsonl"),
                    SourceStep("ParentsSource", "parents.jsonl"),
                    SourceStep("LookupSource", "lookups.jsonl"),
                    firstJoin,
                    secondJoin,
                    TargetStep("JoinTarget", "SecondJoin", "JoinResults")
                };

                using var powershell = System.Management.Automation.PowerShell.Create();
                powershell.AddCommand("Import-Module")
                    .AddParameter("Name", typeof(InvokeMigrationCommand).Assembly.Location);
                powershell.Invoke();
                Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));

                powershell.Commands.Clear();
                var output = powershell.AddCommand("Invoke-Migration")
                    .AddParameter("DatabasePath", Path.Combine(temporaryDirectory, "migration.sqlite3"))
                    .AddParameter("RunName", "SingleJoinOutputTest")
                    .AddParameter("RunId", "single-join-output-test")
                    .AddParameter("BatchSize", 2)
                    .AddParameter("Steps", steps)
                    .AddParameter("Connections", new System.Collections.Hashtable
                    {
                        ["source"] = new DirectoryInfo(sourceDirectory),
                        ["target"] = new DirectoryInfo(targetDirectory)
                    })
                    .AddParameter("MaxDegreeOfParallelism", 2)
                    .AddParameter("MaxAttempts", 1)
                    .Invoke();

                Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
                Assert.IsType<DependencySchedulerResult>(Assert.Single(output).BaseObject);
                var joinedRows = ReadJsonLines(
                    Path.Combine(targetDirectory, "JoinResults", "single-join-output-test"));
                try
                {
                    var joinedRow = Assert.Single(joinedRows);
                    Assert.Equal(
                        "A",
                        joinedRow.RootElement
                            .GetProperty("First")
                            .GetProperty("Left")
                            .GetProperty("JoinKey")
                            .GetString());
                }
                finally
                {
                    foreach (var row in joinedRows)
                    {
                        row.Dispose();
                    }
                }
            }
            finally
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }

        private static PSObject Step(string id, string type, string[] inputs, ScriptBlock script)
        {
            return PSObject.AsPSObject(new
            {
                Id = id,
                Type = type,
                Inputs = inputs,
                Script = script
            });
        }

        private static PSObject TargetStep(string id, string input, string path)
        {
            return PSObject.AsPSObject(new
            {
                Id = id,
                Type = "target",
                Inputs = new[] { input },
                Target = new
                {
                    Type = "jsonl",
                    ConnectionId = "target",
                    Operation = "Write",
                    BatchSize = 1,
                    Path = path,
                    Options = new System.Collections.Hashtable(),
                    Custom = (object?)null
                }
            });
        }

        private static void WriteJsonLines(string path, string content)
        {
            File.WriteAllText(path, content + Environment.NewLine, new UTF8Encoding(false));
        }

        private static List<JsonDocument> ReadJsonLines(string directory)
        {
            var documents = new List<JsonDocument>();
            foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl"))
            {
                foreach (var line in File.ReadLines(path))
                {
                    documents.Add(JsonDocument.Parse(line));
                }
            }
            return documents;
        }
    }
