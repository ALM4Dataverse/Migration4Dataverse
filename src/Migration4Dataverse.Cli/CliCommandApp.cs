using System.Collections;
using System.Management.Automation;
using System.Text.RegularExpressions;
using System.CommandLine;

namespace Migration4Dataverse.Cli;

internal sealed class CliCommandApp(PowerShellCliRuntime runtime, CancellationToken cancellationToken)
{
    private static readonly HashSet<string> AdvancedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "Invoke-M4dMigrationGraph",
        "New-M4dMigrationBinaryValue",
        "New-M4dMigrationJobStore",
        "Complete-M4dMigrationBatch",
        "Set-M4dMigrationRunStatus"
    };

    public RootCommand Build()
    {
        var root = new RootCommand(
            "Run Migration4Dataverse PowerShell commands with the bundled .NET and PowerShell runtimes.");
        var advanced = new Command("advanced", "Lower-level commands for custom integrations.");

        foreach (var commandInfo in runtime.GetExportedCommands())
        {
            var command = CreateCommand(commandInfo);
            if (AdvancedCommands.Contains(commandInfo.Name))
            {
                advanced.Subcommands.Add(command);
            }
            else
            {
                root.Subcommands.Add(command);
            }
        }

        if (advanced.Subcommands.Count > 0)
        {
            root.Subcommands.Add(advanced);
        }

        return root;
    }

    private Command CreateCommand(CommandInfo commandInfo)
    {
        var help = runtime.GetCommandHelp(commandInfo);
        var commandName = ToCommandName(commandInfo.Name);
        var command = new Command(commandName, help.Synopsis);

        var jsonOption = new Option<bool>("--json")
        {
            Description = "Emit newline-delimited JSON events."
        };
        command.Options.Add(jsonOption);

        Option<bool>? noDashboardOption = null;
        var options = new List<ICliOptionBinding>();
        var metadata = commandInfo.Parameters;
        var isMigrationCommand =
            string.Equals(commandInfo.Name, "Invoke-M4dMigration", StringComparison.OrdinalIgnoreCase);
        if (isMigrationCommand)
        {
            noDashboardOption = new Option<bool>("--no-dashboard")
            {
                Description = "Do not start the local dashboard; useful for automated or headless runs."
            };
            command.Options.Add(noDashboardOption);
        }
        var hasDatabasePath = metadata.ContainsKey("DatabasePath");
        var injectsStore = metadata.ContainsKey("Store");

        foreach (var parameter in metadata.Values.OrderBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (isMigrationCommand && parameter.Name.Equals("Path", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsPowerShellCommonParameter(parameter.Name) &&
                parameter.Name is not "WhatIf" and not "Confirm" and not "Verbose" and not "Debug")
            {
                continue;
            }

            var binding = AddParameterOption(command, parameter, help);
            if (binding is not null)
            {
                options.Add(binding);
            }
        }

        if (!hasDatabasePath && injectsStore)
        {
            options.Add(AddStringOption(
                command,
                "--database-path",
                "Path to the migration SQLite database; the CLI opens the store for this command.",
                "DatabasePath"));
        }

        Argument<string?>? migrationFileArgument = null;
        if (isMigrationCommand)
        {
            migrationFileArgument = new Argument<string?>("migration-file")
            {
                Description = "Path to a .m4d migration definition. CLI options override RunOptions in the file.",
                Arity = ArgumentArity.ExactlyOne
            };
            command.Arguments.Add(migrationFileArgument);
        }

        command.SetAction(parseResult =>
        {
            var parameters = new Dictionary<string, CliParameterValue>(StringComparer.OrdinalIgnoreCase);
            foreach (var option in options)
            {
                var value = option.Read(parseResult);
                if (value is not null)
                {
                    parameters[option.ParameterName] = new(
                        value,
                        option.IsJson,
                        option.IsBase64Bytes);
                }
            }

            var migrationFilePath = migrationFileArgument is null
                ? null
                : parseResult.GetValue(migrationFileArgument);
            if (isMigrationCommand && string.IsNullOrWhiteSpace(migrationFilePath))
            {
                WriteParseError("Provide a .m4d migration definition file.", parseResult.GetValue(jsonOption));
                return ExitCodes.InvalidArgumentsOrJob;
            }

            var eventWriter = parseResult.GetValue(jsonOption) ? new CliEventWriter(Console.Out) : null;
            eventWriter?.Write(
                "started",
                new Dictionary<string, object?> { ["command"] = commandInfo.Name });
            var exitCode = runtime.Invoke(
                commandInfo,
                parameters,
                migrationFilePath,
                eventWriter,
                cancellationToken,
                startDashboard: isMigrationCommand && !parseResult.GetValue(noDashboardOption!));
            eventWriter?.Write(
                "completed",
                new Dictionary<string, object?> { ["exitCode"] = exitCode });
            return exitCode;
        });

        return command;
    }

    private static ICliOptionBinding? AddParameterOption(
        Command command,
        ParameterMetadata parameter,
        PowerShellCommandHelp help)
    {
        var optionName = $"--{ToKebabCase(parameter.Name)}";
        var parameterType = parameter.ParameterType;

        if (parameterType == typeof(SwitchParameter) || parameterType == typeof(bool))
        {
            var option = new Option<bool>(optionName)
            {
                Description = help.GetParameterDescription(parameter.Name)
            };
            command.Options.Add(option);
            return new CliOptionBinding<bool>(parameter.Name, parseResult =>
            {
                var value = parseResult.GetValue(option);
                return value ? value : null;
            });
        }

        if (parameterType == typeof(string))
        {
            return AddStringOption(
                command,
                optionName,
                help.GetParameterDescription(parameter.Name),
                parameter.Name);
        }

        if (parameterType == typeof(int))
        {
            var option = new Option<int?>(optionName)
            {
                Description = help.GetParameterDescription(parameter.Name)
            };
            command.Options.Add(option);
            return new CliOptionBinding<int?>(parameter.Name, parseResult => parseResult.GetValue(option));
        }

        if (parameterType == typeof(long))
        {
            var option = new Option<long?>(optionName)
            {
                Description = help.GetParameterDescription(parameter.Name)
            };
            command.Options.Add(option);
            return new CliOptionBinding<long?>(parameter.Name, parseResult => parseResult.GetValue(option));
        }

        if (parameterType == typeof(double))
        {
            var option = new Option<double?>(optionName)
            {
                Description = help.GetParameterDescription(parameter.Name)
            };
            command.Options.Add(option);
            return new CliOptionBinding<double?>(parameter.Name, parseResult => parseResult.GetValue(option));
        }

        if (parameterType == typeof(string[]))
        {
            var option = new Option<string[]?>(optionName)
            {
                Description = help.GetParameterDescription(parameter.Name),
                Arity = ArgumentArity.ZeroOrMore
            };
            command.Options.Add(option);
            return new CliOptionBinding<string[]?>(parameter.Name, parseResult =>
            {
                var values = parseResult.GetValue(option);
                return values is { Length: > 0 } ? values : null;
            });
        }

        if (parameterType == typeof(byte[]))
        {
            var option = new Option<string?>($"{optionName}-base64")
            {
                Description = $"{help.GetParameterDescription(parameter.Name)} Supply the value as Base64."
            };
            command.Options.Add(option);
            return new CliOptionBinding<string?>(parameter.Name, parseResult => parseResult.GetValue(option))
            {
                IsBase64Bytes = true
            };
        }

        if (parameterType.IsArray ||
            parameterType == typeof(IDictionary) ||
            typeof(IDictionary).IsAssignableFrom(parameterType))
        {
            return AddJsonOption(
                command,
                optionName,
                help.GetParameterDescription(parameter.Name),
                parameter.Name);
        }

        if (parameterType.IsEnum ||
            parameterType == typeof(DateTimeOffset) ||
            parameterType == typeof(DateTime) ||
            parameterType == typeof(string))
        {
            return AddStringOption(
                command,
                optionName,
                help.GetParameterDescription(parameter.Name),
                parameter.Name);
        }

        return null;
    }

    private static ICliOptionBinding AddJsonOption(
        Command command,
        string optionName,
        string description,
        string parameterName)
    {
        var option = new Option<string?>($"{optionName}-json")
        {
            Description = $"{description} Supply JSON; use .m4d files for live PowerShell objects."
        };
        command.Options.Add(option);
        return new CliOptionBinding<string?>(parameterName, parseResult => parseResult.GetValue(option))
        {
            IsJson = true
        };
    }

    private static ICliOptionBinding AddStringOption(
        Command command,
        string optionName,
        string description,
        string parameterName)
    {
        var option = new Option<string?>(optionName) { Description = description };
        command.Options.Add(option);
        return new CliOptionBinding<string?>(parameterName, parseResult => parseResult.GetValue(option));
    }

    private static bool IsPowerShellCommonParameter(string name) =>
        name is "ErrorAction" or "WarningAction" or "InformationAction" or "ProgressAction" or
            "ErrorVariable" or "WarningVariable" or "InformationVariable" or "OutVariable" or "OutBuffer" or "PipelineVariable" or
            "UseTransaction";

    private static string ToKebabCase(string value) =>
        Regex.Replace(value, "([a-z0-9])([A-Z])", "$1-$2").ToLowerInvariant();

    private static string ToCommandName(string powerShellName)
    {
        var separatorIndex = powerShellName.IndexOf('-');
        if (separatorIndex < 0)
        {
            return ToKebabCase(powerShellName);
        }

        var verb = powerShellName[..separatorIndex];
        var noun = powerShellName[(separatorIndex + 1)..];
        const string migrationNounPrefix = "M4dMigration";
        if (noun.StartsWith(migrationNounPrefix, StringComparison.OrdinalIgnoreCase))
        {
            noun = noun[migrationNounPrefix.Length..];
        }

        return noun.Length == 0 && verb.Equals("Invoke", StringComparison.OrdinalIgnoreCase)
            ? "run"
            : ToKebabCase($"{verb}-{noun}");
    }

    private static void WriteParseError(string message, bool json)
    {
        if (json)
        {
            new CliEventWriter(Console.Out).Write(
                "error",
                new Dictionary<string, object?>
                {
                    ["message"] = message,
                    ["exitCode"] = ExitCodes.InvalidArgumentsOrJob
                });
            return;
        }

        Console.Error.WriteLine(message);
    }
}

internal interface ICliOptionBinding
{
    string ParameterName { get; }
    bool IsJson { get; }
    bool IsBase64Bytes { get; }
    object? Read(ParseResult parseResult);
}

internal sealed class CliOptionBinding<T>(
    string parameterName,
    Func<ParseResult, object?> read) : ICliOptionBinding
{
    public string ParameterName { get; } = parameterName;
    public bool IsJson { get; init; }
    public bool IsBase64Bytes { get; init; }
    public object? Read(ParseResult parseResult) => read(parseResult);
}

internal sealed record CliParameterValue(object Value, bool IsJson = false, bool IsBase64Bytes = false);
