using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Migration4Dataverse.Engine.Storage;

public static class MigrationStoreFactory
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, OwnedStore> OwnedStores = new(PathComparer);

    public static IMigrationStore Open(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        var pipeName = GetPipeName(fullPath);

        lock (Gate)
        {
            if (OwnedStores.TryGetValue(fullPath, out var existing))
            {
                return existing.Store;
            }

            if (MigrationStoreRpcClient.TryPing(pipeName))
            {
                return MigrationStoreRpcClient.CreateProxy(pipeName);
            }

            var directory = Path.GetDirectoryName(fullPath)
                ?? throw new ArgumentException("The database path must have a parent directory.", nameof(databasePath));
            Directory.CreateDirectory(directory);

            FileStream ownerLock;
            try
            {
                ownerLock = new FileStream(
                    fullPath + ".owner.lock",
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException lockException)
            {
                if (MigrationStoreRpcClient.WaitForServer(pipeName, TimeSpan.FromSeconds(10)))
                {
                    return MigrationStoreRpcClient.CreateProxy(pipeName);
                }

                throw new InvalidOperationException(
                    $"Another process owns migration database '{fullPath}', but its local store service did not become available.",
                    lockException);
            }

            MigrationStoreRpcServer? server = null;
            try
            {
                var store = new SqliteMigrationStore(fullPath);
                server = new MigrationStoreRpcServer(store, pipeName, ownerLock);
                server.Start();
                OwnedStores.Add(fullPath, new OwnedStore(store, server));
                return store;
            }
            catch
            {
                if (server is null)
                {
                    ownerLock.Dispose();
                }
                else
                {
                    server.Dispose();
                }

                throw;
            }
        }
    }

    public static void RunServer(string databasePath, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(databasePath);
        var store = Open(fullPath);
        MigrationStoreRpcServer server;
        lock (Gate)
        {
            if (!OwnedStores.TryGetValue(fullPath, out var owned) ||
                !ReferenceEquals(store, owned.Store))
            {
                throw new InvalidOperationException(
                    $"Migration database '{fullPath}' is already owned by another process.");
            }

            server = owned.Server;
        }

        try
        {
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StopServer(fullPath, server);
        }
    }

    internal static IMigrationStore ConnectToOwner(string databasePath)
    {
        var pipeName = GetPipeName(Path.GetFullPath(databasePath));
        if (!MigrationStoreRpcClient.WaitForServer(pipeName, TimeSpan.FromSeconds(5)))
        {
            throw new IOException($"The migration database owner for '{databasePath}' is not available.");
        }

        return MigrationStoreRpcClient.CreateProxy(pipeName);
    }

    internal static void StopOwnedServer(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        MigrationStoreRpcServer? server;
        lock (Gate)
        {
            server = OwnedStores.TryGetValue(fullPath, out var owned) ? owned.Server : null;
        }

        if (server is not null)
        {
            StopServer(fullPath, server);
        }
    }

    private static void StopServer(string fullPath, MigrationStoreRpcServer server)
    {
        lock (Gate)
        {
            if (OwnedStores.TryGetValue(fullPath, out var owned) &&
                ReferenceEquals(owned.Server, server))
            {
                OwnedStores.Remove(fullPath);
            }
        }

        server.Dispose();
    }

    private static string GetPipeName(string fullPath)
    {
        var canonicalPath = OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath)))[..32].ToLowerInvariant();
        return $"m4d-{hash}";
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record OwnedStore(SqliteMigrationStore Store, MigrationStoreRpcServer Server);
}

public sealed class RemoteStoreException : Exception
{
    internal RemoteStoreException(string remoteType, string message, string? remoteStackTrace, int? sqliteErrorCode)
        : base($"Remote {remoteType}: {message}")
    {
        RemoteType = remoteType;
        RemoteStackTrace = remoteStackTrace;
        SqliteErrorCode = sqliteErrorCode;
    }

    public string RemoteType { get; }

    public string? RemoteStackTrace { get; }

    public int? SqliteErrorCode { get; }
}

internal sealed class MigrationStoreRpcServer : IDisposable
{
    private const int MaximumConcurrentRequests = 8;
    private static readonly IReadOnlyDictionary<string, MethodInfo> StoreMethods =
        typeof(IMigrationStore).GetMethods().ToDictionary(method => method.Name, StringComparer.Ordinal);
    private readonly IMigrationStore _store;
    private readonly string _pipeName;
    private readonly FileStream _ownerLock;
    private readonly CancellationTokenSource _shutdown = new();
    private NamedPipeServerStream[]? _pipes;
    private Task[]? _acceptTasks;

    public MigrationStoreRpcServer(IMigrationStore store, string pipeName, FileStream ownerLock)
    {
        _store = store;
        _pipeName = pipeName;
        _ownerLock = ownerLock;
    }

    public void Start()
    {
        var pipes = new List<NamedPipeServerStream>(MaximumConcurrentRequests);
        try
        {
            for (var index = 0; index < MaximumConcurrentRequests; index++)
            {
                pipes.Add(new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    MaximumConcurrentRequests,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly));
            }
        }
        catch
        {
            foreach (var pipe in pipes)
            {
                pipe.Dispose();
            }

            throw;
        }

        _pipes = pipes.ToArray();
        _acceptTasks = _pipes
            .Select(pipe => Task.Run(() => AcceptClientsAsync(pipe)))
            .ToArray();
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try
        {
            if (_acceptTasks is not null)
            {
                Task.WhenAll(_acceptTasks).GetAwaiter().GetResult();
            }
        }
        finally
        {
            if (_pipes is not null)
            {
                foreach (var pipe in _pipes)
                {
                    pipe.Dispose();
                }
            }

            _ownerLock.Dispose();
            _shutdown.Dispose();
        }
    }

    private async Task AcceptClientsAsync(NamedPipeServerStream pipe)
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                await HandleClientAsync(pipe).ConfigureAwait(false);
                pipe.Disconnect();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceError($"Migration store RPC listener '{_pipeName}' stopped: {exception}");
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe)
    {
        StreamWriter? writer = null;
        try
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            var requestJson = await reader.ReadLineAsync(_shutdown.Token).ConfigureAwait(false);
            if (requestJson is null)
            {
                return;
            }

            var request = JsonSerializer.Deserialize<MigrationStoreRpcRequest>(
                requestJson,
                MigrationStoreRpcJson.Options)
                ?? throw new InvalidDataException("The migration store request was empty.");
            var response = Dispatch(request);
            await writer.WriteLineAsync(JsonSerializer.Serialize(
                response,
                MigrationStoreRpcJson.Options)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Trace.TraceError($"Migration store RPC request failed: {exception}");
            if (writer is not null)
            {
                try
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(
                        MigrationStoreRpcResponse.Failure(exception),
                        MigrationStoreRpcJson.Options)).ConfigureAwait(false);
                }
                catch (Exception writeException)
                {
                    Trace.TraceError($"Migration store RPC error response failed: {writeException}");
                }
            }
        }
        finally
        {
            try
            {
                writer?.Dispose();
            }
            catch (Exception exception)
            {
                Trace.TraceError($"Migration store RPC writer disposal failed: {exception}");
            }
        }
    }

    private MigrationStoreRpcResponse Dispatch(MigrationStoreRpcRequest request)
    {
        try
        {
            if (request.Method == "Ping")
            {
                return MigrationStoreRpcResponse.Success(JsonSerializer.SerializeToElement(true));
            }

            if (!StoreMethods.TryGetValue(request.Method, out var method))
            {
                throw new InvalidDataException($"Unknown migration store method '{request.Method}'.");
            }

            var parameters = method.GetParameters();
            if (request.Arguments.Length != parameters.Length)
            {
                throw new InvalidDataException($"Invalid argument count for migration store method '{request.Method}'.");
            }

            var arguments = new object?[parameters.Length];
            for (var index = 0; index < parameters.Length; index++)
            {
                arguments[index] = request.Arguments[index].Deserialize(
                    parameters[index].ParameterType,
                    MigrationStoreRpcJson.Options);
            }

            var result = method.Invoke(_store, arguments);
            return method.ReturnType == typeof(void) || result is null
                ? MigrationStoreRpcResponse.Success(null)
                : MigrationStoreRpcResponse.Success(JsonSerializer.SerializeToElement(
                    result,
                    method.ReturnType,
                    MigrationStoreRpcJson.Options));
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            return MigrationStoreRpcResponse.Failure(exception.InnerException);
        }
        catch (Exception exception)
        {
            return MigrationStoreRpcResponse.Failure(exception);
        }
    }

}

public static class MigrationStoreProxyDispatcher
{
    private static readonly IReadOnlyDictionary<string, MethodInfo> StoreMethods =
        typeof(IMigrationStore).GetMethods().ToDictionary(method => method.Name, StringComparer.Ordinal);

    public static object? Invoke(string pipeName, string methodName, object?[] args)
    {
        var targetMethod = StoreMethods[methodName];
        var parameters = targetMethod.GetParameters();
        var serializedArguments = new JsonElement[parameters.Length];
        for (var index = 0; index < parameters.Length; index++)
        {
            serializedArguments[index] = JsonSerializer.SerializeToElement(
                args[index],
                parameters[index].ParameterType,
                MigrationStoreRpcJson.Options);
        }

        var response = MigrationStoreRpcClient.Invoke(
            pipeName,
            new MigrationStoreRpcRequest(methodName, serializedArguments));
        if (!response.Succeeded)
        {
            throw CreateRemoteException(response.Error!);
        }

        return targetMethod.ReturnType == typeof(void)
            ? null
            : response.Result?.Deserialize(targetMethod.ReturnType, MigrationStoreRpcJson.Options);
    }

    private static Exception CreateRemoteException(MigrationStoreRpcError error)
    {
        var type = error.ExceptionType;
        return type switch
        {
            "System.ArgumentException" => new ArgumentException(error.Message, error.ParamName),
            "System.ArgumentNullException" => new ArgumentNullException(error.ParamName, error.Message),
            "System.ArgumentOutOfRangeException" => new ArgumentOutOfRangeException(
                error.ParamName,
                error.Message),
            "System.InvalidOperationException" => new InvalidOperationException(error.Message),
            "System.Collections.Generic.KeyNotFoundException" => new KeyNotFoundException(error.Message),
            _ => new RemoteStoreException(type, error.Message, error.StackTrace, error.SqliteErrorCode)
        };
    }
}

internal static class MigrationStoreProxyFactory
{
    private static readonly Lazy<Type> ProxyType = new(BuildProxyType, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IMigrationStore Create(string pipeName) =>
        (IMigrationStore)Activator.CreateInstance(ProxyType.Value, pipeName)!;

    private static Type BuildProxyType()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("Migration4Dataverse.RemoteStoreProxies"),
            AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("Migration4Dataverse.RemoteStoreProxies");
        var typeBuilder = module.DefineType(
            "Migration4Dataverse.Engine.Storage.RemoteMigrationStoreProxy",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
        typeBuilder.AddInterfaceImplementation(typeof(IMigrationStore));
        var pipeNameField = typeBuilder.DefineField("_pipeName", typeof(string), FieldAttributes.Private | FieldAttributes.InitOnly);
        var constructor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [typeof(string)]);
        var constructorIl = constructor.GetILGenerator();
        constructorIl.Emit(OpCodes.Ldarg_0);
        constructorIl.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        constructorIl.Emit(OpCodes.Ldarg_0);
        constructorIl.Emit(OpCodes.Ldarg_1);
        constructorIl.Emit(OpCodes.Stfld, pipeNameField);
        constructorIl.Emit(OpCodes.Ret);

        var dispatchMethod = typeof(MigrationStoreProxyDispatcher).GetMethod(
            nameof(MigrationStoreProxyDispatcher.Invoke))!;
        foreach (var interfaceMethod in typeof(IMigrationStore).GetMethods())
        {
            var parameters = interfaceMethod.GetParameters();
            var parameterTypes = parameters.Select(parameter => parameter.ParameterType).ToArray();
            var methodBuilder = typeBuilder.DefineMethod(
                interfaceMethod.Name,
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final |
                MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                interfaceMethod.ReturnType,
                parameterTypes);
            for (var index = 0; index < parameters.Length; index++)
            {
                var parameter = parameters[index];
                var parameterBuilder = methodBuilder.DefineParameter(
                    index + 1,
                    parameter.Attributes,
                    parameter.Name);
                if (parameter.HasDefaultValue)
                {
                    parameterBuilder.SetConstant(parameter.DefaultValue);
                }
            }

            var il = methodBuilder.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, pipeNameField);
            il.Emit(OpCodes.Ldstr, interfaceMethod.Name);
            il.Emit(OpCodes.Ldc_I4, parameters.Length);
            il.Emit(OpCodes.Newarr, typeof(object));
            for (var index = 0; index < parameters.Length; index++)
            {
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4, index);
                il.Emit(OpCodes.Ldarg, index + 1);
                if (parameterTypes[index].IsValueType)
                {
                    il.Emit(OpCodes.Box, parameterTypes[index]);
                }
                il.Emit(OpCodes.Stelem_Ref);
            }
            il.Emit(OpCodes.Call, dispatchMethod);
            if (interfaceMethod.ReturnType == typeof(void))
            {
                il.Emit(OpCodes.Pop);
            }
            else if (interfaceMethod.ReturnType.IsValueType)
            {
                il.Emit(OpCodes.Unbox_Any, interfaceMethod.ReturnType);
            }
            else
            {
                il.Emit(OpCodes.Castclass, interfaceMethod.ReturnType);
            }
            il.Emit(OpCodes.Ret);
            typeBuilder.DefineMethodOverride(methodBuilder, interfaceMethod);
        }

        return typeBuilder.CreateType()
            ?? throw new InvalidOperationException("The remote migration store proxy type could not be created.");
    }
}

internal static class MigrationStoreRpcClient
{
    public static IMigrationStore CreateProxy(string pipeName) => MigrationStoreProxyFactory.Create(pipeName);

    public static bool TryPing(string pipeName)
    {
        try
        {
            var response = Invoke(pipeName, new MigrationStoreRpcRequest("Ping", []), 150);
            return response.Succeeded;
        }
        catch (IOException)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public static bool WaitForServer(string pipeName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (TryPing(pipeName))
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return false;
    }

    public static MigrationStoreRpcResponse Invoke(
        string pipeName,
        MigrationStoreRpcRequest request,
        int connectTimeoutMilliseconds = 5000)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        pipe.Connect(connectTimeoutMilliseconds);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        writer.WriteLine(JsonSerializer.Serialize(request, MigrationStoreRpcJson.Options));
        var responseJson = reader.ReadLine()
            ?? throw new IOException("The migration database owner closed the IPC connection without a response.");
        return JsonSerializer.Deserialize<MigrationStoreRpcResponse>(
                   responseJson,
                   MigrationStoreRpcJson.Options)
               ?? throw new InvalidDataException("The migration database owner returned an empty IPC response.");
    }
}

internal sealed record MigrationStoreRpcRequest(string Method, JsonElement[] Arguments);

internal sealed record MigrationStoreRpcResponse(
    bool Succeeded,
    JsonElement? Result,
    MigrationStoreRpcError? Error)
{
    public static MigrationStoreRpcResponse Success(JsonElement? result) => new(true, result, null);

    public static MigrationStoreRpcResponse Failure(Exception exception)
    {
        var sqliteException = exception as SqliteException;
        var argumentException = exception as ArgumentException;
        return new(
            false,
            null,
            new MigrationStoreRpcError(
                exception.GetType().FullName ?? exception.GetType().Name,
                exception.Message,
                exception.StackTrace,
                argumentException?.ParamName,
                sqliteException?.SqliteErrorCode));
    }
}

internal sealed record MigrationStoreRpcError(
    string ExceptionType,
    string Message,
    string? StackTrace,
    string? ParamName,
    int? SqliteErrorCode);

internal static class MigrationStoreRpcJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new ReadOnlyDictionaryJsonConverterFactory());
        return options;
    }

    private sealed class ReadOnlyDictionaryJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsGenericType &&
            typeToConvert.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            var arguments = typeToConvert.GetGenericArguments();
            return (JsonConverter)Activator.CreateInstance(
                typeof(ReadOnlyDictionaryJsonConverter<,>).MakeGenericType(arguments))!;
        }
    }

    private sealed class ReadOnlyDictionaryJsonConverter<TKey, TValue> :
        JsonConverter<IReadOnlyDictionary<TKey, TValue>>
        where TKey : notnull
    {
        public override IReadOnlyDictionary<TKey, TValue> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var result = new Dictionary<TKey, TValue>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var key = item.GetProperty("key").Deserialize<TKey>(options)
                    ?? throw new JsonException("A migration store dictionary key cannot be null.");
                var value = item.GetProperty("value").Deserialize<TValue>(options);
                result.Add(key, value!);
            }

            return result;
        }

        public override void Write(
            Utf8JsonWriter writer,
            IReadOnlyDictionary<TKey, TValue> value,
            JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var item in value)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("key");
                JsonSerializer.Serialize(writer, item.Key, options);
                writer.WritePropertyName("value");
                JsonSerializer.Serialize(writer, item.Value, options);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }
    }
}
