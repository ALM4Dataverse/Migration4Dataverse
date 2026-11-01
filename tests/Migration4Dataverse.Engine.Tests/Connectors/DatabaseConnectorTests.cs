using System.Collections;
using System.Text.Json;
using DuckDB.NET.Data;
using System.Management.Automation;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using Migration4Dataverse.Engine.Connectors;
using Migration4Dataverse.Engine.PowerShell;
using Migration4Dataverse.Engine.Scheduling;
using Migration4Dataverse.Engine.Storage;
using PS = System.Management.Automation.PowerShell;

namespace Migration4Dataverse.Engine.Tests.Connectors;

public sealed class DatabaseConnectorTests
{
    [Theory]
    [InlineData("SQLite", typeof(SqliteConnection), "Data Source=:memory:")]
    [InlineData("DuckDB", typeof(DuckDBConnection), "Data Source=:memory:")]
    [InlineData("SQLServer", typeof(SqlConnection), "Server=localhost;Database=test;Integrated Security=true")]
    [InlineData("PostgreSQL", typeof(NpgsqlConnection), "Host=localhost;Database=test;Username=test;Password=test")]
    [InlineData("MySQL", typeof(MySqlConnection), "Server=localhost;Database=test;User=test;Password=test")]
    [InlineData("Oracle", typeof(OracleConnection), "User Id=test;Password=test;Data Source=localhost:1521/test")]
    public void CreatesSupportedDatabaseConnections(string provider, Type expectedType, string connectionString)
    {
        using var connection = MigrationDatabaseConnectionFactory.Create(provider, connectionString);

        Assert.IsType(expectedType, connection);
    }

    [Fact]
    public void ValidatesConnectionProviderTypesWithoutSqlServerAliasing()
    {
        using var mysql = new MySqlConnection("Server=localhost;Database=test;User=test;Password=test");
        using var sqlServer = new SqlConnection("Server=localhost;Database=test;Integrated Security=true");

        DatabaseConnectorSupport.ValidateConnection("mysql", mysql, "target");
        DatabaseConnectorSupport.ValidateConnection("sqlserver", sqlServer, "target");
        Assert.Throws<ArgumentException>(
            () => DatabaseConnectorSupport.ValidateConnection("sqlserver", mysql, "target"));
    }

    [Fact]
    public void RejectsUnsupportedDatabaseProviders()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => MigrationDatabaseConnectionFactory.Create("Postgres", "Host=localhost"));

        Assert.Contains(
            "SQLite, DuckDB, SQL Server, PostgreSQL, MySQL, or Oracle",
            exception.Message,
            StringComparison.Ordinal);
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
            Query = "SELECT id, name FROM records WHERE id >= @minimumId ORDER BY id",
            Parameters = new Dictionary<string, JsonElement>
            {
                ["minimumId"] = JsonSerializer.SerializeToElement(1)
            }
        };
        Assert.Equal(
            2,
            await DatabaseHierarchyRecordReader.CountRecordsAsync(connection, "Source", options));
        Execute(connection, "CREATE TABLE read_only_restored (id INTEGER)");
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
                    CountQuery = "SELECT COUNT(*) FROM records WHERE id = @minimumId",
                    Parameters = options.Parameters
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
        await Assert.ThrowsAsync<ArgumentException>(() => DatabaseHierarchyRecordReader.CountRecordsAsync(
            connection,
            "UnsafeSource",
            new DatabaseSourceOptions { Query = "DELETE FROM records" }));
    }

    [Fact]
    public void SqliteTargetUsesDeclaredSchemaAndKeyAndUpsertsReplaySafely()
    {
        using var files = new TemporaryDirectory();
        using var connection = new SqliteConnection($"Data Source={files.GetPath("target.sqlite")}");
        connection.Open();
        var records = """[{"id":1,"name":"one"},{"id":2,"name":"two"}]""";
        var options = JsonSerializer.Serialize(new DatabaseBatchTargetOptions
        {
            CreateTable = true,
            Schema = new Dictionary<string, string>
            {
                ["id"] = "INTEGER",
                ["name"] = "TEXT"
            },
            KeyColumns = ["id"],
            Indexes =
            [
                new DatabaseIndexDefinition
                {
                    Name = "IX_copied_records_name",
                    Columns = ["name"]
                }
            ]
        });
        DatabaseBatchTargetWriter.WriteBatch(
            connection,
            "sqlite",
            "copied_records",
            "Target",
            "run",
            "batch",
            records,
            options);
        DatabaseBatchTargetWriter.WriteBatch(
            connection,
            "sqlite",
            "copied_records",
            "Target",
            "run",
            "batch",
            records,
            options);

        Assert.Equal(2L, Scalar<long>(connection, "SELECT COUNT(*) FROM copied_records"));
        Assert.Equal("one", Scalar<string>(connection, "SELECT name FROM copied_records WHERE id = 1"));
        Assert.Equal(
            1L,
            Scalar<long>(
                connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_copied_records_name'"));
        DatabaseBatchTargetWriter.WriteBatch(
            connection,
            "sqlite",
            "copied_records",
            "Target",
            "next-run",
            "next-batch",
            """[{"id":1,"name":"updated"},{"id":3,"name":"three"}]""",
            options);
        Assert.Equal(3L, Scalar<long>(connection, "SELECT COUNT(*) FROM copied_records"));
        Assert.Equal("updated", Scalar<string>(connection, "SELECT name FROM copied_records WHERE id = 1"));
        var mismatchedSchema = JsonSerializer.Serialize(new DatabaseBatchTargetOptions
        {
            KeyColumns = ["id"],
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
    public void SqliteTargetDefaultsToValidateOnly()
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
            },
            KeyColumns = ["id"]
        });

        Assert.Throws<InvalidOperationException>(() => DatabaseBatchTargetWriter.PrepareTargetSchema(
            connection,
            "sqlite",
            "missing_records",
            "Target",
            options));
        Assert.Equal(
            0L,
            Scalar<long>(
                connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'missing_records'"));
    }

    [Fact]
    public void SqliteTargetCreatesExplicitSchemaBeforeWritingRows()
    {
        using var files = new TemporaryDirectory();
        using var connection = new SqliteConnection($"Data Source={files.GetPath("target.sqlite")}");
        connection.Open();
        var options = JsonSerializer.Serialize(new DatabaseBatchTargetOptions
        {
            CreateTable = true,
            Schema = new Dictionary<string, string>
            {
                ["id"] = "INTEGER",
                ["name"] = "TEXT"
            },
            KeyColumns = ["id"],
            Indexes =
            [
                new DatabaseIndexDefinition
                {
                    Name = "IX_prepared_records_name",
                    Columns = ["name"]
                }
            ]
        });

        DatabaseBatchTargetWriter.PrepareTargetSchema(
            connection,
            "sqlite",
            "prepared_records",
            "Target",
            options);

        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM prepared_records"));
        Assert.Equal(
            1L,
            Scalar<long>(
                connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_prepared_records_name'"));
        Assert.Throws<InvalidOperationException>(() => DatabaseBatchTargetWriter.PrepareTargetSchema(
            connection,
            "sqlite",
            "missing_records",
            "MissingTarget",
            JsonSerializer.Serialize(new DatabaseBatchTargetOptions
            {
                CreateTable = false,
                Schema = new Dictionary<string, string>
                {
                    ["id"] = "INTEGER",
                    ["name"] = "TEXT"
                },
                KeyColumns = ["id"]
            })));
    }

    [Fact]
    public void SqliteBatchTransformStagesRowsUsesParametersAndPreservesLineage()
    {
        using var files = new TemporaryDirectory();
        using var connection = new SqliteConnection($"Data Source={files.GetPath("transform.sqlite")}");
        connection.Open();
        var outputJson = PreparedDatabaseTransformRunner.ExecuteSqliteBatch(
            connection,
            """
            SELECT __migration_parent_index, Id, upper(Name) AS Name
            FROM __migration4dataverse_input
            WHERE length(Name) >= @minimumLength
            ORDER BY Id
            """,
            """{"minimumLength":4}""",
            """[{"Id":"A","Name":"Ada"},{"Id":"B","Name":"Grace"},{"Id":"C","Name":"Linus"}]""");
        using var output = JsonDocument.Parse(outputJson);

        Assert.Equal(2, output.RootElement.GetArrayLength());
        var first = output.RootElement[0];
        Assert.Equal(1, first.GetProperty("ParentIndices")[0].GetInt32());
        Assert.Equal("GRACE", first.GetProperty("Record").GetProperty("Name").GetString());
        Assert.Equal(
            0L,
            Scalar<long>(
                connection,
                "SELECT COUNT(*) FROM sqlite_temp_master WHERE name = '__migration4dataverse_input'"));

        var mergedJson = PreparedDatabaseTransformRunner.ExecuteSqliteBatch(
            connection,
            """
            SELECT json_group_array(__migration_parent_index) AS __migration_parent_indices,
                   count(*) AS RecordCount
            FROM __migration4dataverse_input
            """,
            "{}",
            """[{"Id":"A"},{"Id":"B"}]""");
        using var merged = JsonDocument.Parse(mergedJson);
        Assert.Equal(2, merged.RootElement[0].GetProperty("ParentIndices").GetArrayLength());
        Assert.Equal(2, merged.RootElement[0].GetProperty("Record").GetProperty("RecordCount").GetInt64());
        Assert.Throws<ArgumentException>(() => PreparedDatabaseTransformRunner.ExecuteSqliteBatch(
            connection,
            "DELETE FROM __migration4dataverse_input",
            "{}",
            """[{"Id":"A"}]"""));
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
    public void InvokeMigrationManagesSqliteTransformDatabaseWithoutConnection()
    {
        using var files = new TemporaryDirectory();
        using var source = new SqliteConnection($"Data Source={files.GetPath("source.sqlite")}");
        using var target = new SqliteConnection($"Data Source={files.GetPath("target.sqlite")}");
        source.Open();
        Execute(source, "CREATE TABLE records (id TEXT NOT NULL, name TEXT NOT NULL)");
        Execute(source, "INSERT INTO records VALUES ('a', 'first'), ('b', 'second')");
        source.Close();

        var sourceStep = PSObject.AsPSObject(new
        {
            Id = "Source",
            Type = "source",
            Inputs = Array.Empty<string>(),
            Source = new Hashtable
            {
                ["Type"] = "sqlite",
                ["ConnectionId"] = "source",
                ["Path"] = "source.sqlite",
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
                ["Type"] = "sqlite"
            },
            Script = ScriptBlock.Create("""
                param($row, $connection)
                $command = $connection.CreateCommand()
                try {
                    $command.CommandText = 'CREATE TABLE IF NOT EXISTS audit (id TEXT NOT NULL); INSERT INTO audit (id) VALUES (@id)'
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
                ["Path"] = "target.sqlite",
                ["Table"] = "result",
                ["Options"] = new Hashtable
                {
                    ["CreateTable"] = true,
                    ["Schema"] = new Hashtable
                    {
                        ["id"] = "TEXT",
                        ["name"] = "TEXT"
                    },
                    ["KeyColumns"] = new[] { "id" }
                }
            }
        });

        using var powershell = PS.Create();
        powershell.AddCommand("Import-Module")
            .AddParameter("Name", typeof(InvokeMigrationCommand).Assembly.Location);
        powershell.Invoke();
        Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
        powershell.Commands.Clear();
        powershell.AddCommand("Invoke-M4dMigrationGraph")
            .AddParameter("DatabasePath", files.GetPath("migration.sqlite"))
            .AddParameter("RunName", "SQLite database connectors")
            .AddParameter("RunId", "sqlite-database-connectors")
            .AddParameter("Steps", new[] { sourceStep, transformStep, targetStep })
            .AddParameter("Connections", new Hashtable
            {
                ["source"] = new DirectoryInfo(Path.GetDirectoryName(files.GetPath("source.sqlite"))!),
                ["target"] = new DirectoryInfo(Path.GetDirectoryName(files.GetPath("target.sqlite"))!)
            })
            .AddParameter("BatchSize", 1)
            .AddParameter("MaxAttempts", 1);
        var output = powershell.Invoke();

        Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
        Assert.IsType<DependencySchedulerResult>(Assert.Single(output).BaseObject);
        target.Open();
        Assert.Equal("FIRST", Scalar<string>(target, "SELECT name FROM result WHERE id = 'a'"));
        target.Close();
        var transformFile = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(Path.GetDirectoryName(files.GetPath("migration.sqlite"))!, ".migration-transforms"),
            "Transform-sqlite-database-connectors-*.sqlite"));
        using var managedTransform = new SqliteConnection($"Data Source={transformFile}");
        managedTransform.Open();
        Assert.Equal(2L, Scalar<long>(managedTransform, "SELECT COUNT(*) FROM audit"));

        powershell.Commands.Clear();
        var secondOutput = powershell.AddCommand("Invoke-M4dMigrationGraph")
            .AddParameter("DatabasePath", files.GetPath("migration.sqlite"))
            .AddParameter("RunName", "SQLite database connectors second run")
            .AddParameter("RunId", "sqlite-database-connectors-second")
            .AddParameter("Steps", new[] { sourceStep, transformStep, targetStep })
            .AddParameter("Connections", new Hashtable
            {
                ["source"] = new DirectoryInfo(Path.GetDirectoryName(files.GetPath("source.sqlite"))!),
                ["target"] = new DirectoryInfo(Path.GetDirectoryName(files.GetPath("target.sqlite"))!)
            })
            .AddParameter("BatchSize", 1)
            .AddParameter("MaxAttempts", 1)
            .Invoke();

        Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
        Assert.IsType<DependencySchedulerResult>(Assert.Single(secondOutput).BaseObject);
        Assert.Equal(
            2,
            Directory.EnumerateFiles(
                    Path.Combine(Path.GetDirectoryName(files.GetPath("migration.sqlite"))!, ".migration-transforms"),
                    "Transform-sqlite-database-connectors-*.sqlite")
                .Count());
    }

    [Fact]
    public void InvokeMigrationRunsSqliteBatchAndWholeDatasetTransforms()
    {
        using var files = new TemporaryDirectory();
        using var source = new SqliteConnection($"Data Source={files.GetPath("source.sqlite")}");
        using var target = new SqliteConnection($"Data Source={files.GetPath("target.sqlite")}");
        source.Open();
        Execute(source, "CREATE TABLE records (Id TEXT NOT NULL, Name TEXT NOT NULL)");
        Execute(source, "INSERT INTO records VALUES ('A001', 'North account'), ('A002', 'South account')");
        source.Close();

        var sourceStep = PSObject.AsPSObject(new
        {
            Id = "Source",
            Type = "source",
            Inputs = Array.Empty<string>(),
            Source = new Hashtable
            {
                ["Type"] = "sqlite",
                ["ConnectionId"] = "source",
                ["Key"] = "Id",
                ["Options"] = new Hashtable
                {
                    ["Query"] = "SELECT Id, Name FROM records WHERE Id >= @minimumId ORDER BY Id",
                    ["Parameters"] = new Hashtable { ["minimumId"] = "A001" }
                }
            }
        });
        var batchTransform = PSObject.AsPSObject(new
        {
            Id = "NormalizeBatch",
            Type = "transform",
            Inputs = new[] { "Source" },
            Transform = new Hashtable
            {
                ["Type"] = "sqlite",
                ["Mode"] = "per_batch",
                ["InitializeSql"] = """
                    CREATE TABLE batch_rules (minimum_length INTEGER NOT NULL);
                    INSERT INTO batch_rules VALUES (4);
                    """,
                ["Sql"] = """
                    SELECT __migration_parent_index, Id, upper(Name) AS Name
                    FROM __migration4dataverse_input
                    WHERE length(Name) >= (SELECT minimum_length FROM batch_rules)
                    ORDER BY Id
                    """
            }
        });
        var stageTarget = PSObject.AsPSObject(new
        {
            Id = "StageTarget",
            Type = "target",
            Inputs = new[] { "NormalizeBatch" },
            Target = new Hashtable
            {
                ["Type"] = "sqlite",
                ["Table"] = "staged_records",
                ["Options"] = new Hashtable
                {
                    ["CreateTable"] = true,
                    ["Schema"] = new Hashtable { ["Id"] = "TEXT", ["Name"] = "TEXT" },
                    ["KeyColumns"] = new[] { "Id" },
                    ["Indexes"] = new[]
                    {
                        new Hashtable
                        {
                            ["Name"] = "IX_staged_records_name",
                            ["Columns"] = new[] { "Name" },
                            ["Unique"] = false
                        }
                    }
                }
            }
        });
        var wholeDatasetTransform = PSObject.AsPSObject(new
        {
            Id = "EnrichWholeDataset",
            Type = "transform",
            Inputs = new[] { "StageTarget" },
            Transform = new Hashtable
            {
                ["Type"] = "sqlite",
                ["Mode"] = "whole_dataset",
                ["Key"] = "Id",
                ["InitializeSql"] = """
                    CREATE TABLE dataset_rules (suffix TEXT NOT NULL);
                    INSERT INTO dataset_rules VALUES ('!');
                    """,
                ["Sql"] = """
                    SELECT s.Id, upper(s.Name) || (SELECT suffix FROM dataset_rules) AS Name
                    FROM staged_records AS s
                    ORDER BY s.Id
                    """
            }
        });
        var finalTarget = PSObject.AsPSObject(new
        {
            Id = "FinalTarget",
            Type = "target",
            Inputs = new[] { "EnrichWholeDataset" },
            Target = new Hashtable
            {
                ["Type"] = "sqlite",
                ["ConnectionId"] = "target",
                ["Table"] = "final_records",
                ["Options"] = new Hashtable
                {
                    ["CreateTable"] = true,
                    ["Schema"] = new Hashtable { ["Id"] = "TEXT", ["Name"] = "TEXT" },
                    ["KeyColumns"] = new[] { "Id" }
                }
            }
        });

        using var powershell = PS.Create();
        powershell.AddCommand("Import-Module")
            .AddParameter("Name", typeof(InvokeMigrationCommand).Assembly.Location);
        powershell.Invoke();
        Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
        powershell.Commands.Clear();
        powershell.AddCommand("Invoke-M4dMigrationGraph")
            .AddParameter("DatabasePath", files.GetPath("migration.sqlite"))
            .AddParameter("RunName", "SQLite SQL transforms")
            .AddParameter("RunId", "sqlite-sql-transforms")
            .AddParameter(
                "Steps",
                new[] { sourceStep, batchTransform, stageTarget, wholeDatasetTransform, finalTarget })
            .AddParameter("Connections", new Hashtable
            {
                ["source"] = new FileInfo(files.GetPath("source.sqlite")),
                ["target"] = new FileInfo(files.GetPath("target.sqlite"))
            })
            .AddParameter("BatchSize", 1)
            .AddParameter("MaxAttempts", 1);
        var output = powershell.Invoke();

        Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
        Assert.IsType<DependencySchedulerResult>(Assert.Single(output).BaseObject);
        target.Open();
        Assert.Equal(2L, Scalar<long>(target, "SELECT COUNT(*) FROM final_records"));
        Assert.Equal(
            "SOUTH ACCOUNT!",
            Scalar<string>(target, "SELECT Name FROM final_records WHERE Id = 'A002'"));
    }

    [Fact]
    public void InvokeMigrationRunsDuckDbPreparedTransformWithPositionalParameters()
    {
        using var files = new TemporaryDirectory();
        using var source = new DuckDBConnection($"Data Source={files.GetPath("source.duckdb")}");
        using var target = new DuckDBConnection($"Data Source={files.GetPath("target.duckdb")}");
        source.Open();
        Execute(source, "CREATE TABLE records (Id VARCHAR, Name VARCHAR)");
        Execute(source, "INSERT INTO records VALUES ('a', 'first'), ('b', 'second')");
        source.Close();

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
        var rowTransform = PSObject.AsPSObject(new
        {
            Id = "Transform",
            Type = "transform",
            Inputs = new[] { "Source" },
            Transform = new Hashtable
            {
                ["Type"] = "duckdb",
                ["InitializeSql"] = """
                    CREATE TABLE IF NOT EXISTS row_rules (suffix VARCHAR);
                    DELETE FROM row_rules;
                    INSERT INTO row_rules VALUES ('!');
                    """,
                ["Sql"] = "SELECT ? AS Id, upper(?) || (SELECT suffix FROM row_rules) AS Name",
                ["Parameters"] = new System.Collections.Specialized.OrderedDictionary
                {
                    ["id"] = "Id",
                    ["name"] = "Name"
                }
            }
        });
        var batchTransform = PSObject.AsPSObject(new
        {
            Id = "BatchTransform",
            Type = "transform",
            Inputs = new[] { "Transform" },
            Transform = new Hashtable
            {
                ["Type"] = "duckdb",
                ["Mode"] = "per_batch",
                ["InitializeSql"] = """
                    CREATE TABLE IF NOT EXISTS batch_rules (minimum_length INTEGER);
                    DELETE FROM batch_rules;
                    INSERT INTO batch_rules VALUES (4);
                    """,
                ["Sql"] = """
                    SELECT __migration_parent_index, Id, upper(Name) AS Name
                    FROM __migration4dataverse_input
                    WHERE length(Name) >= ?
                    ORDER BY Id
                    """,
                ["Parameters"] = new System.Collections.Specialized.OrderedDictionary
                {
                    ["minimumLength"] = 4
                }
            }
        });
        var stageTarget = PSObject.AsPSObject(new
        {
            Id = "StageTarget",
            Type = "target",
            Inputs = new[] { "BatchTransform" },
            Target = new Hashtable
            {
                ["Type"] = "duckdb",
                ["Table"] = "staged_records"
            }
        });
        var wholeDatasetTransform = PSObject.AsPSObject(new
        {
            Id = "WholeDatasetTransform",
            Type = "transform",
            Inputs = new[] { "StageTarget" },
            Transform = new Hashtable
            {
                ["Type"] = "duckdb",
                ["Mode"] = "whole_dataset",
                ["Key"] = "Id",
                ["InitializeSql"] = """
                    CREATE TABLE IF NOT EXISTS dataset_rules (suffix VARCHAR);
                    DELETE FROM dataset_rules;
                    INSERT INTO dataset_rules VALUES ('!');
                    """,
                ["Sql"] = """
                    SELECT Id, upper(Name) || (SELECT suffix FROM dataset_rules) AS Name
                    FROM staged_records
                    WHERE length(Name) >= ?
                    ORDER BY Id
                    """,
                ["Parameters"] = new System.Collections.Specialized.OrderedDictionary
                {
                    ["minimumLength"] = 7
                }
            }
        });
        var targetStep = PSObject.AsPSObject(new
        {
            Id = "Target",
            Type = "target",
            Inputs = new[] { "WholeDatasetTransform" },
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
        powershell.AddCommand("Invoke-M4dMigrationGraph")
            .AddParameter("DatabasePath", files.GetPath("migration.sqlite"))
            .AddParameter("RunName", "DuckDB prepared transform")
            .AddParameter("RunId", "duckdb-prepared-transform")
            .AddParameter(
                "Steps",
                new[] { sourceStep, rowTransform, batchTransform, stageTarget, wholeDatasetTransform, targetStep })
            .AddParameter("Connections", new Hashtable
            {
                ["source"] = new FileInfo(files.GetPath("source.duckdb")),
                ["target"] = new FileInfo(files.GetPath("target.duckdb"))
            })
            .AddParameter("BatchSize", 1)
            .AddParameter("MaxAttempts", 1);
        var output = powershell.Invoke();

        Assert.False(powershell.HadErrors, string.Join(Environment.NewLine, powershell.Streams.Error));
        Assert.IsType<DependencySchedulerResult>(Assert.Single(output).BaseObject);
        target.Open();
        Assert.Equal(1L, Scalar<long>(target, "SELECT COUNT(*) FROM result"));
        Assert.Equal("SECOND!!", Scalar<string>(target, "SELECT Name FROM result WHERE Id = 'b'"));
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(Path.GetDirectoryName(files.GetPath("migration.sqlite"))!, ".migration-transforms"),
            "Transform-duckdb-prepared-transform-*.duckdb"));
        Assert.Equal(
            3,
            Directory.EnumerateFiles(
                    Path.Combine(Path.GetDirectoryName(files.GetPath("migration.sqlite"))!, ".migration-transforms"),
                    "*duckdb-prepared-transform-*.duckdb")
                .Count());
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

    [Theory]
    [InlineData("sqlserver", "[dbo].[records]", "[records]")]
    [InlineData("postgresql", "\"public\".\"records\"", "\"records\"")]
    [InlineData("mysql", "`records`", "`records`")]
    [InlineData("oracle", "\"RECORDS\"", "\"RECORDS\"")]
    [InlineData("sqlite", "\"main\".\"records\"", "\"records\"")]
    [InlineData("duckdb", "\"main\".\"records\"", "\"records\"")]
    public void QuotesDatabaseIdentifiers(string provider, string table, string identifier)
    {
        Assert.Equal(table, DatabaseConnectorSupport.QuoteTable(provider, "records"));
        Assert.Equal(identifier, DatabaseConnectorSupport.QuoteIdentifier(provider, "records"));
    }

    [Theory]
    [InlineData("sqlserver", "NVARCHAR(MAX)", "NVARCHAR(MAX)")]
    [InlineData("sqlserver", "int", "INT")]
    [InlineData("sqlserver", "decimal(18,2)", "DECIMAL(18,2)")]
    [InlineData("postgresql", "character varying(100)", "VARCHAR(100)")]
    [InlineData("postgresql", "timestamp with time zone", "TIMESTAMPTZ")]
    [InlineData("postgresql", "double precision", "DOUBLEPRECISION")]
    [InlineData("mysql", "boolean", "TINYINT")]
    [InlineData("mysql", "varchar(100)", "VARCHAR(100)")]
    [InlineData("mysql", "decimal(18,2)", "DECIMAL(18,2)")]
    [InlineData("oracle", "varchar2(100)", "VARCHAR2(100)")]
    [InlineData("oracle", "number(18,2)", "NUMBER(18,2)")]
    [InlineData("oracle", "timestamp", "TIMESTAMP(6)")]
    [InlineData("oracle", "timestamp(6)", "TIMESTAMP(6)")]
    [InlineData("oracle", "timestamp(6) with time zone", "TIMESTAMPWITHTIMEZONE")]
    [InlineData("oracle", "interval day(2) to second(6)", "INTERVALDAY(2)TOSECOND(6)")]
    public void NormalizesProviderSchemaTypes(string provider, string declared, string expected)
    {
        Assert.Equal(
            expected,
            DatabaseConnectorSupport.NormalizeSchemaType(provider, declared));
    }

    [Theory]
    [InlineData("sqlserver", "NVARCHAR", -1L, null, null, "NVARCHAR(MAX)")]
    [InlineData("sqlserver", "DECIMAL", null, 18, 2, "DECIMAL(18,2)")]
    [InlineData("postgresql", "VARCHAR", 100L, null, null, "VARCHAR(100)")]
    [InlineData("mysql", "VARCHAR", 100L, null, null, "VARCHAR(100)")]
    [InlineData("mysql", "TINYINT", null, 3, 0, "TINYINT")]
    [InlineData("oracle", "VARCHAR2", 100L, null, null, "VARCHAR2(100)")]
    [InlineData("oracle", "NUMBER", null, 18, 2, "NUMBER(18,2)")]
    [InlineData("oracle", "TIMESTAMP(6)", null, null, 6, "TIMESTAMP(6)")]
    [InlineData("oracle", "INTERVAL DAY(2) TO SECOND(6)", null, null, null, "INTERVALDAY(2)TOSECOND(6)")]
    public void BuildsProviderSchemaTypesFromCatalogMetadata(
        string provider,
        string dataType,
        long? maxLength,
        int? precision,
        int? scale,
        string expected)
    {
        Assert.Equal(
            expected,
            DatabaseConnectorSupport.BuildSchemaType(provider, dataType, maxLength, precision, scale));
    }

    [Theory]
    [InlineData("sqlserver", "NVARCHAR(MAX)")]
    [InlineData("sqlserver", "DATETIMEOFFSET")]
    [InlineData("postgresql", "UUID")]
    [InlineData("postgresql", "NUMERIC(18,2)")]
    [InlineData("mysql", "TINYINT(1)")]
    [InlineData("mysql", "JSON")]
    [InlineData("oracle", "VARCHAR2(100)")]
    [InlineData("oracle", "NUMBER(18,2)")]
    [InlineData("oracle", "TIMESTAMP WITH TIME ZONE")]
    [InlineData("oracle", "INTERVAL DAY(2) TO SECOND(6)")]
    public void AcceptsProviderSqlTypes(string provider, string type)
    {
        Assert.Equal(
            DatabaseConnectorSupport.NormalizeSqlType(type),
            DatabaseConnectorSupport.ValidateSqlType(provider, "column", type));
    }

    [Theory]
    [InlineData("sqlserver", "GEOGRAPHY")]
    [InlineData("postgresql", "MONEY")]
    [InlineData("mysql", "GEOGRAPHY")]
    [InlineData("oracle", "GEOGRAPHY")]
    public void RejectsUnsupportedProviderSqlTypes(string provider, string type)
    {
        Assert.Throws<ArgumentException>(
            () => DatabaseConnectorSupport.ValidateSqlType(provider, "column", type));
    }

    [Theory]
    [InlineData("sqlserver", "NVARCHAR(100)")]
    [InlineData("postgresql", "VARCHAR")]
    [InlineData("mysql", "VARCHAR(128)")]
    [InlineData("oracle", "VARCHAR2(256)")]
    [InlineData("sqlite", "VARCHAR")]
    [InlineData("duckdb", "VARCHAR")]
    public void BatchLedgerUsesProviderSafeStringTypes(string provider, string expectedType)
    {
        var sql = DatabaseBatchTargetWriter.GetCreateBatchTableSql(provider);

        Assert.Contains($"{expectedType} NOT NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void NewDatabaseTargetsAllowOptionalSchemaAndRejectIndexes()
    {
        var options = new DatabaseBatchTargetOptions
        {
            KeyColumns = new[] { "id" },
            Schema = new Dictionary<string, string>
            {
                ["id"] = "BIGINT",
                ["name"] = "NVARCHAR(100)"
            }
        };
        options.Validate("sqlserver", "Target");

        Assert.Throws<NotSupportedException>(() => new DatabaseBatchTargetOptions
        {
            Indexes = new[] { new DatabaseIndexDefinition { Name = "IX_name", Columns = new[] { "name" } } }
        }.Validate("postgresql", "Target"));
    }

    [Fact]
    public void SqlServerSourceCountRequiresOverrideForOrderedQueries()
    {
        var options = new DatabaseSourceOptions
        {
            Query = "SELECT id FROM records ORDER BY id"
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => options.GetCountQuery("sqlserver"));

        Assert.Contains("CountQuery", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            "SELECT COUNT(*) FROM (SELECT id FROM records ORDER BY id) AS __migration_count",
            options.GetCountQuery("postgresql"));
    }
}
