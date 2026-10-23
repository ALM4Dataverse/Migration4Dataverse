using System.Data;
using System.Data.Common;
using System.Text.Json;

namespace Migration4Dataverse.Engine.Connectors;

public static class PreparedDatabaseTransformRunner
{
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

    private sealed record PreparedTransformOutput(int ParentIndex, JsonElement Record);
}
