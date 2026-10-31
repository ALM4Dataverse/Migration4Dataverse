using System.Text.Json;
using System.Text.Json.Serialization;

namespace Migration4Dataverse.Cli;

internal sealed class CliEventWriter(TextWriter output)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _writeLock = new();

    public void Write(string type, object? details = null)
    {
        var cliEvent = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["timestamp"] = DateTimeOffset.UtcNow
        };
        if (details is IReadOnlyDictionary<string, object?> fields)
        {
            foreach (var field in fields)
            {
                cliEvent[field.Key] = field.Value;
            }
        }
        else if (details is not null)
        {
            cliEvent["message"] = details;
        }

        lock (_writeLock)
        {
            output.WriteLine(JsonSerializer.Serialize(cliEvent, JsonOptions));
            output.Flush();
        }
    }
}
