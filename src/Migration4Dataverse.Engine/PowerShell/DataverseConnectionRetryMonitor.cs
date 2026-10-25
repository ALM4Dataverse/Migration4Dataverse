using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Globalization;

namespace Migration4Dataverse.Engine.PowerShell;

internal sealed class DataverseConnectionRetryMonitor : IDisposable
{
    private static readonly Regex RetryPattern = new(
        @"\bretry(?:ing|ies)?\b|retry-after",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ThrottlePattern = new(
        @"\b429\b|throttl|service protection|too many concurrent|request limit",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly object _connection;
    private readonly MethodInfo? _getLogs;
    private readonly PropertyInfo? _logCollectionEnabled;
    private readonly object? _originalLogCollectionEnabled;
    private readonly PropertyInfo? _maxRetryCount;
    private readonly object? _originalMaxRetryCount;
    private readonly bool _disableRetries;
    private readonly HashSet<string> _seenRetryEntries = new(StringComparer.Ordinal);

    public DataverseConnectionRetryMonitor(object connection, bool disableRetries)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
        _disableRetries = disableRetries;
        var connectionType = connection.GetType();
        _getLogs = connectionType.GetMethod(
            "GetAllLogs",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            Type.EmptyTypes,
            modifiers: null);
        _logCollectionEnabled = connectionType.GetProperty(
            "InMemoryLogCollectionEnabled",
            BindingFlags.Instance | BindingFlags.Public);
        if (_logCollectionEnabled is { CanRead: true, CanWrite: true } &&
            _logCollectionEnabled.PropertyType == typeof(bool))
        {
            _originalLogCollectionEnabled = _logCollectionEnabled.GetValue(connection);
            if (_originalLogCollectionEnabled is false)
            {
                _logCollectionEnabled.SetValue(connection, true);
            }
        }

        if (disableRetries)
        {
            _maxRetryCount = connectionType.GetProperty(
                "MaxRetryCount",
                BindingFlags.Instance | BindingFlags.Public);
            if (_maxRetryCount is not { CanRead: true, CanWrite: true } ||
                _maxRetryCount.PropertyType != typeof(int))
            {
                throw new InvalidOperationException(
                    $"Dataverse connection type '{connectionType.FullName}' does not expose a writable MaxRetryCount; " +
                    "diagnostic no-retry mode cannot be applied safely.");
            }

            _originalMaxRetryCount = _maxRetryCount.GetValue(connection);
            _maxRetryCount.SetValue(connection, 0);
        }
    }

    public bool IsAvailable => _getLogs is not null;

    public DataverseRetryObservation ReadObservations()
    {
        if (_getLogs is null)
        {
            return new DataverseRetryObservation(0, 0, false);
        }

        var value = _getLogs.Invoke(_connection, null);
        if (value is not IEnumerable entries)
        {
            throw new InvalidOperationException(
                $"Dataverse connection type '{_connection.GetType().FullName}' returned an invalid log collection.");
        }

        var retries = 0;
        var throttles = 0;
        foreach (var entry in entries)
        {
            var message = entry switch
            {
                string text => text,
                null => null,
                _ => entry.GetType().GetProperty("Item2", BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(entry) as string
            };
            if (message is null)
            {
                continue;
            }

            var retry = RetryPattern.IsMatch(message);
            var throttle = ThrottlePattern.IsMatch(message);
            if (!retry && !throttle)
            {
                continue;
            }

            if (entry is not string)
            {
                var timestamp = entry?.GetType()
                    .GetProperty("Item1", BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(entry);
                var timestampText = timestamp is IFormattable formattable
                    ? formattable.ToString("O", CultureInfo.InvariantCulture)
                    : Convert.ToString(timestamp, CultureInfo.InvariantCulture) ?? string.Empty;
                if (!_seenRetryEntries.Add($"{timestampText}\n{message}"))
                {
                    continue;
                }
            }

            if (retry)
            {
                retries++;
            }
            if (throttle)
            {
                throttles++;
            }
        }

        return new DataverseRetryObservation(retries, throttles, true);
    }

    public void Dispose()
    {
        if (_disableRetries && _maxRetryCount is not null)
        {
            _maxRetryCount.SetValue(_connection, _originalMaxRetryCount);
        }

        if (_logCollectionEnabled is not null &&
            _originalLogCollectionEnabled is not null &&
            _logCollectionEnabled.GetValue(_connection) is true)
        {
            _logCollectionEnabled.SetValue(_connection, _originalLogCollectionEnabled);
        }
    }
}

internal readonly record struct DataverseRetryObservation(
    int RetryCount,
    int ThrottleCount,
    bool IsAvailable);
