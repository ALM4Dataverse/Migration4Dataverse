using System.Collections;
using System.Reflection;
using System.Management.Automation;
using System.Text.Json;
using Migration4Dataverse.Engine.PowerShell;
using Migration4Dataverse.Engine.Scheduling;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.Tests.PowerShell;

public sealed class DataverseTargetParentRecordTests
{
    [Theory]
    [InlineData("inner", 2)]
    [InlineData("left", 3)]
    public async Task MapsFinalParentIdsForInnerAndLeftJoinRows(
        string joinType,
        int expectedContactCount)
    {
        using var directory = new TemporaryDirectory();
        var dataverseModulePath = directory.WriteDataverseModule();
        using var executor = new PowerShellScriptExecutor(
            modulesToImport:
            [
                dataverseModulePath,
                typeof(InvokeMigrationCommand).Assembly.Location
            ]);
        var store = new SqliteMigrationStore(directory.DatabasePath);
        const string runId = "target-id-run";

        var steps = new[]
        {
            CreateSource("AccountsSource", "accounts.jsonl"),
            CreateTarget(
                "AccountsTarget",
                "AccountsSource",
                "account",
                matchOn: ["new_salesforceid", "region_code"]),
            CreateSource("ContactsSource", "contacts.jsonl"),
            PSObject.AsPSObject(new
            {
                Id = "JoinContacts",
                Type = "join",
                Inputs = new object[]
                {
                    new Hashtable(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Job"] = "ContactsSource",
                        ["As"] = "Contact"
                    },
                    new Hashtable(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Job"] = "AccountsTarget",
                        ["As"] = "Account",
                        ["Keys"] = new object[]
                        {
                            new Hashtable(StringComparer.OrdinalIgnoreCase)
                            {
                                ["Child"] = "account_id",
                                ["Parent"] = "new_salesforceid"
                            },
                            new Hashtable(StringComparer.OrdinalIgnoreCase)
                            {
                                ["Child"] = "account_region",
                                ["Parent"] = "region_code"
                            }
                        }
                    }
                },
                JoinType = joinType,
                JoinOrder = "stable",
            }),
            PSObject.AsPSObject(new
            {
                Id = "MapContacts",
                Type = "transform",
                Inputs = new[] { "JoinContacts" },
                Script = ScriptBlock.Create("""
                    param($row)
                    $account = $row.Account
                    @{
                        new_salesforceid = [string]$row.Contact.new_salesforceid
                        parentcustomerid = if ($null -ne $account) {
                            @{ TableName = 'account'; Id = [string]$account.Id }
                        }
                        parent_name = if ($null -ne $account) { [string]$account.Name } else { $null }
                    }
                    """)
            }),
            CreateTarget("ContactsTarget", "MapContacts", "contact")
        };
        var connections = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["files"] = new DirectoryInfo(directory.Path),
            ["crm"] = new object()
        };
        var graphFactory = typeof(InvokeMigrationCommand).Assembly.GetType(
            "Migration4Dataverse.Engine.PowerShell.PowerShellMigrationStepFactory",
            throwOnError: true)!;
        var createGraph = graphFactory.GetMethod("CreateGraph", BindingFlags.Public | BindingFlags.Static)!;
        var stepSet = (IDisposable)createGraph.Invoke(
            null,
            [steps, connections, executor, 1, true, store, 32, false])!;

        try
        {
            var definitions = (IReadOnlyList<MigrationStepDefinition>)stepSet.GetType()
                .GetProperty("Steps", BindingFlags.Public | BindingFlags.Instance)!
                .GetValue(stepSet)!;
            var joinContacts = definitions.Single(step => step.Id == "JoinContacts");
            Assert.Equal(["ContactsSource", "AccountsTarget"], joinContacts.Inputs);
            Assert.Empty(joinContacts.CompletionDependencies);
            Assert.NotNull(joinContacts.ReadSourceBatches);
            var mapContacts = definitions.Single(step => step.Id == "MapContacts");
            Assert.Empty(mapContacts.CompletionDependencies);
            var runResult = await new DependencyScheduler(store).ExecuteAsync(
                runId,
                "target-parent-test",
                "hash-1",
                definitions,
                new DependencySchedulerOptions
                {
                    MaxDegreeOfParallelism = 8,
                    MaxAttempts = 1,
                    CaptureDebugData = true
                });
            Assert.Equal(runId, runResult.RunId);
            Assert.Equal(
                expectedContactCount,
                File.ReadAllLines(System.IO.Path.Combine(directory.Path, "contact-rows.txt")).Length);
            var accountTarget = Assert.Single(
                store.GetDashboardRun(runId).Steps,
                step => step.Id == "AccountsTarget");
            var accountTargetBatches = store.GetDashboardBatches(runId, "AccountsTarget");
            Assert.NotEmpty(accountTargetBatches);
            Assert.All(accountTargetBatches, batch => Assert.Contains("SpecialistInfo", batch.OutcomesJson));
            Assert.True(
                accountTarget.SpecialistInfo.HasValue,
                string.Join(";", accountTargetBatches.Select(batch => batch.OutcomesJson)));
            Assert.True(accountTarget.SpecialistInfo.Value.GetProperty("BatchSizeAuto").GetBoolean());
            Assert.False(accountTarget.SpecialistInfo.Value.GetProperty("ConcurrencyAuto").GetBoolean());
            Assert.Equal(
                "fixed step limit",
                accountTarget.SpecialistInfo.Value.GetProperty("ConcurrencyState").GetString());

            Assert.Empty(store.ReadJoinInputRecordPage(
                runId, "JoinContacts", "ContactsSource", 0, 0, 10));
            Assert.Empty(store.ReadJoinInputRecordPage(
                runId, "JoinContacts", "AccountsTarget", 0, 0, 10));
        }
        finally
        {
            stepSet.Dispose();
        }
    }

    [Fact]
    public async Task EmitsMatchedRowsFromOutOfOrderCompletedParentBatchesBeforeParentStepCompletes()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteMigrationStore(directory.DatabasePath);
        const string runId = "streaming-join-run";
        store.CreateRun(runId, "streaming-join-test", "hash");
        store.EnqueueBatch(runId, "Child", 0, "child-batch-0", null, "{}");
        store.EnqueueBatch(runId, "Parent", 0, "parent-batch-0", null, "{}");
        store.EnqueueBatch(runId, "Parent", 1, "parent-batch-1", null, "{}");

        var childBatch = store.TryLeaseNextBatch(runId, "join-worker", TimeSpan.FromMinutes(1));
        Assert.Equal("child-batch-0", childBatch?.BatchId);
        store.CompleteBatch(
            "child-batch-0",
            "join-worker",
            """{"succeeded":1,"failed":0}""",
            hasErrors: false);

        var pendingParentBatch = store.TryLeaseNextBatch(runId, "join-worker", TimeSpan.FromMinutes(1));
        Assert.Equal("parent-batch-0", pendingParentBatch?.BatchId);
        var completedParentBatch = store.TryLeaseNextBatch(runId, "join-worker", TimeSpan.FromMinutes(1));
        Assert.Equal("parent-batch-1", completedParentBatch?.BatchId);
        store.CompleteBatch(
            "parent-batch-1",
            "join-worker",
            """{"succeeded":1,"failed":0}""",
            hasErrors: false);
        Assert.Equal(-1, store.GetStepCheckpoint(runId, "Parent").LastCompletedSequence);

        store.SaveJoinInputRecords(
            runId,
            [
                new JoinInputRecordMapping(
                    "Join",
                    "Child",
                    0,
                    1,
                    """{"Id":1,"ParentId":"not-yet-available"}""",
                    "child-batch-0",
                    0,
                    ["ParentId"]),
                new JoinInputRecordMapping(
                    "Join",
                    "Child",
                    0,
                    2,
                    """{"Id":2,"ParentId":"available"}""",
                    "child-batch-0",
                    1,
                    ["ParentId"]),
                new JoinInputRecordMapping(
                    "Join",
                    "Parent",
                    1,
                    1,
                    """{"Id":"available","Name":"Parent"}""",
                    "parent-batch-1",
                    0,
                    ["Id"])
            ]);

        var completionCoordinator = new StepCompletionCoordinator();
        var reader = new IndexedStreamingJoinBatchSource(
            store,
            completionCoordinator,
            "Join",
            "Child",
            "Parent",
            "Child",
            "Parent",
            "left",
            batchSize: 1).CreateReader();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var batches = reader(
                store.GetStepCheckpoint(runId, "Join"),
                cancellation.Token)
            .GetAsyncEnumerator();

        Assert.True(await batches.MoveNextAsync());
        using var payload = JsonDocument.Parse(batches.Current.PayloadJson);
        var record = Assert.Single(payload.RootElement.GetProperty("Records").EnumerateArray());
        Assert.Equal(2, record.GetProperty("Child").GetProperty("Id").GetInt32());
        Assert.Equal("available", record.GetProperty("Parent").GetProperty("Id").GetString());
        Assert.Contains("parent-batch-1", batches.Current.ParentBatchIds);
        Assert.Contains("Pending", batches.Current.CursorAfterJson);

        completionCoordinator.MarkStepCompleted(runId, "Parent");
        var resumeCheckpoint = store.GetStepCheckpoint(runId, "Join") with
        {
            LastCompletedSequence = 0,
            CursorJson = batches.Current.CursorAfterJson
        };
        await using var resumedBatches = reader(resumeCheckpoint, cancellation.Token).GetAsyncEnumerator();
        Assert.True(await resumedBatches.MoveNextAsync());
        using var pendingPayload = JsonDocument.Parse(resumedBatches.Current.PayloadJson);
        var pendingRecord = Assert.Single(pendingPayload.RootElement.GetProperty("Records").EnumerateArray());
        Assert.Equal(1, pendingRecord.GetProperty("Child").GetProperty("Id").GetInt32());
        Assert.Equal(JsonValueKind.Null, pendingRecord.GetProperty("Parent").ValueKind);
    }

    [Fact]
    public async Task StableJoinOrderWaitsForEarlierChildBeforeEmittingLaterMatches()
    {
        using var directory = new TemporaryDirectory();
        var store = new SqliteMigrationStore(directory.DatabasePath);
        const string runId = "stable-streaming-join-run";
        store.CreateRun(runId, "stable-streaming-join-test", "hash");
        store.EnqueueBatch(runId, "Child", 0, "stable-child-batch-0", null, "{}");
        store.EnqueueBatch(runId, "Parent", 0, "stable-parent-batch-0", null, "{}");

        var childBatch = store.TryLeaseNextBatch(runId, "join-worker", TimeSpan.FromMinutes(1));
        Assert.Equal("stable-child-batch-0", childBatch?.BatchId);
        store.CompleteBatch(
            "stable-child-batch-0",
            "join-worker",
            """{"succeeded":1,"failed":0}""",
            hasErrors: false);
        var parentBatch = store.TryLeaseNextBatch(runId, "join-worker", TimeSpan.FromMinutes(1));
        Assert.Equal("stable-parent-batch-0", parentBatch?.BatchId);
        store.CompleteBatch(
            "stable-parent-batch-0",
            "join-worker",
            """{"succeeded":1,"failed":0}""",
            hasErrors: false);

        store.SaveJoinInputRecords(
            runId,
            [
                new JoinInputRecordMapping(
                    "Join",
                    "Child",
                    0,
                    1,
                    """{"Id":1,"ParentId":"not-yet-available"}""",
                    "stable-child-batch-0",
                    0,
                    ["ParentId"]),
                new JoinInputRecordMapping(
                    "Join",
                    "Child",
                    0,
                    2,
                    """{"Id":2,"ParentId":"available"}""",
                    "stable-child-batch-0",
                    1,
                    ["ParentId"]),
                new JoinInputRecordMapping(
                    "Join",
                    "Parent",
                    0,
                    1,
                    """{"Id":"available"}""",
                    "stable-parent-batch-0",
                    0,
                    ["Id"])
            ]);

        var completionCoordinator = new StepCompletionCoordinator();
        var reader = new IndexedStreamingJoinBatchSource(
            store,
            completionCoordinator,
            "Join",
            "Child",
            "Parent",
            "Child",
            "Parent",
            "left",
            batchSize: 1,
            preserveInputOrder: true).CreateReader();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var batches = reader(
                store.GetStepCheckpoint(runId, "Join"),
                cancellation.Token)
            .GetAsyncEnumerator();
        var completeParent = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellation.Token);
            completionCoordinator.MarkStepCompleted(runId, "Parent");
        });

        Assert.True(await batches.MoveNextAsync());
        using var firstPayload = JsonDocument.Parse(batches.Current.PayloadJson);
        var firstRecord = Assert.Single(firstPayload.RootElement.GetProperty("Records").EnumerateArray());
        Assert.Equal(1, firstRecord.GetProperty("Child").GetProperty("Id").GetInt32());
        Assert.Equal(JsonValueKind.Null, firstRecord.GetProperty("Parent").ValueKind);
        Assert.True(await batches.MoveNextAsync());
        using var secondPayload = JsonDocument.Parse(batches.Current.PayloadJson);
        var secondRecord = Assert.Single(secondPayload.RootElement.GetProperty("Records").EnumerateArray());
        Assert.Equal(2, secondRecord.GetProperty("Child").GetProperty("Id").GetInt32());
        await completeParent;
    }

    private static PSObject CreateSource(string id, string fileName) =>
        PSObject.AsPSObject(new
        {
            Id = id,
            Type = "source",
            Inputs = Array.Empty<string>(),
            Source = new
            {
                Type = "jsonl",
                ConnectionId = "files",
                Path = fileName,
                Key = "id"
            }
        });

    private static PSObject CreateTarget(
        string id,
        string input,
        string tableName,
        string[]? matchOn = null)
    {
        var options = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["MatchOn"] = new[] { matchOn ?? ["new_salesforceid"] }
        };

        return PSObject.AsPSObject(new
        {
            Id = id,
            Type = "target",
            Inputs = new[] { input },
            Target = new Hashtable(StringComparer.OrdinalIgnoreCase)
            {
                ["Type"] = "dataverse",
                ["ConnectionId"] = "crm",
                ["Table"] = tableName,
                ["MaxConcurrency"] = 2,
                ["Options"] = options
            }
        });
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"migration4dataverse-target-id-{Guid.NewGuid():N}");

        public TemporaryDirectory()
        {
            Directory.CreateDirectory(_directory);
            Path = _directory;
            DatabasePath = System.IO.Path.Combine(_directory, "migration.sqlite3");
            File.WriteAllText(
                System.IO.Path.Combine(_directory, "accounts.jsonl"),
                """
                {"id":"A001","new_salesforceid":"002source","region_code":"south","Name":"Parent record 1"}
                {"id":"A002","new_salesforceid":"001source","region_code":"north","Name":"Parent record 2"}
                """);
            File.WriteAllText(
                System.IO.Path.Combine(_directory, "contacts.jsonl"),
                """
                {"id":"C001","new_salesforceid":"003contact","account_id":"002source","account_region":"south","billing_account_id":"001source","billing_region":"north"}
                {"id":"C002","new_salesforceid":"003contact2","account_id":"001source","account_region":"north","billing_account_id":"002source","billing_region":"south"}
                {"id":"C003","new_salesforceid":"003contact3","account_id":"missing","account_region":"south","billing_account_id":"missing","billing_region":"south"}
                """);
        }

        public string Path { get; }

        public string DatabasePath { get; }

        public string WriteDataverseModule()
        {
            var modulePath = System.IO.Path.Combine(Path, "DataverseMock.psm1");
            File.WriteAllText(modulePath, """
                function Set-DataverseRecord {
                    [CmdletBinding()]
                    param(
                        [Parameter(ValueFromPipeline = $true)] $InputObject,
                        [string] $TableName,
                        [object] $Connection,
                        [int] $BatchSize,
                        [int] $MaxDegreeOfParallelism,
                        [int] $Retries,
                        [int] $InitialRetryDelay,
                        [string[][]] $MatchOn,
                        [hashtable] $LookupColumns,
                        [switch] $PassThru
                    )
                    process {
                        if ($TableName -eq 'account') {
                            if (-not $PassThru) { throw 'Account target must request PassThru.' }
                            if ($InputObject.Name -eq 'Parent record 2') {
                                Start-Sleep -Milliseconds 1500
                            }
                            Add-Content -Path (Join-Path $PSScriptRoot 'events.txt') `
                                -Value "AccountComplete:$($InputObject.new_salesforceid)"
                            [pscustomobject]@{
                                new_salesforceid = [string]$InputObject.new_salesforceid
                                region_code = [string]$InputObject.region_code
                                Name = [string]$InputObject.Name
                                Id = [guid]'11111111-1111-1111-1111-111111111111'
                            }
                        }
                        elseif ($TableName -eq 'contact') {
                            Add-Content -Path (Join-Path $PSScriptRoot 'contact-rows.txt') `
                                -Value ([string]$InputObject.new_salesforceid)
                            Add-Content -Path (Join-Path $PSScriptRoot 'events.txt') `
                                -Value "Contact:$($InputObject.new_salesforceid)"
                            if ($InputObject.new_salesforceid -eq '003contact3') {
                                if ($null -ne $InputObject.parentcustomerid) {
                                    throw 'Unmatched left-join row unexpectedly received parent lookups.'
                                }
                            }
                            else {
                                $reference = $InputObject.parentcustomerid
                                if ($null -eq $reference) {
                                    throw 'Contact lookup was null.'
                                }
                                if ($reference.PSObject.Properties['TableName'].Value -ne 'account') {
                                    throw "Contact lookup TableName was '$($reference.PSObject.Properties['TableName'].Value)'."
                                }
                                if ($reference.PSObject.Properties['Id'].Value -ne [guid]'11111111-1111-1111-1111-111111111111') {
                                    throw "Contact lookup Id was '$($reference.PSObject.Properties['Id'].Value)'."
                                }
                                $expectedParentName = if ($InputObject.new_salesforceid -eq '003contact') {
                                    'Parent record 1'
                                }
                                else {
                                    'Parent record 2'
                                }
                                if ($InputObject.parent_name -ne $expectedParentName) {
                                    throw 'Contact mapping did not receive all parent record fields.'
                                }
                            }
                            if ($PassThru) { throw 'Contact target did not request PassThru.' }
                        }
                    }
                }
                Export-ModuleMember -Function Set-DataverseRecord
                """);
            return modulePath;
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
