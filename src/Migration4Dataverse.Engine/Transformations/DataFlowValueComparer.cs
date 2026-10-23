using System.Globalization;
using System.Text.Json;

namespace Migration4Dataverse.Engine.Transformations;

internal static class DataFlowValueComparer
{
    public static object?[] ReadKeys(
        JsonElement record,
        IReadOnlyList<DataFlowSortKey> keys,
        string nodeId)
    {
        if (record.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Data-flow node '{nodeId}' received a record that is not a JSON object.");
        }

        var values = new object?[keys.Count];
        for (var index = 0; index < keys.Count; index++)
        {
            var key = keys[index];
            if (!record.TryGetProperty(key.Name, out var value))
            {
                throw new InvalidDataException(
                    $"Data-flow node '{nodeId}' received a record missing sort key '{key.Name}'.");
            }

            if (value.ValueKind == JsonValueKind.Null)
            {
                values[index] = null;
                continue;
            }

            var type = NormalizeType(key.Type);
            try
            {
                values[index] = type switch
                {
                    "string" => value.GetString()
                        ?? throw new InvalidDataException(
                            $"Sort key '{key.Name}' on node '{nodeId}' must be a string."),
                    "bool" => value.GetBoolean(),
                    "byte" => value.GetByte(),
                    "short" => value.GetInt16(),
                    "int" => value.GetInt32(),
                    "long" => value.GetInt64(),
                    "float" => value.GetSingle(),
                    "double" => value.GetDouble(),
                    "decimal" => value.GetDecimal(),
                    "guid" => value.GetGuid(),
                    "date" => DateOnly.Parse(value.GetString()!, CultureInfo.InvariantCulture),
                    "datetime" => DateTimeOffset.Parse(
                        value.GetString()!,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal),
                    _ => throw new InvalidOperationException(
                        $"Sort key type '{key.Type}' on node '{nodeId}' is unsupported.")
                };
            }
            catch (Exception exception) when (
                exception is FormatException or InvalidOperationException or OverflowException)
            {
                throw new InvalidDataException(
                    $"Sort key '{key.Name}' on node '{nodeId}' contains a value that is not a valid {key.Type}.",
                    exception);
            }
        }

        return values;
    }

    public static int Compare(
        IReadOnlyList<object?> left,
        IReadOnlyList<object?> right,
        IReadOnlyList<DataFlowSortKey> keys)
    {
        for (var index = 0; index < keys.Count; index++)
        {
            var key = keys[index];
            var leftValue = left[index];
            var rightValue = right[index];
            int comparison;
            if (leftValue is null || rightValue is null)
            {
                comparison = leftValue is null
                    ? rightValue is null ? 0 : key.NullsFirst ? -1 : 1
                    : key.NullsFirst ? 1 : -1;
            }
            else
            {
                comparison = CompareNonNull(
                    leftValue,
                    rightValue,
                    NormalizeType(key.Type));
                if (key.Descending)
                {
                    comparison = comparison > 0 ? -1 : comparison < 0 ? 1 : 0;
                }
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    public static long EstimateRecordMemory(byte[] json, IReadOnlyList<object?> keys)
    {
        var estimate = checked(json.LongLength + 128L + (keys.Count * 32L));
        foreach (var key in keys)
        {
            if (key is string text)
            {
                estimate = checked(estimate + (text.Length * 2L));
            }
        }

        return estimate;
    }

    public static string NormalizeType(string type)
    {
        var normalized = type.Trim().ToLowerInvariant();
        return normalized switch
        {
            "boolean" => "bool",
            "int16" => "short",
            "int32" => "int",
            "int64" => "long",
            "single" => "float",
            "number" => "decimal",
            "datetimeoffset" => "datetime",
            _ => normalized
        };
    }

    private static int CompareNonNull(object left, object right, string type)
    {
        return type switch
        {
            "string" => StringComparer.Ordinal.Compare((string)left, (string)right),
            "bool" => ((bool)left).CompareTo((bool)right),
            "byte" => ((byte)left).CompareTo((byte)right),
            "short" => ((short)left).CompareTo((short)right),
            "int" => ((int)left).CompareTo((int)right),
            "long" => ((long)left).CompareTo((long)right),
            "float" => ((float)left).CompareTo((float)right),
            "double" => ((double)left).CompareTo((double)right),
            "decimal" => ((decimal)left).CompareTo((decimal)right),
            "guid" => ((Guid)left).CompareTo((Guid)right),
            "date" => ((DateOnly)left).CompareTo((DateOnly)right),
            "datetime" => ((DateTimeOffset)left).CompareTo((DateTimeOffset)right),
            _ => throw new InvalidOperationException($"Sort key type '{type}' is unsupported.")
        };
    }
}
