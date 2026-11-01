using System.Collections;
using System.Reflection;
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using Microsoft.PowerShell.Commands;

namespace Migration4Dataverse.Cli;

internal sealed class PowerShellCliRuntime : IDisposable
{
    private readonly string _modulesPath;
    private readonly TextWriter _standardOutput;
    private readonly TextWriter _standardError;
    private readonly PowerShellModulePathScope _modulePathScope;
    private readonly Runspace _runspace;
    private readonly object _outputLock = new();
    private CliEventWriter? _jsonOutput;
    private volatile bool _hadErrors;
    private volatile int _errorExitCode = ExitCodes.MigrationFailure;

    public PowerShellCliRuntime(
        string modulesPath,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        _modulesPath = Path.GetFullPath(modulesPath);
        _standardOutput = standardOutput;
        _standardError = standardError;
        var powerShellModulesPath = Path.Combine(
            AppContext.BaseDirectory,
            "runtimes",
            OperatingSystem.IsWindows() ? "win" : "unix",
            "lib",
            "net10.0",
            "Modules");
        _modulePathScope = new PowerShellModulePathScope(_modulesPath, powerShellModulesPath);
        _runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
        try
        {
            _runspace.Open();
            _runspace.SessionStateProxy.SetVariable("__m4dCliMigrationFailed", false);
            _runspace.SessionStateProxy.SetVariable("__m4dCliMigrationInvoked", false);
            _runspace.SessionStateProxy.SetVariable("__m4dCliDashboardHandle", null);
            ImportBundledModules();
        }
        catch
        {
            _runspace.Dispose();
            _modulePathScope.Dispose();
            throw;
        }
    }

    public IReadOnlyList<CommandInfo> GetExportedCommands()
    {
        using var powerShell = PowerShell.Create();
        powerShell.Runspace = _runspace;
        var commands = powerShell.AddCommand("Get-Command")
            .AddParameter("Module", "Migration4Dataverse")
            .Invoke()
            .Select(command => command.BaseObject)
            .OfType<CommandInfo>()
            .OrderBy(command => command.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (powerShell.HadErrors)
        {
            throw new InvalidOperationException("Could not enumerate Migration4Dataverse commands.");
        }
        if (commands.Length == 0)
        {
            throw new InvalidOperationException("The bundled module exports no user-facing commands.");
        }
        return commands;
    }

    public PowerShellCommandHelp GetCommandHelp(CommandInfo commandInfo)
    {
        using var powerShell = PowerShell.Create();
        powerShell.Runspace = _runspace;
        var helpOutput = powerShell.AddCommand("Get-Help")
            .AddParameter("Name", commandInfo.Name)
            .AddParameter("Full", true)
            .Invoke();
        if (powerShell.HadErrors)
        {
            var errors = string.Join(
                Environment.NewLine,
                powerShell.Streams.Error.Select(error => error.ToString()));
            throw new InvalidOperationException(
                $"Could not load PowerShell help for '{commandInfo.Name}'.{Environment.NewLine}{errors}");
        }

        var help = helpOutput.FirstOrDefault();
        var synopsis = GetHelpText(GetPropertyValue(help, "Synopsis"));
        if (string.IsNullOrWhiteSpace(synopsis) ||
            synopsis.StartsWith(commandInfo.Name + " ", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"PowerShell help for '{commandInfo.Name}' must include a descriptive synopsis. " +
                $"Retrieved synopsis: '{synopsis}'.");
        }

        var parameterCollection = GetPropertyValue(
            GetPropertyValue(help, "Parameters"),
            "Parameter");
        var parameterDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in EnumerateHelpItems(parameterCollection))
        {
            var name = GetHelpText(GetPropertyValue(parameter, "Name"));
            var description = GetHelpText(GetPropertyValue(parameter, "Description"));
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(description))
            {
                parameterDescriptions[name] = description;
            }
        }

        return new PowerShellCommandHelp(synopsis, parameterDescriptions);
    }

    public int Invoke(
        CommandInfo commandInfo,
        IReadOnlyDictionary<string, CliParameterValue> cliParameters,
        string? migrationFilePath,
        CliEventWriter? jsonOutput,
        CancellationToken cancellationToken,
        bool startDashboard = false)
    {
        _jsonOutput = jsonOutput;
        _hadErrors = false;
        _errorExitCode = ExitCodes.MigrationFailure;
        _runspace.SessionStateProxy.SetVariable("__m4dCliMigrationFailed", false);
        _runspace.SessionStateProxy.SetVariable("__m4dCliMigrationInvoked", false);
        _runspace.SessionStateProxy.SetVariable("__m4dCliDashboardHandle", null);
        _runspace.SessionStateProxy.SetVariable("__m4dCliStartDashboard", startDashboard);

        var isMigrationCommand =
            string.Equals(commandInfo.Name, "Invoke-M4dMigration", StringComparison.OrdinalIgnoreCase);
        if (isMigrationCommand)
        {
            if (string.IsNullOrWhiteSpace(migrationFilePath))
            {
                WriteError("Provide a .m4d migration definition file.", ExitCodes.InvalidArgumentsOrJob);
                return ExitCodes.InvalidArgumentsOrJob;
            }

            var scriptPath = Path.GetFullPath(migrationFilePath);
            if (!File.Exists(scriptPath) ||
                !string.Equals(Path.GetExtension(scriptPath), ".m4d", StringComparison.OrdinalIgnoreCase))
            {
                WriteError(
                    "The migration definition must be an existing .m4d file.",
                    ExitCodes.InvalidArgumentsOrJob);
                return ExitCodes.InvalidArgumentsOrJob;
            }

            Parser.ParseFile(scriptPath, out _, out var parseErrors);
            if (parseErrors.Length > 0)
            {
                WriteError(
                    string.Join(Environment.NewLine, parseErrors.Select(error => error.Message)),
                    ExitCodes.InvalidArgumentsOrJob);
                return ExitCodes.InvalidArgumentsOrJob;
            }

            var definitionInvocation = CreateInvocation(
                commandInfo,
                cliParameters,
                loadSessionDefaults: false);
            if (definitionInvocation.Error is not null)
            {
                WriteError(definitionInvocation.Error, ExitCodes.InvalidArgumentsOrJob);
                return ExitCodes.InvalidArgumentsOrJob;
            }

            _errorExitCode = ExitCodes.InvalidArgumentsOrJob;
            var migrationResult = InvokeMigrationDefinition(
                commandInfo,
                scriptPath,
                definitionInvocation.Parameters,
                cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                StopStartedDashboard();
                return ExitCodes.Cancelled;
            }
            var exitCode = migrationResult;
            if (_hadErrors)
            {
                exitCode = _errorExitCode;
            }
            else if (HasInvokedMigration() && HasFailedMigrationResult())
            {
                exitCode = ExitCodes.MigrationFailure;
            }
            if (startDashboard && HasStartedDashboard())
            {
                return WaitForDashboardToStop(cancellationToken, exitCode);
            }
            if (_hadErrors)
            {
                return _errorExitCode;
            }
            if (HasInvokedMigration())
            {
                return HasFailedMigrationResult()
                    ? ExitCodes.MigrationFailure
                    : migrationResult;
            }
            return migrationResult;
        }

        _errorExitCode = ExitCodes.InvalidArgumentsOrJob;

        var invocation = CreateInvocation(commandInfo, cliParameters);
        if (invocation.Error is not null)
        {
            WriteError(invocation.Error, ExitCodes.InvalidArgumentsOrJob);
            return ExitCodes.InvalidArgumentsOrJob;
        }

        _errorExitCode = ExitCodes.MigrationFailure;
        var result = InvokeCommandPipeline(commandInfo, invocation.Parameters, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            StopStartedDashboard();
            return ExitCodes.Cancelled;
        }
        if (_hadErrors)
        {
            return _errorExitCode;
        }
        if (HasFailedMigrationResult())
        {
            return ExitCodes.MigrationFailure;
        }

        if (string.Equals(
                commandInfo.Name,
                "Start-M4dMigrationDashboard",
                StringComparison.OrdinalIgnoreCase))
        {
            return WaitForDashboardToStop(cancellationToken, ExitCodes.Cancelled);
        }

        return result;
    }

    public void Dispose()
    {
        _runspace.Dispose();
        _modulePathScope.Dispose();
    }

    private void ImportBundledModules()
    {
        var dataverseVersion = GetAssemblyMetadata("DataversePowerShellModuleVersion");
        var migrationVersion = GetAssemblyMetadata("Migration4DataverseModuleVersion");
        var manifests = new[]
        {
            GetBundledManifestPath(
                "Rnwood.Dataverse.Data.PowerShell",
                dataverseVersion,
                "Rnwood.Dataverse.Data.PowerShell.psd1"),
            GetBundledManifestPath(
                "Migration4Dataverse",
                migrationVersion,
                "Migration4Dataverse.psd1")
        };

        foreach (var manifest in manifests)
        {
            if (!File.Exists(manifest))
            {
                throw new FileNotFoundException("A required bundled PowerShell module manifest is missing.", manifest);
            }

            using var powerShell = CreatePipeline(ExitCodes.RuntimeStartupFailure);
            powerShell.AddCommand("Import-Module")
                .AddParameter("Name", manifest)
                .AddParameter("ErrorAction", ActionPreference.Stop);
            var exitCode = InvokePipeline(powerShell, CancellationToken.None);
            if (exitCode != ExitCodes.Success)
            {
                throw new InvalidOperationException(
                    $"Could not load bundled PowerShell module '{Path.GetFileNameWithoutExtension(manifest)}'.");
            }
        }
    }

    private string GetBundledManifestPath(string moduleName, string moduleVersion, string fileName) =>
        Path.Combine(_modulesPath, moduleName, moduleVersion, fileName);

    private static string GetAssemblyMetadata(string name) =>
        typeof(PowerShellCliRuntime).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, name, StringComparison.Ordinal))
            ?.Value
        ?? throw new InvalidOperationException($"Assembly metadata '{name}' is missing.");

    private int InvokeMigrationDefinition(
        CommandInfo commandInfo,
        string path,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        var migrationParameters = new Dictionary<string, object?>(parameters, StringComparer.OrdinalIgnoreCase)
        {
            ["Path"] = path
        };

        var breakpoint = _runspace.Debugger.SetCommandBreakpoint(
            "Invoke-M4dMigrationGraph",
            ScriptBlock.Create("$global:__m4dCliMigrationInvoked = $true; continue"),
            null);
        try
        {
            return InvokeCommandPipeline(commandInfo, migrationParameters, cancellationToken);
        }
        finally
        {
            _runspace.Debugger.RemoveBreakpoint(breakpoint);
        }
    }

    private int InvokeCommandPipeline(
        CommandInfo commandInfo,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        using var powerShell = CreatePipeline(_errorExitCode);
        var command = powerShell.AddCommand(commandInfo.Name);
        foreach (var parameter in parameters)
        {
            if (parameter.Value is not null)
            {
                command.AddParameter(parameter.Key, parameter.Value);
            }
        }
        AddOutputObserver(powerShell);
        return InvokePipeline(powerShell, cancellationToken);
    }

    private void AddOutputObserver(PowerShell powerShell)
    {
        var observer = ScriptBlock.Create(
            "if ($null -ne $_ -and $_.GetType().FullName -eq " +
            "'Migration4Dataverse.Engine.Scheduling.DependencySchedulerResult' -and " +
            "($_.CompletedWithErrors -or $_.TargetRowsFailed -gt 0)) { " +
            "$global:__m4dCliMigrationFailed = $true }; " +
            "if ($null -ne $_ -and $_.GetType().FullName -eq " +
            "'Migration4Dataverse.Engine.Dashboard.MigrationDashboardHandle') { " +
            "$global:__m4dCliDashboardHandle = $_ }; $_");
        powerShell.AddCommand("ForEach-Object")
            .AddParameter("Process", observer)
            .AddCommand("Out-String")
            .AddParameter("Stream");
    }

    private int InvokePipeline(PowerShell powerShell, CancellationToken cancellationToken)
    {
        try
        {
            var output = new PSDataCollection<PSObject>();
            output.DataAdded += (_, eventArgs) =>
                WriteMessage("output", output[eventArgs.Index].ToString());
            var invocation = powerShell.BeginInvoke<PSObject, PSObject>(null, output);
            while (!invocation.IsCompleted)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    powerShell.Stop();
                    break;
                }
                invocation.AsyncWaitHandle.WaitOne(100);
            }
            powerShell.EndInvoke(invocation);
            return powerShell.HadErrors ? _errorExitCode : ExitCodes.Success;
        }
        catch (PipelineStoppedException) when (cancellationToken.IsCancellationRequested)
        {
            return ExitCodes.Cancelled;
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ExitCodes.Cancelled;
            }
            if (IsMigrationLifecycleFailure(exception))
            {
                _errorExitCode = ExitCodes.MigrationFailure;
            }
            if (!_hadErrors)
            {
                _hadErrors = true;
                WriteError(exception.Message, _errorExitCode);
            }
            return _errorExitCode;
        }
    }

    private InvocationResult CreateInvocation(
        CommandInfo commandInfo,
        IReadOnlyDictionary<string, CliParameterValue> cliParameters,
        bool loadSessionDefaults = true)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        string? commonDatabasePath = null;
        foreach (var supplied in cliParameters)
        {
            if (string.Equals(supplied.Key, "DatabasePath", StringComparison.OrdinalIgnoreCase) &&
                !commandInfo.Parameters.ContainsKey("DatabasePath"))
            {
                commonDatabasePath = supplied.Value.Value.ToString();
                continue;
            }

            if (!commandInfo.Parameters.TryGetValue(supplied.Key, out var metadata))
            {
                return new InvocationResult(
                    parameters,
                    $"The {commandInfo.Name} command has no '{supplied.Key}' parameter.");
            }
            try
            {
                parameters[supplied.Key] = ConvertParameterValue(metadata, supplied.Value);
            }
            catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
            {
                return new InvocationResult(
                    parameters,
                    $"Invalid value for --{ToKebabCase(supplied.Key)}: {exception.Message}");
            }
        }

        IEnumerable<ParameterMetadata> parametersToLoad = loadSessionDefaults
            ? commandInfo.Parameters.Values
            : Enumerable.Empty<ParameterMetadata>();
        foreach (var parameter in parametersToLoad)
        {
            if (parameters.ContainsKey(parameter.Name))
            {
                continue;
            }

            if (parameter.Name is "Store" or "Dashboard" or "InputStream" ||
                IsComplexParameter(parameter.ParameterType))
            {
                var value = _runspace.SessionStateProxy.GetVariable(parameter.Name);
                if (value is not null)
                {
                    parameters[parameter.Name] = value;
                }
            }
            else
            {
                var value = _runspace.SessionStateProxy.GetVariable(parameter.Name);
                if (value is not null)
                {
                    parameters[parameter.Name] = value;
                }
            }
        }

        commonDatabasePath ??= _runspace.SessionStateProxy.GetVariable("DatabasePath")?.ToString();
        if (commandInfo.Parameters.ContainsKey("Store") &&
            !parameters.ContainsKey("Store") &&
            !string.IsNullOrWhiteSpace(commonDatabasePath))
        {
            try
            {
                parameters["Store"] = OpenStore(commonDatabasePath);
            }
            catch (Exception exception)
            {
                return new InvocationResult(
                    parameters,
                    $"Could not open the migration store at '{commonDatabasePath}': {exception.Message}");
            }
        }

        return new InvocationResult(parameters, null);
    }

    private object? ConvertParameterValue(ParameterMetadata metadata, CliParameterValue value)
    {
        if (value.IsBase64Bytes)
        {
            return Convert.FromBase64String(value.Value.ToString() ?? string.Empty);
        }
        if (!value.IsJson)
        {
            return value.Value;
        }

        using var document = JsonDocument.Parse(value.Value.ToString() ?? string.Empty);
        var parsed = ConvertJsonElement(document.RootElement);
        var parameterType = metadata.ParameterType;
        if (parameterType == typeof(PSObject[]))
        {
            return ToObjectArray(parsed).Select(PSObject.AsPSObject).ToArray();
        }
        if (parameterType == typeof(ScriptBlock[]))
        {
            return ToObjectArray(parsed)
                .Select(script => ScriptBlock.Create(script?.ToString() ?? string.Empty))
                .ToArray();
        }
        if (parameterType == typeof(ModuleSpecification[]))
        {
            return ToObjectArray(parsed)
                .Select(specification => new ModuleSpecification(specification as IDictionary
                    as Hashtable ?? throw new JsonException("Each module specification must be a JSON object.")))
                .ToArray();
        }
        if (parameterType == typeof(IDictionary))
        {
            return parsed as IDictionary ?? throw new JsonException("The value must be a JSON object.");
        }
        return parsed;
    }

    private static object? ConvertJsonElement(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => ConvertJsonObject(element),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt32(out var intValue) => intValue,
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => throw new JsonException($"Unsupported JSON value kind '{element.ValueKind}'.")
        };

    private static IDictionary ConvertJsonObject(JsonElement element)
    {
        var result = new Hashtable(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = ConvertJsonElement(property.Value);
        }
        return result;
    }

    private static object?[] ToObjectArray(object? value) =>
        value switch
        {
            object?[] array => array,
            IEnumerable enumerable when value is not string => enumerable.Cast<object?>().ToArray(),
            _ => throw new JsonException("The value must be a JSON array.")
        };

    private static bool IsComplexParameter(Type type) =>
        type == typeof(IDictionary) ||
        typeof(IDictionary).IsAssignableFrom(type) ||
        type == typeof(PSObject[]) ||
        type == typeof(ScriptBlock[]) ||
        type == typeof(ModuleSpecification[]) ||
        type == typeof(Stream) ||
        type == typeof(object);

    private object OpenStore(string databasePath)
    {
        using var powerShell = PowerShell.Create();
        powerShell.Runspace = _runspace;
        var result = powerShell.AddCommand("New-M4dMigrationJobStore")
            .AddParameter("DatabasePath", databasePath)
            .Invoke();
        if (powerShell.HadErrors || result.Count != 1)
        {
            var message = powerShell.Streams.Error.Count > 0
                ? powerShell.Streams.Error[0].ToString()
                : "The store factory did not return one store.";
            throw new InvalidOperationException(message);
        }
        return result[0].BaseObject;
    }

    private int WaitForDashboardToStop(CancellationToken cancellationToken, int exitCodeAfterStop)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            cancellationToken.WaitHandle.WaitOne(200);
        }

        var stopResult = StopStartedDashboard();
        return stopResult == ExitCodes.Success ? exitCodeAfterStop : stopResult;
    }

    private int StopStartedDashboard()
    {
        var dashboard = _runspace.SessionStateProxy.GetVariable("__m4dCliDashboardHandle");
        if (dashboard is null)
        {
            return ExitCodes.Success;
        }

        using var powerShell = CreatePipeline(ExitCodes.MigrationFailure);
        powerShell.AddCommand("Stop-M4dMigrationDashboard")
            .AddParameter("Dashboard", dashboard);
        var result = InvokePipeline(powerShell, CancellationToken.None);
        _runspace.SessionStateProxy.SetVariable("__m4dCliDashboardHandle", null);
        return result;
    }

    private bool HasStartedDashboard() =>
        _runspace.SessionStateProxy.GetVariable("__m4dCliDashboardHandle") is not null;

    private PowerShell CreatePipeline(int errorExitCode)
    {
        var powerShell = PowerShell.Create();
        powerShell.Runspace = _runspace;
        powerShell.Streams.Error.DataAdded += (_, eventArgs) =>
        {
            var error = powerShell.Streams.Error[eventArgs.Index];
            _hadErrors = true;
            var reportedExitCode = errorExitCode;
            if (IsMigrationLifecycleFailure(error))
            {
                _errorExitCode = ExitCodes.MigrationFailure;
                reportedExitCode = ExitCodes.MigrationFailure;
            }
            else if (IsParameterError(error))
            {
                _errorExitCode = ExitCodes.InvalidArgumentsOrJob;
                reportedExitCode = ExitCodes.InvalidArgumentsOrJob;
            }
            else if (HasInvokedMigration())
            {
                _errorExitCode = ExitCodes.MigrationFailure;
                reportedExitCode = ExitCodes.MigrationFailure;
            }
            WriteError(error.ToString(), reportedExitCode);
        };
        powerShell.Streams.Warning.DataAdded += (_, eventArgs) =>
            WriteMessage("warning", powerShell.Streams.Warning[eventArgs.Index].Message);
        powerShell.Streams.Verbose.DataAdded += (_, eventArgs) =>
            WriteMessage("verbose", powerShell.Streams.Verbose[eventArgs.Index].Message);
        powerShell.Streams.Debug.DataAdded += (_, eventArgs) =>
            WriteMessage("debug", powerShell.Streams.Debug[eventArgs.Index].Message);
        powerShell.Streams.Information.DataAdded += (_, eventArgs) =>
            WriteMessage(
                "information",
                powerShell.Streams.Information[eventArgs.Index].MessageData?.ToString() ?? string.Empty);
        powerShell.Streams.Progress.DataAdded += (_, eventArgs) =>
            WriteProgress(powerShell.Streams.Progress[eventArgs.Index]);
        return powerShell;
    }

    private static bool IsParameterError(ErrorRecord error) =>
        error.Exception is ParameterBindingException or ValidationMetadataException ||
        error.FullyQualifiedErrorId.Contains("MissingMandatoryParameter", StringComparison.OrdinalIgnoreCase) ||
        error.FullyQualifiedErrorId.Contains("ParameterBinding", StringComparison.OrdinalIgnoreCase);

    private static bool IsMigrationLifecycleFailure(Exception exception) =>
        exception.ToString().Contains("Migration lifecycle failed:", StringComparison.OrdinalIgnoreCase) ||
        exception.ToString().Contains("Migration execution failed:", StringComparison.OrdinalIgnoreCase);

    private static bool IsMigrationLifecycleFailure(ErrorRecord error) =>
        error.FullyQualifiedErrorId.Contains(
            "M4dMigrationLifecycleFailure",
            StringComparison.OrdinalIgnoreCase) ||
        error.FullyQualifiedErrorId.Contains(
            "M4dMigrationExecutionFailure",
            StringComparison.OrdinalIgnoreCase) ||
        IsMigrationLifecycleFailure(error.Exception);

    private static string ToKebabCase(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "([a-z0-9])([A-Z])", "$1-$2")
            .ToLowerInvariant();

    private static object? GetPropertyValue(object? value, string propertyName) =>
        value is null ? null : PSObject.AsPSObject(value).Properties[propertyName]?.Value;

    private static string? GetHelpText(object? value)
    {
        var text = GetPropertyValue(value, "Text") ?? value;
        if (text is IEnumerable values and not string)
        {
            return string.Join(
                " ",
                values.Cast<object?>()
                    .Select(item => Convert.ToString(item))
                    .Where(item => !string.IsNullOrWhiteSpace(item)))
                .Trim();
        }

        return Convert.ToString(text)?.Trim();
    }

    private static IEnumerable<object> EnumerateHelpItems(object? value)
    {
        if (value is null)
        {
            yield break;
        }

        if (value is IEnumerable items and not string and not PSObject)
        {
            foreach (var item in items)
            {
                if (item is not null)
                {
                    yield return item;
                }
            }

            yield break;
        }

        yield return value;
    }

    private bool HasFailedMigrationResult() =>
        _runspace.SessionStateProxy.GetVariable("__m4dCliMigrationFailed") is true;

    private bool HasInvokedMigration() =>
        _runspace.SessionStateProxy.GetVariable("__m4dCliMigrationInvoked") is true;

    private void WriteProgress(ProgressRecord record)
    {
        var details = new Dictionary<string, object?>
        {
            ["activity"] = record.Activity,
            ["status"] = record.StatusDescription,
            ["percentComplete"] = record.PercentComplete >= 0 ? record.PercentComplete : null,
            ["currentOperation"] = string.IsNullOrWhiteSpace(record.CurrentOperation) ? null : record.CurrentOperation,
            ["recordType"] = record.RecordType.ToString()
        };
        if (_jsonOutput is not null)
        {
            _jsonOutput.Write("progress", details);
            return;
        }

        var percentage = record.PercentComplete >= 0 ? $" ({record.PercentComplete}%)" : string.Empty;
        WriteConsole(_standardOutput, $"{record.Activity}: {record.StatusDescription}{percentage}");
    }

    private void WriteMessage(string type, string message)
    {
        if (_jsonOutput is not null)
        {
            _jsonOutput.Write(type, new Dictionary<string, object?> { ["message"] = message });
            return;
        }

        var writer = type == "warning" ? _standardError : _standardOutput;
        var prefix = type switch
        {
            "warning" => "WARNING: ",
            "verbose" => "VERBOSE: ",
            "debug" => "DEBUG: ",
            _ => string.Empty
        };
        WriteConsole(writer, prefix + message);
    }

    private void WriteError(string message, int exitCode)
    {
        if (_jsonOutput is not null)
        {
            _jsonOutput.Write(
                "error",
                new Dictionary<string, object?>
                {
                    ["message"] = message,
                    ["exitCode"] = exitCode
                });
            return;
        }

        WriteConsole(_standardError, message);
    }

    private void WriteConsole(TextWriter writer, string message)
    {
        lock (_outputLock)
        {
            writer.WriteLine(message);
            writer.Flush();
        }
    }

    private sealed record InvocationResult(
        IReadOnlyDictionary<string, object?> Parameters,
        string? Error);
}

internal sealed record PowerShellCommandHelp(
    string Synopsis,
    IReadOnlyDictionary<string, string> ParameterDescriptions)
{
    private static readonly IReadOnlyDictionary<string, string> CommonParameterDescriptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Confirm"] = "Prompts you for confirmation before running the cmdlet.",
            ["Debug"] = "Displays programmer-level detail about the operation performed by the command.",
            ["Verbose"] = "Displays detailed information about the operation performed by the command.",
            ["WhatIf"] = "Shows what would happen if the cmdlet runs. The cmdlet is not run."
        };

    public string GetParameterDescription(string parameterName)
    {
        if (ParameterDescriptions.TryGetValue(parameterName, out var description))
        {
            return description;
        }

        if (CommonParameterDescriptions.TryGetValue(parameterName, out description))
        {
            return description;
        }

        throw new InvalidDataException(
            $"PowerShell help for parameter '{parameterName}' is missing a description.");
    }
}
