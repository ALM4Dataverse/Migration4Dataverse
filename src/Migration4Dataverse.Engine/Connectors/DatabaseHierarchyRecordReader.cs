using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.Connectors;

public sealed class DatabaseHierarchyRecordReader : IHierarchyRecordReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HierarchySourceLevel _level;
    private readonly DbConnection _connection;
    private readonly DatabaseSourceOptions _options;
    private DbCommand? _command;
    private DbDataReader? _reader;
    private JsonElement? _lookahead;
    private IDisposable? _connectionLease;
    private bool _openedConnection;
    private bool _initialized;
    private bool _disposed;

    public DatabaseHierarchyRecordReader(
        HierarchySourceLevel level,
        DbConnection connection,
        DatabaseSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);
        if (level.Type is not ("sqlite" or "duckdb"))
        {
            throw new ArgumentException("A database reader requires a SQLite or DuckDB source.", nameof(level));
        }

        DatabaseConnectorSupport.ValidateConnection(level.Type, connection, $"source '{level.StepId}'");
        options.Validate(level.StepId);
        _level = level;
        _connection = connection;
        _options = options;
    }

    public long RecordCount { get; private set; }

    public static async Task<long> CountRecordsAsync(
        DbConnection connection,
        string stepId,
        DatabaseSourceOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(stepId);
        var countQuery = options.GetCountQuery();

        using var connectionLease = await DatabaseConnectionGate.AcquireAsync(
            connection,
            cancellationToken).ConfigureAwait(false);
        var openedConnection = false;
        try
        {
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                openedConnection = true;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = countQuery;
            command.CommandTimeout = options.CommandTimeoutSeconds;
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is null or DBNull)
            {
                throw new InvalidDataException(
                    $"Database source '{stepId}' CountQuery returned no count value.");
            }

            decimal count;
            try
            {
                count = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (
                exception is InvalidCastException or FormatException or OverflowException)
            {
                throw new InvalidDataException(
                    $"Database source '{stepId}' CountQuery must return a non-negative integer.",
                    exception);
            }

            if (count < 0 || decimal.Truncate(count) != count || count > long.MaxValue)
            {
                throw new InvalidDataException(
                    $"Database source '{stepId}' CountQuery must return a non-negative integer.");
            }

            return (long)count;
        }
        finally
        {
            if (openedConnection)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    public async ValueTask<JsonElement?> PeekAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lookahead.HasValue)
        {
            return _lookahead.Value;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (!await _reader!.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var record = new Dictionary<string, object?>(_reader.FieldCount, StringComparer.Ordinal);
        for (var index = 0; index < _reader.FieldCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            record.Add(_reader.GetName(index), DatabaseConnectorSupport.ToJsonValue(_reader.GetValue(index)));
        }

        _lookahead = JsonSerializer.SerializeToElement(record);
        RecordCount++;
        return _lookahead.Value;
    }

    public JsonElement Take()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = _lookahead
            ?? throw new InvalidOperationException($"Database source '{_level.StepId}' has no buffered record.");
        _lookahead = null;
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_reader is not null)
            {
                await _reader.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                _command?.Dispose();
            }
            finally
            {
                try
                {
                    if (_openedConnection)
                    {
                        await _connection.CloseAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    _connectionLease?.Dispose();
                    _disposed = true;
                }
            }
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        _connectionLease = await DatabaseConnectionGate.AcquireAsync(
            _connection,
            cancellationToken).ConfigureAwait(false);
        if (_connection.State != ConnectionState.Open)
        {
            await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            _openedConnection = true;
        }

        _command = _connection.CreateCommand();
        _command.CommandText = _options.Query;
        _command.CommandTimeout = _options.CommandTimeoutSeconds;
        _reader = await _command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (_reader.FieldCount == 0)
        {
            throw new InvalidDataException(
                $"Database source '{_level.StepId}' Query must return a result set.");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < _reader.FieldCount; index++)
        {
            var name = _reader.GetName(index);
            if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
            {
                throw new InvalidDataException(
                    $"Database source '{_level.StepId}' returned an empty or duplicate column name.");
            }
        }

        _initialized = true;
    }
}

public sealed class DatabaseSourceOptions
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public string Query { get; init; } = string.Empty;

    public string? CountQuery { get; init; }

    public int CommandTimeoutSeconds { get; init; } = 30;

    public string GetCountQuery()
    {
        if (!string.IsNullOrWhiteSpace(CountQuery))
        {
            return CountQuery;
        }

        var query = Query.Trim();
        if (query.EndsWith(';'))
        {
            query = query[..^1].TrimEnd();
        }

        return $"SELECT COUNT(*) FROM ({query}) AS __migration_count";
    }

    public void Validate(string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        if (string.IsNullOrWhiteSpace(Query))
        {
            throw new ArgumentException($"Database source '{stepId}' Options.Query is required.");
        }
        if (CountQuery is not null && string.IsNullOrWhiteSpace(CountQuery))
        {
            throw new ArgumentException($"Database source '{stepId}' Options.CountQuery cannot be empty.");
        }
        if (CommandTimeoutSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CommandTimeoutSeconds),
                $"Database source '{stepId}' CommandTimeoutSeconds cannot be negative.");
        }
    }

    public static DatabaseSourceOptions FromJson(string optionsJson, string stepId)
    {
        try
        {
            return JsonSerializer.Deserialize<DatabaseSourceOptions>(optionsJson, JsonOptions)
                ?? throw new InvalidDataException($"Database source options for '{stepId}' are empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Database source options for '{stepId}' are invalid JSON.",
                exception);
        }
    }
}
