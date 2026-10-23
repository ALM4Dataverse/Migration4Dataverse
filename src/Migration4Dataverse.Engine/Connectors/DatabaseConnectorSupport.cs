using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace Migration4Dataverse.Engine.Connectors;

internal static class DatabaseConnectorSupport
{
    public static void ValidateConnection(string type, DbConnection connection, string description)
    {
        var connectionType = connection.GetType().FullName ?? connection.GetType().Name;
        var valid = type switch
        {
            "sqlite" => connectionType.Contains("SqliteConnection", StringComparison.OrdinalIgnoreCase),
            "duckdb" => connectionType.Contains("DuckDBConnection", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
        if (!valid)
        {
            throw new ArgumentException(
                $"Database {description} requires a {type} ADO.NET connection; received '{connectionType}'.",
                nameof(connection));
        }
    }

    public static string QuoteTable(string type, string tableName)
    {
        var parts = tableName.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Database table names must be a table name or a schema-qualified table name.",
                nameof(tableName));
        }

        var quote = type is "sqlite" or "duckdb"
            ? "\""
            : throw new ArgumentOutOfRangeException(nameof(type));
        return string.Join(".", parts.Select(part => $"{quote}{part.Replace(quote, quote + quote, StringComparison.Ordinal)}{quote}"));
    }

    public static (string Schema, string Table) SplitTableName(string type, string tableName)
    {
        var parts = tableName.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Database table names must be a table name or a schema-qualified table name.",
                nameof(tableName));
        }

        return parts.Length == 1
            ? (type == "sqlite" ? "main" : "main", parts[0])
            : (parts[0], parts[1]);
    }

    public static object? ToJsonValue(object value)
    {
        if (value is DBNull)
        {
            return null;
        }
        if (value is DateOnly dateOnly)
        {
            return dateOnly.ToString("O", CultureInfo.InvariantCulture);
        }
        if (value is TimeOnly timeOnly)
        {
            return timeOnly.ToString("O", CultureInfo.InvariantCulture);
        }
        if (value is TimeSpan timeSpan)
        {
            return timeSpan.ToString("c", CultureInfo.InvariantCulture);
        }
        if (value is byte[] bytes)
        {
            return Convert.ToBase64String(bytes);
        }
        if (value is System.Numerics.BigInteger bigInteger)
        {
            return bigInteger.ToString(CultureInfo.InvariantCulture);
        }
        return value;
    }

    public static object ToDatabaseValue(JsonElement value, string connectorType, string sqlType)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return DBNull.Value;
        }

        var normalizedType = NormalizeSqlType(sqlType);
        if (normalizedType.StartsWith("BOOLEAN", StringComparison.Ordinal) ||
            normalizedType.StartsWith("BOOL", StringComparison.Ordinal))
        {
            return value.ValueKind == JsonValueKind.True ||
                (value.ValueKind == JsonValueKind.String &&
                 bool.TryParse(value.GetString(), out var boolean) && boolean);
        }
        if (normalizedType.StartsWith("BLOB", StringComparison.Ordinal) ||
            normalizedType.StartsWith("BYTEA", StringComparison.Ordinal))
        {
            return Convert.FromBase64String(value.GetString()!);
        }
        if (normalizedType.StartsWith("DATE", StringComparison.Ordinal) &&
            value.ValueKind == JsonValueKind.String &&
            DateOnly.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var dateOnly))
        {
            return dateOnly.ToDateTime(TimeOnly.MinValue);
        }
        if ((normalizedType.StartsWith("TIMESTAMP", StringComparison.Ordinal) ||
             normalizedType.StartsWith("DATETIME", StringComparison.Ordinal)) &&
            value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateTime))
        {
            return dateTime;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => connectorType == "sqlite" ? 1L : true,
            JsonValueKind.False => connectorType == "sqlite" ? 0L : false,
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when value.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.Array or JsonValueKind.Object => value.GetRawText(),
            _ => throw new InvalidDataException($"Unsupported JSON value kind '{value.ValueKind}'.")
        };
    }

    public static string NormalizeSqlType(string value)
    {
        return value.Trim().ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    public static string NormalizeSchemaType(string connectorType, string value)
    {
        var normalized = NormalizeSqlType(value);
        if (connectorType == "duckdb")
        {
            return normalized switch
            {
                "TEXT" or "STRING" => "VARCHAR",
                "INT" or "INT4" => "INTEGER",
                "INT8" => "BIGINT",
                "FLOAT4" => "FLOAT",
                "FLOAT8" => "DOUBLE",
                "TIMESTAMPTZ" => "TIMESTAMPWITHTIMEZONE",
                _ => normalized
            };
        }
        if (connectorType == "sqlite")
        {
            return normalized switch
            {
                "VARCHAR" or "CHAR" or "CLOB" => "TEXT",
                "INT" or "INT2" or "INT8" or "SMALLINT" or "MEDIUMINT" or "BIGINT" => "INTEGER",
                "FLOAT" or "DOUBLE" => "REAL",
                "DECIMAL" => "NUMERIC",
                _ => normalized
            };
        }
        throw new ArgumentOutOfRangeException(nameof(connectorType));
    }

    public static string InferSqlType(string connectorType, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
        {
            return connectorType == "sqlite" ? "INTEGER" : "BOOLEAN";
        }
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out _))
            {
                return connectorType == "sqlite" ? "INTEGER" : "BIGINT";
            }
            return connectorType == "sqlite" ? "REAL" : "DOUBLE";
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            return connectorType == "sqlite" ? "TEXT" : "VARCHAR";
        }
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null)
        {
            return connectorType == "sqlite" ? "TEXT" : "VARCHAR";
        }
        throw new InvalidDataException($"Cannot infer a database column type from '{value.ValueKind}'.");
    }

    public static string ValidateSqlType(string connectorType, string name, string sqlType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlType);
        var normalized = NormalizeSqlType(sqlType);
        var allowed = connectorType == "sqlite"
            ? new HashSet<string>(StringComparer.Ordinal)
            {
                "INTEGER", "REAL", "TEXT", "BLOB", "NUMERIC"
            }
            : connectorType == "duckdb"
                ? new HashSet<string>(StringComparer.Ordinal)
                {
                    "BOOLEAN", "TINYINT", "SMALLINT", "INTEGER", "BIGINT", "UTINYINT",
                    "USMALLINT", "UINTEGER", "UBIGINT", "FLOAT", "REAL", "DOUBLE",
                    "DECIMAL", "DECIMAL(38,10)", "VARCHAR", "TEXT", "BLOB", "DATE",
                    "TIMESTAMP", "TIMESTAMPWITHTIMEZONE", "TIMESTAMPTZ", "UUID"
                }
                : throw new ArgumentOutOfRangeException(nameof(connectorType));
        if (!allowed.Contains(normalized))
        {
            throw new ArgumentException(
                $"Column '{name}' uses unsupported {connectorType} type '{sqlType}'.",
                nameof(sqlType));
        }
        return sqlType.Trim().ToUpperInvariant();
    }
}
