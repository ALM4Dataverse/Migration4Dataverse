using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace Migration4Dataverse.Engine.Connectors;

public static class PreparedDatabaseTransformRunner
{
    private const string InputTable = "__migration4dataverse_input";
    private const string ParentIndexColumn = "__migration_parent_index";
    private const string ParentIndicesColumn = "__migration_parent_indices";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string ExecutePreparedBatch(
        DbConnection connection,
        string connectorType,
        string sql,
        string parametersJson,
        string recordsJson)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        DatabaseConnectorSupport.ValidateConnection(connectorType, connection, "transform");
        if (connectorType != "duckdb")
        {
            throw new ArgumentException("Prepared SQL transform steps currently require DuckDB.", nameof(connectorType));
        }

        var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(parametersJson, JsonOptions)
            ?? throw new InvalidDataException("DuckDB transform Parameters must be an object.");
        using var recordsDocument = JsonDocument.Parse(recordsJson);
        if (recordsDocument.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("DuckDB transform input rows must be an array.");
        }

        using var connectionLease = DatabaseConnectionGate.Acquire(connection);
        var openedConnection = connection.State != ConnectionState.Open;
        if (openedConnection)
        {
            connection.Open();
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var commandParameters = new List<(string FieldName, DbParameter Parameter)>(parameters.Count);
            foreach (var (parameterName, fieldName) in parameters)
            {
                if (string.IsNullOrWhiteSpace(parameterName) || string.IsNullOrWhiteSpace(fieldName))
                {
                    throw new InvalidDataException("DuckDB transform parameter names and field names must be non-empty.");
                }
                var parameter = command.CreateParameter();
                parameter.ParameterName = (commandParameters.Count + 1)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
                command.Parameters.Add(parameter);
                commandParameters.Add((fieldName, parameter));
            }
            command.Prepare();

            var results = new List<PreparedTransformOutput>();
            var inputIndex = 0;
            foreach (var input in recordsDocument.RootElement.EnumerateArray())
            {
                if (input.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("DuckDB transform input rows must be JSON objects.");
                }
                foreach (var (fieldName, parameter) in commandParameters)
                {
                    if (!input.TryGetProperty(fieldName, out var value))
                    {
                        throw new InvalidDataException(
                            $"DuckDB transform input row is missing parameter field '{fieldName}'.");
                    }
                    parameter.Value = DatabaseConnectorSupport.ToDatabaseValue(value, connectorType, "VARCHAR");
                }

                using var reader = command.ExecuteReader();
                if (reader.FieldCount == 0)
                {
                    results.Add(new PreparedTransformOutput(inputIndex, input.Clone()));
                }
                else
                {
                    var columnNames = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
                    if (columnNames.Distinct(StringComparer.Ordinal).Count() != columnNames.Length ||
                        columnNames.Any(string.IsNullOrWhiteSpace))
                    {
                        throw new InvalidDataException("DuckDB transform returned duplicate or empty column names.");
                    }
                    while (reader.Read())
                    {
                        var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
                        for (var index = 0; index < reader.FieldCount; index++)
                        {
                            row.Add(columnNames[index], DatabaseConnectorSupport.ToJsonValue(reader.GetValue(index)));
                        }
                        results.Add(new PreparedTransformOutput(
                            inputIndex,
                            JsonSerializer.SerializeToElement(row)));
                    }
                }
                inputIndex++;
            }

            return JsonSerializer.Serialize(results);
        }
        finally
        {
            if (openedConnection)
            {
                connection.Close();
            }
        }
    }

    public static string ExecuteSqliteBatch(
        DbConnection connection,
        string sql,
        string parametersJson,
        string recordsJson,
        int commandTimeoutSeconds = 30)
    {
        return ExecuteSetBasedBatch(
            connection,
            "sqlite",
            sql,
            parametersJson,
            recordsJson,
            commandTimeoutSeconds);
    }

    public static string ExecuteDuckDbBatch(
        DbConnection connection,
        string sql,
        string parametersJson,
        string recordsJson,
        int commandTimeoutSeconds = 30)
    {
        return ExecuteSetBasedBatch(
            connection,
            "duckdb",
            sql,
            parametersJson,
            recordsJson,
            commandTimeoutSeconds);
    }

    private static string ExecuteSetBasedBatch(
        DbConnection connection,
        string connectorType,
        string sql,
        string parametersJson,
        string recordsJson,
        int commandTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentOutOfRangeException.ThrowIfNegative(commandTimeoutSeconds);
        DatabaseConnectorSupport.ValidateConnection(connectorType, connection, "transform");
        ReadOnlySqlValidator.ValidateSelect(sql, $"{connectorType} Transform.Sql");

        var parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parametersJson, JsonOptions)
            ?? throw new InvalidDataException($"{connectorType} transform Parameters must be an object.");
        using var recordsDocument = JsonDocument.Parse(recordsJson);
        if (recordsDocument.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{connectorType} transform input rows must be an array.");
        }

        var records = recordsDocument.RootElement.EnumerateArray().ToArray();
        if (records.Length == 0)
        {
            return "[]";
        }
        var columns = GetInputColumns(records, connectorType);

        using var connectionLease = DatabaseConnectionGate.Acquire(connection);
        var openedConnection = connection.State != ConnectionState.Open;
        if (openedConnection)
        {
            connection.Open();
        }

        try
        {
            CreateInputTable(connection, connectorType, columns, records, commandTimeoutSeconds);
            try
            {
                using var readOnlyScope = DatabaseReadOnlyScope.Enter(connection, connectorType);
                using var command = connection.CreateCommand();
                command.CommandTimeout = commandTimeoutSeconds;
                command.CommandText = sql;
                if (connectorType == "sqlite")
                {
                    DatabaseConnectorSupport.AddQueryParameters(command, connectorType, parameters);
                }
                else
                {
                    AddDuckDbPositionalParameters(command, parameters);
                }
                using var reader = command.ExecuteReader();
                if (reader.FieldCount == 0)
                {
                    throw new InvalidDataException($"{connectorType} Transform.Sql must return a result set.");
                }

                var columnNames = Enumerable.Range(0, reader.FieldCount)
                    .Select(reader.GetName)
                    .ToArray();
                if (columnNames.Distinct(StringComparer.Ordinal).Count() != columnNames.Length ||
                    columnNames.Any(string.IsNullOrWhiteSpace))
                {
                    throw new InvalidDataException($"{connectorType} transform returned duplicate or empty column names.");
                }
                var parentIndexOrdinal = Array.FindIndex(
                    columnNames,
                    name => string.Equals(name, ParentIndexColumn, StringComparison.Ordinal));
                var parentIndicesOrdinal = Array.FindIndex(
                    columnNames,
                    name => string.Equals(name, ParentIndicesColumn, StringComparison.Ordinal));
                if (parentIndexOrdinal >= 0 && parentIndicesOrdinal >= 0)
                {
                    throw new InvalidDataException(
                        $"{connectorType} Transform.Sql cannot return both '{ParentIndexColumn}' and '{ParentIndicesColumn}'.");
                }

                var output = new List<SetBasedTransformOutput>();
                while (reader.Read())
                {
                    if (parentIndexOrdinal < 0 && parentIndicesOrdinal < 0)
                    {
                        throw new InvalidDataException(
                            $"{connectorType} Transform.Sql must return '{ParentIndexColumn}' or '{ParentIndicesColumn}' " +
                            "to preserve row lineage when it emits rows.");
                    }
                    var parentIndices = parentIndexOrdinal >= 0
                        ? [Convert.ToInt32(reader.GetValue(parentIndexOrdinal), CultureInfo.InvariantCulture)]
                        : ReadParentIndices(reader.GetValue(parentIndicesOrdinal), connectorType);
                    if (parentIndices.Length == 0 ||
                        parentIndices.Any(index => index < 0 || index >= records.Length) ||
                        parentIndices.Distinct().Count() != parentIndices.Length)
                    {
                        throw new InvalidDataException(
                            $"{connectorType} transform parent indices must be distinct indexes of input rows in this batch.");
                    }

                    var row = new Dictionary<string, object?>(reader.FieldCount - 1, StringComparer.Ordinal);
                    for (var index = 0; index < reader.FieldCount; index++)
                    {
                        if (index == parentIndexOrdinal || index == parentIndicesOrdinal)
                        {
                            continue;
                        }
                        row.Add(columnNames[index], DatabaseConnectorSupport.ToJsonValue(reader.GetValue(index)));
                    }
                    output.Add(new SetBasedTransformOutput(
                        parentIndices,
                        JsonSerializer.SerializeToElement(row)));
                }

                return JsonSerializer.Serialize(output);
            }
            finally
            {
                DropInputTable(connection, commandTimeoutSeconds);
            }
        }
        finally
        {
            if (openedConnection)
            {
                connection.Close();
            }
        }
    }

    private static string[] GetInputColumns(IReadOnlyList<JsonElement> records, string connectorType)
    {
        var first = records[0];
        if (first.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{connectorType} transform input rows must be JSON objects.");
        }

        var columns = first.EnumerateObject().Select(property => property.Name).ToArray();
        if (columns.Length == 0 ||
            columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != columns.Length ||
            columns.Any(string.IsNullOrWhiteSpace) ||
            columns.Any(name => string.Equals(name, ParentIndexColumn, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, ParentIndicesColumn, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                $"{connectorType} transform input rows must have distinct field names and must not use reserved lineage columns.");
        }

        var expectedColumns = columns.ToHashSet(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (record.ValueKind != JsonValueKind.Object ||
                !record.EnumerateObject().Select(property => property.Name)
                    .ToHashSet(StringComparer.Ordinal).SetEquals(expectedColumns))
            {
                throw new InvalidDataException($"{connectorType} transform input rows must all have the same fields.");
            }
        }
        return columns;
    }

    private static void CreateInputTable(
        DbConnection connection,
        string connectorType,
        IReadOnlyList<string> columns,
        IReadOnlyList<JsonElement> records,
        int commandTimeoutSeconds)
    {
        var columnTypes = connectorType == "sqlite"
            ? columns.ToDictionary(column => column, _ => string.Empty, StringComparer.Ordinal)
            : columns.ToDictionary(
                column => column,
                column => InferDuckDbColumnType(records, column),
                StringComparer.Ordinal);
        using var transaction = connection.BeginTransaction();
        using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandTimeout = commandTimeoutSeconds;
            create.CommandText =
                $"CREATE TEMP TABLE {QuoteIdentifier(InputTable)} (" +
                $"{QuoteIdentifier(ParentIndexColumn)} {(connectorType == "sqlite" ? "INTEGER" : "BIGINT")} NOT NULL, " +
                $"{string.Join(", ", columns.Select(column =>
                    $"{QuoteIdentifier(column)}{(string.IsNullOrEmpty(columnTypes[column]) ? string.Empty : $" {columnTypes[column]}")}"))})";
            create.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandTimeout = commandTimeoutSeconds;
            var allColumns = new[] { ParentIndexColumn }.Concat(columns).ToArray();
            var parameterNames = allColumns
                .Select((_, index) => connectorType == "sqlite" ? $"@p{index}" : "?")
                .ToArray();
            insert.CommandText =
                $"INSERT INTO {QuoteIdentifier(InputTable)} " +
                $"({string.Join(", ", allColumns.Select(QuoteIdentifier))}) " +
                $"VALUES ({string.Join(", ", parameterNames)})";
            var commandParameters = allColumns.Select((_, index) =>
            {
                var parameter = insert.CreateParameter();
                parameter.ParameterName = connectorType == "sqlite"
                    ? parameterNames[index]
                    : (index + 1).ToString(CultureInfo.InvariantCulture);
                insert.Parameters.Add(parameter);
                return parameter;
            }).ToArray();
            insert.Prepare();

            for (var rowIndex = 0; rowIndex < records.Count; rowIndex++)
            {
                commandParameters[0].Value = rowIndex;
                for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
                {
                    commandParameters[columnIndex + 1].Value = DatabaseConnectorSupport.ToDatabaseValue(
                        records[rowIndex].GetProperty(columns[columnIndex]),
                        connectorType,
                        columnTypes[columns[columnIndex]]);
                }
                insert.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    private static string InferDuckDbColumnType(IReadOnlyList<JsonElement> records, string column)
    {
        var values = records
            .Select(record => record.GetProperty(column))
            .Where(value => value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            .ToArray();
        if (values.Length == 0)
        {
            return "VARCHAR";
        }

        if (values.All(value => value.ValueKind is JsonValueKind.True or JsonValueKind.False))
        {
            return "BOOLEAN";
        }
        if (values.All(value => value.ValueKind == JsonValueKind.Number))
        {
            return values.All(value => value.TryGetInt64(out _)) ? "BIGINT" : "DOUBLE";
        }
        return "VARCHAR";
    }

    private static void AddDuckDbPositionalParameters(
        DbCommand command,
        IReadOnlyDictionary<string, JsonElement> parameters)
    {
        foreach (var value in parameters.Values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = (command.Parameters.Count + 1).ToString(CultureInfo.InvariantCulture);
            parameter.Value = DatabaseConnectorSupport.ToDatabaseValue(value, "duckdb", string.Empty);
            command.Parameters.Add(parameter);
        }
    }

    private static int[] ReadParentIndices(object value, string connectorType)
    {
        if (value is DBNull)
        {
            return Array.Empty<int>();
        }

        if (value is string json)
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    $"{connectorType} transform column '{ParentIndicesColumn}' must contain a JSON array of input indexes.");
            }
            return document.RootElement.EnumerateArray()
                .Select(index => index.GetInt32())
                .ToArray();
        }

        if (value is System.Collections.IEnumerable indices)
        {
            return indices.Cast<object?>()
                .Select(index => Convert.ToInt32(index, CultureInfo.InvariantCulture))
                .ToArray();
        }

        throw new InvalidDataException(
            $"{connectorType} transform column '{ParentIndicesColumn}' must contain a JSON array of input indexes.");
    }

    private static void DropInputTable(DbConnection connection, int commandTimeoutSeconds)
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = $"DROP TABLE {QuoteIdentifier(InputTable)}";
        command.ExecuteNonQuery();
    }

    private static string QuoteIdentifier(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private sealed record PreparedTransformOutput(int ParentIndex, JsonElement Record);

    private sealed record SetBasedTransformOutput(int[] ParentIndices, JsonElement Record);
}
