using System.Collections;
using System.Runtime.CompilerServices;
using System.Management.Automation;
using System.Text.Json;
using Migration4Dataverse.Engine.Transformations;

namespace Migration4Dataverse.Engine.Tests.Transformations;

public sealed class DataFlowPipelineTests
{
    [Theory]
    [InlineData("append")]
    [InlineData("union-all")]
    [InlineData("union all")]
    public async Task AppendStreamsInputsInDeclaredOrder(string nodeType)
    {
        var definition = new DataFlowPipelineDefinition
        {
            Sources = [CreateSource("first"), CreateSource("second")],
            Nodes =
            [
                CreateSourceNode("first-node", "first"),
                CreateSourceNode("second-node", "second"),
                new()
                {
                    Id = "combined",
                    Type = nodeType,
                    Inputs = ["first-node", "second-node"]
                }
            ],
            OutputNodeId = "combined"
        };
        var pipeline = new DataFlowPipeline(definition);
        var result = await ReadAllAsync(
            pipeline,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["first"] = """[{"value":"first-1"},{"value":"first-2"}]""",
                ["second"] = """[{"value":"second-1"}]"""
            });

        Assert.Equal(
            new[] { "first-1", "first-2", "second-1" },
            result.Select(record => record.GetProperty("value").GetString()).ToArray());
    }

    [Fact]
    public async Task AppendDoesNotReadLaterInputUntilEarlierInputCompletes()
    {
        var definition = new DataFlowPipelineDefinition
        {
            Sources = [CreateSource("first"), CreateSource("second")],
            Nodes =
            [
                CreateSourceNode("first-node", "first"),
                CreateSourceNode("second-node", "second"),
                new()
                {
                    Id = "combined",
                    Type = "append",
                    Inputs = ["first-node", "second-node"]
                }
            ],
            OutputNodeId = "combined"
        };
        var pipeline = new DataFlowPipeline(definition);
        var reads = new List<string>();
        var sourceRecords = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["first"] = ["""{"value":"first-1"}""", """{"value":"first-2"}"""],
            ["second"] = ["""{"value":"second-1"}"""]
        };
        DataFlowSourceReader sourceReader = (source, cancellationToken) =>
            ReadTrackedRowsAsync(source.Id, sourceRecords[source.Id], reads, cancellationToken);

        await using var output = pipeline.ReadAsync(sourceReader).GetAsyncEnumerator();
        Assert.True(await output.MoveNextAsync());
        Assert.Equal("first-1", output.Current.GetProperty("value").GetString());
        Assert.Equal(new[] { "first:first-1" }, reads);

        Assert.True(await output.MoveNextAsync());
        Assert.Equal("first-2", output.Current.GetProperty("value").GetString());
        Assert.Equal(new[] { "first:first-1", "first:first-2" }, reads);

        Assert.True(await output.MoveNextAsync());
        Assert.Equal("second-1", output.Current.GetProperty("value").GetString());
        Assert.Equal(new[] { "first:first-1", "first:first-2", "second:second-1" }, reads);
    }

    [Fact]
    public async Task TransformAndFilterNodesUseRuntimePowerShellScripts()
    {
        var definition = new DataFlowPipelineDefinition
        {
            Sources = [CreateSource("records")],
            Nodes =
            [
                CreateSourceNode("source", "records"),
                new()
                {
                    Id = "expand",
                    Type = "transform",
                    Inputs = ["source"]
                },
                new()
                {
                    Id = "keep-selected",
                    Type = "filter",
                    Inputs = ["expand"]
                }
            ],
            OutputNodeId = "keep-selected"
        };
        var scripts = new Dictionary<string, ScriptBlock>(StringComparer.Ordinal)
        {
            ["expand"] = ScriptBlock.Create("""
                param($row)
                if ($row.Value -eq 1) { return }
                [pscustomobject]@{ Value = $row.Value * 10; Kind = 'first' }
                [pscustomobject]@{ Value = $row.Value * 10 + 1; Kind = 'selected' }
                """),
            ["keep-selected"] = ScriptBlock.Create("param($row) $row.Value -eq 21")
        };
        var result = await ReadAllAsync(
            new DataFlowPipeline(definition),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["records"] = """[{"Value":1},{"Value":2}]"""
            },
            scripts);

        var record = Assert.Single(result);
        Assert.Equal(21, record.GetProperty("Value").GetInt32());
        Assert.Equal("selected", record.GetProperty("Kind").GetString());
    }

    [Fact]
    public async Task TransformNodeReportsScriptErrorsWithNodeIdentity()
    {
        var definition = new DataFlowPipelineDefinition
        {
            Sources = [CreateSource("records")],
            Nodes =
            [
                CreateSourceNode("source", "records"),
                new()
                {
                    Id = "broken-transform",
                    Type = "transform",
                    Inputs = ["source"]
                }
            ],
            OutputNodeId = "broken-transform"
        };
        var pipeline = new DataFlowPipeline(definition);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReadAllAsync(
                pipeline,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["records"] = """[{"Value":1}]"""
                },
                new Dictionary<string, ScriptBlock>(StringComparer.Ordinal)
                {
                    ["broken-transform"] = ScriptBlock.Create("param($row) throw 'script failed'")
                }));

        Assert.Contains("broken-transform", exception.Message, StringComparison.Ordinal);
        Assert.Contains("script failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptNodesRequireRuntimeScriptsForEveryNodeAndRejectUnmatchedScripts()
    {
        var definition = new DataFlowPipelineDefinition
        {
            Sources = [CreateSource("records")],
            Nodes =
            [
                CreateSourceNode("source", "records"),
                new()
                {
                    Id = "filtered",
                    Type = "filter",
                    Inputs = ["source"]
                }
            ],
            OutputNodeId = "filtered"
        };
        var pipeline = new DataFlowPipeline(definition);
        DataFlowSourceReader sourceReader = (_, _) => ReadRowsAsync("[]", CancellationToken.None);

        Assert.Throws<ArgumentException>(() => pipeline.ReadAsync(sourceReader));
        Assert.Throws<ArgumentException>(
            () => pipeline.ReadAsync(
                sourceReader,
                new Dictionary<string, ScriptBlock>(StringComparer.Ordinal)
                {
                    ["not-a-node"] = ScriptBlock.Create("param($row) $true")
                }));
    }

    [Fact]
    public async Task MergeInterleavesSortedInputsAndKeepsLeftInputFirstForTies()
    {
        var definition = new DataFlowPipelineDefinition
        {
            Sources = [CreateSource("left"), CreateSource("right")],
            Nodes =
            [
                CreateSourceNode("left-node", "left"),
                CreateSourceNode("right-node", "right"),
                new()
                {
                    Id = "merged",
                    Type = "merge",
                    Inputs = ["left-node", "right-node"],
                    Keys = [new() { Name = "key", Type = "int" }]
                }
            ],
            OutputNodeId = "merged"
        };
        var pipeline = new DataFlowPipeline(definition);
        var result = await ReadAllAsync(
            pipeline,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["left"] = """[{"key":1,"value":"L1"},{"key":2,"value":"L2a"},{"key":2,"value":"L2b"},{"key":4,"value":"L4"}]""",
                ["right"] = """[{"key":2,"value":"R2"},{"key":3,"value":"R3"}]"""
            });

        Assert.Equal(
            new[] { "L1", "L2a", "L2b", "R2", "R3", "L4" },
            result.Select(record => record.GetProperty("value").GetString()).ToArray());
    }

    [Fact]
    public async Task MergeRejectsAnInputThatIsNotSorted()
    {
        var definition = new DataFlowPipelineDefinition
        {
            Sources = [CreateSource("left"), CreateSource("right")],
            Nodes =
            [
                CreateSourceNode("left-node", "left"),
                CreateSourceNode("right-node", "right"),
                new()
                {
                    Id = "merged",
                    Type = "merge",
                    Inputs = ["left-node", "right-node"],
                    Keys = [new() { Name = "key", Type = "int" }]
                }
            ],
            OutputNodeId = "merged"
        };
        var pipeline = new DataFlowPipeline(definition);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadAllAsync(
                pipeline,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["left"] = """[{"key":2},{"key":1}]""",
                    ["right"] = "[]"
                }));

        Assert.Contains("record 2 is out of order", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SortSpillsMultipleRunsAndCleansTemporaryFiles()
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"m4d-sort-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var definition = new DataFlowPipelineDefinition
            {
                Sources = [CreateSource("records")],
                Nodes =
                [
                    CreateSourceNode("records-node", "records"),
                    new()
                    {
                        Id = "sorted",
                        Type = "sort",
                        Inputs = ["records-node"],
                        Keys = [new() { Name = "key", Type = "int" }],
                        SortOptions = new()
                        {
                            MemoryBudgetBytes = 4_096,
                            MaxInMemoryRows = 1,
                            MaxRecordBytes = 1_024,
                            MaxSpillBytes = 1_000_000,
                            TemporaryDirectory = temporaryDirectory
                        }
                    }
                ],
                OutputNodeId = "sorted"
            };
            var pipeline = new DataFlowPipeline(definition);
            var result = await ReadAllAsync(
                pipeline,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["records"] = """[{"key":3,"value":"three"},{"key":2,"value":"two-a"},{"key":1,"value":"one"},{"key":2,"value":"two-b"},{"key":0,"value":"zero"}]"""
                });

            Assert.Equal(
                new[] { "zero", "one", "two-a", "two-b", "three" },
                result.Select(record => record.GetProperty("value").GetString()).ToArray());
            Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryDirectory));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SortRejectsSpillLimitAndCleansTemporaryFiles()
    {
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"m4d-sort-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var definition = new DataFlowPipelineDefinition
            {
                Sources = [CreateSource("records")],
                Nodes =
                [
                    CreateSourceNode("records-node", "records"),
                    new()
                    {
                        Id = "sorted",
                        Type = "sort",
                        Inputs = ["records-node"],
                        Keys = [new() { Name = "key", Type = "int" }],
                        SortOptions = new()
                        {
                            MemoryBudgetBytes = 4_096,
                            MaxInMemoryRows = 1,
                            MaxRecordBytes = 1_024,
                            MaxSpillBytes = 1,
                            TemporaryDirectory = temporaryDirectory
                        }
                    }
                ],
                OutputNodeId = "sorted"
            };
            var pipeline = new DataFlowPipeline(definition);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => ReadAllAsync(
                    pipeline,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["records"] = """[{"key":2},{"key":1}]"""
                    }));

            Assert.Contains("MaxSpillBytes", exception.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(temporaryDirectory));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task JsonLinesHelperReadsTransformsAndAtomicallyWritesOutput()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"m4d-flow-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var inputPath = Path.Combine(directory, "input.jsonl");
            var outputPath = Path.Combine(directory, "output.jsonl");
            await File.WriteAllLinesAsync(
                inputPath,
                ["""{"key":2,"value":"two"}""", """{"key":1,"value":"one"}"""]);
            var definition = new DataFlowPipelineDefinition
            {
                Sources =
                [
                    new()
                    {
                        Id = "input",
                        Type = "jsonl",
                        ConnectionId = "files",
                        Path = inputPath
                    }
                ],
                Nodes =
                [
                    CreateSourceNode("input-node", "input"),
                    new()
                    {
                        Id = "sorted",
                        Type = "sort",
                        Inputs = ["input-node"],
                        Keys = [new() { Name = "key", Type = "int" }],
                        SortOptions = new()
                        {
                            MemoryBudgetBytes = 4_096,
                            MaxInMemoryRows = 1,
                            MaxRecordBytes = 1_024
                        }
                    }
                ],
                OutputNodeId = "sorted"
            };

            await DataFlowPipeline.WriteJsonLinesAsync(
                JsonSerializer.Serialize(definition),
                outputPath);

            var output = await File.ReadAllLinesAsync(outputPath);
            Assert.Equal(2, output.Length);
            Assert.Equal(1, JsonDocument.Parse(output[0]).RootElement.GetProperty("key").GetInt32());
            Assert.Equal(2, JsonDocument.Parse(output[1]).RootElement.GetProperty("key").GetInt32());
            Assert.Equal(
                new[] { "input.jsonl", "output.jsonl" },
                Directory.EnumerateFiles(directory)
                    .Select(Path.GetFileName)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task JsonLinesHelperAcceptsInlinePowerShellScriptBlocksInConfiguration()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"m4d-flow-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var inputPath = Path.Combine(directory, "input.jsonl");
            var outputPath = Path.Combine(directory, "output.jsonl");
            await File.WriteAllLinesAsync(inputPath, ["""{"Value":1}""", """{"Value":2}"""]);

            var definition = new Hashtable
            {
                ["Sources"] = new object[]
                {
                    new Hashtable
                    {
                        ["Id"] = "records",
                        ["Type"] = "jsonl",
                        ["ConnectionId"] = "files",
                        ["Path"] = inputPath
                    }
                },
                ["Nodes"] = new object[]
                {
                    new Hashtable
                    {
                        ["Id"] = "source",
                        ["Type"] = "source",
                        ["SourceId"] = "records"
                    },
                    new Hashtable
                    {
                        ["Id"] = "keep-selected",
                        ["Type"] = "filter",
                        ["Inputs"] = new[] { "source" },
                        ["Script"] = ScriptBlock.Create("param($row) $row.Value -gt 1")
                    },
                    new Hashtable
                    {
                        ["Id"] = "multiply",
                        ["Type"] = "transform",
                        ["Inputs"] = new[] { "keep-selected" },
                        ["Script"] = ScriptBlock.Create(
                            "param($row) [pscustomobject]@{ Value = $row.Value * 10 }")
                    }
                },
                ["OutputNodeId"] = "multiply"
            };

            await DataFlowPipeline.WriteJsonLinesAsync(definition, outputPath);

            var output = await File.ReadAllLinesAsync(outputPath);
            var record = Assert.Single(output);
            Assert.Equal(20, JsonDocument.Parse(record).RootElement.GetProperty("Value").GetInt32());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PipelineRejectsCyclesAndFanOut()
    {
        var cycle = new DataFlowPipelineDefinition
        {
            Nodes =
            [
                new()
                {
                    Id = "loop",
                    Type = "sort",
                    Inputs = ["loop"],
                    Keys = [new() { Name = "key", Type = "int" }]
                }
            ],
            OutputNodeId = "loop"
        };
        Assert.Throws<ArgumentException>(() => new DataFlowPipeline(cycle));

        var fanOut = new DataFlowPipelineDefinition
        {
            Sources = [CreateSource("records")],
            Nodes =
            [
                CreateSourceNode("source", "records"),
                new()
                {
                    Id = "left",
                    Type = "sort",
                    Inputs = ["source"],
                    Keys = [new() { Name = "key", Type = "int" }]
                },
                new()
                {
                    Id = "right",
                    Type = "sort",
                    Inputs = ["source"],
                    Keys = [new() { Name = "key", Type = "int" }]
                }
            ],
            OutputNodeId = "left"
        };
        Assert.Throws<ArgumentException>(() => new DataFlowPipeline(fanOut));
    }

    private static DataFlowSourceDefinition CreateSource(string id)
    {
        return new DataFlowSourceDefinition
        {
            Id = id,
            Type = "memory",
            ConnectionId = "test-connection"
        };
    }

    private static DataFlowNodeDefinition CreateSourceNode(string id, string sourceId)
    {
        return new DataFlowNodeDefinition
        {
            Id = id,
            Type = "source",
            SourceId = sourceId
        };
    }

    private static async Task<JsonElement[]> ReadAllAsync(
        DataFlowPipeline pipeline,
        IReadOnlyDictionary<string, string> sourceRecords,
        IReadOnlyDictionary<string, ScriptBlock>? nodeScripts = null)
    {
        var records = new List<JsonElement>();
        DataFlowSourceReader sourceReader = (source, cancellationToken) =>
            ReadRowsAsync(sourceRecords[source.Id], cancellationToken);
        var source = nodeScripts is null
            ? pipeline.ReadAsync(sourceReader)
            : pipeline.ReadAsync(sourceReader, nodeScripts);
        await foreach (var record in source)
        {
            records.Add(record.Clone());
        }

        return records.ToArray();
    }

    private static async IAsyncEnumerable<JsonElement> ReadRowsAsync(
        string jsonArray,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(jsonArray);
        foreach (var record in document.RootElement.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return record.Clone();
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<JsonElement> ReadTrackedRowsAsync(
        string sourceId,
        IEnumerable<string> rows,
        IList<string> reads,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var json in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(json);
            reads.Add($"{sourceId}:{document.RootElement.GetProperty("value").GetString()}");
            yield return document.RootElement.Clone();
            await Task.Yield();
        }
    }
}
