using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace Migration4Dataverse.Engine.Connectors;

internal static class DatabaseConnectorSupport
{
    private static readonly HashSet<string> SqlServerTypes = new(StringComparer.Ordinal)
    {
        "BIT", "TINYINT", "SMALLINT", "INT", "BIGINT", "DECIMAL", "NUMERIC", "MONEY",
        "SMALLMONEY", "FLOAT", "REAL", "DATE", "TIME", "DATETIME", "DATETIME2",
        "DATETIMEOFFSET", "SMALLDATETIME", "CHAR", "VARCHAR", "NCHAR", "NVARCHAR",
        "TEXT", "NTEXT", "BINARY", "VARBINARY", "IMAGE", "UNIQUEIDENTIFIER", "XML"
    };

    private static readonly HashSet<string> MySqlTypes = new(StringComparer.Ordinal)
    {
        "BOOLEAN", "BOOL", "BIT", "TINYINT", "SMALLINT", "MEDIUMINT", "INT", "INTEGER",
        "BIGINT", "DECIMAL", "NUMERIC", "FLOAT", "DOUBLE", "DATE", "DATETIME", "TIMESTAMP",
        "TIME", "YEAR", "CHAR", "VARCHAR", "TINYTEXT", "TEXT", "MEDIUMTEXT", "LONGTEXT",
        "BINARY", "VARBINARY", "TINYBLOB", "BLOB", "MEDIUMBLOB", "LONGBLOB", "JSON"
    };

    private static readonly HashSet<string> OracleTypes = new(StringComparer.Ordinal)
    {
        "BOOLEAN", "NUMBER", "FLOAT", "BINARYFLOAT", "BINARYDOUBLE", "DATE", "TIMESTAMP",
        "TIMESTAMPWITHTIMEZONE", "TIMESTAMPWITHLOCALTIMEZONE", "CHAR", "NCHAR", "VARCHAR2",
        "NVARCHAR2", "CLOB", "NCLOB", "BLOB", "RAW", "LONG", "LONGRAW", "ROWID", "XMLTYPE",
        "INTERVALDAYTOSECOND", "INTERVALYEARTOMONT"
    };

    private static readonly HashSet<string> PostgreSqlTypes = new(StringComparer.Ordinal)
    {
        "BOOLEAN", "SMALLINT", "INTEGER", "BIGINT", "REAL", "DOUBLEPRECISION", "NUMERIC",
        "DECIMAL", "VARCHAR", "TEXT", "CHAR", "BYTEA", "DATE", "TIME", "TIMESTAMP",
        "TIMESTAMPTZ", "UUID", "JSON", "JSONB", "SERIAL", "BIGSERIAL"
    };

    public static bool IsDatabaseConnectorType(string type)
    {
        return type is "sqlite" or "duckdb" or "sqlserver" or "postgresql" or "mysql" or "oracle";
    }

    public static bool IsFileDatabaseConnectorType(string type)
    {
        return type is "sqlite" or "duckdb";
    }

    public static bool IsSqlServer(string type)
    {
        return string.Equals(type, "sqlserver", StringComparison.Ordinal);
    }

    public static bool IsPostgreSql(string type)
    {
        return string.Equals(type, "postgresql", StringComparison.Ordinal);
    }

    public static bool IsMySql(string type)
    {
        return string.Equals(type, "mysql", StringComparison.Ordinal);
    }

    public static bool IsOracle(string type)
    {
        return string.Equals(type, "oracle", StringComparison.Ordinal);
    }

    private static bool HasConnectionType(string connectionType, string expectedType)
    {
        return string.Equals(connectionType, expectedType, StringComparison.Ordinal) ||
            connectionType.EndsWith($".{expectedType}", StringComparison.Ordinal);
    }

    public static string GetConnectorType(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var connectionType = connection.GetType().FullName ?? connection.GetType().Name;
        if (connectionType.Contains("SqliteConnection", StringComparison.OrdinalIgnoreCase))
        {
            return "sqlite";
        }
        if (connectionType.Contains("DuckDBConnection", StringComparison.OrdinalIgnoreCase))
        {
            return "duckdb";
        }
        if (connectionType.Contains("NpgsqlConnection", StringComparison.OrdinalIgnoreCase))
        {
            return "postgresql";
        }
        if (connectionType.Contains("MySqlConnection", StringComparison.OrdinalIgnoreCase))
        {
            return "mysql";
        }
        if (connectionType.Contains("OracleConnection", StringComparison.OrdinalIgnoreCase))
        {
            return "oracle";
        }
        if (connectionType.Contains("SqlConnection", StringComparison.OrdinalIgnoreCase))
        {
            return "sqlserver";
        }

        throw new ArgumentException(
            $"Unsupported database connection type '{connectionType}'.",
            nameof(connection));
    }

    public static void ValidateConnection(string type, DbConnection connection, string description)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var connectionType = connection.GetType().FullName ?? connection.GetType().Name;
        var valid = type switch
        {
            "sqlite" => HasConnectionType(connectionType, "SqliteConnection"),
            "duckdb" => HasConnectionType(connectionType, "DuckDBConnection"),
            "sqlserver" => HasConnectionType(connectionType, "SqlConnection"),
            "postgresql" => HasConnectionType(connectionType, "NpgsqlConnection"),
            "mysql" => HasConnectionType(connectionType, "MySqlConnection"),
            "oracle" => HasConnectionType(connectionType, "OracleConnection"),
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
        var parts = SplitTableName(type, tableName);
        return string.IsNullOrEmpty(parts.Schema)
            ? QuoteIdentifier(type, parts.Table)
            : $"{QuoteIdentifier(type, parts.Schema)}.{QuoteIdentifier(type, parts.Table)}";
    }

    public static (string Schema, string Table) SplitTableName(string type, string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        var parts = tableName.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Database table names must be a table name or a schema-qualified table name.",
                nameof(tableName));
        }

        var defaultSchema = type switch
        {
            "sqlite" or "duckdb" => "main",
            "postgresql" => "public",
            "sqlserver" => "dbo",
            "mysql" => string.Empty,
            "oracle" => string.Empty,
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
        return parts.Length == 1 ? (defaultSchema, parts[0]) : (parts[0], parts[1]);
    }

    public static string QuoteIdentifier(string type, string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return type switch
        {
            "sqlserver" => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]",
            "mysql" => $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`",
            "oracle" => $"\"{identifier.ToUpperInvariant().Replace("\"", "\"\"", StringComparison.Ordinal)}\"",
            _ => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
        };
    }

    public static string ParameterName(string connectorType, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (string.Equals(connectorType, "duckdb", StringComparison.Ordinal))
        {
            return "?";
        }
        if (IsOracle(connectorType))
        {
            return name[0] is ':' or '@' ? name : $":{name}";
        }
        return name[0] is '@' or ':' or '$' ? name : $"@{name}";
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
        var baseType = GetBaseType(normalizedType);
        if (baseType is "BOOLEAN" or "BOOL")
        {
            return value.ValueKind == JsonValueKind.True ||
                (value.ValueKind == JsonValueKind.String &&
                 bool.TryParse(value.GetString(), out var boolean) && boolean);
        }
        if (baseType is "BLOB" or "BYTEA" or "BINARY" or "VARBINARY" or "IMAGE")
        {
            return value.ValueKind == JsonValueKind.String
                ? Convert.FromBase64String(value.GetString()!)
                : throw new InvalidDataException($"A {baseType} column requires a base64 string.");
        }
        if (baseType is "UNIQUEIDENTIFIER" or "UUID")
        {
            return value.ValueKind == JsonValueKind.String
                ? Guid.Parse(value.GetString()!)
                : throw new InvalidDataException($"A {baseType} column requires a GUID string.");
        }
        if (baseType is "JSON" or "JSONB")
        {
            return value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
        }
        if (baseType == "DATE" &&
            value.ValueKind == JsonValueKind.String &&
            DateOnly.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var dateOnly))
        {
            return dateOnly.ToDateTime(TimeOnly.MinValue);
        }
        if ((baseType is "TIMESTAMP" or "TIMESTAMPTZ" or "DATETIME" or "DATETIME2" or
             "DATETIMEOFFSET" or "SMALLDATETIME" or "TIME") &&
            value.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var dateTime))
        {
            return baseType is "TIMESTAMPTZ" or "DATETIMEOFFSET"
                ? dateTime
                : dateTime.UtcDateTime;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => connectorType is "sqlite" or "oracle" ? 1L : true,
            JsonValueKind.False => connectorType is "sqlite" or "oracle" ? 0L : false,
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when value.TryGetDecimal(out var decimalValue) => decimalValue,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.Array or JsonValueKind.Object => value.GetRawText(),
            _ => throw new InvalidDataException($"Unsupported JSON value kind '{value.ValueKind}'.")
        };
    }

    public static void AddQueryParameters(
        DbCommand command,
        string connectorType,
        IReadOnlyDictionary<string, JsonElement>? parameters)
    {
        if (parameters is null)
        {
            return;
        }

        foreach (var (name, value) in parameters)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            var parameter = command.CreateParameter();
            parameter.ParameterName = string.Equals(connectorType, "duckdb", StringComparison.Ordinal)
                ? (command.Parameters.Count + 1).ToString(CultureInfo.InvariantCulture)
                : IsOracle(connectorType)
                    ? name.TrimStart('@', ':')
                    : ParameterName(connectorType, name);
            parameter.Value = ToDatabaseValue(value, connectorType, string.Empty);
            command.Parameters.Add(parameter);
        }
    }

    public static string NormalizeSqlType(string value)
    {
        return value.Trim().ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    public static string GetBaseType(string value)
    {
        var normalized = NormalizeSqlType(value);
        var parenthesis = normalized.IndexOf('(');
        return parenthesis < 0 ? normalized : normalized[..parenthesis];
    }

    public static string NormalizeSchemaType(string connectorType, string value)
    {
        var normalized = NormalizeSqlType(value);
        if (string.Equals(connectorType, "duckdb", StringComparison.Ordinal))
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
        if (string.Equals(connectorType, "sqlite", StringComparison.Ordinal))
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
        if (IsSqlServer(connectorType))
        {
            var (baseType, arguments) = SplitTypeArguments(normalized);
            var canonicalBase = baseType switch
            {
                "INTEGER" => "INT",
                "BOOL" => "BIT",
                "CHARACTERVARYING" => "VARCHAR",
                "NATIONALCHARACTERVARYING" => "NVARCHAR",
                "NATIONALCHAR" => "NCHAR",
                "DOUBLEPRECISION" => "FLOAT",
                _ => baseType
            };
            return arguments is null ? canonicalBase : $"{canonicalBase}({arguments})";
        }
        if (IsOracle(connectorType))
        {
            if (normalized.StartsWith("INTERVAL", StringComparison.Ordinal))
            {
                return normalized;
            }
            if (normalized.Contains("WITHTIMEZONE", StringComparison.Ordinal))
            {
                return "TIMESTAMPWITHTIMEZONE";
            }
            if (normalized.Contains("WITHLOCALTIMEZONE", StringComparison.Ordinal))
            {
                return "TIMESTAMPWITHLOCALTIMEZONE";
            }
            var (baseType, arguments) = SplitTypeArguments(normalized);
            var canonicalBase = baseType switch
            {
                "VARCHAR" => "VARCHAR2",
                "NVARCHAR" => "NVARCHAR2",
                "INT" or "INTEGER" or "BIGINT" or "SMALLINT" or "TINYINT" or "DECIMAL" or "NUMERIC" =>
                    "NUMBER",
                "DOUBLE" or "DOUBLEPRECISION" => "BINARYDOUBLE",
                "REAL" => "BINARYFLOAT",
                "BOOL" => "BOOLEAN",
                _ => baseType
            };
            if (canonicalBase == "TIMESTAMP" && arguments is null)
            {
                return "TIMESTAMP(6)";
            }
            return arguments is null ? canonicalBase : $"{canonicalBase}({arguments})";
        }
        if (IsMySql(connectorType))
        {
            var (baseType, arguments) = SplitTypeArguments(normalized);
            var canonicalBase = baseType switch
            {
                "BOOL" or "BOOLEAN" => "TINYINT",
                "INTEGER" => "INT",
                "DECIMAL" => "DECIMAL",
                "NUMERIC" => "DECIMAL",
                "DOUBLEPRECISION" => "DOUBLE",
                "CHARACTERVARYING" => "VARCHAR",
                "CHARACTER" => "CHAR",
                _ => baseType
            };
            if (canonicalBase is "TINYINT" or "SMALLINT" or "MEDIUMINT" or "INT" or "BIGINT" or
                "BIT" or "YEAR")
            {
                return canonicalBase;
            }
            return arguments is null ? canonicalBase : $"{canonicalBase}({arguments})";
        }
        if (IsPostgreSql(connectorType))
        {
            var (baseType, arguments) = SplitTypeArguments(normalized);
            var canonicalBase = baseType switch
            {
                "BOOL" => "BOOLEAN",
                "INT" or "INT4" => "INTEGER",
                "INT8" => "BIGINT",
                "INT2" => "SMALLINT",
                "CHARACTERVARYING" => "VARCHAR",
                "CHARACTER" => "CHAR",
                "FLOAT4" => "REAL",
                "FLOAT8" or "DOUBLE" => "DOUBLEPRECISION",
                "DECIMAL" => "NUMERIC",
                "TIMESTAMPWITHOUTTIMEZONE" => "TIMESTAMP",
                "TIMESTAMPWITHTIMEZONE" => "TIMESTAMPTZ",
                "TIME" => "TIME",
                "TIMETZ" => "TIMETZ",
                _ => baseType
            };
            return arguments is null ? canonicalBase : $"{canonicalBase}({arguments})";
        }
        throw new ArgumentOutOfRangeException(nameof(connectorType));
    }

    public static string InferSqlType(string connectorType, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return connectorType switch
            {
                "sqlite" => "INTEGER",
                "sqlserver" => "BIT",
                "mysql" => "TINYINT(1)",
                "oracle" => "NUMBER(1)",
                _ => "BOOLEAN"
            };
        }
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out _))
            {
                return string.Equals(connectorType, "sqlite", StringComparison.Ordinal) ? "INTEGER" : "BIGINT";
            }
            return connectorType switch
            {
                "sqlite" => "REAL",
                "sqlserver" => "FLOAT",
                "mysql" => "DOUBLE",
                "oracle" => "BINARYDOUBLE",
                _ => "DOUBLE PRECISION"
            };
        }
        if (value.ValueKind is JsonValueKind.String or JsonValueKind.Object or
            JsonValueKind.Array or JsonValueKind.Null)
        {
            return connectorType switch
            {
                "sqlite" => "TEXT",
                "sqlserver" => "NVARCHAR(MAX)",
                "mysql" => "TEXT",
                "oracle" => "CLOB",
                _ => "TEXT"
            };
        }
        throw new InvalidDataException($"Cannot infer a database column type from '{value.ValueKind}'.");
    }

    public static string ValidateSqlType(string connectorType, string name, string sqlType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlType);
        var normalized = NormalizeSqlType(sqlType);
        var baseType = GetBaseType(normalized);
        var allowed = string.Equals(connectorType, "sqlite", StringComparison.Ordinal)
            ? new HashSet<string>(StringComparer.Ordinal)
            {
                "INTEGER", "REAL", "TEXT", "BLOB", "NUMERIC"
            }
            : string.Equals(connectorType, "duckdb", StringComparison.Ordinal)
                ? new HashSet<string>(StringComparer.Ordinal)
                {
                    "BOOLEAN", "TINYINT", "SMALLINT", "INTEGER", "BIGINT", "UTINYINT",
                    "USMALLINT", "UINTEGER", "UBIGINT", "FLOAT", "REAL", "DOUBLE",
                    "DECIMAL", "DECIMAL(38,10)", "VARCHAR", "TEXT", "BLOB", "DATE",
                    "TIMESTAMP", "TIMESTAMPWITHTIMEZONE", "TIMESTAMPTZ", "UUID"
                }
                : IsSqlServer(connectorType)
                    ? SqlServerTypes
                    : IsPostgreSql(connectorType)
                        ? PostgreSqlTypes
                        : IsMySql(connectorType)
                            ? MySqlTypes
                            : IsOracle(connectorType)
                                ? OracleTypes
                                : throw new ArgumentOutOfRangeException(nameof(connectorType));
        var isOracleInterval = IsOracle(connectorType) &&
            ((normalized.StartsWith("INTERVALDAY", StringComparison.Ordinal) &&
              normalized.Contains("TOSECOND", StringComparison.Ordinal)) ||
             (normalized.StartsWith("INTERVALYEAR", StringComparison.Ordinal) &&
              normalized.Contains("TOMONT", StringComparison.Ordinal)));
        if (!allowed.Contains(normalized) && !allowed.Contains(baseType) && !isOracleInterval)
        {
            throw new ArgumentException(
                $"Column '{name}' uses unsupported {connectorType} type '{sqlType}'.",
                nameof(sqlType));
        }
        if (!isOracleInterval)
        {
            ValidateTypeArguments(connectorType, name, normalized);
        }
        return normalized;
    }

    public static string GetSchemaQuery(string connectorType, string schemaName, string tableName)
    {
        if (string.Equals(connectorType, "sqlite", StringComparison.Ordinal))
        {
            return $"PRAGMA {QuoteIdentifier(connectorType, schemaName)}.table_info(" +
                $"{QuoteIdentifier(connectorType, tableName)})";
        }

        if (IsOracle(connectorType))
        {
            var owner = string.IsNullOrEmpty(schemaName)
                ? "SYS_CONTEXT('USERENV','CURRENT_SCHEMA')"
                : QuoteLiteral(schemaName.ToUpperInvariant());
            return "SELECT column_name, data_type, data_length, data_precision, data_scale " +
                "FROM all_tab_columns " +
                $"WHERE owner = {owner} AND table_name = {QuoteLiteral(tableName.ToUpperInvariant())} " +
                "ORDER BY column_id";
        }

        var extraColumns = IsSqlServer(connectorType)
            ? ", character_maximum_length, numeric_precision, numeric_scale, datetime_precision"
            : ", character_maximum_length, numeric_precision, numeric_scale";
        var schemaPredicate = IsMySql(connectorType) && string.IsNullOrEmpty(schemaName)
            ? "DATABASE()"
            : QuoteLiteral(schemaName);
        return "SELECT column_name, data_type" + extraColumns +
            " FROM information_schema.columns " +
            $"WHERE table_schema = {schemaPredicate} " +
            $"AND table_name = {QuoteLiteral(tableName)} ORDER BY ordinal_position";
    }

    public static (string Name, string SqlType) ReadSchemaColumn(
        string connectorType,
        DbDataReader reader)
    {
        if (string.Equals(connectorType, "sqlite", StringComparison.Ordinal))
        {
            return (reader.GetString(1), NormalizeSqlType(reader.GetString(2)));
        }

        var name = reader.GetString(0);
        var dataType = reader.GetString(1);
        long? maxLength = reader.IsDBNull(2) ? null : Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
        int? precision = reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture);
        int? scale = reader.IsDBNull(4) ? null : Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture);
        return (name, BuildSchemaType(connectorType, dataType, maxLength, precision, scale));
    }

    public static string QuoteLiteral(string value)
    {
        return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    internal static string BuildSchemaType(
        string connectorType,
        string dataType,
        long? maxLength,
        int? precision,
        int? scale)
    {
        var normalized = NormalizeSqlType(dataType);
        if (IsSqlServer(connectorType))
        {
            var baseType = normalized switch
            {
                "INT" => "INT",
                "BIT" => "BIT",
                "VARCHAR" => "VARCHAR",
                "NVARCHAR" => "NVARCHAR",
                "CHAR" => "CHAR",
                "NCHAR" => "NCHAR",
                "VARBINARY" => "VARBINARY",
                "BINARY" => "BINARY",
                "DECIMAL" => "DECIMAL",
                "NUMERIC" => "NUMERIC",
                "DATETIME2" => "DATETIME2",
                "DATETIMEOFFSET" => "DATETIMEOFFSET",
                "TIME" => "TIME",
                _ => normalized
            };
            return baseType switch
            {
                "VARCHAR" or "NVARCHAR" or "CHAR" or "NCHAR" or "VARBINARY" or "BINARY" =>
                    maxLength == -1 ? $"{baseType}(MAX)" : maxLength is null ? baseType : $"{baseType}({maxLength})",
                "DECIMAL" or "NUMERIC" when precision is not null && scale is not null =>
                    $"{baseType}({precision},{scale})",
                _ => baseType
            };
        }

        if (IsOracle(connectorType))
        {
            if (normalized.StartsWith("INTERVAL", StringComparison.Ordinal))
            {
                return normalized;
            }
            if (normalized.Contains("WITHTIMEZONE", StringComparison.Ordinal))
            {
                return "TIMESTAMPWITHTIMEZONE";
            }
            if (normalized.Contains("WITHLOCALTIMEZONE", StringComparison.Ordinal))
            {
                return "TIMESTAMPWITHLOCALTIMEZONE";
            }
            var baseType = GetBaseType(normalized);
            return baseType switch
            {
                "VARCHAR2" or "NVARCHAR2" or "CHAR" or "NCHAR" or "RAW" when maxLength is not null =>
                    $"{baseType}({maxLength})",
                "NUMBER" when precision is not null && scale is not null =>
                    $"NUMBER({precision},{scale})",
                "NUMBER" when precision is not null => $"NUMBER({precision})",
                "TIMESTAMP" when normalized.Contains('(') => normalized,
                _ => baseType
            };
        }
        if (IsMySql(connectorType))
        {
            var baseType = normalized switch
            {
                "INTEGER" => "INT",
                "BOOL" => "BOOLEAN",
                "DECIMAL" => "DECIMAL",
                "NUMERIC" => "DECIMAL",
                "DOUBLEPRECISION" => "DOUBLE",
                "CHARACTERVARYING" => "VARCHAR",
                "CHARACTER" => "CHAR",
                _ => normalized
            };
            return baseType switch
            {
                "VARCHAR" or "CHAR" or "BINARY" or "VARBINARY" when maxLength is not null =>
                    $"{baseType}({maxLength})",
                "DECIMAL" when precision is not null && scale is not null =>
                    $"{baseType}({precision},{scale})",
                _ => baseType
            };
        }
        if (IsPostgreSql(connectorType))
        {
            var baseType = normalized switch
            {
                "CHARACTERVARYING" => "VARCHAR",
                "CHARACTER" => "CHAR",
                "INTEGER" => "INTEGER",
                "BIGINT" => "BIGINT",
                "SMALLINT" => "SMALLINT",
                "BOOLEAN" => "BOOLEAN",
                "REAL" => "REAL",
                "DOUBLEPRECISION" => "DOUBLEPRECISION",
                "NUMERIC" => "NUMERIC",
                "TIMESTAMPWITHTIMEZONE" => "TIMESTAMPTZ",
                "TIMESTAMPWITHOUTTIMEZONE" => "TIMESTAMP",
                "TIME" => "TIME",
                "TIMETZ" => "TIMETZ",
                "UUID" => "UUID",
                "BYTEA" => "BYTEA",
                "TEXT" => "TEXT",
                "JSONB" => "JSONB",
                "JSON" => "JSON",
                _ => normalized
            };
            return baseType switch
            {
                "VARCHAR" or "CHAR" when maxLength is not null => $"{baseType}({maxLength})",
                "NUMERIC" when precision is not null && scale is not null =>
                    $"{baseType}({precision},{scale})",
                _ => baseType
            };
        }

        return normalized;
    }

    private static (string BaseType, string? Arguments) SplitTypeArguments(string normalized)
    {
        var parenthesis = normalized.IndexOf('(');
        if (parenthesis < 0)
        {
            return (normalized, null);
        }
        if (!normalized.EndsWith(')'))
        {
            throw new ArgumentException($"SQL type '{normalized}' has an invalid argument list.");
        }
        return (normalized[..parenthesis], normalized[(parenthesis + 1)..^1]);
    }

    private static void ValidateTypeArguments(string connectorType, string name, string normalized)
    {
        if (IsOracle(connectorType) &&
            normalized.Contains("TIMEZONE", StringComparison.Ordinal))
        {
            return;
        }
        var parenthesis = normalized.IndexOf('(');
        if (parenthesis < 0)
        {
            return;
        }
        if (!normalized.EndsWith(')'))
        {
            throw new ArgumentException($"Column '{name}' has an invalid SQL type '{normalized}'.");
        }

        var arguments = normalized[(parenthesis + 1)..^1];
        if (string.IsNullOrWhiteSpace(arguments))
        {
            throw new ArgumentException($"Column '{name}' has an invalid SQL type '{normalized}'.");
        }
        foreach (var argument in arguments.Split(','))
        {
            var trimmed = argument.Trim();
            if (string.Equals(trimmed, "MAX", StringComparison.Ordinal) &&
                IsSqlServer(connectorType))
            {
                continue;
            }
            if (!int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
                value < 0)
            {
                throw new ArgumentException($"Column '{name}' has an invalid SQL type '{normalized}'.");
            }
        }
    }
}
