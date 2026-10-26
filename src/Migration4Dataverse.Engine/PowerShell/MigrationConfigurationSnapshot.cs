using System.Collections;
using System.Globalization;
using System.Management.Automation;
using System.Reflection;
using System.Security;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModuleSpecification = Microsoft.PowerShell.Commands.ModuleSpecification;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.PowerShell;

public static class MigrationConfigurationSnapshot
{
    private static readonly string[] SensitivePropertyFragments =
    [
        "password",
        "secret",
        "token",
        "credential",
        "authorization",
        "apikey",
        "privatekey",
        "connectionstring",
        "sas"
    ];

    public static string CreateJson(
        PSObject[] steps,
        IDictionary connections,
        MigrationSchedulerLimits? schedulerLimits = null,
        bool isWhatIf = false,
        IEnumerable<string>? modulesToImport = null,
        IEnumerable<ModuleSpecification>? requiredModules = null,
        IEnumerable<PSModuleInfo>? resolvedRequiredModules = null,
        IEnumerable<(string Path, string Content)>? helperFiles = null,
        IEnumerable<string>? sharedScripts = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(connections);

        var stepNodes = new JsonArray();
        foreach (var step in steps)
        {
            ArgumentNullException.ThrowIfNull(step);
            var properties = ConvertToNode(step) as JsonObject ?? new JsonObject();
            var id = ReadStringProperty(step, "Id");
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("Every migration step must have a non-empty Id property.", nameof(steps));
            }

            var type = ReadStringProperty(step, "Type") ?? string.Empty;
            stepNodes.Add(new JsonObject
            {
                ["id"] = id,
                ["type"] = type,
                ["inputs"] = ConvertToNode(ReadProperty(step, "Inputs")) ?? new JsonArray(),
                ["hasSource"] = string.Equals(type, "source", StringComparison.OrdinalIgnoreCase),
                ["sourceConnectionId"] = ReadEndpointStringProperty(step, "Source", "ConnectionId"),
                ["sourceConnectionType"] = ReadEndpointStringProperty(step, "Source", "Type"),
                ["targetConnectionId"] = ReadEndpointStringProperty(step, "Target", "ConnectionId"),
                ["targetConnectionType"] = ReadEndpointStringProperty(step, "Target", "Type"),
                ["properties"] = properties
            });
        }

        var connectionNodes = CreateConnectionNodes(connections);
        var configuration = new JsonObject
        {
            ["steps"] = stepNodes,
            ["connections"] = connectionNodes,
            ["isWhatIf"] = isWhatIf,
            ["powerShell"] = CreatePowerShellConfiguration(
                modulesToImport,
                requiredModules,
                resolvedRequiredModules,
                helperFiles,
                sharedScripts)
        };
        if (schedulerLimits is not null)
        {
            configuration["schedulerLimits"] = new JsonObject
            {
                ["maxDegreeOfParallelism"] = schedulerLimits.MaxDegreeOfParallelism,
                ["maxQueuedBatches"] = schedulerLimits.MaxQueuedBatches,
                ["maxQueuedBatchesPerStep"] = schedulerLimits.MaxQueuedBatchesPerStep
            };
        }
        return configuration.ToJsonString();
    }

    private static JsonObject CreatePowerShellConfiguration(
        IEnumerable<string>? modulesToImport,
        IEnumerable<ModuleSpecification>? requiredModules,
        IEnumerable<PSModuleInfo>? resolvedRequiredModules,
        IEnumerable<(string Path, string Content)>? helperFiles,
        IEnumerable<string>? sharedScripts)
    {
        var modules = new JsonArray();
        foreach (var module in (modulesToImport ?? Array.Empty<string>())
                     .OrderBy(module => module, StringComparer.OrdinalIgnoreCase))
        {
            modules.Add(module);
        }

        var required = new JsonArray();
        foreach (var module in (requiredModules ?? Array.Empty<ModuleSpecification>())
                     .OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase))
        {
            required.Add(new JsonObject
            {
                ["name"] = module.Name,
                ["moduleVersion"] = module.Version?.ToString(),
                ["maximumVersion"] = module.MaximumVersion?.ToString(),
                ["requiredVersion"] = module.RequiredVersion?.ToString(),
                ["guid"] = module.Guid?.ToString("D")
            });
        }

        var resolved = new JsonArray();
        foreach (var module in (resolvedRequiredModules ?? Array.Empty<PSModuleInfo>())
                     .OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase))
        {
            resolved.Add(new JsonObject
            {
                ["name"] = module.Name,
                ["version"] = module.Version.ToString(),
                ["guid"] = module.Guid.ToString("D")
            });
        }

        var scripts = new JsonArray();
        foreach (var helper in helperFiles ?? Array.Empty<(string Path, string Content)>())
        {
            scripts.Add(new JsonObject
            {
                ["path"] = helper.Path,
                ["sha256"] = GetScriptHash(helper.Content)
            });
        }
        foreach (var script in sharedScripts ?? Array.Empty<string>())
        {
            scripts.Add(new JsonObject
            {
                ["source"] = "inline",
                ["sha256"] = GetScriptHash(script)
            });
        }

        return new JsonObject
        {
            ["modulesToImport"] = modules,
            ["requiredModules"] = required,
            ["resolvedRequiredModules"] = resolved,
            ["sharedScripts"] = scripts
        };
    }

    private static string GetScriptHash(string script)
    {
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(script)))
            .ToLowerInvariant();
    }

    public static string CaptureConnectionInstances(IDictionary connections, string phase)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        return new JsonObject
        {
            ["phase"] = phase,
            ["capturedUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["connections"] = CreateConnectionNodes(connections)
        }.ToJsonString();
    }

    public static string AppendConnectionInstanceSnapshot(string? existingJson, string snapshotJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotJson);
        var snapshots = string.IsNullOrWhiteSpace(existingJson)
            ? new JsonArray()
            : JsonNode.Parse(existingJson) as JsonArray
                ?? throw new InvalidDataException("Stored connection instance snapshots must be a JSON array.");
        snapshots.Add(JsonNode.Parse(snapshotJson));
        return snapshots.ToJsonString();
    }

    private static JsonArray CreateConnectionNodes(IDictionary connections)
    {
        var connectionNodes = new JsonArray();
        foreach (DictionaryEntry connection in connections)
        {
            var id = Convert.ToString(connection.Key, CultureInfo.InvariantCulture)
                ?? throw new ArgumentException("Connection IDs cannot be null.", nameof(connections));
            var node = new JsonObject
            {
                ["id"] = id,
                ["properties"] = ConvertToNode(connection.Value)
            };
            node["typeName"] = GetBaseObject(connection.Value)?.GetType().FullName;
            connectionNodes.Add(node);
        }

        return connectionNodes;
    }

    private static object? ReadProperty(PSObject source, string name)
    {
        if (GetBaseObject(source) is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (string.Equals(
                    Convert.ToString(entry.Key, CultureInfo.InvariantCulture),
                    name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Value;
                }
            }
            return null;
        }

        return source.Properties[name]?.Value;
    }

    private static string? ReadStringProperty(PSObject source, string name)
    {
        return Convert.ToString(ReadProperty(source, name), CultureInfo.InvariantCulture);
    }

    private static string? ReadEndpointStringProperty(PSObject source, string endpointName, string propertyName)
    {
        var endpoint = ReadProperty(source, endpointName);
        if (endpoint is null)
        {
            return null;
        }

        object? value = null;
        if (GetBaseObject(endpoint) is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (string.Equals(
                    Convert.ToString(entry.Key, CultureInfo.InvariantCulture),
                    propertyName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    value = entry.Value;
                    break;
                }
            }
        }
        else
        {
            var endpointObject = endpoint is PSObject psObject
                ? psObject
                : PSObject.AsPSObject(endpoint);
            var property = endpointObject.Properties.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase));
            value = property?.Value;
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static JsonNode? ConvertToNode(
        object? value,
        string? propertyName = null,
        HashSet<object>? visiting = null,
        int depth = 0)
    {
        if (propertyName is not null && IsSensitive(propertyName))
        {
            return JsonValue.Create("[REDACTED]");
        }

        if (value is null)
        {
            return null;
        }

        if (value is SecureString)
        {
            return JsonValue.Create("[REDACTED]");
        }

        if (value is ScriptBlock)
        {
            return JsonValue.Create("[ScriptBlock]");
        }

        if (value is Delegate)
        {
            return JsonValue.Create("[Delegate]");
        }

        if (value is JsonElement element)
        {
            return RedactJsonNode(JsonNode.Parse(element.GetRawText()));
        }

        if (value is string text)
        {
            return JsonValue.Create(text);
        }

        if (value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or
            float or double or decimal)
        {
            return JsonSerializer.SerializeToNode(value);
        }

        if (value is char character)
        {
            return JsonValue.Create(character.ToString());
        }

        if (value is DateTime or DateTimeOffset or TimeSpan or Guid or Uri or Enum)
        {
            return JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        if (depth >= 8)
        {
            return JsonValue.Create("[Maximum inspection depth reached]");
        }

        visiting ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
        var unwrappedValue = GetBaseObject(value);
        var instance = unwrappedValue;
        if (instance is not null && !instance.GetType().IsValueType && instance is not string &&
            !visiting.Add(instance))
        {
            return JsonValue.Create("[Circular reference]");
        }

        try
        {
            if (unwrappedValue is IDictionary dictionary)
            {
                var dictionaryNode = new JsonObject();
                var count = 0;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (count++ >= 500)
                    {
                        dictionaryNode["…"] = "[Additional properties omitted]";
                        break;
                    }

                    var name = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "(null)";
                    dictionaryNode[name] = ConvertToNode(entry.Value, name, visiting, depth + 1);
                }
                return dictionaryNode;
            }

            if (unwrappedValue is IEnumerable enumerable)
            {
                var array = new JsonArray();
                var count = 0;
                foreach (var item in enumerable)
                {
                    if (count++ >= 500)
                    {
                        array.Add("[Additional items omitted]");
                        break;
                    }
                    array.Add(ConvertToNode(item, visiting: visiting, depth: depth + 1));
                }
                return array;
            }

            var properties = value is PSObject wrapped
                ? wrapped.Properties
                : PSObject.AsPSObject(value).Properties;
            var objectNode = new JsonObject();
            var propertyCount = 0;
            foreach (var property in properties)
            {
                if (property.MemberType is not (PSMemberTypes.NoteProperty or PSMemberTypes.Property or PSMemberTypes.AliasProperty))
                {
                    continue;
                }

                if (propertyCount++ >= 500)
                {
                    objectNode["…"] = "[Additional properties omitted]";
                    break;
                }

                if (IsSensitive(property.Name))
                {
                    objectNode[property.Name] = "[REDACTED]";
                    continue;
                }

                try
                {
                    objectNode[property.Name] = ConvertToNode(property.Value, property.Name, visiting, depth + 1);
                }
                catch (GetValueInvocationException)
                {
                    objectNode[property.Name] = "[Property could not be read]";
                }
                catch (TargetInvocationException)
                {
                    objectNode[property.Name] = "[Property could not be read]";
                }
            }

            return objectNode;
        }
        finally
        {
            if (instance is not null && !instance.GetType().IsValueType && instance is not string)
            {
                visiting.Remove(instance);
            }
        }
    }

    private static JsonNode? RedactJsonNode(JsonNode? node)
    {
        if (node is JsonObject objectNode)
        {
            foreach (var property in objectNode.ToArray())
            {
                if (IsSensitive(property.Key))
                {
                    objectNode[property.Key] = "[REDACTED]";
                }
                else
                {
                    RedactJsonNode(property.Value);
                }
            }
        }
        else if (node is JsonArray arrayNode)
        {
            foreach (var item in arrayNode)
            {
                RedactJsonNode(item);
            }
        }

        return node;
    }

    private static bool IsSensitive(string propertyName) =>
        SensitivePropertyFragments.Any(fragment =>
            propertyName.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static object? GetBaseObject(object? value) =>
        value is PSObject psObject ? psObject.BaseObject : value;
}
