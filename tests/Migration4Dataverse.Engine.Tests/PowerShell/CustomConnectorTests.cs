using System.Text.Json;
using System.Collections;
using System.Management.Automation;
using System.Net;
using System.Text;
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

    [Fact]
    public async Task BinaryValuesPassThroughAndCanBeStreamModifiedAndBase64EncodedAcrossGraphBatches()
    {
        using var directory = new TemporaryDirectory();
        using var executor = new PowerShellScriptExecutor(
            modulesToImport: [typeof(InvokeMigrationCommand).Assembly.Location]);
        var store = new SqliteMigrationStore(Path.Combine(directory.Path, "migration.sqlite"));
        var source = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["Type"] = "custom",
            ["ConnectionId"] = "source",
            ["Key"] = "SourceId",
            ["Custom"] = new Hashtable(StringComparer.OrdinalIgnoreCase)
            {
                ["Identity"] = "binary-source-v1",
                ["Callback"] = ScriptBlock.Create("""
                    param($Connection, $Cursor, $PageSize, $StepId, $Path, $BinaryContext)
                    $binary = [Migration4Dataverse.Engine.Connectors.MigrationBinaryValue]::FromBytes(
                        [System.Text.Encoding]::UTF8.GetBytes('original'),
                        $BinaryContext.StagingRoot,
                        'original.txt',
                        'text/plain')
                    @{
                        Records = @(@{ SourceId = '001'; content = $binary })
                        Done = $true
                    }
                    """)
            }
        };
        var target = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["Type"] = "custom",
            ["ConnectionId"] = "target",
            ["Custom"] = new Hashtable(StringComparer.OrdinalIgnoreCase)
            {
                ["Identity"] = "binary-target-v1",
                ["Callback"] = ScriptBlock.Create("""
                    param($Connection, $Records, $Context)
                    $record = $Records[0]
                    if ($record.content.FileName -ne 'original.txt' -or
                        $record.content.Representation -ne 'binary') {
                        throw 'Pass-through changed binary metadata or representation.'
                    }
                    $reader = $record.content.OpenTextReader()
                    try { $original = $reader.ReadToEnd() } finally { $reader.Dispose() }
                    if ($original -ne 'original') { throw 'Pass-through changed binary content.' }
                    if ($record.encoded.Representation -ne 'base64') {
                        throw 'Mapped binary value was not marked as Base64.'
                    }
                    $decoded = $record.encoded.DecodeBase64()
                    $reader = $decoded.OpenTextReader()
                    try { $mapped = $reader.ReadToEnd() } finally { $reader.Dispose() }
                    if ($mapped -ne 'modified') { throw 'Base64 decode did not restore mapped content.' }
                    [System.IO.File]::WriteAllText(
                        [System.IO.Path]::Combine($Connection.FullName, 'binary-result.txt'),
                        "$original|$mapped|$($decoded.FileName)|$($decoded.ContentType)")
                    return $true
                    """)
            }
        };
        var steps = new[]
        {
            PSObject.AsPSObject(new
            {
                Id = "BinarySource",
                Type = "source",
                Inputs = Array.Empty<string>(),
                Source = source
            }),
            PSObject.AsPSObject(new
            {
                Id = "BinaryMap",
                Type = "transform",
                Inputs = new[] { "BinarySource" },
                Script = ScriptBlock.Create("""
                    param($row, $BinaryContext)
                    if ($row.content -isnot [Migration4Dataverse.Engine.Connectors.MigrationBinaryValue]) {
                        throw "Binary transport was not restored; received keys '$($row.content.Keys -join ',')'."
                    }
                    $writer = $row.content.CreateWriter('modified.txt', 'text/plain')
                    try {
                        $bytes = [System.Text.Encoding]::UTF8.GetBytes('modified')
                        $writer.Stream.Write($bytes, 0, $bytes.Length)
                        $modified = $writer.Complete()
                    }
                    finally {
                        $writer.Dispose()
                    }
                    @{
                        SourceId = $row.SourceId
                        content = $row.content
                        encoded = $modified.EncodeBase64()
                    }
                    """)
            }),
            PSObject.AsPSObject(new
            {
                Id = "BinaryTarget",
                Type = "target",
                Inputs = new[] { "BinaryMap" },
                Target = target
            })
        };
        var connections = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = new object(),
            ["target"] = new DirectoryInfo(directory.Path)
        };
        var factory = typeof(InvokeMigrationCommand).Assembly.GetType(
            "Migration4Dataverse.Engine.PowerShell.PowerShellMigrationStepFactory",
            throwOnError: true)!;
        var createGraph = factory.GetMethod("CreateGraph", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        using var stepSet = (IDisposable)createGraph.Invoke(
            null,
            [steps, connections, executor, 1, false, store, 1, false, false, "binary-run", store.DatabasePath])!;
        var definitions = (IReadOnlyList<MigrationStepDefinition>)stepSet.GetType()
            .GetProperty("Steps", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!
            .GetValue(stepSet)!;

        await new DependencyScheduler(store).ExecuteAsync(
            "binary-run",
            "binary-round-trip",
            "binary-step-hash",
            definitions,
            new DependencySchedulerOptions
            {
                MaxDegreeOfParallelism = 1,
                MaxAttempts = 1
            });

        Assert.Equal(
            "original|modified|modified.txt|text/plain",
            File.ReadAllText(Path.Combine(directory.Path, "binary-result.txt")));
    }

    [Fact]
    public async Task StructuredFileTargetUsesCreateBinaryReferenceCallback()
    {
        using var directory = new TemporaryDirectory();
        using var executor = new PowerShellScriptExecutor(
            modulesToImport: [typeof(InvokeMigrationCommand).Assembly.Location]);
        var store = new SqliteMigrationStore(Path.Combine(directory.Path, "migration.sqlite"));
        var source = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["Type"] = "custom",
            ["ConnectionId"] = "source",
            ["Key"] = "Id",
            ["Custom"] = new Hashtable(StringComparer.OrdinalIgnoreCase)
            {
                ["Identity"] = "sidecar-source-v1",
                ["Callback"] = ScriptBlock.Create("""
                    param($Connection, $Cursor, $PageSize, $StepId, $Path, $BinaryContext)
                    $value = [Migration4Dataverse.Engine.Connectors.MigrationBinaryValue]::FromBytes(
                        [System.Text.Encoding]::UTF8.GetBytes('sidecar-content'),
                        $BinaryContext.StagingRoot,
                        'invoice.pdf',
                        'application/pdf')
                    @{
                        Records = @(@{ Id = 'A001'; Content = $value })
                        Done = $true
                    }
                    """)
            }
        };
        var target = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["Type"] = "file",
            ["Format"] = new Hashtable(StringComparer.OrdinalIgnoreCase)
            {
                ["Type"] = "jsonl",
                ["Options"] = new Hashtable(StringComparer.OrdinalIgnoreCase)
                {
                    ["BinaryColumns"] = new[] { "Content" }
                }
            },
            ["ConnectionId"] = "files",
            ["Path"] = "records.jsonl",
            ["Options"] = new Hashtable(StringComparer.OrdinalIgnoreCase),
            ["CreateBinaryReference"] = ScriptBlock.Create("""
                param($Row, $ColumnName, $Value, $DefaultRelativePath, $Context)
                'custom/invoice.pdf'
                """)
        };
        var steps = new[]
        {
            PSObject.AsPSObject(new
            {
                Id = "AttachmentSource",
                Type = "source",
                Inputs = Array.Empty<string>(),
                Source = source
            }),
            PSObject.AsPSObject(new
            {
                Id = "AttachmentTarget",
                Type = "target",
                Inputs = new[] { "AttachmentSource" },
                Target = target
            })
        };
        var connections = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = new object(),
            ["files"] = new DirectoryInfo(directory.Path)
        };
        var factory = typeof(InvokeMigrationCommand).Assembly.GetType(
            "Migration4Dataverse.Engine.PowerShell.PowerShellMigrationStepFactory",
            throwOnError: true)!;
        var createGraph = factory.GetMethod(
            "CreateGraph",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        using var stepSet = (IDisposable)createGraph.Invoke(
            null,
            [steps, connections, executor, 1, false, store, 1, false, false, "sidecar-run", store.DatabasePath])!;
        var definitions = (IReadOnlyList<MigrationStepDefinition>)stepSet.GetType()
            .GetProperty("Steps", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!
            .GetValue(stepSet)!;

        await new DependencyScheduler(store).ExecuteAsync(
            "sidecar-run",
            "sidecar-output",
            "sidecar-output-hash",
            definitions,
            new DependencySchedulerOptions
            {
                MaxDegreeOfParallelism = 1,
                MaxAttempts = 1
            });

        var targetPath = Path.Combine(directory.Path, "records.jsonl");
        using var record = JsonDocument.Parse(File.ReadAllText(targetPath));
        var reference = record.RootElement.GetProperty("Content").GetString()!;
        var sidecarPath = Path.Combine(
            directory.Path,
            reference.Replace('/', Path.DirectorySeparatorChar));
        Assert.EndsWith("/custom/invoice.pdf", reference, StringComparison.Ordinal);
        Assert.Equal("sidecar-content", File.ReadAllText(sidecarPath));
    }

    [Fact]
    public async Task StructuredFileSourceUsesResolveBinaryReferenceCallback()
    {
        using var directory = new TemporaryDirectory();
        using var executor = new PowerShellScriptExecutor(
            modulesToImport: [typeof(InvokeMigrationCommand).Assembly.Location]);
        var sourcePath = Path.Combine(directory.Path, "records.jsonl");
        var externalPath = Path.Combine(directory.Path, "resolved.bin");
        File.WriteAllText(sourcePath, """{"Id":"A001","Content":"custom-reference"}""" + Environment.NewLine);
        File.WriteAllText(externalPath, "resolved-content");
        var store = new SqliteMigrationStore(Path.Combine(directory.Path, "migration.sqlite"));
        var source = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["Type"] = "file",
            ["Format"] = new Hashtable(StringComparer.OrdinalIgnoreCase)
            {
                ["Type"] = "jsonl",
                ["Options"] = new Hashtable(StringComparer.OrdinalIgnoreCase)
                {
                    ["BinaryColumns"] = new[] { "Content" }
                }
            },
            ["ConnectionId"] = "files",
            ["Path"] = "records.jsonl",
            ["Key"] = "Id",
            ["Options"] = new Hashtable(StringComparer.OrdinalIgnoreCase),
            ["ResolveBinaryReference"] = ScriptBlock.Create("""
                param($Reference, $Row, $ColumnName, $Context)
                if ($Reference -ne 'custom-reference' -or $ColumnName -ne 'Content') {
                    throw 'Resolver received unexpected reference details.'
                }
                [System.IO.Path]::Combine(
                    [System.IO.Path]::GetDirectoryName($Context.SourcePath),
                    'resolved.bin')
                """)
        };
        var target = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["Type"] = "custom",
            ["ConnectionId"] = "output",
            ["Custom"] = new Hashtable(StringComparer.OrdinalIgnoreCase)
            {
                ["Identity"] = "sidecar-resolver-target-v1",
                ["Callback"] = ScriptBlock.Create("""
                    param($Connection, $Records, $Context)
                    $reader = $Records[0].Content.OpenTextReader()
                    try {
                        [System.IO.File]::WriteAllText(
                            [System.IO.Path]::Combine($Connection.FullName, 'resolved.txt'),
                            $reader.ReadToEnd())
                    }
                    finally {
                        $reader.Dispose()
                    }
                    return $true
                    """)
            }
        };
        var steps = new[]
        {
            PSObject.AsPSObject(new
            {
                Id = "AttachmentSource",
                Type = "source",
                Inputs = Array.Empty<string>(),
                Source = source
            }),
            PSObject.AsPSObject(new
            {
                Id = "AttachmentTarget",
                Type = "target",
                Inputs = new[] { "AttachmentSource" },
                Target = target
            })
        };
        var connections = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["files"] = new DirectoryInfo(directory.Path),
            ["output"] = new DirectoryInfo(directory.Path)
        };
        var factory = typeof(InvokeMigrationCommand).Assembly.GetType(
            "Migration4Dataverse.Engine.PowerShell.PowerShellMigrationStepFactory",
            throwOnError: true)!;
        var createGraph = factory.GetMethod(
            "CreateGraph",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        using var stepSet = (IDisposable)createGraph.Invoke(
            null,
            [steps, connections, executor, 1, false, store, 1, false, false, "sidecar-resolver-run", store.DatabasePath])!;
        var definitions = (IReadOnlyList<MigrationStepDefinition>)stepSet.GetType()
            .GetProperty("Steps", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!
            .GetValue(stepSet)!;

        await new DependencyScheduler(store).ExecuteAsync(
            "sidecar-resolver-run",
            "sidecar-resolver",
            "sidecar-resolver-hash",
            definitions,
            new DependencySchedulerOptions
            {
                MaxDegreeOfParallelism = 1,
                MaxAttempts = 1
            });

        Assert.Equal("resolved-content", File.ReadAllText(Path.Combine(directory.Path, "resolved.txt")));
    }

    [Fact]
    public async Task BinaryFileSourceAndTargetCopyContentThroughTheMigrationGraph()
    {
        using var directory = new TemporaryDirectory();
        using var executor = new PowerShellScriptExecutor(
            modulesToImport: [typeof(InvokeMigrationCommand).Assembly.Location]);
        var sourcePath = Path.Combine(directory.Path, "source.bin");
        var targetPath = Path.Combine(directory.Path, "target.bin");
        var content = new byte[] { 0, 1, 2, 255 };
        File.WriteAllBytes(sourcePath, content);
        var store = new SqliteMigrationStore(Path.Combine(directory.Path, "migration.sqlite"));
        var steps = new[]
        {
            PSObject.AsPSObject(new
            {
                Id = "BinarySource",
                Type = "source",
                Inputs = Array.Empty<string>(),
                Source = new Hashtable(StringComparer.OrdinalIgnoreCase)
                {
                    ["Type"] = "file",
                    ["Format"] = new Hashtable { ["Type"] = "binary" },
                    ["ConnectionId"] = "source",
                    ["Path"] = "source.bin",
                    ["Key"] = "FileName"
                }
            }),
            PSObject.AsPSObject(new
            {
                Id = "BinaryTarget",
                Type = "target",
                Inputs = new[] { "BinarySource" },
                Target = new Hashtable(StringComparer.OrdinalIgnoreCase)
                {
                    ["Type"] = "file",
                    ["Format"] = new Hashtable { ["Type"] = "binary" },
                    ["ConnectionId"] = "target",
                    ["Path"] = "target.bin"
                }
            })
        };
        var connections = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = new DirectoryInfo(directory.Path),
            ["target"] = new DirectoryInfo(directory.Path)
        };
        var factory = typeof(InvokeMigrationCommand).Assembly.GetType(
            "Migration4Dataverse.Engine.PowerShell.PowerShellMigrationStepFactory",
            throwOnError: true)!;
        var createGraph = factory.GetMethod(
            "CreateGraph",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        using var stepSet = (IDisposable)createGraph.Invoke(
            null,
            [steps, connections, executor, 1, false, store, 1, false, false, "binary-file-run", store.DatabasePath])!;
        var definitions = (IReadOnlyList<MigrationStepDefinition>)stepSet.GetType()
            .GetProperty("Steps", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!
            .GetValue(stepSet)!;

        await new DependencyScheduler(store).ExecuteAsync(
            "binary-file-run",
            "binary-file-copy",
            "binary-file-copy-hash",
            definitions,
            new DependencySchedulerOptions
            {
                MaxDegreeOfParallelism = 1,
                MaxAttempts = 1
            });

        Assert.Equal(content, File.ReadAllBytes(targetPath));
    }

    [Fact]
    public async Task BinaryFileSourceUploadsToSharePointThroughTheMigrationGraph()
    {
        using var directory = new TemporaryDirectory();
        byte[]? uploadedContent = null;
        using var client = new HttpClient(new StubHandler(async request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            if (request.Method == HttpMethod.Put)
            {
                uploadedContent = await request.Content!.ReadAsByteArrayAsync();
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("""{"id":"drive-item"}""", Encoding.UTF8, "application/json")
                };
            }
            throw new InvalidOperationException(
                $"Unexpected SharePoint request: {request.Method} {request.RequestUri}");
        }))
        {
            BaseAddress = new Uri("https://graph.microsoft.com/v1.0/")
        };
        using var executor = new PowerShellScriptExecutor(
            modulesToImport: [typeof(InvokeMigrationCommand).Assembly.Location]);
        var sourcePath = Path.Combine(directory.Path, "source.bin");
        var content = new byte[] { 10, 0, 255, 42 };
        File.WriteAllBytes(sourcePath, content);
        var store = new SqliteMigrationStore(Path.Combine(directory.Path, "migration.sqlite"));
        var steps = new[]
        {
            PSObject.AsPSObject(new
            {
                Id = "BinarySource",
                Type = "source",
                Inputs = Array.Empty<string>(),
                Source = new Hashtable(StringComparer.OrdinalIgnoreCase)
                {
                    ["Type"] = "file",
                    ["Format"] = new Hashtable { ["Type"] = "binary" },
                    ["ConnectionId"] = "source",
                    ["Path"] = "source.bin",
                    ["Key"] = "FileName"
                }
            }),
            PSObject.AsPSObject(new
            {
                Id = "SharePointTarget",
                Type = "target",
                Inputs = new[] { "BinarySource" },
                Target = new Hashtable(StringComparer.OrdinalIgnoreCase)
                {
                    ["Type"] = "sharepoint",
                    ["ConnectionId"] = "target",
                    ["Options"] = new Hashtable(StringComparer.OrdinalIgnoreCase)
                    {
                        ["DriveId"] = "drive"
                    }
                }
            })
        };
        var connections = new Hashtable(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = new DirectoryInfo(directory.Path),
            ["target"] = client
        };
        var factory = typeof(InvokeMigrationCommand).Assembly.GetType(
            "Migration4Dataverse.Engine.PowerShell.PowerShellMigrationStepFactory",
            throwOnError: true)!;
        var createGraph = factory.GetMethod(
            "CreateGraph",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        using var stepSet = (IDisposable)createGraph.Invoke(
            null,
            [steps, connections, executor, 1, false, store, 1, false, false, "sharepoint-run", store.DatabasePath])!;
        var definitions = (IReadOnlyList<MigrationStepDefinition>)stepSet.GetType()
            .GetProperty("Steps", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!
            .GetValue(stepSet)!;

        await new DependencyScheduler(store).ExecuteAsync(
            "sharepoint-run",
            "sharepoint-binary-copy",
            "sharepoint-binary-copy-hash",
            definitions,
            new DependencySchedulerOptions
            {
                MaxDegreeOfParallelism = 1,
                MaxAttempts = 1
            });

        Assert.Equal(content, uploadedContent);
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

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return handler(request);
        }
    }
}
