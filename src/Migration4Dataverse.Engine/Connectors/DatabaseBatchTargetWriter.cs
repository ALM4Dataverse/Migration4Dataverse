using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Migration4Dataverse.Engine.Connectors;

public sealed class DatabaseBatchTargetOptions
{
    public Dictionary<string, string>? Schema { get; init; }

    public bool? CreateTable { get; init; }

    public string[]? KeyColumns { get; init; }

    public DatabaseIndexDefinition[]? Indexes { get; init; }

    public int CommandTimeoutSeconds { get; init; } = 30;

    public bool ShouldCreateTable(string connectorType)
    {
        return CreateTable ?? !string.Equals(connectorType, "sqlite", StringComparison.Ordinal);
    }

    public void Validate(string connectorType, string stepId)
    {
        if (CommandTimeoutSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CommandTimeoutSeconds),
                $"Database target '{stepId}' CommandTimeoutSeconds cannot be negative.");
        }

        if (Schema is null)
        {
            if (connectorType == "sqlite")
            {
                throw new ArgumentException(
                    $"Database target '{stepId}' requires an explicit Schema for SQLite.");
            }
        }
        else
        {
            if (Schema.Count == 0)
            {
                throw new ArgumentException($"Database target '{stepId}' Schema must not be empty.");
            }
            foreach (var (name, type) in Schema)
            {
                DatabaseConnectorSupport.ValidateSqlType(connectorType, name, type);
            }
        }

        if (connectorType == "sqlite")
        {
            if (KeyColumns is not { Length: > 0 })
            {
                throw new ArgumentException(
                    $"Database target '{stepId}' requires KeyColumns for SQLite upserts.");
            }
            var columnNames = Schema!.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (KeyColumns.Any(string.IsNullOrWhiteSpace) ||
                KeyColumns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != KeyColumns.Length ||
                KeyColumns.Any(key => !columnNames.Contains(key)))
            {
                throw new ArgumentException(
                    $"Database target '{stepId}' KeyColumns must be distinct columns declared in Schema.");
            }

            var indexNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var index in Indexes ?? Array.Empty<DatabaseIndexDefinition>())
            {
                index.Validate(stepId, columnNames);
                if (!indexNames.Add(index.Name))
                {
                    throw new ArgumentException(
                        $"Database target '{stepId}' declares duplicate index name '{index.Name}'.");
                }
            }
            return;
        }

        if (Indexes is { Length: > 0 })
        {
            throw new NotSupportedException(
                $"Database target '{stepId}' Indexes are currently supported only for SQLite.");
        }
        if (KeyColumns is { Length: > 0 })
        {
            if (KeyColumns.Any(string.IsNullOrWhiteSpace) ||
                KeyColumns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != KeyColumns.Length)
            {
                throw new ArgumentException(
                    $"Database target '{stepId}' KeyColumns must be distinct non-empty columns.");
            }
            if (Schema is not null)
            {
                var columnNames = Schema.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (KeyColumns.Any(key => !columnNames.Contains(key)))
                {
                    throw new ArgumentException(
                        $"Database target '{stepId}' KeyColumns must be columns declared in Schema.");
                }
            }
        }
        if (connectorType == "duckdb" && KeyColumns is { Length: > 0 })
        {
            throw new NotSupportedException(
                $"Database target '{stepId}' KeyColumns are currently supported only for SQLite, SQL Server, PostgreSQL, MySQL, and Oracle.");
        }
    }
}

public sealed class DatabaseIndexDefinition
{
    public string Name { get; init; } = string.Empty;

    public string[] Columns { get; init; } = Array.Empty<string>();

    public bool Unique { get; init; }

    internal void Validate(string stepId, IReadOnlySet<string> schemaColumns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        if (Columns.Length == 0 ||
            Columns.Any(string.IsNullOrWhiteSpace) ||
            Columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Columns.Length ||
            Columns.Any(column => !schemaColumns.Contains(column)))
        {
            throw new ArgumentException(
                $"Database target '{stepId}' index '{Name}' must declare distinct columns from Schema.");
        }
    }
}

public static class DatabaseBatchTargetWriter
{
    private const string BatchTableName = "__migration4dataverse_batches";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static void PrepareTargetSchema(
        DbConnection connection,
        string connectorType,
        string tableName,
        string stepId,
        string optionsJson)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        DatabaseConnectorSupport.ValidateConnection(connectorType, connection, $"target '{stepId}'");
        EnsureTargetNameIsNotReserved(connectorType, tableName);

        var options = DeserializeOptions(optionsJson);
        options.Validate(connectorType, stepId);
        var createTable = options.ShouldCreateTable(connectorType);
        if (options.Schema is null && createTable)
        {
            return;
        }

        using var connectionLease = DatabaseConnectionGate.Acquire(connection);
        var openedConnection = connection.State != ConnectionState.Open;
        if (openedConnection)
        {
            connection.Open();
        }
        try
        {
            using var transaction = connection.BeginTransaction();
            var actualSchema = ReadTableSchema(
                connection,
                transaction,
                connectorType,
                tableName,
                options.CommandTimeoutSeconds);
            if (actualSchema.Count == 0)
            {
                if (!createTable)
                {
                    throw new InvalidOperationException(
                        $"Database target table '{tableName}' does not exist and CreateTable is false.");
                }
                var declaredSchema = CreateExplicitSchema(
                    connectorType,
                    options.Schema ?? throw new InvalidOperationException(
                        $"Database target '{stepId}' requires Schema to create its table up front."));
                Execute(
                    connection,
                    transaction,
                    GetCreateTableSql(connectorType, tableName, declaredSchema, options.KeyColumns),
                    options.CommandTimeoutSeconds);
                actualSchema = ReadTableSchema(
                    connection,
                    transaction,
                    connectorType,
                    tableName,
                    options.CommandTimeoutSeconds);
            }

            if (options.Schema is not null)
            {
                ValidateSchema(
                    connectorType,
                    tableName,
                    CreateExplicitSchema(connectorType, options.Schema),
                    actualSchema);
            }
            EnsureTargetIndexes(
                connection,
                transaction,
                connectorType,
                tableName,
                options,
                createTable);
            transaction.Commit();
        }
        finally
        {
            if (openedConnection)
            {
                connection.Close();
            }
        }
    }

    public static void WriteBatch(
        DbConnection connection,
        string connectorType,
        string tableName,
        string stepId,
        string runId,
        string batchId,
        string recordsJson,
        string optionsJson)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorType);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordsJson);
        DatabaseConnectorSupport.ValidateConnection(connectorType, connection, $"target '{stepId}'");
        EnsureTargetNameIsNotReserved(connectorType, tableName);

        var options = DeserializeOptions(optionsJson);
        options.Validate(connectorType, stepId);
        var createTable = options.ShouldCreateTable(connectorType);
        var records = ParseRecords(recordsJson, stepId);
        if (records.Count == 0)
        {
            return;
        }

        using var connectionLease = DatabaseConnectionGate.Acquire(connection);
        var openedConnection = connection.State != ConnectionState.Open;
        if (openedConnection)
        {
            connection.Open();
        }

        try
        {
            using var transaction = connection.BeginTransaction();
            Execute(
                connection,
                transaction,
                GetCreateBatchTableSql(connectorType),
                options.CommandTimeoutSeconds);

            var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recordsJson)));
            var quotedTable = DatabaseConnectorSupport.QuoteTable(connectorType, tableName);
            var tableSchema = ReadTableSchema(
                connection,
                transaction,
                connectorType,
                tableName,
                options.CommandTimeoutSeconds);
            var schema = options.Schema is null
                ? InferSchema(connectorType, records)
                : CreateExplicitSchema(connectorType, options.Schema);

            if (tableSchema.Count == 0)
            {
                if (!createTable)
                {
                    throw new InvalidOperationException(
                        $"Database target table '{tableName}' does not exist and CreateTable is false.");
                }
                Execute(
                    connection,
                    transaction,
                    GetCreateTableSql(connectorType, tableName, schema, options.KeyColumns),
                    options.CommandTimeoutSeconds);
                tableSchema = ReadTableSchema(
                    connection,
                    transaction,
                    connectorType,
                    tableName,
                    options.CommandTimeoutSeconds);
            }

            ValidateSchema(connectorType, tableName, schema, tableSchema);
            EnsureTargetIndexes(
                connection,
                transaction,
                connectorType,
                tableName,
                options,
                createTable);
            ValidateRecords(tableName, records, tableSchema, options.KeyColumns);
            var existingHash = FindBatchHash(
                connection,
                transaction,
                connectorType,
                runId,
                stepId,
                batchId,
                options.CommandTimeoutSeconds);
            if (existingHash is not null)
            {
                if (!string.Equals(existingHash, payloadHash, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Database target batch '{batchId}' for step '{stepId}' was replayed with different records.");
                }
                transaction.Commit();
                return;
            }

            InsertRecords(
                connection,
                transaction,
                connectorType,
                quotedTable,
                tableSchema,
                records,
                options.KeyColumns,
                options.CommandTimeoutSeconds);
            InsertBatchMarker(
                connection,
                transaction,
                connectorType,
                runId,
                stepId,
                batchId,
                payloadHash,
                options.CommandTimeoutSeconds);
            transaction.Commit();
        }
        finally
        {
            if (openedConnection)
            {
                connection.Close();
            }
        }
    }

    private static DatabaseBatchTargetOptions DeserializeOptions(string optionsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<DatabaseBatchTargetOptions>(optionsJson, JsonOptions)
                ?? throw new InvalidDataException("Database target options are empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Database target options are invalid JSON.", exception);
        }
    }

    private static List<JsonElement> ParseRecords(string recordsJson, string stepId)
    {
        using var document = JsonDocument.Parse(recordsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Database target records for '{stepId}' must be an array.");
        }

        var records = document.RootElement.EnumerateArray().Select(record => record.Clone()).ToList();
        if (records.Any(record => record.ValueKind != JsonValueKind.Object))
        {
            throw new InvalidDataException($"Database target records for '{stepId}' must all be objects.");
        }
        return records;
    }

    private static IReadOnlyList<DatabaseColumn> InferSchema(string connectorType, IReadOnlyList<JsonElement> records)
    {
        var first = records[0];
        var firstProperties = first.EnumerateObject().ToArray();
        if (firstProperties.Length == 0)
        {
            throw new InvalidDataException("Database target records must contain at least one field.");
        }
        var names = firstProperties.Select(property => property.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
        {
            throw new InvalidDataException("Database target records contain duplicate field names.");
        }

        var columns = new List<DatabaseColumn>(names.Length);
        foreach (var name in names)
        {
            string? inferredType = null;
            foreach (var record in records)
            {
                if (!record.TryGetProperty(name, out var value))
                {
                    throw new InvalidDataException(
                        $"Database target record is missing field '{name}' from the first record.");
                }
                if (value.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                var currentType = DatabaseConnectorSupport.InferSqlType(connectorType, value);
                if (inferredType is null)
                {
                    inferredType = currentType;
                }
                else if (!string.Equals(inferredType, currentType, StringComparison.Ordinal))
                {
                    inferredType = connectorType switch
                    {
                        "sqlite" => "NUMERIC",
                        "sqlserver" => "NVARCHAR(MAX)",
                        _ => "TEXT"
                    };
                }
            }

            columns.Add(new DatabaseColumn(
                name,
                inferredType ?? connectorType switch
                {
                    "sqlite" => "TEXT",
                    "sqlserver" => "NVARCHAR(MAX)",
                    _ => "TEXT"
                }));
        }
        return columns;
    }

    private static IReadOnlyList<DatabaseColumn> CreateExplicitSchema(
        string connectorType,
        IReadOnlyDictionary<string, string> schema)
    {
        return schema.Select(column => new DatabaseColumn(
            column.Key,
            DatabaseConnectorSupport.ValidateSqlType(connectorType, column.Key, column.Value))).ToArray();
    }

    private static List<DatabaseColumn> ReadTableSchema(
        DbConnection connection,
        DbTransaction transaction,
        string connectorType,
        string tableName,
        int commandTimeoutSeconds)
    {
        var (schemaName, actualTableName) = DatabaseConnectorSupport.SplitTableName(connectorType, tableName);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = DatabaseConnectorSupport.GetSchemaQuery(
            connectorType,
            schemaName,
            actualTableName);

        using var reader = command.ExecuteReader();
        var result = new List<DatabaseColumn>();
        while (reader.Read())
        {
            var (name, type) = DatabaseConnectorSupport.ReadSchemaColumn(connectorType, reader);
            result.Add(new DatabaseColumn(name, type));
        }
        return result;
    }

    private static void ValidateSchema(
        string connectorType,
        string tableName,
        IReadOnlyList<DatabaseColumn> expected,
        IReadOnlyList<DatabaseColumn> actual)
    {
        var actualByName = actual.ToDictionary(column => column.Name, StringComparer.Ordinal);
        if (expected.Count != actual.Count ||
            expected.Any(expectedColumn => !actualByName.TryGetValue(expectedColumn.Name, out var actualColumn) ||
                !string.Equals(
                    DatabaseConnectorSupport.NormalizeSchemaType(connectorType, expectedColumn.SqlType),
                    DatabaseConnectorSupport.NormalizeSchemaType(connectorType, actualColumn.SqlType),
                    StringComparison.Ordinal)))
        {
            var expectedDescription = string.Join(", ", expected.Select(column => $"{column.Name} {column.SqlType}"));
            var actualDescription = string.Join(", ", actual.Select(column => $"{column.Name} {column.SqlType}"));
            throw new InvalidDataException(
                $"Database target table '{tableName}' has a schema mismatch. " +
                $"Expected [{expectedDescription}]; found [{actualDescription}].");
        }
    }

    private static void ValidateRecords(
        string tableName,
        IReadOnlyList<JsonElement> records,
        IReadOnlyList<DatabaseColumn> schema,
        IReadOnlyList<string>? keyColumns)
    {
        var expectedNames = schema.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var record in records)
        {
            var actualNames = record.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
            if (!actualNames.SetEquals(expectedNames))
            {
                throw new InvalidDataException(
                    $"Database target record fields do not match table '{tableName}'.");
            }

            foreach (var keyColumn in keyColumns ?? Array.Empty<string>())
            {
                if (record.GetProperty(keyColumn).ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    throw new InvalidDataException(
                        $"Database target record for table '{tableName}' has a null key column '{keyColumn}'.");
                }
            }
        }
    }

    private static void EnsureTargetIndexes(
        DbConnection connection,
        DbTransaction transaction,
        string connectorType,
        string tableName,
        DatabaseBatchTargetOptions options,
        bool createMissing)
    {
        if (connectorType != "sqlite")
        {
            return;
        }

        var indexes = ReadSqliteIndexes(connection, transaction, tableName);
        var keyColumns = options.KeyColumns!;
        if (!indexes.Values.Any(index =>
                index.Unique &&
                !index.IsPartial &&
                !index.HasExpression &&
                index.Columns.SequenceEqual(keyColumns, StringComparer.OrdinalIgnoreCase)))
        {
            if (!createMissing)
            {
                throw new InvalidOperationException(
                    $"SQLite target table '{tableName}' is missing a unique index for KeyColumns " +
                    $"[{string.Join(", ", keyColumns)}].");
            }

            CreateSqliteIndex(
                connection,
                transaction,
                tableName,
                CreateKeyIndexName(tableName, keyColumns),
                keyColumns,
                unique: true,
                options.CommandTimeoutSeconds);
            indexes = ReadSqliteIndexes(connection, transaction, tableName);
        }

        foreach (var definition in options.Indexes ?? Array.Empty<DatabaseIndexDefinition>())
        {
            if (indexes.TryGetValue(definition.Name, out var existing))
            {
                ValidateIndexDefinition(tableName, definition, existing);
                continue;
            }
            if (!createMissing)
            {
                throw new InvalidOperationException(
                    $"SQLite target table '{tableName}' is missing declared index '{definition.Name}'.");
            }

            CreateSqliteIndex(
                connection,
                transaction,
                tableName,
                definition.Name,
                definition.Columns,
                definition.Unique,
                options.CommandTimeoutSeconds);
            indexes = ReadSqliteIndexes(connection, transaction, tableName);
        }
    }

    private static Dictionary<string, SqliteIndex> ReadSqliteIndexes(
        DbConnection connection,
        DbTransaction transaction,
        string tableName)
    {
        var (schemaName, actualTableName) = DatabaseConnectorSupport.SplitTableName("sqlite", tableName);
        var indexNames = new List<(string Name, bool Unique, bool IsPartial)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                $"PRAGMA {DatabaseConnectorSupport.QuoteIdentifier("sqlite", schemaName)}." +
                $"index_list({DatabaseConnectorSupport.QuoteIdentifier("sqlite", actualTableName)})";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                indexNames.Add((reader.GetString(1), reader.GetInt64(2) != 0, reader.GetInt64(4) != 0));
            }
        }

        var indexes = new Dictionary<string, SqliteIndex>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, unique, isPartial) in indexNames)
        {
            var columns = new List<string>();
            var hasExpression = false;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    $"PRAGMA {DatabaseConnectorSupport.QuoteIdentifier("sqlite", schemaName)}." +
                    $"index_info({DatabaseConnectorSupport.QuoteIdentifier("sqlite", name)})";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(2))
                    {
                        hasExpression = true;
                        continue;
                    }
                    columns.Add(reader.GetString(2));
                }
            }

            indexes.Add(name, new SqliteIndex(unique, isPartial, hasExpression, columns.ToArray()));
        }
        return indexes;
    }

    private static void ValidateIndexDefinition(
        string tableName,
        DatabaseIndexDefinition definition,
        SqliteIndex actual)
    {
        if (actual.Unique != definition.Unique ||
            actual.IsPartial ||
            actual.HasExpression ||
            !actual.Columns.SequenceEqual(definition.Columns, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"SQLite target table '{tableName}' index '{definition.Name}' is incompatible with its declaration.");
        }
    }

    private static void CreateSqliteIndex(
        DbConnection connection,
        DbTransaction transaction,
        string tableName,
        string indexName,
        IReadOnlyList<string> columns,
        bool unique,
        int commandTimeoutSeconds)
    {
        var (schemaName, actualTableName) = DatabaseConnectorSupport.SplitTableName("sqlite", tableName);
        var qualifiedIndex =
            $"{DatabaseConnectorSupport.QuoteIdentifier("sqlite", schemaName)}." +
            $"{DatabaseConnectorSupport.QuoteIdentifier("sqlite", indexName)}";
        var columnNames = string.Join(", ", columns.Select(column =>
            DatabaseConnectorSupport.QuoteIdentifier("sqlite", column)));
        Execute(
            connection,
            transaction,
            $"CREATE {(unique ? "UNIQUE " : string.Empty)}INDEX {qualifiedIndex} " +
            $"ON {DatabaseConnectorSupport.QuoteIdentifier("sqlite", actualTableName)} ({columnNames})",
            commandTimeoutSeconds);
    }

    private static string CreateKeyIndexName(string tableName, IReadOnlyList<string> keyColumns)
    {
        var identity = $"{tableName}\0{string.Join("\0", keyColumns)}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
        return $"UX_m4d_{hash}";
    }

    internal static string GetCreateBatchTableSql(string connectorType)
    {
        var table = DatabaseConnectorSupport.QuoteIdentifier(connectorType, BatchTableName);
        var runId = DatabaseConnectorSupport.QuoteIdentifier(connectorType, "RunId");
        var stepId = DatabaseConnectorSupport.QuoteIdentifier(connectorType, "StepId");
        var batchId = DatabaseConnectorSupport.QuoteIdentifier(connectorType, "BatchId");
        var payloadHash = DatabaseConnectorSupport.QuoteIdentifier(connectorType, "PayloadHash");
        var stringType = DatabaseConnectorSupport.IsSqlServer(connectorType)
            ? "NVARCHAR(100)"
            : DatabaseConnectorSupport.IsOracle(connectorType)
                ? "VARCHAR2(256)"
                : DatabaseConnectorSupport.IsMySql(connectorType)
                    ? "VARCHAR(128)"
                    : "VARCHAR";
        var columns =
            $"{runId} {stringType} NOT NULL, {stepId} {stringType} NOT NULL, " +
            $"{batchId} {stringType} NOT NULL, {payloadHash} {stringType} NOT NULL, " +
            $"PRIMARY KEY ({runId}, {stepId}, {batchId})";
        if (DatabaseConnectorSupport.IsSqlServer(connectorType))
        {
            return $"IF OBJECT_ID(N'{BatchTableName}', N'U') IS NULL CREATE TABLE {table} ({columns})";
        }
        if (DatabaseConnectorSupport.IsOracle(connectorType))
        {
            return "BEGIN" + Environment.NewLine +
                $"    EXECUTE IMMEDIATE 'CREATE TABLE {table} ({columns})';" + Environment.NewLine +
                "EXCEPTION" + Environment.NewLine +
                "    WHEN OTHERS THEN" + Environment.NewLine +
                "        IF SQLCODE != -955 THEN RAISE; END IF;" + Environment.NewLine +
                "END;";
        }
        return $"CREATE TABLE IF NOT EXISTS {table} ({columns})";
    }

    private static string GetCreateTableSql(
        string connectorType,
        string tableName,
        IReadOnlyList<DatabaseColumn> schema,
        IReadOnlyList<string>? keyColumns)
    {
        var columns = schema
            .Select(column =>
                $"{DatabaseConnectorSupport.QuoteIdentifier(connectorType, column.Name)} {column.SqlType}")
            .ToList();
        if (keyColumns is { Count: > 0 } &&
            (DatabaseConnectorSupport.IsPostgreSql(connectorType) ||
             DatabaseConnectorSupport.IsSqlServer(connectorType) ||
             DatabaseConnectorSupport.IsMySql(connectorType) ||
             DatabaseConnectorSupport.IsOracle(connectorType)))
        {
            columns.Add(
                $"PRIMARY KEY ({string.Join(", ", keyColumns.Select(key =>
                    DatabaseConnectorSupport.QuoteIdentifier(connectorType, key)))})");
        }
        return $"CREATE TABLE {DatabaseConnectorSupport.QuoteTable(connectorType, tableName)} " +
            $"({string.Join(", ", columns)})";
    }

    private static string BuildUpsertSql(
        string connectorType,
        string quotedTable,
        IReadOnlyList<DatabaseColumn> schema,
        IReadOnlyList<string>? keyColumns,
        string[] names,
        string[] parameterNames,
        string insertSql)
    {
        if (keyColumns is not { Count: > 0 })
        {
            return insertSql;
        }

        var keySet = keyColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var indexes = schema
            .Select((column, index) => (column.Name, Index: index))
            .ToDictionary(item => item.Name, item => item.Index, StringComparer.OrdinalIgnoreCase);
        var conflictColumns = string.Join(", ", keyColumns.Select(key =>
            DatabaseConnectorSupport.QuoteIdentifier(connectorType, key)));
        var updateColumns = schema
            .Where(column => !keySet.Contains(column.Name))
            .Select(column =>
            {
                var quoted = DatabaseConnectorSupport.QuoteIdentifier(connectorType, column.Name);
                return $"{quoted} = EXCLUDED.{quoted}";
            })
            .ToArray();

        if (connectorType == "sqlite")
        {
            return insertSql + (updateColumns.Length == 0
                ? $" ON CONFLICT ({conflictColumns}) DO NOTHING"
                : $" ON CONFLICT ({conflictColumns}) DO UPDATE SET {string.Join(", ", updateColumns)}");
        }

        if (DatabaseConnectorSupport.IsPostgreSql(connectorType))
        {
            return insertSql + (updateColumns.Length == 0
                ? $" ON CONFLICT ({conflictColumns}) DO NOTHING"
                : $" ON CONFLICT ({conflictColumns}) DO UPDATE SET {string.Join(", ", updateColumns)}");
        }

        if (DatabaseConnectorSupport.IsMySql(connectorType))
        {
            var assignments = schema
                .Where(column => !keySet.Contains(column.Name))
                .Select(column =>
                {
                    var quoted = DatabaseConnectorSupport.QuoteIdentifier(connectorType, column.Name);
                    return $"{quoted} = VALUES({quoted})";
                })
                .ToArray();
            return assignments.Length == 0
                ? $"{insertSql} ON DUPLICATE KEY UPDATE {DatabaseConnectorSupport.QuoteIdentifier(connectorType, keyColumns[0])} = {DatabaseConnectorSupport.QuoteIdentifier(connectorType, keyColumns[0])}"
                : $"{insertSql} ON DUPLICATE KEY UPDATE {string.Join(", ", assignments)}";
        }

        if (DatabaseConnectorSupport.IsOracle(connectorType))
        {
            var where = string.Join(" AND ", keyColumns.Select(key =>
                $"{DatabaseConnectorSupport.QuoteIdentifier(connectorType, key)} = {parameterNames[indexes[key]]}"));
            var assignments = schema
                .Where(column => !keySet.Contains(column.Name))
                .Select(column =>
                {
                    var quoted = DatabaseConnectorSupport.QuoteIdentifier(connectorType, column.Name);
                    return $"{quoted} = {parameterNames[indexes[column.Name]]}";
                })
                .ToArray();
            if (assignments.Length == 0)
            {
                var firstKey = DatabaseConnectorSupport.QuoteIdentifier(connectorType, keyColumns[0]);
                assignments = new[] { $"{firstKey} = {firstKey}" };
            }
            return "BEGIN" + Environment.NewLine +
                $"    UPDATE {quotedTable} SET {string.Join(", ", assignments)} WHERE {where};" +
                Environment.NewLine +
                "    IF SQL%ROWCOUNT = 0 THEN" + Environment.NewLine +
                $"        {insertSql};" + Environment.NewLine +
                "    END IF;" + Environment.NewLine +
                "END;";
        }

        if (DatabaseConnectorSupport.IsSqlServer(connectorType))
        {
            var where = string.Join(" AND ", keyColumns.Select(key =>
                $"{DatabaseConnectorSupport.QuoteIdentifier(connectorType, key)} = {parameterNames[indexes[key]]}"));
            var assignments = schema
                .Where(column => !keySet.Contains(column.Name))
                .Select(column =>
                {
                    var quoted = DatabaseConnectorSupport.QuoteIdentifier(connectorType, column.Name);
                    return $"{quoted} = {parameterNames[indexes[column.Name]]}";
                })
                .ToArray();
            if (assignments.Length == 0)
            {
                var firstKey = DatabaseConnectorSupport.QuoteIdentifier(connectorType, keyColumns[0]);
                assignments = new[] { $"{firstKey} = {firstKey}" };
            }
            return $"UPDATE {quotedTable} SET {string.Join(", ", assignments)} WHERE {where};" +
                $"{Environment.NewLine}IF @@ROWCOUNT = 0{Environment.NewLine}    {insertSql}";
        }

        return insertSql;
    }

    private static void InsertRecords(
        DbConnection connection,
        DbTransaction transaction,
        string connectorType,
        string quotedTable,
        IReadOnlyList<DatabaseColumn> schema,
        IReadOnlyList<JsonElement> records,
        IReadOnlyList<string>? keyColumns,
        int commandTimeoutSeconds)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = commandTimeoutSeconds;
        var names = schema.Select(column => DatabaseConnectorSupport.QuoteIdentifier(connectorType, column.Name)).ToArray();
        var parameterNames = schema
            .Select((_, index) => DatabaseConnectorSupport.ParameterName(connectorType, $"p{index}"))
            .ToArray();
        var insertSql =
            $"INSERT INTO {quotedTable} ({string.Join(", ", names)}) VALUES ({string.Join(", ", parameterNames)})";
        command.CommandText = BuildUpsertSql(
            connectorType,
            quotedTable,
            schema,
            keyColumns,
            names,
            parameterNames,
            insertSql);
        var parameters = schema.Select((_, index) =>
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = connectorType == "duckdb"
                ? (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : $"p{index}";
            command.Parameters.Add(parameter);
            return parameter;
        }).ToArray();

        foreach (var record in records)
        {
            for (var index = 0; index < schema.Count; index++)
            {
                var value = record.GetProperty(schema[index].Name);
                parameters[index].Value = DatabaseConnectorSupport.ToDatabaseValue(
                    value,
                    connectorType,
                    schema[index].SqlType);
            }
            command.ExecuteNonQuery();
        }
    }

    private static string? FindBatchHash(
        DbConnection connection,
        DbTransaction transaction,
        string connectorType,
        string runId,
        string stepId,
        string batchId,
        int commandTimeoutSeconds)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = commandTimeoutSeconds;
        var payloadHashColumn = DatabaseConnectorSupport.QuoteIdentifier(connectorType, "PayloadHash");
        var runIdColumn = DatabaseConnectorSupport.QuoteIdentifier(connectorType, "RunId");
        var stepIdColumn = DatabaseConnectorSupport.QuoteIdentifier(connectorType, "StepId");
        var batchIdColumn = DatabaseConnectorSupport.QuoteIdentifier(connectorType, "BatchId");
        command.CommandText =
            $"SELECT {payloadHashColumn} FROM {DatabaseConnectorSupport.QuoteIdentifier(connectorType, BatchTableName)} " +
            $"WHERE {runIdColumn} = {DatabaseConnectorSupport.ParameterName(connectorType, "run")} " +
            $"AND {stepIdColumn} = {DatabaseConnectorSupport.ParameterName(connectorType, "step")} " +
            $"AND {batchIdColumn} = {DatabaseConnectorSupport.ParameterName(connectorType, "batch")}";
        AddParameter(command, connectorType, "run", runId);
        AddParameter(command, connectorType, "step", stepId);
        AddParameter(command, connectorType, "batch", batchId);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void InsertBatchMarker(
        DbConnection connection,
        DbTransaction transaction,
        string connectorType,
        string runId,
        string stepId,
        string batchId,
        string payloadHash,
        int commandTimeoutSeconds)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = commandTimeoutSeconds;
        var quotedTable = DatabaseConnectorSupport.QuoteIdentifier(connectorType, BatchTableName);
        var columns = string.Join(", ", new[] { "RunId", "StepId", "BatchId", "PayloadHash" }
            .Select(column => DatabaseConnectorSupport.QuoteIdentifier(connectorType, column)));
        var values = string.Join(", ", new[] { "run", "step", "batch", "hash" }
            .Select(name => DatabaseConnectorSupport.ParameterName(connectorType, name)));
        command.CommandText = $"INSERT INTO {quotedTable} ({columns}) VALUES ({values})";
        AddParameter(command, connectorType, "run", runId);
        AddParameter(command, connectorType, "step", stepId);
        AddParameter(command, connectorType, "batch", batchId);
        AddParameter(command, connectorType, "hash", payloadHash);
        command.ExecuteNonQuery();
    }

    private static void Execute(
        DbConnection connection,
        DbTransaction transaction,
        string commandText,
        int commandTimeoutSeconds)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static void AddParameter(DbCommand command, string connectorType, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = connectorType == "duckdb"
            ? (command.Parameters.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static void EnsureTargetNameIsNotReserved(string connectorType, string tableName)
    {
        var (_, actualTableName) = DatabaseConnectorSupport.SplitTableName(connectorType, tableName);
        if (string.Equals(actualTableName, BatchTableName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Database table name '{BatchTableName}' is reserved by the migration engine.",
                nameof(tableName));
        }
    }

    private sealed record DatabaseColumn(string Name, string SqlType);

    private sealed record SqliteIndex(bool Unique, bool IsPartial, bool HasExpression, string[] Columns);
}
