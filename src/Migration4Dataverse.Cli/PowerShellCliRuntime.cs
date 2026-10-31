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

    public int Invoke(
        CommandInfo commandInfo,
        IReadOnlyDictionary<string, CliParameterValue> cliParameters,
        string? setupScriptPath,
        CliEventWriter? jsonOutput,
        CancellationToken cancellationToken)
    {
        _jsonOutput = jsonOutput;
        _hadErrors = false;
        _errorExitCode = ExitCodes.MigrationFailure;
        _runspace.SessionStateProxy.SetVariable("__m4dCliMigrationFailed", false);
        _runspace.SessionStateProxy.SetVariable("__m4dCliMigrationInvoked", false);
        _runspace.SessionStateProxy.SetVariable("__m4dCliDashboardHandle", null);

        if (!string.IsNullOrWhiteSpace(setupScriptPath))
        {
            var scriptPath = Path.GetFullPath(setupScriptPath);
            if (!File.Exists(scriptPath) ||
                !string.Equals(Path.GetExtension(scriptPath), ".ps1", StringComparison.OrdinalIgnoreCase))
            {
                WriteError("The setup script must be an existing .ps1 file.", ExitCodes.InvalidArgumentsOrJob);
                return ExitCodes.InvalidArgumentsOrJob;
            }

            var scriptAst = Parser.ParseFile(scriptPath, out _, out var parseErrors);
            if (parseErrors.Length > 0)
            {
                WriteError(
                    string.Join(Environment.NewLine, parseErrors.Select(error => error.Message)),
                    ExitCodes.InvalidArgumentsOrJob);
                return ExitCodes.InvalidArgumentsOrJob;
            }

            var scriptParameterDefaults = CreateScriptParameterDefaults(
                commandInfo,
                cliParameters,
                out var parameterError);
            if (parameterError is not null)
            {
                WriteError(parameterError, ExitCodes.InvalidArgumentsOrJob);
                return ExitCodes.InvalidArgumentsOrJob;
            }

            var originalParameterDefaults = _runspace.SessionStateProxy.GetVariable("PSDefaultParameterValues");
            if (scriptParameterDefaults is not null)
            {
                var parameterDefaults = originalParameterDefaults is IDictionary existingDefaults
                    ? new Hashtable(existingDefaults, StringComparer.OrdinalIgnoreCase)
                    : new Hashtable(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry parameterDefault in scriptParameterDefaults)
                {
                    parameterDefaults[parameterDefault.Key] = parameterDefault.Value;
                }
                _runspace.SessionStateProxy.SetVariable("PSDefaultParameterValues", parameterDefaults);
            }

            _errorExitCode = ExitCodes.InvalidArgumentsOrJob;
            int scriptResult;
            try
            {
                scriptResult = InvokeScript(scriptPath, commandInfo.Name, cancellationToken);
            }
            finally
            {
                if (scriptParameterDefaults is not null)
                {
                    _runspace.SessionStateProxy.SetVariable(
                        "PSDefaultParameterValues",
                        originalParameterDefaults);
                }
            }
            if (cancellationToken.IsCancellationRequested)
            {
                return ExitCodes.Cancelled;
            }
            if (_hadErrors)
            {
                return _errorExitCode;
            }
            if (HasInvokedMigration())
            {
                return HasFailedMigrationResult()
                    ? ExitCodes.MigrationFailure
                    : scriptResult;
            }
        }

        _errorExitCode = ExitCodes.InvalidArgumentsOrJob;
        if (string.Equals(commandInfo.Name, "Invoke-M4dMigration", StringComparison.OrdinalIgnoreCase))
        {
            SetMigrationDefaults(cliParameters, setupScriptPath);
        }

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
            return WaitForDashboardToStop(cancellationToken);
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

    private int InvokeScript(string scriptPath, string commandName, CancellationToken cancellationToken)
    {
        _errorExitCode = ExitCodes.InvalidArgumentsOrJob;
        var breakpoint = _runspace.Debugger.SetCommandBreakpoint(
            commandName,
            ScriptBlock.Create("$global:__m4dCliMigrationInvoked = $true; continue"),
            null);
        try
        {
            using var powerShell = CreatePipeline(_errorExitCode);
            powerShell.AddScript($". '{scriptPath.Replace("'", "''", StringComparison.Ordinal)}'", useLocalScope: false);
            AddOutputObserver(powerShell);
            return InvokePipeline(powerShell, cancellationToken);
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
        IReadOnlyDictionary<string, CliParameterValue> cliParameters)
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

        foreach (var parameter in commandInfo.Parameters.Values)
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
        if (string.Equals(commandInfo.Name, "Invoke-M4dMigration", StringComparison.OrdinalIgnoreCase))
        {
            if (!parameters.TryGetValue("Steps", out var steps))
            {
                return new InvocationResult(parameters, "The migration job must define one or more $Steps.");
            }
            if (ValidateMigrationSteps(steps) is { } validationError)
            {
                return new InvocationResult(parameters, validationError);
            }
        }

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

        if (string.Equals(commandInfo.Name, "Invoke-M4dMigration", StringComparison.OrdinalIgnoreCase))
        {
            var scriptPath = _runspace.SessionStateProxy.GetVariable("M4dCliScriptPath")?.ToString();
            if (string.IsNullOrWhiteSpace(parameters.GetValueOrDefault("DatabasePath")?.ToString()))
            {
                parameters["DatabasePath"] = Path.Combine(Environment.CurrentDirectory, "migration.sqlite3");
            }
            if (string.IsNullOrWhiteSpace(parameters.GetValueOrDefault("RunName")?.ToString()))
            {
                parameters["RunName"] = string.IsNullOrWhiteSpace(scriptPath)
                    ? "Migration"
                    : Path.GetFileNameWithoutExtension(scriptPath);
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

    private static string? ValidateMigrationSteps(object? value)
    {
        while (value is PSObject psObject && psObject.BaseObject is not null)
        {
            value = psObject.BaseObject;
        }

        var steps = value switch
        {
            IDictionary => [value],
            IEnumerable enumerable when value is not string => enumerable.Cast<object?>().ToArray(),
            _ => Array.Empty<object?>()
        };
        if (steps.Length == 0)
        {
            return "The migration job must define one or more $Steps.";
        }

        for (var index = 0; index < steps.Length; index++)
        {
            var step = steps[index];
            var id = GetStepProperty(step, "Id");
            var type = GetStepProperty(step, "Type");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(type))
            {
                return $"Migration step {index + 1} must define non-empty Id and Type values.";
            }
        }

        return null;
    }

    private static string? GetStepProperty(object? step, string propertyName)
    {
        while (step is PSObject psObject && psObject.BaseObject is not null)
        {
            step = psObject.BaseObject;
        }

        if (step is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                if (string.Equals(entry.Key?.ToString(), propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Value?.ToString();
                }
            }
            return null;
        }

        return step is null
            ? null
            : PSObject.AsPSObject(step).Properties[propertyName]?.Value?.ToString();
    }

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

    private void SetMigrationDefaults(
        IReadOnlyDictionary<string, CliParameterValue> cliParameters,
        string? setupScriptPath)
    {
        _runspace.SessionStateProxy.SetVariable("M4dCliScriptPath", setupScriptPath);
        if (cliParameters.TryGetValue("DatabasePath", out var databasePath) &&
            databasePath.Value is string path)
        {
            _runspace.SessionStateProxy.SetVariable("DatabasePath", path);
        }
        if (cliParameters.TryGetValue("RunName", out var runName) &&
            runName.Value is string name)
        {
            _runspace.SessionStateProxy.SetVariable("RunName", name);
        }
    }

    private Hashtable? CreateScriptParameterDefaults(
        CommandInfo commandInfo,
        IReadOnlyDictionary<string, CliParameterValue> cliParameters,
        out string? error)
    {
        error = null;
        if (cliParameters.Count == 0)
        {
            return null;
        }

        var defaults = new Hashtable(StringComparer.OrdinalIgnoreCase);
        foreach (var supplied in cliParameters)
        {
            if (!commandInfo.Parameters.TryGetValue(supplied.Key, out var metadata))
            {
                error = $"The {commandInfo.Name} command has no '{supplied.Key}' parameter.";
                return null;
            }

            try
            {
                defaults[$"{commandInfo.Name}:{metadata.Name}"] =
                    ConvertParameterValue(metadata, supplied.Value);
            }
            catch (Exception exception) when (
                exception is FormatException or JsonException or ArgumentException)
            {
                error = $"Invalid value for --{ToKebabCase(supplied.Key)}: {exception.Message}";
                return null;
            }
        }

        return defaults;
    }

    private int WaitForDashboardToStop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            cancellationToken.WaitHandle.WaitOne(200);
        }

        var dashboard = _runspace.SessionStateProxy.GetVariable("__m4dCliDashboardHandle");
        if (dashboard is null)
        {
            return ExitCodes.Cancelled;
        }

        using var powerShell = CreatePipeline(ExitCodes.MigrationFailure);
        powerShell.AddCommand("Stop-M4dMigrationDashboard")
            .AddParameter("Dashboard", dashboard);
        InvokePipeline(powerShell, CancellationToken.None);
        return ExitCodes.Cancelled;
    }

    private PowerShell CreatePipeline(int errorExitCode)
    {
        var powerShell = PowerShell.Create();
        powerShell.Runspace = _runspace;
        powerShell.Streams.Error.DataAdded += (_, eventArgs) =>
        {
            var error = powerShell.Streams.Error[eventArgs.Index];
            _hadErrors = true;
            var reportedExitCode = errorExitCode;
            if (IsParameterError(error))
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

    private static string ToKebabCase(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "([a-z0-9])([A-Z])", "$1-$2")
            .ToLowerInvariant();

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
