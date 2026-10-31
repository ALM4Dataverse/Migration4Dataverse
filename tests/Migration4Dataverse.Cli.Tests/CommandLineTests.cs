using System.Text.Json;
using Migration4Dataverse.Cli;

namespace Migration4Dataverse.Cli.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void WritesEachJsonEventAsOneValidLine()
    {
        using var output = new StringWriter();
        var writer = new CliEventWriter(output);

        writer.Write("output", new Dictionary<string, object?> { ["message"] = "line one\nline two" });

        var jsonLine = Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        using var document = JsonDocument.Parse(jsonLine);
        Assert.Equal("output", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("line one\nline two", document.RootElement.GetProperty("message").GetString());
    }
}
