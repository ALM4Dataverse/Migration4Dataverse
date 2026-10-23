using System.Collections;
using System.Management.Automation;
using System.Text.Json;
using Migration4Dataverse.Engine.PowerShell;

namespace Migration4Dataverse.Engine.Transformations;

internal sealed class DataFlowPowerShellScriptRunner : IDisposable
{
    private readonly PowerShellScriptExecutor _executor = new(maxRunspaces: 1);
    private readonly IReadOnlyDictionary<string, string> _scripts;

    public DataFlowPowerShellScriptRunner(IReadOnlyDictionary<string, string> scripts)
    {
        _scripts = scripts;
    }

    public async Task<IReadOnlyList<JsonElement>> TransformAsync(
        string nodeId,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        var output = await InvokeAsync(nodeId, input, cancellationToken).ConfigureAwait(false);
        if (output.Count == 0)
        {
            return Array.Empty<JsonElement>();
        }

        var records = new JsonElement[output.Count];
        for (var index = 0; index < output.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            records[index] = ToJsonRecord(output[index], nodeId);
        }

        return records;
    }

    public async Task<bool> FilterAsync(
        string nodeId,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        var output = await InvokeAsync(nodeId, input, cancellationToken).ConfigureAwait(false);
        return output.Count switch
        {
            0 => false,
            1 => LanguagePrimitives.IsTrue(output[0].BaseObject),
            _ => LanguagePrimitives.IsTrue(output.ToArray())
        };
    }

    public void Dispose()
    {
        _executor.Dispose();
    }

    private async Task<IReadOnlyList<PSObject>> InvokeAsync(
        string nodeId,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _executor.InvokeAsync(
                    _scripts[nodeId],
                    [ToPowerShellValue(input)],
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PowerShellScriptException or RuntimeException)
        {
            throw new InvalidOperationException(
                $"PowerShell script for data-flow node '{nodeId}' failed: {exception.Message}",
                exception);
        }
    }

    private static object ToPowerShellValue(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Object => ToPowerShellObject(value),
            JsonValueKind.Array => value.EnumerateArray().Select(ToPowerShellValue).ToArray(),
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.Number => ToPowerShellNumber(value),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null!,
            _ => throw new InvalidDataException(
                $"A data-flow script input uses unsupported JSON value kind '{value.ValueKind}'.")
        };
    }

    private static PSObject ToPowerShellObject(JsonElement value)
    {
        var result = new PSObject();
        foreach (var property in value.EnumerateObject())
        {
            result.Properties.Add(new PSNoteProperty(property.Name, ToPowerShellValue(property.Value)));
        }

        return result;
    }

    private static object ToPowerShellNumber(JsonElement value)
    {
        if (value.TryGetInt32(out var int32))
        {
            return int32;
        }
        if (value.TryGetInt64(out var int64))
        {
            return int64;
        }
        if (value.TryGetDecimal(out var decimalValue))
        {
            return decimalValue;
        }

        return value.GetDouble();
    }

    private static JsonElement ToJsonRecord(PSObject output, string nodeId)
    {
        object? converted = ConvertPowerShellValue(output);
        JsonElement record;
        try
        {
            record = JsonSerializer.SerializeToElement(converted);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new InvalidDataException(
                $"Transform node '{nodeId}' produced a value that cannot be serialized as JSON.",
                exception);
        }

        if (record.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Transform node '{nodeId}' must produce JSON objects; received '{record.ValueKind}'.");
        }

        return record.Clone();
    }

    private static object? ConvertPowerShellValue(object? value)
    {
        if (value is null)
        {
            return null;
        }
        if (value is JsonElement jsonElement)
        {
            return jsonElement.Clone();
        }
        if (value is PSObject psObject)
        {
            if (psObject.BaseObject is PSCustomObject)
            {
                var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in psObject.Properties)
                {
                    if (property.MemberType is PSMemberTypes.NoteProperty or
                        PSMemberTypes.Property or
                        PSMemberTypes.AliasProperty or
                        PSMemberTypes.ScriptProperty)
                    {
                        properties[property.Name] = ConvertPowerShellValue(property.Value);
                    }
                }

                return properties;
            }

            return ConvertPowerShellValue(psObject.BaseObject);
        }
        if (value is IDictionary dictionary)
        {
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key is not string name)
                {
                    throw new InvalidDataException(
                        "Transform output dictionaries must use string keys.");
                }

                properties.Add(name, ConvertPowerShellValue(entry.Value));
            }

            return properties;
        }
        if (value is IEnumerable values and not string and not byte[])
        {
            return values.Cast<object?>().Select(ConvertPowerShellValue).ToArray();
        }

        return value;
    }
}
