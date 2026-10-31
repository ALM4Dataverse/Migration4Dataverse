using System.IO.Compression;
using Parquet;
using Parquet.Schema;

namespace Migration4Dataverse.Engine.Connectors;

public sealed class ParquetConnectorOptions
{
    public string CompressionMethod { get; init; } = "Snappy";

    public string CompressionLevel { get; init; } = "Optimal";

    public int TargetRowGroupSize { get; init; } = 100_000;

    public int MaxSourceRowGroupRows { get; init; } = 250_000;

    public double DictionaryEncodingThreshold { get; init; } = 0.8;

    public int DictionaryEncodingSampleSize { get; init; } = 1_000;

    public bool TreatByteArrayAsString { get; init; }

    public bool TreatBigIntegersAsDates { get; init; }

    public bool UseDateOnlyTypeForDates { get; init; } = true;

    public string[]? Columns { get; init; }

    public string[]? BinaryColumns { get; init; }

    public Dictionary<string, ParquetFieldSchema[]>? SchemaByStep { get; init; }

    public Dictionary<string, ParquetFieldSchema[]>? SourceSchemaByStep { get; init; }

    internal ParquetOptions CreateParquetOptions(bool forWriting)
    {
        Validate();
        var options = new ParquetOptions
        {
            CompressionMethod = Enum.Parse<CompressionMethod>(CompressionMethod, ignoreCase: true),
            CompressionLevel = Enum.Parse<System.IO.Compression.CompressionLevel>(
                CompressionLevel,
                ignoreCase: true),
            DictionaryEncodingThreshold = DictionaryEncodingThreshold,
            DictionaryEncodingSampleSize = DictionaryEncodingSampleSize,
            TreatByteArrayAsString = TreatByteArrayAsString,
            TreatBigIntegersAsDates = TreatBigIntegersAsDates,
            UseDateOnlyTypeForDates = UseDateOnlyTypeForDates
        };
        if (forWriting)
        {
            options.RowGroupSize = TargetRowGroupSize;
        }

        return options;
    }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(CompressionMethod);
        ArgumentException.ThrowIfNullOrWhiteSpace(CompressionLevel);
        if (!Enum.TryParse<Parquet.CompressionMethod>(CompressionMethod, ignoreCase: true, out _))
        {
            throw new ArgumentException($"Parquet compression method '{CompressionMethod}' is not supported.");
        }
        if (!Enum.TryParse<System.IO.Compression.CompressionLevel>(
                CompressionLevel,
                ignoreCase: true,
                out _))
        {
            throw new ArgumentException($"Parquet compression level '{CompressionLevel}' is not supported.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TargetRowGroupSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxSourceRowGroupRows);
        ArgumentOutOfRangeException.ThrowIfNegative(DictionaryEncodingSampleSize);
        if (DictionaryEncodingThreshold is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DictionaryEncodingThreshold),
                "The Parquet dictionary encoding threshold must be between zero and one.");
        }
        if (Columns is not null)
        {
            ValidateNames(Columns, nameof(Columns));
        }
        if (BinaryColumns is not null)
        {
            ValidateNames(BinaryColumns, nameof(BinaryColumns));
        }
        ValidateSchemas(SchemaByStep, "SchemaByStep");
        ValidateSchemas(SourceSchemaByStep, "SourceSchemaByStep");
    }

    internal IReadOnlyList<ParquetFieldSchema> GetSchema(string jobName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        if (SchemaByStep is null ||
            !SchemaByStep.TryGetValue(jobName, out var schema) ||
            schema is null ||
            schema.Length == 0)
        {
            throw new InvalidOperationException(
                $"Parquet target options must declare an explicit SchemaByStep entry for '{jobName}'.");
        }

        return schema;
    }

    internal IReadOnlyList<ParquetFieldSchema> GetSourceSchema(string jobName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        if (SourceSchemaByStep is null ||
            !SourceSchemaByStep.TryGetValue(jobName, out var schema) ||
            schema is null ||
            schema.Length == 0)
        {
            throw new InvalidOperationException(
                $"Parquet source options must declare an explicit SourceSchemaByStep entry for '{jobName}' when converting fixture data.");
        }

        return schema;
    }

    private static void ValidateSchemas(
        IReadOnlyDictionary<string, ParquetFieldSchema[]>? schemas,
        string propertyName)
    {
        if (schemas is null)
        {
            return;
        }

        foreach (var (jobName, schema) in schemas)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
            if (schema is null || schema.Length == 0)
            {
                throw new ArgumentException($"Parquet option '{propertyName}.{jobName}' must declare an explicit schema.");
            }
            ValidateNames(schema.Select(field => field.Name).ToArray(), $"{propertyName}.{jobName}");
            foreach (var field in schema)
            {
                field.Validate();
            }
        }
    }

    internal static DataField CreateDataField(ParquetFieldSchema field)
    {
        field.Validate();
        var type = field.Type.Trim().ToLowerInvariant();
        return type switch
        {
            "decimal" => new DecimalDataField(
                field.Name,
                field.Precision,
                field.Scale,
                forceByteArrayEncoding: false,
                isNullable: field.Nullable),
            "datetime" => new DateTimeDataField(
                field.Name,
                Enum.Parse<DateTimeFormat>(field.DateTimeFormat, ignoreCase: true),
                field.IsAdjustedToUtc,
                Enum.Parse<DateTimeTimeUnit>(field.DateTimeUnit, ignoreCase: true),
                isNullable: field.Nullable),
            _ => new DataField(field.Name, ResolveClrType(type), isNullable: field.Nullable, isArray: false)
        };
    }

    internal static object? ConvertJsonValue(
        System.Text.Json.JsonElement value,
        ParquetFieldSchema field)
    {
        if (value.ValueKind is System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined)
        {
            if (!field.Nullable)
            {
                throw new InvalidDataException($"Parquet field '{field.Name}' is not nullable but received null.");
            }

            return null;
        }

        var type = field.Type.Trim().ToLowerInvariant();
        try
        {
            return type switch
            {
                "string" or "json" => value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? value.GetString()
                    : value.GetRawText(),
                "bool" or "boolean" => value.GetBoolean(),
                "byte" => value.GetByte(),
                "short" or "int16" => value.GetInt16(),
                "int" or "int32" => value.GetInt32(),
                "long" or "int64" => value.GetInt64(),
                "float" or "single" => value.GetSingle(),
                "double" => value.GetDouble(),
                "decimal" => value.GetDecimal(),
                "guid" => value.GetGuid(),
                "date" => value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? DateOnly.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
                    : throw new InvalidDataException($"Parquet field '{field.Name}' must contain an ISO date string."),
                "datetime" => value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? DateTime.Parse(
                        value.GetString()!,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind)
                    : throw new InvalidDataException($"Parquet field '{field.Name}' must contain an ISO date/time string."),
                "binary" => value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? Convert.FromBase64String(value.GetString()!)
                    : throw new InvalidDataException($"Parquet field '{field.Name}' must contain a base64 string."),
                _ => throw new InvalidDataException($"Parquet field '{field.Name}' uses unsupported type '{field.Type}'.")
            };
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                $"Parquet field '{field.Name}' contains a value that is not a valid {field.Type}.",
                exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException(
                $"Parquet field '{field.Name}' contains a value that is not a valid {field.Type}.",
                exception);
        }
    }

    internal static Type ResolveClrType(string type)
    {
        return type switch
        {
            "string" or "json" => typeof(string),
            "bool" or "boolean" => typeof(bool),
            "byte" => typeof(byte),
            "short" or "int16" => typeof(short),
            "int" or "int32" => typeof(int),
            "long" or "int64" => typeof(long),
            "float" or "single" => typeof(float),
            "double" => typeof(double),
            "guid" => typeof(Guid),
            "date" => typeof(DateOnly),
            "datetime" => typeof(DateTime),
            "binary" => typeof(byte[]),
            _ => throw new ArgumentException($"Parquet field type '{type}' is not supported.")
        };
    }

    private static void ValidateNames(IReadOnlyList<string> names, string propertyName)
    {
        var uniqueNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (!uniqueNames.Add(name))
            {
                throw new ArgumentException($"Parquet option '{propertyName}' contains duplicate field '{name}'.");
            }
        }
    }
}

public sealed class ParquetFieldSchema
{
    public string Name { get; init; } = string.Empty;

    public string Type { get; init; } = "string";

    public bool Nullable { get; init; } = true;

    public int Precision { get; init; } = 29;

    public int Scale { get; init; } = 10;

    public string DateTimeFormat { get; init; } = "Timestamp";

    public string DateTimeUnit { get; init; } = "Micros";

    public bool IsAdjustedToUtc { get; init; } = true;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(Type);
        var normalizedType = Type.Trim().ToLowerInvariant();
        if (normalizedType == "decimal")
        {
            if (Precision is < 1 or > 29)
            {
                throw new ArgumentOutOfRangeException(nameof(Precision), "Decimal precision must be between 1 and 29.");
            }
            if (Scale < 0 || Scale > Precision)
            {
                throw new ArgumentOutOfRangeException(nameof(Scale), "Decimal scale must be between zero and precision.");
            }
        }
        else if (normalizedType == "datetime")
        {
            if (!Enum.TryParse<DateTimeFormat>(DateTimeFormat, ignoreCase: true, out _))
            {
                throw new ArgumentException($"Date/time format '{DateTimeFormat}' is not supported.");
            }
            if (!Enum.TryParse<DateTimeTimeUnit>(DateTimeUnit, ignoreCase: true, out _))
            {
                throw new ArgumentException($"Date/time unit '{DateTimeUnit}' is not supported.");
            }
        }
        else
        {
            _ = ParquetConnectorOptions.ResolveClrType(normalizedType);
        }
    }
}
