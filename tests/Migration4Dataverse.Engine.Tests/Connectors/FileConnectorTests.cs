using System.Text.Json;
using Migration4Dataverse.Engine.Connectors;
using Migration4Dataverse.Engine.Scheduling;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.Tests.Connectors;

public sealed class FileConnectorTests
{
    [Fact]
    public async Task CsvHierarchyReaderStreamsAndGroupsDependentRows()
    {
        using var files = new TemporaryFiles();
        var accountId = Guid.NewGuid();
        files.Write(
            "accounts.csv",
            "SourceId,DataverseId,Name,Revenue",
            $"A001,{accountId:D},Account One,123.45");
        files.Write(
            "contacts.csv",
            "SourceId,AccountSourceId,Name",
            "C001,A001,First Contact",
            "C002,A001,Second Contact");
        var options = JsonSerializer.Serialize(new CsvConnectorOptions
        {
            ColumnTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DataverseId"] = "guid",
                ["Revenue"] = "decimal"
            }
        });
        var contactLevel = new HierarchySourceLevel(
            "Contacts",
            "csv",
            files.GetPath("contacts.csv"),
            "SourceId",
            "AccountSourceId",
            rowsPerParent: 2,
            expectedRecordCount: 2,
            optionsJson: options);
        var accountLevel = new HierarchySourceLevel(
            "Accounts",
            "csv",
            files.GetPath("accounts.csv"),
            "SourceId",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 1,
            children: new[] { contactLevel },
            optionsJson: options);
        var source = new HierarchyBatchSource(
            accountLevel,
            batchSize: 1,
            HierarchyRecordReaderFactory.CreateFileReader);

        var batch = Assert.Single(await ReadAllAsync(
            source.CreateReader()(new MigrationStepCheckpoint("run", "Accounts", -1, null, null), CancellationToken.None)));
        using var payload = JsonDocument.Parse(batch.PayloadJson);
        var account = Assert.Single(payload.RootElement.GetProperty("Records").EnumerateArray().ToArray());
        var accountSource = account.GetProperty("Source");
        Assert.Equal(accountId, accountSource.GetProperty("DataverseId").GetGuid());
        Assert.Equal(123.45m, accountSource.GetProperty("Revenue").GetDecimal());
        Assert.Equal(
            2,
            account.GetProperty("Children").GetProperty("Contacts").GetArrayLength());
    }

    [Fact]
    public async Task CsvHierarchyReaderAcceptsWindowsLineEndingsWithDefaultOptions()
    {
        using var files = new TemporaryFiles();
        var csvPath = files.GetPath("accounts.csv");
        await File.WriteAllTextAsync(
            csvPath,
            "Id,Name\r\nA001,Account One\r\n",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var level = new HierarchySourceLevel(
            "Accounts",
            "csv",
            csvPath,
            "Id",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 1);

        await using var reader = new CsvHierarchyRecordReader(level, new CsvConnectorOptions());
        var row = await reader.PeekAsync(CancellationToken.None)
            ?? throw new InvalidDataException("The CRLF CSV source did not return its record.");

        Assert.Equal("A001", row.GetProperty("Id").GetString());
        Assert.Equal("Account One", row.GetProperty("Name").GetString());
        reader.Take();
        Assert.Null(await reader.PeekAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CsvFixtureConverterWritesConfiguredProjectionAndCanBeReadBack()
    {
        using var files = new TemporaryFiles();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        files.Write(
            "records.jsonl",
            JsonSerializer.Serialize(new { Id = firstId, Name = "North|West", Ignored = true }),
            JsonSerializer.Serialize(new { Id = secondId, Name = "South", Ignored = false }));
        var options = new CsvConnectorOptions
        {
            Delimiter = "|",
            NewLine = "\r\n",
            ColumnTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Id"] = "guid"
            },
            Columns = ["Id", "Name"],
            SourceColumnsByStep = new Dictionary<string, string[]>
            {
                ["Accounts"] = ["Id", "Name"]
            }
        };
        var optionsJson = JsonSerializer.Serialize(options);
        var csvPath = files.GetPath("records.csv");
        StreamingSourceFileConverter.ConvertJsonLinesToCsv(
            files.GetPath("records.jsonl"),
            csvPath,
            "Accounts",
            optionsJson);

        var level = new HierarchySourceLevel(
            "Accounts",
            "csv",
            csvPath,
            "Id",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 2,
            optionsJson: optionsJson);
        await using var reader = new CsvHierarchyRecordReader(level, options);
        var first = await reader.PeekAsync(CancellationToken.None)
            ?? throw new InvalidDataException("The CSV source did not return its first record.");
        Assert.Equal(firstId, first.GetProperty("Id").GetGuid());
        Assert.Equal("North|West", first.GetProperty("Name").GetString());
        Assert.False(first.TryGetProperty("Ignored", out _));
        reader.Take();
        var second = await reader.PeekAsync(CancellationToken.None)
            ?? throw new InvalidDataException("The CSV source did not return its second record.");
        Assert.Equal(secondId, second.GetProperty("Id").GetGuid());
        reader.Take();
        Assert.Null(await reader.PeekAsync(CancellationToken.None));
    }

    [Fact]
    public void CsvTargetWritesConfiguredColumnsAndAcceptsIdenticalReplay()
    {
        using var files = new TemporaryFiles();
        var options = JsonSerializer.Serialize(new CsvConnectorOptions
        {
            Delimiter = "|",
            QuoteAllFields = true,
            ColumnsByStep = new Dictionary<string, string[]>
            {
                ["Accounts"] = ["Id", "Name", "Amount"]
            }
        });
        var recordId = Guid.NewGuid();
        var records = JsonSerializer.Serialize(new[]
        {
            new { Id = recordId, Name = "North|West", Amount = 10.5m }
        });

        FileBatchTargetWriter.WriteCsvBatch(
            files.Root,
            "run",
            "Accounts",
            "batch-1",
            0,
            records,
            options);

        var outputPath = Path.Combine(files.Root, "run", "000000000000-batch-1.csv");
        var firstContent = File.ReadAllText(outputPath);
        FileBatchTargetWriter.WriteCsvBatch(
            files.Root,
            "run",
            "Accounts",
            "batch-1",
            0,
            records,
            options);

        Assert.Equal(firstContent, File.ReadAllText(outputPath));
        Assert.Contains("\"North|West\"", firstContent, StringComparison.Ordinal);
        Assert.Contains(recordId.ToString("D"), firstContent, StringComparison.Ordinal);
        Assert.StartsWith("\"Id\"|\"Name\"|\"Amount\"", firstContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SingleFileTargetsAggregateOrderedBatchesForAllFormats()
    {
        using var files = new TemporaryFiles();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var firstBatch = JsonSerializer.Serialize(new[] { new { Id = firstId, Name = "First" } });
        var secondBatch = JsonSerializer.Serialize(new[] { new { Id = secondId, Name = "Second" } });

        var jsonLinesPath = files.GetPath("accounts.jsonl");
        FileBatchTargetWriter.WriteSingleFileTargetBatch(
            jsonLinesPath, "run", "Accounts", "batch-2", 1, secondBatch);
        FileBatchTargetWriter.WriteSingleFileTargetBatch(
            jsonLinesPath, "run", "Accounts", "batch-1", 0, firstBatch);
        FileBatchTargetWriter.FinalizeSingleFileTarget(jsonLinesPath, "run", "Accounts", "jsonl", "{}");
        var jsonLines = File.ReadLines(jsonLinesPath)
            .Select(line => JsonDocument.Parse(line))
            .ToArray();
        try
        {
            Assert.Equal(
                new[] { firstId, secondId },
                jsonLines.Select(document => document.RootElement.GetProperty("Id").GetGuid()).ToArray());
        }
        finally
        {
            foreach (var document in jsonLines)
            {
                document.Dispose();
            }
        }

        var csvPath = files.GetPath("accounts.csv");
        var csvOptions = JsonSerializer.Serialize(new CsvConnectorOptions
        {
            ColumnsByStep = new Dictionary<string, string[]>
            {
                ["Accounts"] = ["Id", "Name"]
            }
        });
        FileBatchTargetWriter.WriteSingleFileTargetBatch(
            csvPath, "run", "Accounts", "batch-1", 0, firstBatch);
        FileBatchTargetWriter.WriteSingleFileTargetBatch(
            csvPath, "run", "Accounts", "batch-2", 1, secondBatch);
        FileBatchTargetWriter.FinalizeSingleFileTarget(csvPath, "run", "Accounts", "csv", csvOptions);
        var csvLines = File.ReadAllLines(csvPath);
        Assert.Equal(3, csvLines.Length);
        Assert.Equal("Id,Name", csvLines[0]);

        var parquetPath = files.GetPath("accounts.parquet");
        var parquetOptions = JsonSerializer.Serialize(new ParquetConnectorOptions
        {
            TargetRowGroupSize = 1,
            SchemaByStep = new Dictionary<string, ParquetFieldSchema[]>
            {
                ["Accounts"] =
                [
                    new() { Name = "Id", Type = "guid", Nullable = false },
                    new() { Name = "Name", Type = "string", Nullable = false }
                ]
            }
        });
        FileBatchTargetWriter.WriteSingleFileTargetBatch(
            parquetPath, "run", "Accounts", "batch-2", 1, secondBatch);
        FileBatchTargetWriter.WriteSingleFileTargetBatch(
            parquetPath, "run", "Accounts", "batch-1", 0, firstBatch);
        FileBatchTargetWriter.FinalizeSingleFileTarget(
            parquetPath, "run", "Accounts", "parquet", parquetOptions);
        var parquetLevel = new HierarchySourceLevel(
            "Accounts",
            "parquet",
            parquetPath,
            "Id",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 2,
            optionsJson: parquetOptions);
        await using var parquetReader = new ParquetHierarchyRecordReader(
            parquetLevel,
            JsonSerializer.Deserialize<ParquetConnectorOptions>(parquetOptions)!);
        var parquetIds = new List<Guid>();
        while (await parquetReader.PeekAsync(CancellationToken.None) is JsonElement row)
        {
            parquetIds.Add(row.GetProperty("Id").GetGuid());
            parquetReader.Take();
        }
        Assert.Equal(new[] { firstId, secondId }, parquetIds);
    }

    [Fact]
    public async Task ParquetSourceConverterWritesBoundedRowGroupsThatCanBeReadBack()
    {
        using var files = new TemporaryFiles();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var thirdId = Guid.NewGuid();
        files.Write(
            "records.jsonl",
            $$"""{"SourceId":"A001","Id":"{{firstId:D}}","Amount":10.5,"OptionalText":"first"}""",
            $$"""{"SourceId":"A002","Id":"{{secondId:D}}","Amount":20.5,"OptionalText":null}""",
            $$"""{"SourceId":"A003","Id":"{{thirdId:D}}","Amount":30.5,"OptionalText":"last"}""");
        var options = new ParquetConnectorOptions
        {
            TargetRowGroupSize = 2,
            SourceSchemaByStep = new Dictionary<string, ParquetFieldSchema[]>
            {
                ["Accounts"] =
                [
                    new() { Name = "SourceId", Type = "string", Nullable = false },
                    new() { Name = "Id", Type = "guid", Nullable = false },
                    new() { Name = "Amount", Type = "decimal", Precision = 10, Scale = 1, Nullable = false },
                    new() { Name = "OptionalText", Type = "string", Nullable = true }
                ]
            }
        };
        var optionsJson = JsonSerializer.Serialize(options);
        var parquetPath = files.GetPath("records.parquet");
        StreamingSourceFileConverter.ConvertJsonLinesToParquet(
            files.GetPath("records.jsonl"),
            parquetPath,
            "Accounts",
            optionsJson);

        var level = new HierarchySourceLevel(
            "Accounts",
            "parquet",
            parquetPath,
            "SourceId",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 3,
            optionsJson: optionsJson);
        Assert.Equal(
            3,
            await HierarchyRecordReaderFactory.GetMetadataRecordCountAsync(level));
        await using var reader = new ParquetHierarchyRecordReader(level, options);
        var values = new List<JsonElement>();
        while (await reader.PeekAsync(CancellationToken.None) is JsonElement value)
        {
            values.Add(reader.Take());
        }

        Assert.Equal(3, reader.RecordCount);
        Assert.Equal(
            new[] { "A001", "A002", "A003" },
            values.Select(value => value.GetProperty("SourceId").GetString()).ToArray());
        Assert.Equal(
            new[] { firstId, secondId, thirdId },
            values.Select(value => value.GetProperty("Id").GetGuid()).ToArray());
        Assert.Equal(20.5m, values[1].GetProperty("Amount").GetDecimal());
        Assert.Equal("first", values[0].GetProperty("OptionalText").GetString());
        Assert.Equal(JsonValueKind.Null, values[1].GetProperty("OptionalText").ValueKind);
        Assert.Equal("last", values[2].GetProperty("OptionalText").GetString());
    }

    [Fact]
    public async Task ParquetTargetWritesExplicitSchemaAndAcceptsIdenticalReplay()
    {
        using var files = new TemporaryFiles();
        var recordId = Guid.NewGuid();
        var options = new ParquetConnectorOptions
        {
            TargetRowGroupSize = 10,
            SchemaByStep = new Dictionary<string, ParquetFieldSchema[]>
            {
                ["Accounts"] =
                [
                    new() { Name = "Id", Type = "guid", Nullable = false },
                    new() { Name = "Name", Type = "string", Nullable = false },
                    new() { Name = "Amount", Type = "decimal", Precision = 10, Scale = 1, Nullable = false },
                    new() { Name = "OptionalName", Type = "string", Nullable = true },
                    new() { Name = "OptionalAmount", Type = "decimal", Precision = 10, Scale = 1, Nullable = true }
                ]
            }
        };
        var optionsJson = JsonSerializer.Serialize(options);
        var recordsJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                Id = recordId,
                Name = "Account A",
                Amount = 10.5m,
                OptionalName = (string?)null,
                OptionalAmount = (decimal?)null
            }
        });

        FileBatchTargetWriter.WriteParquetBatch(
            files.Root,
            "run",
            "Accounts",
            "batch-1",
            0,
            recordsJson,
            optionsJson);
        var parquetPath = Path.Combine(files.Root, "run", "000000000000-batch-1.parquet");
        var firstContent = File.ReadAllBytes(parquetPath);
        FileBatchTargetWriter.WriteParquetBatch(
            files.Root,
            "run",
            "Accounts",
            "batch-1",
            0,
            recordsJson,
            optionsJson);

        Assert.Equal(firstContent, File.ReadAllBytes(parquetPath));
        var level = new HierarchySourceLevel(
            "Accounts",
            "parquet",
            parquetPath,
            "Id",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 1,
            optionsJson: optionsJson);
        await using var reader = new ParquetHierarchyRecordReader(level, options);
        var record = (await reader.PeekAsync(CancellationToken.None))!.Value;
        Assert.Equal(recordId, record.GetProperty("Id").GetGuid());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("OptionalName").ValueKind);
        Assert.Equal(JsonValueKind.Null, record.GetProperty("OptionalAmount").ValueKind);
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

    private sealed class TemporaryFiles : IDisposable
    {
        public TemporaryFiles()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"migration4dataverse-connectors-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string GetPath(string fileName)
        {
            return Path.Combine(Root, fileName);
        }

        public void Write(string fileName, params string[] lines)
        {
            File.WriteAllLines(GetPath(fileName), lines);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
