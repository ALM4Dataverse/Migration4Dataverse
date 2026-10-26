using System.Reflection;
using System.Runtime.InteropServices;

namespace Migration4Dataverse.Engine.Connectors;

internal static class DuckDbNativeLibraryResolver
{
    private static readonly object RegistrationLock = new();
    private static bool _isRegistered;

    public static void Register()
    {
        lock (RegistrationLock)
        {
            if (_isRegistered)
            {
                return;
            }

            var bindingsAssembly = Assembly.Load(new AssemblyName("DuckDB.NET.Bindings"));
            NativeLibrary.SetDllImportResolver(bindingsAssembly, Resolve);
            _isRegistered = true;
        }
    }

    private static IntPtr Resolve(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "duckdb", StringComparison.Ordinal))
        {
            return IntPtr.Zero;
        }

        var runtimeIdentifier = GetRuntimeIdentifier();
        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "duckdb.dll"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "libduckdb.dylib"
                : "libduckdb.so";
        var assemblyDirectory = Path.GetDirectoryName(assembly.Location);
        if (string.IsNullOrEmpty(assemblyDirectory) || string.IsNullOrEmpty(runtimeIdentifier))
        {
            return IntPtr.Zero;
        }

        var nativeLibraryPath = Path.Combine(
            assemblyDirectory,
            "runtimes",
            runtimeIdentifier,
            "native",
            fileName);
        return File.Exists(nativeLibraryPath)
            ? NativeLibrary.Load(nativeLibraryPath)
            : IntPtr.Zero;
    }

    private static string GetRuntimeIdentifier()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "osx";
        }

        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => null
        };
        if (architecture is null)
        {
            return string.Empty;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return $"win-{architecture}";
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var linuxRuntime = RuntimeInformation.RuntimeIdentifier.Contains(
                "musl",
                StringComparison.OrdinalIgnoreCase)
                ? "linux-musl"
                : "linux";
            return $"{linuxRuntime}-{architecture}";
        }
        return string.Empty;
    }
}
