using System.Collections;
using System.Management.Automation;
using System.Text.RegularExpressions;
using System.CommandLine;

namespace Migration4Dataverse.Cli;

internal sealed class CliCommandApp(PowerShellCliRuntime runtime, CancellationToken cancellationToken)
{
    public RootCommand Build()
    {
        var root = new RootCommand(
            "Run Migration4Dataverse PowerShell commands with the bundled .NET and PowerShell runtimes.");

        foreach (var commandInfo in runtime.GetExportedCommands())
        {
            root.Subcommands.Add(CreateCommand(commandInfo));
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
        var scriptOption = new Option<string?>("--script")
        {
            Description = "Run a PowerShell setup/job script before invoking the command."
        };
        command.Options.Add(jsonOption);
        command.Options.Add(scriptOption);

        var options = new List<ICliOptionBinding>();
        var metadata = commandInfo.Parameters;
        var hasDatabasePath = metadata.ContainsKey("DatabasePath");
        var injectsStore = metadata.ContainsKey("Store");

        foreach (var parameter in metadata.Values.OrderBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase))
        {
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

        Argument<string?>? scriptArgument = null;
        if (string.Equals(commandInfo.Name, "Invoke-M4dMigration", StringComparison.OrdinalIgnoreCase))
        {
            scriptArgument = new Argument<string?>("migration-script")
            {
                Description = "PowerShell script that invokes Invoke-M4dMigration or defines $Steps and $Connections. CLI options act as defaults for script invocations.",
                Arity = ArgumentArity.ZeroOrOne
            };
            command.Arguments.Add(scriptArgument);
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

            var scriptPath = parseResult.GetValue(scriptOption);
            if (scriptArgument is not null)
            {
                var positionalScript = parseResult.GetValue(scriptArgument);
                if (scriptPath is not null && positionalScript is not null)
                {
                    WriteParseError("Provide the migration script either positionally or with --script, not both.", parseResult.GetValue(jsonOption));
                    return ExitCodes.InvalidArgumentsOrJob;
                }
                scriptPath ??= positionalScript;
            }

            if (string.Equals(commandInfo.Name, "Invoke-M4dMigration", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(scriptPath) &&
                !parameters.ContainsKey("Steps"))
            {
                WriteParseError(
                    "Provide a migration script with --script or define the migration graph with --steps-json.",
                    parseResult.GetValue(jsonOption));
                return ExitCodes.InvalidArgumentsOrJob;
            }

            var eventWriter = parseResult.GetValue(jsonOption) ? new CliEventWriter(Console.Out) : null;
            eventWriter?.Write(
                "started",
                new Dictionary<string, object?> { ["command"] = commandInfo.Name });
            var exitCode = runtime.Invoke(
                commandInfo,
                parameters,
                scriptPath,
                eventWriter,
                cancellationToken);
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
            Description = $"{description} Supply JSON or use --script for live PowerShell objects."
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
