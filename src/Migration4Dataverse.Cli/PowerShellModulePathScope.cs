namespace Migration4Dataverse.Cli;

internal sealed class PowerShellModulePathScope : IDisposable
{
    private static readonly object EnvironmentLock = new();
    private readonly string? _originalValue;

    public PowerShellModulePathScope(params string[] modulesPaths)
    {
        Monitor.Enter(EnvironmentLock);
        try
        {
            foreach (var modulesPath in modulesPaths)
            {
                if (!Directory.Exists(modulesPath))
                {
                    throw new DirectoryNotFoundException(
                        $"A required PowerShell module directory was not found: '{modulesPath}'.");
                }
            }

            _originalValue = Environment.GetEnvironmentVariable("PSModulePath");
            var searchPaths = modulesPaths.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(_originalValue))
            {
                searchPaths = searchPaths.Append(_originalValue);
            }

            Environment.SetEnvironmentVariable(
                "PSModulePath",
                string.Join(Path.PathSeparator, searchPaths));
        }
        catch
        {
            Monitor.Exit(EnvironmentLock);
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            Environment.SetEnvironmentVariable("PSModulePath", _originalValue);
        }
        finally
        {
            Monitor.Exit(EnvironmentLock);
        }
    }
}
