using System.Collections;
using System.Text.Json;
using DuckDB.NET.Data;
using System.Management.Automation;
using Microsoft.Data.Sqlite;
using Migration4Dataverse.Engine.Connectors;
using Migration4Dataverse.Engine.PowerShell;
using Migration4Dataverse.Engine.Scheduling;
using Migration4Dataverse.Engine.Storage;
using PS = System.Management.Automation.PowerShell;

namespace Migration4Dataverse.Engine.Tests.Connectors;

public sealed class DatabaseConnectorTests
{
    [Theory]
    [InlineData("SQLite", typeof(SqliteConnection))]
    [InlineData("DuckDB", typeof(DuckDBConnection))]
    public void CreatesSupportedDatabaseConnections(string provider, Type expectedType)
    {
        using var connection = MigrationDatabaseConnectionFactory.Create(provider, "Data Source=:memory:");

        Assert.IsType(expectedType, connection);
    }

    [Fact]
    public void RejectsUnsupportedDatabaseProviders()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => MigrationDatabaseConnectionFactory.Create("Postgres", "Host=localhost"));

        Assert.Contains("SQLite or DuckDB", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NewMigrationDatabaseConnectionCmdletCreatesProviderConnection()
    {
        using var powershell = PS.Create();
        powershell.AddCommand("Import-Module")
            .AddParameter("Name", typeof(InvokeMigrationCommand).Assembly.Location);
        powershell.Invoke();
        Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));

        powershell.Commands.Clear();
        var output = powershell.AddCommand("New-MigrationDatabaseConnection")
            .AddParameter("Provider", "SQLite")
            .AddParameter("ConnectionString", "Data Source=:memory:")
            .Invoke();

        Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
        using var connection = Assert.IsType<SqliteConnection>(Assert.Single(output).BaseObject);
    }

    [Fact]
    public async Task SqliteSourceExecutesConfiguredQueryAndStreamsRows()
    {
        using var files = new TemporaryDirectory();
        using var connection = new SqliteConnection($"Data Source={files.GetPath("source.sqlite")}");
        connection.Open();
        Execute(connection, "CREATE TABLE records (id INTEGER NOT NULL, name TEXT NOT NULL)");
        Execute(connection, "INSERT INTO records VALUES (1, 'first'), (2, 'second')");
        var options = new DatabaseSourceOptions
        {
            Query = "SELECT id, name FROM records ORDER BY id"
        };
        Assert.Equal(
            2,
            await DatabaseHierarchyRecordReader.CountRecordsAsync(connection, "Source", options));
        Assert.Equal(
            1,
            await DatabaseHierarchyRecordReader.CountRecordsAsync(
                connection,
                "FilteredSource",
                new DatabaseSourceOptions
                {
                    Query = "SELECT id, name FROM records WHERE id = 1 ORDER BY id LIMIT 1;"
                }));
        Assert.Equal(
            1,
            await DatabaseHierarchyRecordReader.CountRecordsAsync(
                connection,
                "OverrideCountSource",
                new DatabaseSourceOptions
                {
                    Query = options.Query,
                    CountQuery = "SELECT COUNT(*) FROM records WHERE id = 1"
                }));
        var level = new HierarchySourceLevel(
            "Source",
            "sqlite",
            path: null,
            keyProperty: "id",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 0,
            optionsJson: JsonSerializer.Serialize(options),
            databaseConnection: connection);
        await using var reader = new DatabaseHierarchyRecordReader(
            level,
            connection,
            options);

        var first = await reader.PeekAsync(CancellationToken.None);
        Assert.True(first.HasValue);
        Assert.Equal(1, first.Value.GetProperty("id").GetInt64());
        Assert.Equal("first", first.Value.GetProperty("name").GetString());
        reader.Take();
        var second = await reader.PeekAsync(CancellationToken.None);
        Assert.True(second.HasValue);
        Assert.Equal(2, second.Value.GetProperty("id").GetInt64());
        reader.Take();
        Assert.Null(await reader.PeekAsync(CancellationToken.None));
    }

    [Fact]
    public void SqliteTargetInfersSchemaIsReplaySafeAndRejectsMismatchedSchema()
    {
        using var files = new TemporaryDirectory();
        using var connection = new SqliteConnection($"Data Source={files.GetPath("target.sqlite")}");
        connection.Open();
        var records = """[{"id":1,"name":"one"},{"id":2,"name":"two"}]""";
        DatabaseBatchTargetWriter.WriteBatch(
            connection,
            "sqlite",
            "copied_records",
            "Target",
            "run",
            "batch",
            records,
            "{}");
        DatabaseBatchTargetWriter.WriteBatch(
            connection,
            "sqlite",
            "copied_records",
            "Target",
            "run",
            "batch",
            records,
            "{}");

        Assert.Equal(2L, Scalar<long>(connection, "SELECT COUNT(*) FROM copied_records"));
        Assert.Equal("one", Scalar<string>(connection, "SELECT name FROM copied_records WHERE id = 1"));
        var mismatchedSchema = JsonSerializer.Serialize(new DatabaseBatchTargetOptions
        {
            Schema = new Dictionary<string, string>
            {
                ["id"] = "TEXT",
                ["name"] = "TEXT"
            }
        });
        Assert.Throws<InvalidDataException>(() => DatabaseBatchTargetWriter.WriteBatch(
            connection,
            "sqlite",
            "copied_records",
            "Target",
            "another-run",
            "batch",
            records,
            mismatchedSchema));
    }

    [Fact]
    public void SqliteTargetCreatesExplicitSchemaBeforeWritingRows()
    {
        using var files = new TemporaryDirectory();
        using var connection = new SqliteConnection($"Data Source={files.GetPath("target.sqlite")}");
        connection.Open();
        var options = JsonSerializer.Serialize(new DatabaseBatchTargetOptions
        {
            Schema = new Dictionary<string, string>
            {
                ["id"] = "INTEGER",
                ["name"] = "TEXT"
            }
        });

        DatabaseBatchTargetWriter.PrepareTargetSchema(
            connection,
            "sqlite",
            "prepared_records",
            "Target",
            options);

        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM prepared_records"));
        Assert.Throws<InvalidOperationException>(() => DatabaseBatchTargetWriter.PrepareTargetSchema(
            connection,
            "sqlite",
            "missing_records",
            "MissingTarget",
            JsonSerializer.Serialize(new DatabaseBatchTargetOptions
            {
                CreateTable = false
            })));
    }

    [Fact]
    public async Task DuckDbSourceTargetAndPreparedTransformWorkWithSeparateDatabases()
    {
        using var files = new TemporaryDirectory();
        using var source = new DuckDBConnection($"Data Source={files.GetPath("source.duckdb")}");
        using var target = new DuckDBConnection($"Data Source={files.GetPath("target.duckdb")}");
        source.Open();
        target.Open();
        Execute(source, "CREATE TABLE records (id BIGINT, name VARCHAR)");
        Execute(source, "INSERT INTO records VALUES (1, 'one'), (2, 'two')");
        var level = new HierarchySourceLevel(
            "Source",
            "duckdb",
            path: null,
            keyProperty: "id",
            parentKeyProperty: null,
            rowsPerParent: 0,
            expectedRecordCount: 0,
            optionsJson: JsonSerializer.Serialize(new DatabaseSourceOptions
            {
                Query = "SELECT id, name FROM records ORDER BY id"
            }),
            databaseConnection: source);
        await using (var reader = new DatabaseHierarchyRecordReader(
            level,
            source,
            DatabaseSourceOptions.FromJson(level.OptionsJson, level.StepId)))
        {
            var record = await reader.PeekAsync(CancellationToken.None);
            Assert.True(record.HasValue);
            Assert.Equal(1, record.Value.GetProperty("id").GetInt64());
        }

        var recordsJson = """[{"id":1,"name":"one"},{"id":2,"name":"two"}]""";
        var targetOptions = JsonSerializer.Serialize(new DatabaseBatchTargetOptions
        {
            Schema = new Dictionary<string, string>
            {
                ["id"] = "BIGINT",
                ["name"] = "TEXT"
            }
        });
        DatabaseBatchTargetWriter.PrepareTargetSchema(
            target,
            "duckdb",
            "copied_records",
            "Target",
            targetOptions);
        DatabaseBatchTargetWriter.WriteBatch(
            target,
            "duckdb",
            "copied_records",
            "Target",
            "run",
            "batch",
            recordsJson,
            targetOptions);
        Execute(target, "CREATE TABLE audit (id BIGINT, name VARCHAR)");
        var outputJson = PreparedDatabaseTransformRunner.ExecutePreparedBatch(
            target,
            "duckdb",
            "INSERT INTO audit VALUES (?, ?)",
            """{"id":"id","name":"name"}""",
            recordsJson);
        using var output = JsonDocument.Parse(outputJson);

        Assert.Equal(2, output.RootElement.GetArrayLength());
        Assert.Equal(2L, Scalar<long>(target, "SELECT COUNT(*) FROM copied_records"));
        Assert.Equal(2L, Scalar<long>(target, "SELECT COUNT(*) FROM audit"));
        var selectedJson = PreparedDatabaseTransformRunner.ExecutePreparedBatch(
            target,
            "duckdb",
            "SELECT ? AS id, upper(?) AS name",
            """{"id":"id","name":"name"}""",
            """[{"id":3,"name":"three"}]""");
        using var selected = JsonDocument.Parse(selectedJson);
        var selectedRow = Assert.Single(selected.RootElement.EnumerateArray());
        Assert.Equal(3, selectedRow.GetProperty("Record").GetProperty("id").GetInt64());
        Assert.Equal("THREE", selectedRow.GetProperty("Record").GetProperty("name").GetString());
    }

    [Fact]
    public void InvokeMigrationSupportsSeparateSqliteDatabasesAndConnectionBackedTransform()
    {
        using var files = new TemporaryDirectory();
        using var source = new SqliteConnection($"Data Source={files.GetPath("source.sqlite")}");
        using var transform = new SqliteConnection($"Data Source={files.GetPath("transform.sqlite")}");
        using var target = new SqliteConnection($"Data Source={files.GetPath("target.sqlite")}");
        source.Open();
        transform.Open();
        target.Open();
        Execute(source, "CREATE TABLE records (id TEXT NOT NULL, name TEXT NOT NULL)");
        Execute(source, "INSERT INTO records VALUES ('a', 'first'), ('b', 'second')");
        Execute(transform, "CREATE TABLE audit (id TEXT NOT NULL)");

        var sourceStep = PSObject.AsPSObject(new
        {
            Id = "Source",
            Type = "source",
            Inputs = Array.Empty<string>(),
            Source = new Hashtable
            {
                ["Type"] = "sqlite",
                ["ConnectionId"] = "source",
                ["Key"] = "id",
                ["Options"] = new Hashtable
                {
                    ["Query"] = "SELECT id, name FROM records ORDER BY id"
                }
            }
        });
        var transformStep = PSObject.AsPSObject(new
        {
            Id = "Transform",
            Type = "transform",
            Inputs = new[] { "Source" },
            Transform = new Hashtable
            {
                ["Type"] = "sqlite",
                ["ConnectionId"] = "transform"
            },
            Script = ScriptBlock.Create("""
                param($row, $connection)
                $command = $connection.CreateCommand()
                try {
                    $command.CommandText = 'INSERT INTO audit (id) VALUES (@id)'
                    $parameter = $command.CreateParameter()
                    $parameter.ParameterName = 'id'
                    $parameter.Value = $row.id
                    $null = $command.Parameters.Add($parameter)
                    $null = $command.ExecuteNonQuery()
                }
                finally {
                    $command.Dispose()
                }
                [pscustomobject]@{ id = $row.id; name = $row.name.ToUpperInvariant() }
                """)
        });
        var targetStep = PSObject.AsPSObject(new
        {
            Id = "Target",
            Type = "target",
            Inputs = new[] { "Transform" },
            Target = new Hashtable
            {
                ["Type"] = "sqlite",
                ["ConnectionId"] = "target",
                ["Table"] = "result",
                ["Options"] = new Hashtable
                {
                    ["Schema"] = new Hashtable
                    {
                        ["id"] = "TEXT",
                        ["name"] = "TEXT"
                    }
                }
            }
        });

        using var powershell = PS.Create();
        powershell.AddCommand("Import-Module")
            .AddParameter("Name", typeof(InvokeMigrationCommand).Assembly.Location);
        powershell.Invoke();
        Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
        powershell.Commands.Clear();
        powershell.AddCommand("Invoke-Migration")
            .AddParameter("DatabasePath", files.GetPath("migration.sqlite"))
            .AddParameter("RunName", "SQLite database connectors")
            .AddParameter("RunId", "sqlite-database-connectors")
            .AddParameter("Steps", new[] { sourceStep, transformStep, targetStep })
            .AddParameter("Connections", new Hashtable
            {
                ["source"] = source,
                ["transform"] = transform,
                ["target"] = target
            })
            .AddParameter("BatchSize", 1)
            .AddParameter("MaxAttempts", 1);
        var output = powershell.Invoke();

        Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
        Assert.IsType<DependencySchedulerResult>(Assert.Single(output).BaseObject);
        Assert.Equal("FIRST", Scalar<string>(target, "SELECT name FROM result WHERE id = 'a'"));
        Assert.Equal(2L, Scalar<long>(transform, "SELECT COUNT(*) FROM audit"));
    }

    [Fact]
    public void InvokeMigrationRunsDuckDbPreparedTransformWithPositionalParameters()
    {
        using var files = new TemporaryDirectory();
        using var source = new DuckDBConnection($"Data Source={files.GetPath("source.duckdb")}");
        using var transform = new DuckDBConnection($"Data Source={files.GetPath("transform.duckdb")}");
        using var target = new DuckDBConnection($"Data Source={files.GetPath("target.duckdb")}");
        source.Open();
        transform.Open();
        target.Open();
        Execute(source, "CREATE TABLE records (Id VARCHAR, Name VARCHAR)");
        Execute(source, "INSERT INTO records VALUES ('a', 'first'), ('b', 'second')");
        Execute(transform, "CREATE TABLE audit (Id VARCHAR, Name VARCHAR)");

        var sourceStep = PSObject.AsPSObject(new
        {
            Id = "Source",
            Type = "source",
            Inputs = Array.Empty<string>(),
            Source = new Hashtable
            {
                ["Type"] = "duckdb",
                ["ConnectionId"] = "source",
                ["Key"] = "Id",
                ["Options"] = new Hashtable
                {
                    ["Query"] = "SELECT Id, Name FROM records ORDER BY Id"
                }
            }
        });
        var transformStep = PSObject.AsPSObject(new
        {
            Id = "Transform",
            Type = "transform",
            Inputs = new[] { "Source" },
            Transform = new Hashtable
            {
                ["Type"] = "duckdb",
                ["ConnectionId"] = "transform",
                ["Sql"] = "INSERT INTO audit VALUES (?, ?)",
                ["Parameters"] = new System.Collections.Specialized.OrderedDictionary
                {
                    ["id"] = "Id",
                    ["name"] = "Name"
                }
            }
        });
        var targetStep = PSObject.AsPSObject(new
        {
            Id = "Target",
            Type = "target",
            Inputs = new[] { "Transform" },
            Target = new Hashtable
            {
                ["Type"] = "duckdb",
                ["ConnectionId"] = "target",
                ["Table"] = "result"
            }
        });

        using var powershell = PS.Create();
        powershell.AddCommand("Import-Module")
            .AddParameter("Name", typeof(InvokeMigrationCommand).Assembly.Location);
        powershell.Invoke();
        Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
        powershell.Commands.Clear();
        powershell.AddCommand("Invoke-Migration")
            .AddParameter("DatabasePath", files.GetPath("migration.sqlite"))
            .AddParameter("RunName", "DuckDB prepared transform")
            .AddParameter("RunId", "duckdb-prepared-transform")
            .AddParameter("Steps", new[] { sourceStep, transformStep, targetStep })
            .AddParameter("Connections", new Hashtable
            {
                ["source"] = source,
                ["transform"] = transform,
                ["target"] = target
            })
            .AddParameter("BatchSize", 1)
            .AddParameter("MaxAttempts", 1);
        var output = powershell.Invoke();

        Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
        Assert.IsType<DependencySchedulerResult>(Assert.Single(output).BaseObject);
        Assert.Equal(2L, Scalar<long>(target, "SELECT COUNT(*) FROM result"));
        Assert.Equal(2L, Scalar<long>(transform, "SELECT COUNT(*) FROM audit"));
    }

    private static void Execute(System.Data.Common.DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(System.Data.Common.DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = command.ExecuteScalar()
            ?? throw new InvalidOperationException($"Query '{sql}' returned null.");
        return (T)Convert.ChangeType(
            result,
            typeof(T),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), $"migration4dataverse-db-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        private string Root { get; }

        public string GetPath(string name) => Path.Combine(Root, name);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Directory.Delete(Root, recursive: true);
        }
    }
}
