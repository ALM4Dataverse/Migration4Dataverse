using System.Management.Automation;
using System.Text.RegularExpressions;

namespace Migration4Dataverse.Engine.PowerShell;

[Cmdlet(VerbsCommon.Get, "M4dMigrationComponent")]
[OutputType(typeof(MigrationComponentHelp))]
public sealed class GetMigrationComponentCommand : PSCmdlet
{
    [Parameter]
    [ValidateSet("Source", "Target", "Action")]
    public string? Category { get; set; }

    [Parameter]
    [ValidateNotNullOrEmpty]
    public string? Name { get; set; }

    [Parameter]
    public SwitchParameter Detailed { get; set; }

    protected override void ProcessRecord()
    {
        var matches = MigrationComponentCatalog.GetComponents()
            .Where(component => Category is null ||
                component.Category.Equals(Category, StringComparison.OrdinalIgnoreCase))
            .Where(component => Name is null ||
                component.Name.Equals(Name, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (Name is not null && matches.Length == 0)
        {
            throw new ItemNotFoundException(
                $"No migration component named '{Name}' was found" +
                (Category is null ? "." : $" in category '{Category}'."));
        }

        foreach (var component in matches)
        {
            WriteObject(new MigrationComponentHelp(
                component.Category,
                component.Name,
                Detailed ? component.Details : component.Summary,
                component.Documentation));
        }
    }
}

public sealed record MigrationComponentHelp(
    string Category,
    string Name,
    string Help,
    string Documentation);

internal sealed record MigrationComponent(
    string Category,
    string Name,
    string Summary,
    string Documentation,
    string Details);

internal static class MigrationComponentCatalog
{
    private static readonly Regex MarkdownLink = new(@"\[[^\]]+\]\((?<target>[^)]+)\)", RegexOptions.Compiled);
    private static readonly Regex MarkdownCode = new(@"`([^`]+)`", RegexOptions.Compiled);
    private static readonly Lazy<IReadOnlyList<MigrationComponent>> Components = new(LoadComponents);

    public static IReadOnlyList<MigrationComponent> GetComponents() => Components.Value;

    private static IReadOnlyList<MigrationComponent> LoadComponents()
    {
        var components = new List<MigrationComponent>();
        AddConnectorComponents(components, "Source", "connectors/sources/README.md");
        AddConnectorComponents(components, "Target", "connectors/targets/README.md");
        AddActions(components);
        return components;
    }

    private static void AddConnectorComponents(
        ICollection<MigrationComponent> components,
        string category,
        string catalogPath)
    {
        var markdown = ReadMarkdown(catalogPath);
        foreach (var cells in ReadTableRows(markdown))
        {
            if (cells.Count < 3)
            {
                continue;
            }

            var name = MarkdownCode.Match(cells[0]);
            var link = MarkdownLink.Match(cells[2]);
            if (!name.Success || !link.Success)
            {
                continue;
            }

            var target = link.Groups["target"].Value;
            var documentPath = ResolveRelativePath(catalogPath, target);
            components.Add(new MigrationComponent(
                category,
                name.Groups[1].Value,
                StripMarkdown(cells[1]),
                $"docs/{documentPath}{GetAnchor(target)}",
                ReadMarkdown(documentPath)));
        }
    }

    private static void AddActions(ICollection<MigrationComponent> components)
    {
        var reference = ReadMarkdown("reference.md");
        var actionDescriptions = ReadTableRows(reference)
            .Where(cells => cells.Count >= 2)
            .Select(cells => (Cells: cells, Name: MarkdownCode.Match(cells[0])))
            .Where(item => item.Name.Success)
            .ToDictionary(
                item => item.Name.Groups[1].Value,
                item => StripMarkdown(item.Cells[1]),
                StringComparer.OrdinalIgnoreCase);

        var configuration = ReadMarkdown("configuration.md");
        var actionContracts = ReadTableRows(configuration, "## Node contract")
            .Where(cells => cells.Count >= 3)
            .Select(cells => (Cells: cells, Name: MarkdownCode.Match(cells[0])))
            .Where(item => item.Name.Success)
            .ToDictionary(
                item => item.Name.Groups[1].Value,
                item => StripMarkdown(item.Cells[1]) + "; " + StripMarkdown(item.Cells[2]),
                StringComparer.OrdinalIgnoreCase);

        foreach (var (name, summary) in actionDescriptions)
        {
            if (name.Equals("source", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("target", StringComparison.OrdinalIgnoreCase) ||
                !actionContracts.TryGetValue(name, out var contract))
            {
                continue;
            }

            var detailSection = name.ToLowerInvariant() switch
            {
                "transform" or "filter" => "Transform and Filter semantics",
                "callback" => "Callback nodes",
                "append" or "join" => "Source dependencies",
                _ => null
            };
            var details = $"# {name}\n\n{summary}\n\n## Node contract\n\n{contract}";
            if (detailSection is not null)
            {
                var section = ReadMarkdownSection(configuration, detailSection);
                if (!string.IsNullOrWhiteSpace(section))
                {
                    details += $"\n\n{section}";
                }
            }

            components.Add(new MigrationComponent(
                "Action",
                name,
                summary,
                "docs/configuration.md#node-contract",
                details));
        }
    }

    private static IEnumerable<IReadOnlyList<string>> ReadTableRows(string markdown, string? heading = null)
    {
        var inSection = heading is null;
        var inTable = false;
        foreach (var line in markdown.Split('\n'))
        {
            var trimmed = line.Trim();
            if (heading is not null && trimmed.StartsWith('#'))
            {
                if (trimmed.Equals(heading, StringComparison.OrdinalIgnoreCase))
                {
                    inSection = true;
                    inTable = false;
                    continue;
                }
                if (inSection)
                {
                    break;
                }
            }

            if (!inSection || !trimmed.StartsWith('|'))
            {
                if (inTable && trimmed.Length == 0)
                {
                    break;
                }
                continue;
            }

            var cells = trimmed.Trim('|')
                .Split('|')
                .Select(cell => cell.Trim())
                .ToArray();
            if (cells.All(cell => cell.Length == 0 || cell.All(character => character is '-' or ':' or ' ')))
            {
                inTable = true;
                continue;
            }

            if (!inTable)
            {
                inTable = true;
                continue;
            }

            yield return cells;
        }
    }

    private static string ReadMarkdownSection(string markdown, string heading)
    {
        var lines = markdown.Split('\n');
        var start = Array.FindIndex(
            lines,
            line => line.Trim().Equals($"## {heading}", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            return string.Empty;
        }

        var end = Array.FindIndex(
            lines,
            start + 1,
            line => line.TrimStart().StartsWith("## ", StringComparison.Ordinal));
        if (end < 0)
        {
            end = lines.Length;
        }

        return string.Join('\n', lines[start..end]).Trim();
    }

    private static string ReadMarkdown(string path)
    {
        var normalizedPath = path.Replace('\\', '/').TrimStart('/');
        var fileName = Path.GetFileName(normalizedPath);
        var directories = normalizedPath[..^fileName.Length]
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        directories = directories
            .Where(directory => !directory.Equals("connectors", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var candidates = typeof(GetMigrationComponentCommand).Assembly
            .GetManifestResourceNames()
            .Where(resource => resource.EndsWith('.' + fileName, StringComparison.OrdinalIgnoreCase))
            .Where(resource => directories.All(directory =>
                resource.Contains('.' + directory + '.', StringComparison.OrdinalIgnoreCase) ||
                resource.Contains('/' + directory + '/', StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (candidates.Length != 1)
        {
            throw new InvalidDataException(
                $"Expected one embedded Markdown resource for '{normalizedPath}', found {candidates.Length}.");
        }

        using var stream = typeof(GetMigrationComponentCommand).Assembly.GetManifestResourceStream(candidates[0])
            ?? throw new InvalidDataException($"Embedded Markdown resource '{candidates[0]}' could not be opened.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string ResolveRelativePath(string currentPath, string target)
    {
        var path = target.Split('#', 2)[0].Split('?', 2)[0];
        var parts = currentPath.Split('/').SkipLast(1).ToList();
        foreach (var part in path.Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".")
            {
                continue;
            }
            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
                continue;
            }
            parts.Add(Uri.UnescapeDataString(part));
        }
        return string.Join('/', parts);
    }

    private static string GetAnchor(string target)
    {
        var anchorIndex = target.IndexOf('#');
        return anchorIndex < 0 ? string.Empty : target[anchorIndex..];
    }

    private static string StripMarkdown(string value) =>
        MarkdownLink.Replace(MarkdownCode.Replace(value, "$1"), "$1");
}
