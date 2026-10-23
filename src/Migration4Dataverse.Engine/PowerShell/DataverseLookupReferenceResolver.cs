using System.Collections;
using System.Management.Automation;

namespace Migration4Dataverse.Engine.PowerShell;

public static class DataverseLookupReferenceResolver
{
    public static object? Resolve(object? value) => ResolveValue(value);

    private static object? ResolveValue(object? value)
    {
        if (value is PSObject psObject)
        {
            value = psObject.BaseObject;
        }

        if (value is IDictionary dictionary)
        {
            var hasTableName = TryGetValue(dictionary, "TableName", out var tableNameValue);
            var hasId = TryGetValue(dictionary, "Id", out var idValue);
            if (hasTableName && hasId)
            {
                if (dictionary.Count != 2)
                {
                    throw new InvalidDataException(
                        "Dataverse lookup references may contain only TableName and Id properties.");
                }
                return CreateReference(tableNameValue, idValue);
            }

            foreach (var key in dictionary.Keys.Cast<object?>().ToArray())
            {
                if (key is not null)
                {
                    dictionary[key] = ResolveValue(dictionary[key]);
                }
            }
            return value;
        }

        if (value is Array array)
        {
            var resolved = new object?[array.Length];
            for (var index = 0; index < array.Length; index++)
            {
                resolved[index] = ResolveValue(array.GetValue(index));
            }
            return resolved;
        }

        if (value is IList list)
        {
            for (var index = 0; index < list.Count; index++)
            {
                list[index] = ResolveValue(list[index]);
            }
        }

        return value;
    }

    private static PSObject CreateReference(object? tableNameValue, object? idValue)
    {
        var tableName = Convert.ToString(tableNameValue);
        var idText = Convert.ToString(idValue);
        if (string.IsNullOrWhiteSpace(tableName) || string.IsNullOrWhiteSpace(idText))
        {
            throw new InvalidDataException("Dataverse lookup references require non-empty TableName and Id values.");
        }
        if (!Guid.TryParse(idText, out var recordId))
        {
            throw new InvalidDataException(
                $"Dataverse lookup reference '{tableName}' must contain a Dataverse GUID in Id.");
        }

        var result = new PSObject();
        result.Properties.Add(new PSNoteProperty("TableName", tableName));
        result.Properties.Add(new PSNoteProperty("Id", recordId));
        return result;
    }

    private static bool TryGetValue(IDictionary dictionary, string key, out object? value)
    {
        foreach (var candidate in dictionary.Keys)
        {
            if (string.Equals(Convert.ToString(candidate), key, StringComparison.OrdinalIgnoreCase))
            {
                value = dictionary[candidate!];
                return true;
            }
        }

        value = null;
        return false;
    }
}
