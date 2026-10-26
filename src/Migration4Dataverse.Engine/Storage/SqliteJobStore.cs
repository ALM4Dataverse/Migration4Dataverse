using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Migration4Dataverse.Engine.Storage;

internal sealed partial class SqliteMigrationStore : IMigrationStore
{
    private readonly string _connectionString;

    static SqliteMigrationStore()
    {
        SqliteNativeLibraryResolver.Register();
    }

    internal SqliteMigrationStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (string.Equals(databasePath, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A durable file path is required for the job store.", nameof(databasePath));
        }

        DatabasePath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30
        };
        _connectionString = connectionString.ToString();
        Initialize();
    }

    public string DatabasePath { get; }

    public void CreateRun(
        string runId,
        string runName,
        string stepHash,
        DateTimeOffset? nowUtc = null,
        string pipelineJson = "[]",
        bool debugCaptureEnabled = false,
        int debugCaptureRetentionDays = 30,
        string configurationJson = "null",
        string connectionInstancesJson = "[]")
    {
        using var writeLock = SqliteDatabaseWriteGate.Enter(DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runName);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepHash);
        ValidateJson(pipelineJson, nameof(pipelineJson));
        ValidateJson(configurationJson, nameof(configurationJson));
        ValidateJson(connectionInstancesJson, nameof(connectionInstancesJson));
        if (debugCaptureRetentionDays is < 1 or > 3650)
        {
            throw new ArgumentOutOfRangeException(
                nameof(debugCaptureRetentionDays),
                "Debug capture retention must be between 1 and 3650 days.");
        }

        var now = FormatUtc(nowUtc ?? DateTimeOffset.UtcNow);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO Runs (
                    RunId, RunName, StepHash, Status, CreatedUtc, UpdatedUtc,
                    PipelineJson, DebugCaptureEnabled, DebugCaptureRetentionDays,
                    ConfigurationJson, ConnectionInstancesJson)
                VALUES (
                    $runId, $runName, $stepHash, 'Running', $now, $now,
                    $pipelineJson, $debugCaptureEnabled, $debugCaptureRetentionDays,
                    $configurationJson, $connectionInstancesJson)
                ON CONFLICT (RunId) DO NOTHING;
                """;
            AddParameter(insert, "$runId", runId);
            AddParameter(insert, "$runName", runName);
            AddParameter(insert, "$stepHash", stepHash);
            AddParameter(insert, "$now", now);
            AddParameter(insert, "$pipelineJson", pipelineJson);
            AddParameter(insert, "$debugCaptureEnabled", debugCaptureEnabled ? 1 : 0);
            AddParameter(insert, "$debugCaptureRetentionDays", debugCaptureRetentionDays);
            AddParameter(insert, "$configurationJson", configurationJson);
            AddParameter(insert, "$connectionInstancesJson", connectionInstancesJson);
            insert.ExecuteNonQuery();
        }

        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT RunName, StepHash, DebugCaptureEnabled, DebugCaptureRetentionDays
                FROM Runs
                WHERE RunId = $runId;
                """;
            AddParameter(read, "$runId", runId);
            using var reader = read.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException($"Run '{runId}' could not be created.");
            }

            var existingName = reader.GetString(0);
            var existingHash = reader.GetString(1);
            if (!string.Equals(existingName, runName, StringComparison.Ordinal) ||
                !string.Equals(existingHash, stepHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Run '{runId}' already exists with a different run name or step definition. Use a new run ID to change it.");
            }
            if ((reader.GetInt32(2) != 0) != debugCaptureEnabled)
            {
                throw new InvalidOperationException(
                    $"Run '{runId}' was created with debug capture " +
                    $"{(reader.GetInt32(2) != 0 ? "enabled" : "disabled")}; that setting cannot be changed when resuming.");
            }
            if (reader.GetInt32(3) != debugCaptureRetentionDays)
            {
                throw new InvalidOperationException(
                    $"Run '{runId}' was created with a {reader.GetInt32(3)}-day debug retention policy; " +
                    "that setting cannot be changed when resuming.");
            }
        }

        transaction.Commit();
    }

    public void UpdateConnectionInstanceSnapshots(string runId, string snapshotsJson)
    {
        using var writeLock = SqliteDatabaseWriteGate.Enter(DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ValidateJson(snapshotsJson, nameof(snapshotsJson));
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Runs
            SET ConnectionInstancesJson = $snapshotsJson
            WHERE RunId = $runId;
            """;
        AddParameter(command, "$snapshotsJson", snapshotsJson);
        AddParameter(command, "$runId", runId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new KeyNotFoundException($"Run '{runId}' was not found.");
        }
    }

    public void SetRunStatus(string runId, MigrationRunStatus status, DateTimeOffset? nowUtc = null)
    {
        using var writeLock = SqliteDatabaseWriteGate.Enter(DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        var now = nowUtc ?? DateTimeOffset.UtcNow;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Runs
            SET Status = $status,
                UpdatedUtc = $now,
                FinishedUtc = CASE WHEN $status = 'Running' THEN NULL ELSE $now END
            WHERE RunId = $runId;
            """;
        AddParameter(command, "$status", status.ToString());
        AddParameter(command, "$now", FormatUtc(now));
        AddParameter(command, "$runId", runId);

        if (command.ExecuteNonQuery() != 1)
        {
            throw new KeyNotFoundException($"Run '{runId}' was not found.");
        }

        if (status == MigrationRunStatus.Running)
        {
            RecoverStepActivities(connection, transaction, runId, now);
        }
        else
        {
            FinalizeStepActivities(connection, transaction, runId, now);
        }

        transaction.Commit();
    }

    public void SaveTargetRecordIds(string runId, IReadOnlyList<TargetRecordIdMapping> mappings)
    {
        using var writeLock = SqliteDatabaseWriteGate.Enter(DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(mappings);
        if (mappings.Count == 0)
        {
            return;
        }

        foreach (var mapping in mappings)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(mapping.ConnectionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(mapping.TableName);
            ArgumentException.ThrowIfNullOrWhiteSpace(mapping.KeyColumn);
            ArgumentException.ThrowIfNullOrWhiteSpace(mapping.KeyValue);
            if (mapping.RecordId == Guid.Empty)
            {
                throw new ArgumentException("Target record IDs must not be empty.", nameof(mappings));
            }
        }

        var updatedUtc = FormatUtc(DateTimeOffset.UtcNow);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO TargetRecordIds (
                RunId, ConnectionId, TableName, KeyColumn, KeyValue, RecordId, UpdatedUtc)
            VALUES (
                $runId, $connectionId, $tableName, $keyColumn, $keyValue, $recordId, $updatedUtc)
            ON CONFLICT (RunId, ConnectionId, TableName, KeyColumn, KeyValue)
            DO UPDATE SET RecordId = excluded.RecordId, UpdatedUtc = excluded.UpdatedUtc;
            """;
        var runIdParameter = command.Parameters.Add("$runId", SqliteType.Text);
        var connectionIdParameter = command.Parameters.Add("$connectionId", SqliteType.Text);
        var tableNameParameter = command.Parameters.Add("$tableName", SqliteType.Text);
        var keyColumnParameter = command.Parameters.Add("$keyColumn", SqliteType.Text);
        var keyValueParameter = command.Parameters.Add("$keyValue", SqliteType.Text);
        var recordIdParameter = command.Parameters.Add("$recordId", SqliteType.Text);
        var updatedUtcParameter = command.Parameters.Add("$updatedUtc", SqliteType.Text);
        command.Prepare();

        foreach (var mapping in mappings)
        {
            runIdParameter.Value = runId;
            connectionIdParameter.Value = mapping.ConnectionId;
            tableNameParameter.Value = mapping.TableName;
            keyColumnParameter.Value = mapping.KeyColumn;
            keyValueParameter.Value = mapping.KeyValue;
            recordIdParameter.Value = mapping.RecordId.ToString("D");
            updatedUtcParameter.Value = updatedUtc;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyDictionary<TargetRecordIdLookup, Guid> GetTargetRecordIds(
        string runId,
        string connectionId,
        IReadOnlyList<TargetRecordIdLookup> lookups)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(lookups);
        if (lookups.Count == 0)
        {
            return new Dictionary<TargetRecordIdLookup, Guid>();
        }

        foreach (var lookup in lookups)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(lookup.TableName);
            ArgumentException.ThrowIfNullOrWhiteSpace(lookup.KeyColumn);
            ArgumentException.ThrowIfNullOrWhiteSpace(lookup.KeyValue);
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT RecordId
            FROM TargetRecordIds
            WHERE RunId = $runId
              AND ConnectionId = $connectionId
              AND TableName = $tableName
              AND KeyColumn = $keyColumn
              AND KeyValue = $keyValue;
            """;
        var runIdParameter = command.Parameters.Add("$runId", SqliteType.Text);
        var connectionIdParameter = command.Parameters.Add("$connectionId", SqliteType.Text);
        var tableNameParameter = command.Parameters.Add("$tableName", SqliteType.Text);
        var keyColumnParameter = command.Parameters.Add("$keyColumn", SqliteType.Text);
        var keyValueParameter = command.Parameters.Add("$keyValue", SqliteType.Text);
        command.Prepare();

        var results = new Dictionary<TargetRecordIdLookup, Guid>();
        foreach (var lookup in lookups.Distinct())
        {
            runIdParameter.Value = runId;
            connectionIdParameter.Value = connectionId;
            tableNameParameter.Value = lookup.TableName;
            keyColumnParameter.Value = lookup.KeyColumn;
            keyValueParameter.Value = lookup.KeyValue;
            var value = command.ExecuteScalar()?.ToString();
            if (value is not null)
            {
                results.Add(lookup, Guid.Parse(value));
            }
        }

        return results;
    }

    public void SaveTargetRecordContexts(
        string runId,
        IReadOnlyList<TargetRecordContextMapping> contexts)
    {
        using var writeLock = SqliteDatabaseWriteGate.Enter(DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(contexts);
        if (contexts.Count == 0)
        {
            return;
        }

        foreach (var context in contexts)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(context.StepId);
            ArgumentException.ThrowIfNullOrWhiteSpace(context.ConnectionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(context.TableName);
            ValidateJson(context.KeyColumnsJson, nameof(contexts));
            ValidateJson(context.KeyValuesJson, nameof(contexts));
            ValidateJson(context.RecordJson, nameof(contexts));
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO TargetRecordContexts (
                RunId, StepId, ConnectionId, TableName, KeyColumn, KeyValue, RecordJson)
            VALUES (
                $runId, $stepId, $connectionId, $tableName, $keyColumn, $keyValue, $recordJson)
            ON CONFLICT (RunId, StepId, ConnectionId, TableName, KeyColumn, KeyValue)
            DO UPDATE SET RecordJson = excluded.RecordJson;
            """;
        var runIdParameter = command.Parameters.Add("$runId", SqliteType.Text);
        var stepIdParameter = command.Parameters.Add("$stepId", SqliteType.Text);
        var connectionIdParameter = command.Parameters.Add("$connectionId", SqliteType.Text);
        var tableNameParameter = command.Parameters.Add("$tableName", SqliteType.Text);
        var keyColumnParameter = command.Parameters.Add("$keyColumn", SqliteType.Text);
        var keyValueParameter = command.Parameters.Add("$keyValue", SqliteType.Text);
        var recordJsonParameter = command.Parameters.Add("$recordJson", SqliteType.Text);
        command.Prepare();

        foreach (var context in contexts)
        {
            runIdParameter.Value = runId;
            stepIdParameter.Value = context.StepId;
            connectionIdParameter.Value = context.ConnectionId;
            tableNameParameter.Value = context.TableName;
            keyColumnParameter.Value = context.KeyColumnsJson;
            keyValueParameter.Value = context.KeyValuesJson;
            recordJsonParameter.Value = context.RecordJson;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public string? GetTargetRecordContext(
        string runId,
        string stepId,
        string connectionId,
        string tableName,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> keyValues,
        bool required = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentNullException.ThrowIfNull(keyColumns);
        ArgumentNullException.ThrowIfNull(keyValues);
        if (keyColumns.Count != keyValues.Count || keyColumns.Count == 0 ||
            keyColumns.Any(string.IsNullOrWhiteSpace) || keyValues.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Target record context key columns and values must be non-empty and have the same length.");
        }

        var contexts = GetTargetRecordContexts(
            runId,
            stepId,
            connectionId,
            tableName,
            TargetRecordContextKey.Serialize(keyColumns),
            [TargetRecordContextKey.Serialize(keyValues)]);
        var keyValueJson = TargetRecordContextKey.Serialize(keyValues);
        if (!contexts.TryGetValue(keyValueJson, out var recordJson))
        {
            if (!required)
            {
                return null;
            }
            throw new KeyNotFoundException(
                $"No parent record was captured for target step '{stepId}', table '{tableName}', composite key '{TargetRecordContextKey.Serialize(keyValues)}' in run '{runId}'.");
        }

        return recordJson;
    }

    public IReadOnlyDictionary<string, string> GetTargetRecordContexts(
        string runId,
        string stepId,
        string connectionId,
        string tableName,
        string keyColumnsJson,
        IReadOnlyList<string> keyValuesJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ValidateJson(keyColumnsJson, nameof(keyColumnsJson));
        ArgumentNullException.ThrowIfNull(keyValuesJson);
        foreach (var keyValue in keyValuesJson)
        {
            ValidateJson(keyValue, nameof(keyValuesJson));
        }

        var distinctKeyValues = keyValuesJson.Distinct(StringComparer.Ordinal).ToArray();
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        if (distinctKeyValues.Length == 0)
        {
            return results;
        }

        using var connection = OpenConnection();
        const int keysPerCommand = 500;
        for (var offset = 0; offset < distinctKeyValues.Length; offset += keysPerCommand)
        {
            var keyCount = Math.Min(keysPerCommand, distinctKeyValues.Length - offset);
            var keyParameters = Enumerable.Range(0, keyCount)
                .Select(index => $"$key{index}")
                .ToArray();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT DISTINCT KeyValue, RecordJson
                FROM TargetRecordContexts
                WHERE RunId = $runId
                  AND StepId = $stepId
                  AND ConnectionId = $connectionId
                  AND TableName = $tableName
                  AND KeyColumn = $keyColumnsJson
                  AND KeyValue IN ({string.Join(", ", keyParameters)});
                """;
            AddParameter(command, "$runId", runId);
            AddParameter(command, "$stepId", stepId);
            AddParameter(command, "$connectionId", connectionId);
            AddParameter(command, "$tableName", tableName);
            AddParameter(command, "$keyColumnsJson", keyColumnsJson);
            for (var index = 0; index < keyCount; index++)
            {
                AddParameter(command, keyParameters[index], distinctKeyValues[offset + index]);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var keyValueJson = reader.GetString(0);
                var recordJson = reader.GetString(1);
                if (results.TryGetValue(keyValueJson, out var existingRecordJson) &&
                    !string.Equals(existingRecordJson, recordJson, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"More than one parent record was captured for target step '{stepId}', table '{tableName}', composite key '{keyValueJson}' in run '{runId}'.");
                }
                results[keyValueJson] = recordJson;
            }
        }

        return results;
    }

    public void SaveJoinInputRecords(
        string runId,
        IReadOnlyList<JoinInputRecordMapping> records)
    {
        using var writeLock = SqliteDatabaseWriteGate.Enter(DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return;
        }

        foreach (var record in records)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(record.JoinStepId);
            ArgumentException.ThrowIfNullOrWhiteSpace(record.InputStepId);
            ArgumentException.ThrowIfNullOrWhiteSpace(record.OriginBatchId);
            if (record.Sequence < 0 || record.RecordIndex < 0 || record.OriginRecordIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(records), "Join record positions must be non-negative.");
            }
            ValidateJson(record.RecordJson, nameof(records));
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO JoinInputRecords (
                RunId, JoinStepId, InputStepId, Sequence, RecordIndex, RecordJson,
                OriginBatchId, OriginRecordIndex)
            VALUES (
                $runId, $joinStepId, $inputStepId, $sequence, $recordIndex, $recordJson,
                $originBatchId, $originRecordIndex)
            ON CONFLICT (RunId, JoinStepId, InputStepId, Sequence, RecordIndex)
            DO UPDATE SET
                RecordJson = excluded.RecordJson,
                OriginBatchId = excluded.OriginBatchId,
                OriginRecordIndex = excluded.OriginRecordIndex;
            """;
        var runIdParameter = command.Parameters.Add("$runId", SqliteType.Text);
        var joinStepIdParameter = command.Parameters.Add("$joinStepId", SqliteType.Text);
        var inputStepIdParameter = command.Parameters.Add("$inputStepId", SqliteType.Text);
        var sequenceParameter = command.Parameters.Add("$sequence", SqliteType.Integer);
        var recordIndexParameter = command.Parameters.Add("$recordIndex", SqliteType.Integer);
        var recordJsonParameter = command.Parameters.Add("$recordJson", SqliteType.Text);
        var originBatchIdParameter = command.Parameters.Add("$originBatchId", SqliteType.Text);
        var originRecordIndexParameter = command.Parameters.Add("$originRecordIndex", SqliteType.Integer);
        command.Prepare();

        using var keyCommand = connection.CreateCommand();
        keyCommand.Transaction = transaction;
        keyCommand.CommandText = """
            INSERT INTO JoinInputKeyIndex (
                RunId, JoinStepId, InputStepId, Sequence, RecordIndex, KeyJson)
            VALUES (
                $runId, $joinStepId, $inputStepId, $sequence, $recordIndex, $keyJson)
            ON CONFLICT (RunId, JoinStepId, InputStepId, Sequence, RecordIndex)
            DO UPDATE SET KeyJson = excluded.KeyJson;
            """;
        var keyRunIdParameter = keyCommand.Parameters.Add("$runId", SqliteType.Text);
        var keyJoinStepIdParameter = keyCommand.Parameters.Add("$joinStepId", SqliteType.Text);
        var keyInputStepIdParameter = keyCommand.Parameters.Add("$inputStepId", SqliteType.Text);
        var keySequenceParameter = keyCommand.Parameters.Add("$sequence", SqliteType.Integer);
        var keyRecordIndexParameter = keyCommand.Parameters.Add("$recordIndex", SqliteType.Integer);
        var keyJsonParameter = keyCommand.Parameters.Add("$keyJson", SqliteType.Text);
        keyCommand.Prepare();

        foreach (var record in records)
        {
            runIdParameter.Value = runId;
            joinStepIdParameter.Value = record.JoinStepId;
            inputStepIdParameter.Value = record.InputStepId;
            sequenceParameter.Value = record.Sequence;
            recordIndexParameter.Value = record.RecordIndex;
            recordJsonParameter.Value = record.RecordJson;
            originBatchIdParameter.Value = record.OriginBatchId;
            originRecordIndexParameter.Value = record.OriginRecordIndex;
            command.ExecuteNonQuery();

            keyRunIdParameter.Value = runId;
            keyJoinStepIdParameter.Value = record.JoinStepId;
            keyInputStepIdParameter.Value = record.InputStepId;
            keySequenceParameter.Value = record.Sequence;
            keyRecordIndexParameter.Value = record.RecordIndex;
            keyJsonParameter.Value = (object?)SerializeJoinKey(record.RecordJson, record.KeyColumns) ?? DBNull.Value;
            keyCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<JoinInputRecord> ReadJoinInputRecordPage(
        string runId,
        string joinStepId,
        string inputStepId,
        long afterSequence,
        int afterRecordIndex,
        int pageSize,
        long? throughSequence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(joinStepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputStepId);
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfNegative(afterRecordIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        if (throughSequence is < -1)
        {
            throw new ArgumentOutOfRangeException(nameof(throughSequence));
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT records.Sequence, records.RecordIndex, records.RecordJson,
                   records.OriginBatchId, records.OriginRecordIndex, keys.KeyJson
            FROM JoinInputRecords AS records
            LEFT JOIN JoinInputKeyIndex AS keys
              ON keys.RunId = records.RunId
             AND keys.JoinStepId = records.JoinStepId
             AND keys.InputStepId = records.InputStepId
             AND keys.Sequence = records.Sequence
             AND keys.RecordIndex = records.RecordIndex
            WHERE records.RunId = $runId
              AND records.JoinStepId = $joinStepId
              AND records.InputStepId = $inputStepId
              AND records.Sequence <= $throughSequence
              AND (records.Sequence > $afterSequence
                   OR (records.Sequence = $afterSequence AND records.RecordIndex > $afterRecordIndex))
            ORDER BY records.Sequence, records.RecordIndex
            LIMIT $pageSize;
            """;
        AddParameter(command, "$runId", runId);
        AddParameter(command, "$joinStepId", joinStepId);
        AddParameter(command, "$inputStepId", inputStepId);
        AddParameter(command, "$throughSequence", throughSequence ?? long.MaxValue);
        AddParameter(command, "$afterSequence", afterSequence);
        AddParameter(command, "$afterRecordIndex", afterRecordIndex);
        AddParameter(command, "$pageSize", pageSize);

        var result = new List<JoinInputRecord>(pageSize);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new JoinInputRecord(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4))
            {
                KeyJson = ReadNullableString(reader, 5)
            });
        }
        return result;
    }

    public JoinInputRecord? FindJoinInputRecordByKey(
        string runId,
        string joinStepId,
        string inputStepId,
        string keyJson,
        long throughSequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(joinStepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputStepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyJson);
        if (throughSequence < -1)
        {
            throw new ArgumentOutOfRangeException(nameof(throughSequence));
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT records.Sequence, records.RecordIndex, records.RecordJson,
                   records.OriginBatchId, records.OriginRecordIndex
            FROM JoinInputKeyIndex AS keys
            JOIN JoinInputRecords AS records
              ON records.RunId = keys.RunId
             AND records.JoinStepId = keys.JoinStepId
             AND records.InputStepId = keys.InputStepId
             AND records.Sequence = keys.Sequence
             AND records.RecordIndex = keys.RecordIndex
            JOIN Batches AS batches
              ON batches.RunId = records.RunId
             AND batches.BatchId = records.OriginBatchId
            WHERE keys.RunId = $runId
              AND keys.JoinStepId = $joinStepId
              AND keys.InputStepId = $inputStepId
              AND keys.KeyJson = $keyJson
              AND keys.Sequence <= $throughSequence
              AND batches.State = 'Succeeded'
            ORDER BY keys.Sequence, keys.RecordIndex
            LIMIT 2;
            """;
        AddParameter(command, "$runId", runId);
        AddParameter(command, "$joinStepId", joinStepId);
        AddParameter(command, "$inputStepId", inputStepId);
        AddParameter(command, "$keyJson", keyJson);
        AddParameter(command, "$throughSequence", throughSequence);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var result = new JoinInputRecord(
            reader.GetInt64(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4));
        if (reader.Read())
        {
            throw new InvalidDataException(
                $"Join step '{joinStepId}' found multiple parent records for the same key in input '{inputStepId}'.");
        }
        return result;
    }

    public JoinInputRecord? GetJoinInputRecord(
        string runId,
        string joinStepId,
        string inputStepId,
        long sequence,
        int recordIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(joinStepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputStepId);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentOutOfRangeException.ThrowIfNegative(recordIndex);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT records.RecordJson, records.OriginBatchId, records.OriginRecordIndex, keys.KeyJson
            FROM JoinInputRecords AS records
            JOIN Batches AS batches
              ON batches.RunId = records.RunId
             AND batches.BatchId = records.OriginBatchId
            LEFT JOIN JoinInputKeyIndex AS keys
              ON keys.RunId = records.RunId
             AND keys.JoinStepId = records.JoinStepId
             AND keys.InputStepId = records.InputStepId
             AND keys.Sequence = records.Sequence
             AND keys.RecordIndex = records.RecordIndex
            WHERE records.RunId = $runId
              AND records.JoinStepId = $joinStepId
              AND records.InputStepId = $inputStepId
              AND records.Sequence = $sequence
              AND records.RecordIndex = $recordIndex
              AND batches.State = 'Succeeded';
            """;
        AddParameter(command, "$runId", runId);
        AddParameter(command, "$joinStepId", joinStepId);
        AddParameter(command, "$inputStepId", inputStepId);
        AddParameter(command, "$sequence", sequence);
        AddParameter(command, "$recordIndex", recordIndex);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new JoinInputRecord(
            sequence,
            recordIndex,
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2))
        {
            KeyJson = ReadNullableString(reader, 3)
        };
    }

    private static string? SerializeJoinKey(string recordJson, IReadOnlyList<string>? keyColumns)
    {
        if (keyColumns is null || keyColumns.Count == 0)
        {
            return null;
        }

        using var document = JsonDocument.Parse(recordJson);
        var keyValues = new string?[keyColumns.Count];
        for (var index = 0; index < keyColumns.Count; index++)
        {
            var value = GetJsonPath(document.RootElement, keyColumns[index], recordJson);
            keyValues[index] = value.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => NormalizeStringKey(value.GetString()),
                JsonValueKind.Number when value.TryGetDecimal(out var decimalValue) =>
                    decimalValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                JsonValueKind.Object or JsonValueKind.Array => throw new InvalidDataException(
                    $"Join key column '{keyColumns[index]}' must contain a scalar value."),
                _ => NormalizeStringKey(value.ToString())
            };
        }

        return keyValues.Any(value => value is null)
            ? null
            : JsonSerializer.Serialize(keyValues);
    }

    private static JsonElement GetJsonPath(JsonElement root, string path, string recordJson)
    {
        var value = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"Join key path '{path}' does not exist in record '{recordJson}'.");
            }
            var found = false;
            foreach (var property in value.EnumerateObject())
            {
                if (!string.Equals(property.Name, segment, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                value = property.Value;
                found = true;
                break;
            }
            if (!found)
            {
                throw new InvalidDataException(
                    $"Join key path '{path}' is missing from an input record.");
            }
        }
        return value;
    }

    private static string? NormalizeStringKey(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public void SaveJoinRelationshipMatches(
        string runId,
        string joinStepId,
        IReadOnlyList<JoinRelationshipMatch> matches)
    {
        using var writeLock = SqliteDatabaseWriteGate.Enter(DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(joinStepId);
        ArgumentNullException.ThrowIfNull(matches);
        if (matches.Count == 0)
        {
            return;
        }

        foreach (var match in matches)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(match.Alias);
            if (match.Sequence < 0 || match.RecordIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(matches), "Join record positions must be non-negative.");
            }
            if ((match.ParentInputStepId is null) != (match.ParentSequence is null) ||
                (match.ParentInputStepId is null) != (match.ParentRecordIndex is null))
            {
                throw new ArgumentException("Join parent row references must be complete or null.", nameof(matches));
            }
            if (match.ParentInputStepId is not null &&
                (string.IsNullOrWhiteSpace(match.ParentInputStepId) ||
                 match.ParentSequence < 0 ||
                 match.ParentRecordIndex < 0))
            {
                throw new ArgumentOutOfRangeException(nameof(matches), "Join parent row positions must be valid.");
            }
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO JoinRelationshipMatches (
                RunId, JoinStepId, Sequence, RecordIndex, Alias,
                ParentInputStepId, ParentSequence, ParentRecordIndex)
            VALUES (
                $runId, $joinStepId, $sequence, $recordIndex, $alias,
                $parentInputStepId, $parentSequence, $parentRecordIndex)
            ON CONFLICT (RunId, JoinStepId, Sequence, RecordIndex, Alias)
            DO UPDATE SET
                ParentInputStepId = excluded.ParentInputStepId,
                ParentSequence = excluded.ParentSequence,
                ParentRecordIndex = excluded.ParentRecordIndex;
            """;
        var runIdParameter = command.Parameters.Add("$runId", SqliteType.Text);
        var joinStepIdParameter = command.Parameters.Add("$joinStepId", SqliteType.Text);
        var sequenceParameter = command.Parameters.Add("$sequence", SqliteType.Integer);
        var recordIndexParameter = command.Parameters.Add("$recordIndex", SqliteType.Integer);
        var aliasParameter = command.Parameters.Add("$alias", SqliteType.Text);
        var parentInputStepIdParameter = command.Parameters.Add("$parentInputStepId", SqliteType.Text);
        var parentSequenceParameter = command.Parameters.Add("$parentSequence", SqliteType.Integer);
        var parentRecordIndexParameter = command.Parameters.Add("$parentRecordIndex", SqliteType.Integer);
        command.Prepare();

        foreach (var match in matches)
        {
            runIdParameter.Value = runId;
            joinStepIdParameter.Value = joinStepId;
            sequenceParameter.Value = match.Sequence;
            recordIndexParameter.Value = match.RecordIndex;
            aliasParameter.Value = match.Alias;
            parentInputStepIdParameter.Value = (object?)match.ParentInputStepId ?? DBNull.Value;
            parentSequenceParameter.Value = (object?)match.ParentSequence ?? DBNull.Value;
            parentRecordIndexParameter.Value = (object?)match.ParentRecordIndex ?? DBNull.Value;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyDictionary<JoinInputRecordKey, IReadOnlyDictionary<string, JoinRelationshipMatch>> ReadJoinRelationshipMatches(
        string runId,
        string joinStepId,
        IReadOnlyList<string> aliases,
        JoinInputRecordKey first,
        JoinInputRecordKey last)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(joinStepId);
        ArgumentNullException.ThrowIfNull(aliases);
        if (aliases.Count == 0 || aliases.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one non-empty join alias is required.", nameof(aliases));
        }
        if (first.Sequence < 0 || first.RecordIndex < 0 || last.Sequence < first.Sequence ||
            last.Sequence == first.Sequence && last.RecordIndex < first.RecordIndex)
        {
            throw new ArgumentException("Join match range is invalid.");
        }

        using var connection = OpenConnection();
        var aliasParameters = aliases.Distinct(StringComparer.Ordinal)
            .Select((_, index) => $"$alias{index}")
            .ToArray();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Sequence, RecordIndex, Alias, ParentInputStepId, ParentSequence, ParentRecordIndex
            FROM JoinRelationshipMatches
            WHERE RunId = $runId
              AND JoinStepId = $joinStepId
              AND Alias IN ({string.Join(", ", aliasParameters)})
              AND (
                    Sequence > $firstSequence
                    OR (Sequence = $firstSequence AND RecordIndex >= $firstRecordIndex)
                  )
              AND (
                    Sequence < $lastSequence
                    OR (Sequence = $lastSequence AND RecordIndex <= $lastRecordIndex)
                  )
            ORDER BY Sequence, RecordIndex, Alias;
            """;
        AddParameter(command, "$runId", runId);
        AddParameter(command, "$joinStepId", joinStepId);
        AddParameter(command, "$firstSequence", first.Sequence);
        AddParameter(command, "$firstRecordIndex", first.RecordIndex);
        AddParameter(command, "$lastSequence", last.Sequence);
        AddParameter(command, "$lastRecordIndex", last.RecordIndex);
        var aliasIndex = 0;
        foreach (var alias in aliases.Distinct(StringComparer.Ordinal))
        {
            AddParameter(command, aliasParameters[aliasIndex++], alias);
        }

        var result = new Dictionary<JoinInputRecordKey, Dictionary<string, JoinRelationshipMatch>>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = new JoinInputRecordKey(reader.GetInt64(0), reader.GetInt32(1));
            if (!result.TryGetValue(key, out var rowMatches))
            {
                rowMatches = new Dictionary<string, JoinRelationshipMatch>(StringComparer.Ordinal);
                result.Add(key, rowMatches);
            }
            rowMatches.Add(reader.GetString(2), new JoinRelationshipMatch(
                key.Sequence,
                key.RecordIndex,
                reader.GetString(2),
                ReadNullableString(reader, 3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5)));
        }
        return result.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyDictionary<string, JoinRelationshipMatch>)entry.Value);
    }

    public IReadOnlyDictionary<JoinInputRecordKey, JoinInputRecord> ReadJoinInputRecordsByKeys(
        string runId,
        string joinStepId,
        string inputStepId,
        IReadOnlyList<JoinInputRecordKey> keys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(joinStepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputStepId);
        ArgumentNullException.ThrowIfNull(keys);
        var distinctKeys = keys.Distinct().ToArray();
        var results = new Dictionary<JoinInputRecordKey, JoinInputRecord>();
        if (distinctKeys.Length == 0)
        {
            return results;
        }

        using var connection = OpenConnection();
        const int keysPerCommand = 400;
        for (var offset = 0; offset < distinctKeys.Length; offset += keysPerCommand)
        {
            var keyCount = Math.Min(keysPerCommand, distinctKeys.Length - offset);
            var predicates = Enumerable.Range(0, keyCount)
                .Select(index => $"(Sequence = $sequence{index} AND RecordIndex = $recordIndex{index})")
                .ToArray();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT Sequence, RecordIndex, RecordJson, OriginBatchId, OriginRecordIndex
                FROM JoinInputRecords
                WHERE RunId = $runId
                  AND JoinStepId = $joinStepId
                  AND InputStepId = $inputStepId
                  AND ({string.Join(" OR ", predicates)});
                """;
            AddParameter(command, "$runId", runId);
            AddParameter(command, "$joinStepId", joinStepId);
            AddParameter(command, "$inputStepId", inputStepId);
            for (var index = 0; index < keyCount; index++)
            {
                var key = distinctKeys[offset + index];
                AddParameter(command, $"$sequence{index}", key.Sequence);
                AddParameter(command, $"$recordIndex{index}", key.RecordIndex);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var key = new JoinInputRecordKey(reader.GetInt64(0), reader.GetInt32(1));
                results.Add(key, new JoinInputRecord(
                    key.Sequence,
                    key.RecordIndex,
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt32(4)));
            }
        }
        return results;
    }

    public void ClearJoinStaging(string runId)
    {
        using var writeLock = SqliteDatabaseWriteGate.Enter(DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var table in new[]
                 {
                     "JoinInputKeyIndex",
                     "JoinRelationshipMatches",
                     "JoinInputRecords",
                     "TargetRecordContexts"
                 })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE RunId = $runId;";
            AddParameter(command, "$runId", runId);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void StartStepActivity(string runId, string stepId, DateTimeOffset? nowUtc = null)
    {
        using var writeLock = SqliteDatabaseWriteGate.Enter(DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        EnsureStepActivity(connection, transaction, runId, stepId, nowUtc ?? DateTimeOffset.UtcNow);
        transaction.Commit();
    }

    public void EnterStepActivity(string runId, string stepId, DateTimeOffset? nowUtc = null)
    {
        using var writeLock = SqliteDatabaseWriteGate.Enter(DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);

        var now = nowUtc ?? DateTimeOffset.UtcNow;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        EnterStepActivity(connection, transaction, runId, stepId, now);
        transaction.Commit();
    }

    public void ExitStepActivity(string runId, string stepId, DateTimeOffset? nowUtc = null)
    {
        using var writeLock = SqliteDatabaseWriteGate.Enter(DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);

        var now = nowUtc ?? DateTimeOffset.UtcNow;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        ExitStepActivity(connection, transaction, runId, stepId, now);
        transaction.Commit();
    }

    public void CompleteStepActivity(string runId, string stepId, DateTimeOffset? nowUtc = null)
    {
        using var writeLock = SqliteDatabaseWriteGate.Enter(DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);

        var now = nowUtc ?? DateTimeOffset.UtcNow;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        CompleteStepActivity(connection, transaction, runId, stepId, now);
        transaction.Commit();
    }

    public bool TryAcquireRunLease(
        string runId,
        string workerId,
        TimeSpan leaseDuration,
        DateTimeOffset? nowUtc = null)
    {
        using var writeLock = SqliteDatabaseWriteGate.Enter(DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "Lease duration must be positive.");
        }

        var now = nowUtc ?? DateTimeOffset.UtcNow;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Runs
            SET SchedulerLeaseOwner = $workerId,
                SchedulerLeaseExpiresUtc = $leaseExpiresUtc,
                UpdatedUtc = $now
            WHERE RunId = $runId
              AND (
                SchedulerLeaseOwner IS NULL
                OR SchedulerLeaseExpiresUtc <= $now
                OR SchedulerLeaseOwner = $workerId
              );
            """;
        AddParameter(command, "$workerId", workerId);
        AddParameter(command, "$leaseExpiresUtc", FormatUtc(now.Add(leaseDuration)));
        AddParameter(command, "$now", FormatUtc(now));
        AddParameter(command, "$runId", runId);
        var acquired = command.ExecuteNonQuery() == 1;
        if (acquired)
        {
            transaction.Commit();
        }

        return acquired;
    }

    public void ReleaseRunLease(string runId, string workerId, DateTimeOffset? nowUtc = null)
    {
        using var writeLock = SqliteDatabaseWriteGate.Enter(DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Runs
            SET SchedulerLeaseOwner = NULL,
                SchedulerLeaseExpiresUtc = NULL,
                UpdatedUtc = $now
            WHERE RunId = $runId AND SchedulerLeaseOwner = $workerId;
            """;
        AddParameter(command, "$now", FormatUtc(nowUtc ?? DateTimeOffset.UtcNow));
        AddParameter(command, "$runId", runId);
        AddParameter(command, "$workerId", workerId);

        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException($"Worker '{workerId}' does not hold run lease '{runId}'.");
        }
    }

    public MigrationRunProgress GetRunProgress(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        using var connection = OpenConnection();

        using (var run = connection.CreateCommand())
        {
            run.CommandText = "SELECT 1 FROM Runs WHERE RunId = $runId;";
            AddParameter(run, "$runId", runId);
            if (run.ExecuteScalar() is null)
            {
                throw new KeyNotFoundException($"Run '{runId}' was not found.");
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COALESCE(SUM(CASE WHEN State = 'Pending' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN State = 'InFlight' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN State = 'Succeeded' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN State = 'CompletedWithErrors' THEN 1 ELSE 0 END), 0),
                (SELECT COUNT(*) FROM DependencyEvents WHERE RunId = $runId AND State = 'Pending'),
                (SELECT COUNT(*) FROM DependencyEvents WHERE RunId = $runId AND State = 'Completed')
            FROM Batches
            WHERE RunId = $runId;
            """;
        AddParameter(command, "$runId", runId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidOperationException($"Progress for run '{runId}' could not be read.");
        }

        return new MigrationRunProgress(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5));
    }

    public IReadOnlyDictionary<string, long> GetNonTerminalBatchCountsByStep(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT StepId, COUNT(*)
            FROM Batches
            WHERE RunId = $runId AND State IN ('Pending', 'InFlight')
            GROUP BY StepId;
            """;
        AddParameter(command, "$runId", runId);

        using var reader = command.ExecuteReader();
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        while (reader.Read())
        {
            counts.Add(reader.GetString(0), reader.GetInt64(1));
        }

        return counts;
    }

    public IReadOnlyList<PendingBatchStepCandidate> GetPendingBatchStepCandidates(
        string runId,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        var now = FormatUtc(nowUtc ?? DateTimeOffset.UtcNow);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT batch.StepId,
                   MIN(CASE WHEN EXISTS (
                       SELECT 1
                       FROM DependencyEvents AS dependency
                       WHERE dependency.RunId = batch.RunId
                         AND dependency.EventId = batch.BatchId
                         AND dependency.State = 'Completed'
                   ) THEN 0 ELSE 1 END) AS Priority,
                   COUNT(*) AS PendingBatchCount
            FROM Batches AS batch
            WHERE batch.RunId = $runId
              AND (
                batch.State = 'Pending'
                OR (batch.State = 'InFlight' AND batch.LeaseExpiresUtc <= $now)
              )
            GROUP BY batch.StepId
            ORDER BY Priority, MIN(batch.CreatedUtc), batch.StepId;
            """;
        AddParameter(command, "$runId", runId);
        AddParameter(command, "$now", now);

        using var reader = command.ExecuteReader();
        var candidates = new List<PendingBatchStepCandidate>();
        while (reader.Read())
        {
            candidates.Add(new PendingBatchStepCandidate(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt64(2)));
        }

        return candidates;
    }

    public long GetPendingDependencyEventCountWithoutPrerequisites(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM DependencyEvents
            WHERE RunId = $runId
              AND State = 'Pending'
              AND PrerequisiteBatchIdsJson = '[]';
            """;
        AddParameter(command, "$runId", runId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public IReadOnlyDictionary<string, long> GetPendingDependencyEventCountsWithoutPrerequisitesByStep(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ChildStepId, COUNT(*)
            FROM DependencyEvents
            WHERE RunId = $runId
              AND State = 'Pending'
              AND PrerequisiteBatchIdsJson = '[]'
            GROUP BY ChildStepId;
            """;
        AddParameter(command, "$runId", runId);
        using var reader = command.ExecuteReader();
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        while (reader.Read())
        {
            counts.Add(reader.GetString(0), reader.GetInt64(1));
        }
        return counts;
    }

    public IReadOnlyDictionary<string, long> GetPendingDependencyEventCountsByStep(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ChildStepId, COUNT(*)
            FROM DependencyEvents
            WHERE RunId = $runId
              AND State = 'Pending'
            GROUP BY ChildStepId;
            """;
        AddParameter(command, "$runId", runId);
        using var reader = command.ExecuteReader();
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        while (reader.Read())
        {
            counts.Add(reader.GetString(0), reader.GetInt64(1));
        }
        return counts;
    }

    public string EnqueueBatch(
        string runId,
        string tableName,
        long sequence,
        string batchId,
        string? cursorAfterJson,
        string payloadJson,
        DateTimeOffset? nowUtc = null,
        IReadOnlyList<string>? parentBatchIds = null,
        long filteredRows = 0,
        IReadOnlyList<SourceKeyEntry>? sourceKeys = null)
    {
        using var writeLock = SqliteDatabaseWriteGate.Enter(DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentNullException.ThrowIfNull(payloadJson);
        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "Batch sequence must be zero or greater.");
        }
        ArgumentOutOfRangeException.ThrowIfNegative(filteredRows);

        ValidateJson(payloadJson, nameof(payloadJson));
        var distinctParentBatchIds = (parentBatchIds ?? Array.Empty<string>())
            .Select(parentBatchId =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(parentBatchId);
                return parentBatchId;
            })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var sourceKeyEntries = (sourceKeys ?? Array.Empty<SourceKeyEntry>())
            .Select(entry =>
            {
                ArgumentNullException.ThrowIfNull(entry);
                ArgumentException.ThrowIfNullOrWhiteSpace(entry.SourceKey);
                ArgumentOutOfRangeException.ThrowIfNegative(entry.RecordIndex);
                return entry;
            })
            .ToArray();
        if (sourceKeyEntries.Select(entry => entry.SourceKey)
            .Distinct(StringComparer.Ordinal)
            .Count() != sourceKeyEntries.Length)
        {
            throw new ArgumentException("A source batch must not contain duplicate source keys.", nameof(sourceKeys));
        }
        if (cursorAfterJson is not null)
        {
            ValidateJson(cursorAfterJson, nameof(cursorAfterJson));
        }

        var payloadHash = Hash(payloadJson);
        var now = FormatUtc(nowUtc ?? DateTimeOffset.UtcNow);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.Transaction = transaction;
            checkpoint.CommandText = """
                INSERT INTO StepCheckpoints (RunId, StepId, LastCompletedSequence, CursorJson, UpdatedUtc)
                VALUES ($runId, $stepId, -1, NULL, $now)
                ON CONFLICT (RunId, StepId) DO NOTHING;
                """;
            AddParameter(checkpoint, "$runId", runId);
            AddParameter(checkpoint, "$stepId", tableName);
            AddParameter(checkpoint, "$now", now);
            checkpoint.ExecuteNonQuery();
        }

        var existing = FindBatchByIdentity(connection, transaction, runId, tableName, sequence, batchId);
        if (existing is not null)
        {
            if (!string.Equals(existing.BatchId, batchId, StringComparison.Ordinal) ||
                !string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal) ||
                !string.Equals(existing.CursorAfterJson, cursorAfterJson, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Batch identity '{runId}/{tableName}/{sequence}' was already used for different content.");
            }

            InsertSourceKeyIndex(connection, transaction, runId, tableName, batchId, sourceKeyEntries);
            transaction.Commit();
            return existing.BatchId;
        }

        var expectedSequence = GetNextSequence(connection, transaction, runId, tableName);
        if (sequence != expectedSequence)
        {
            throw new InvalidOperationException(
                $"Expected batch sequence {expectedSequence} for '{runId}/{tableName}', received {sequence}.");
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO Batches (
                    BatchId, RunId, StepId, Sequence, CursorAfterJson, PayloadHash, PayloadJson,
                    State, Attempts, CreatedUtc, UpdatedUtc, InputRows, FilteredRows)
                VALUES (
                    $batchId, $runId, $stepId, $sequence, $cursorAfterJson, $payloadHash, $payloadJson,
                    'Pending', 0, $now, $now, $inputRows, $filteredRows);
                """;
            AddParameter(insert, "$batchId", batchId);
            AddParameter(insert, "$runId", runId);
            AddParameter(insert, "$stepId", tableName);
            AddParameter(insert, "$sequence", sequence);
            AddParameter(insert, "$cursorAfterJson", cursorAfterJson);
            AddParameter(insert, "$payloadHash", payloadHash);
            AddParameter(insert, "$payloadJson", payloadJson);
            AddParameter(insert, "$now", now);
            AddParameter(insert, "$inputRows", GetInputRowCount(payloadJson));
            AddParameter(insert, "$filteredRows", filteredRows);
            insert.ExecuteNonQuery();
        }

        foreach (var parentBatchId in distinctParentBatchIds)
        {
            using var parentReference = connection.CreateCommand();
            parentReference.Transaction = transaction;
            parentReference.CommandText = """
                INSERT INTO BatchParentReferences (RunId, BatchId, ParentBatchId)
                VALUES ($runId, $batchId, $parentBatchId);
                """;
            AddParameter(parentReference, "$runId", runId);
            AddParameter(parentReference, "$batchId", batchId);
            AddParameter(parentReference, "$parentBatchId", parentBatchId);
            parentReference.ExecuteNonQuery();
        }

        InsertSourceKeyIndex(connection, transaction, runId, tableName, batchId, sourceKeyEntries);
        TouchRun(connection, transaction, runId, now);
        transaction.Commit();
        return batchId;
    }

    public SourceKeyBatchReference? GetSourceKeyBatchReference(
        string runId,
        string sourceStepId,
        string sourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SourceKey, BatchId, RecordIndex
            FROM SourceKeyIndex
            WHERE RunId = $runId AND StepId = $stepId AND SourceKey = $sourceKey;
            """;
        AddParameter(command, "$runId", runId);
        AddParameter(command, "$stepId", sourceStepId);
        AddParameter(command, "$sourceKey", sourceKey);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new SourceKeyBatchReference(reader.GetString(0), reader.GetString(1), reader.GetInt32(2))
            : null;
    }

    public IEnumerable<SourceKeyBatchReference> EnumerateSourceKeyBatchReferences(
        string runId,
        string sourceStepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceStepId);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT SourceKey, BatchId, RecordIndex
            FROM SourceKeyIndex
            WHERE RunId = $runId AND StepId = $stepId
            ORDER BY SourceKey COLLATE BINARY;
            """;
        AddParameter(command, "$runId", runId);
        AddParameter(command, "$stepId", sourceStepId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return new SourceKeyBatchReference(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2));
        }
    }

    public StoredBatch? TryLeaseNextBatch(
        string runId,
        string workerId,
        TimeSpan leaseDuration,
        DateTimeOffset? nowUtc = null,
        string? stepId = null)
    {
        using var writeLock = SqliteDatabaseWriteGate.Enter(DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "Lease duration must be positive.");
        }

        var now = nowUtc ?? DateTimeOffset.UtcNow;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Batches
            SET State = 'InFlight',
                Attempts = Attempts + 1,
                LeaseOwner = $workerId,
                LeaseExpiresUtc = $leaseExpiresUtc,
                UpdatedUtc = $now,
                LastError = NULL
            WHERE BatchId = (
                SELECT BatchId
                FROM Batches
                WHERE RunId = $runId
                  AND ($stepId IS NULL OR StepId = $stepId)
                  AND (
                    State = 'Pending'
                    OR (State = 'InFlight' AND LeaseExpiresUtc <= $now)
                  )
                ORDER BY
                    CASE WHEN EXISTS (
                        SELECT 1
                        FROM DependencyEvents AS dependency
                        WHERE dependency.RunId = Batches.RunId
                          AND dependency.EventId = Batches.BatchId
                          AND dependency.State = 'Completed'
                    ) THEN 0 ELSE 1 END,
                    CreatedUtc, StepId, Sequence
                LIMIT 1
            )
            RETURNING BatchId, RunId, StepId, Sequence, CursorAfterJson, PayloadJson, State,
                      Attempts, LeaseOwner, LeaseExpiresUtc, OutcomesJson, LastError, CreatedUtc, UpdatedUtc;
            """;
        AddParameter(command, "$workerId", workerId);
        AddParameter(command, "$leaseExpiresUtc", FormatUtc(now.Add(leaseDuration)));
        AddParameter(command, "$now", FormatUtc(now));
        AddParameter(command, "$runId", runId);
        AddParameter(command, "$stepId", stepId);

        StoredBatch? batch;
        using (var reader = command.ExecuteReader())
        {
            batch = reader.Read() ? ReadBatch(reader) : null;
        }

        if (batch is not null)
        {
            EnterStepActivity(connection, transaction, runId, batch.StepId, now);
            batch = AttachDependencyParents(connection, transaction, batch);
            using var touch = connection.CreateCommand();
            touch.Transaction = transaction;
            touch.CommandText = "UPDATE Runs SET UpdatedUtc = $now WHERE RunId = $runId;";
            AddParameter(touch, "$now", FormatUtc(now));
            AddParameter(touch, "$runId", runId);
            touch.ExecuteNonQuery();
        }

        transaction.Commit();
        return batch;
    }

    public void AbandonBatch(
        string batchId,
        string workerId,
        string error,
        DateTimeOffset? nowUtc = null)
    {
        if (!TryAbandonBatch(batchId, workerId, error, nowUtc))
        {
            throw new InvalidOperationException($"Worker '{workerId}' does not hold batch lease '{batchId}'.");
        }
    }

    public bool TryAbandonBatch(
        string batchId,
        string workerId,
        string error,
        DateTimeOffset? nowUtc = null)
    {
        using var writeLock = SqliteDatabaseWriteGate.Enter(DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentNullException.ThrowIfNull(error);

        var now = nowUtc ?? DateTimeOffset.UtcNow;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var batch = FindBatchById(connection, transaction, batchId)
            ?? throw new KeyNotFoundException($"Batch '{batchId}' was not found.");
        if (batch.State != MigrationBatchState.InFlight ||
            !string.Equals(batch.LeaseOwner, workerId, StringComparison.Ordinal))
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Batches
            SET State = 'Pending',
                LeaseOwner = NULL,
                LeaseExpiresUtc = NULL,
                OutcomesJson = NULL,
                LastError = $error,
                UpdatedUtc = $now
            WHERE BatchId = $batchId
              AND State = 'InFlight'
              AND LeaseOwner = $workerId;
            """;
        AddParameter(command, "$error", error);
        AddParameter(command, "$now", FormatUtc(now));
        AddParameter(command, "$batchId", batchId);
        AddParameter(command, "$workerId", workerId);

        if (command.ExecuteNonQuery() != 1)
        {
            return false;
        }
        ExitStepActivity(connection, transaction, batch.RunId, batch.StepId, now);
        TouchRun(connection, transaction, batch.RunId, FormatUtc(now));
        transaction.Commit();
        return true;
    }

    public bool RenewBatchLease(
        string batchId,
        string workerId,
        TimeSpan leaseDuration,
        DateTimeOffset? nowUtc = null)
    {
        using var writeLock = SqliteDatabaseWriteGate.Enter(DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "Lease duration must be positive.");
        }

        var now = nowUtc ?? DateTimeOffset.UtcNow;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Batches
            SET LeaseExpiresUtc = $leaseExpiresUtc,
                UpdatedUtc = $now
            WHERE BatchId = $batchId
              AND State = 'InFlight'
              AND LeaseOwner = $workerId;
            """;
        AddParameter(command, "$leaseExpiresUtc", FormatUtc(now.Add(leaseDuration)));
        AddParameter(command, "$now", FormatUtc(now));
        AddParameter(command, "$batchId", batchId);
        AddParameter(command, "$workerId", workerId);
        return command.ExecuteNonQuery() == 1;
    }

    public MigrationStepCheckpoint CompleteBatch(
        string batchId,
        string workerId,
        string outcomesJson,
        bool hasErrors,
        IReadOnlyCollection<DependencyEvent>? dependencyEvents = null,
        string? lastError = null,
        DateTimeOffset? nowUtc = null,
        long filteredRows = 0)
    {
        return TryCompleteBatch(
                   batchId,
                   workerId,
                   outcomesJson,
                   hasErrors,
                   dependencyEvents,
                   lastError,
                   nowUtc,
                   filteredRows)
               ?? throw new InvalidOperationException($"Worker '{workerId}' does not hold batch lease '{batchId}'.");
    }

    public MigrationStepCheckpoint? TryCompleteBatch(
        string batchId,
        string workerId,
        string outcomesJson,
        bool hasErrors,
        IReadOnlyCollection<DependencyEvent>? dependencyEvents = null,
        string? lastError = null,
        DateTimeOffset? nowUtc = null,
        long filteredRows = 0)
    {
        using var writeLock = SqliteDatabaseWriteGate.Enter(DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentNullException.ThrowIfNull(outcomesJson);
        ValidateJson(outcomesJson, nameof(outcomesJson));
        ArgumentOutOfRangeException.ThrowIfNegative(filteredRows);

        var events = dependencyEvents ?? Array.Empty<DependencyEvent>();
        if (hasErrors && events.Count > 0)
        {
            throw new ArgumentException(
                "A batch with row errors cannot release dependent work.",
                nameof(dependencyEvents));
        }

        var now = FormatUtc(nowUtc ?? DateTimeOffset.UtcNow);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);

        var batch = FindBatchById(connection, transaction, batchId)
            ?? throw new KeyNotFoundException($"Batch '{batchId}' was not found.");
        if (batch.State != MigrationBatchState.InFlight ||
            !string.Equals(batch.LeaseOwner, workerId, StringComparison.Ordinal))
        {
            return null;
        }

        var terminalState = hasErrors
            ? MigrationBatchState.CompletedWithErrors
            : MigrationBatchState.Succeeded;
        var rowCounts = ReadRowCounts(outcomesJson);

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE Batches
                SET State = $state,
                    PayloadJson = CASE WHEN $state = 'Succeeded' THEN NULL ELSE PayloadJson END,
                    OutcomesJson = $outcomesJson,
                    SucceededRows = $succeededRows,
                    FailedRows = $failedRows,
                    FilteredRows = FilteredRows + $filteredRows,
                    LeaseOwner = NULL,
                    LeaseExpiresUtc = NULL,
                    LastError = $lastError,
                    UpdatedUtc = $now
                WHERE BatchId = $batchId
                  AND State = 'InFlight'
                  AND LeaseOwner = $workerId;
                """;
            AddParameter(update, "$state", terminalState.ToString());
            AddParameter(update, "$outcomesJson", outcomesJson);
            AddParameter(update, "$succeededRows", rowCounts.Succeeded);
            AddParameter(update, "$failedRows", rowCounts.Failed);
            AddParameter(update, "$filteredRows", filteredRows);
            AddParameter(update, "$lastError", lastError);
            AddParameter(update, "$now", now);
            AddParameter(update, "$batchId", batchId);
            AddParameter(update, "$workerId", workerId);
            if (update.ExecuteNonQuery() != 1)
            {
                return null;
            }
        }
        ExitStepActivity(connection, transaction, batch.RunId, batch.StepId, ParseUtc(now));
        TouchRun(connection, transaction, batch.RunId, now);

        foreach (var dependencyEvent in events)
        {
            InsertDependencyEvent(connection, transaction, batch, dependencyEvent, now);
        }

        var checkpoint = AdvanceCheckpoint(connection, transaction, batch.RunId, batch.StepId, now);
        transaction.Commit();
        return checkpoint;
    }

    public StoredBatch? GetBatch(string batchId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        using var connection = OpenConnection();
        return FindBatchById(connection, null, batchId);
    }

    public MigrationStepCheckpoint GetStepCheckpoint(string runId, string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        using var connection = OpenConnection();
        return ReadCheckpoint(connection, null, runId, tableName)
            ?? new MigrationStepCheckpoint(runId, tableName, -1, null, null);
    }

    public MigrationStepCheckpoint GetStepSourceCheckpoint(string runId, string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        using var connection = OpenConnection();
        var checkpoint = ReadCheckpoint(connection, null, runId, stepId)
            ?? new MigrationStepCheckpoint(runId, stepId, -1, null, null);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Sequence, CursorAfterJson
            FROM Batches
            WHERE RunId = $runId AND StepId = $stepId
            ORDER BY Sequence DESC
            LIMIT 1;
            """;
        AddParameter(command, "$runId", runId);
        AddParameter(command, "$stepId", stepId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return checkpoint;
        }

        return checkpoint with
        {
            LastEnqueuedSequence = reader.GetInt64(0),
            LastEnqueuedCursorJson = reader.IsDBNull(1) ? null : reader.GetString(1)
        };
    }

    public IReadOnlyList<StoredDependencyEvent> GetPendingDependencyEvents(
        string runId,
        int limit = 100,
        DateTimeOffset? afterCreatedUtc = null,
        string? afterEventId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be positive.");
        }
        if (afterCreatedUtc.HasValue != (afterEventId is not null))
        {
            throw new ArgumentException(
                "The dependency-event page cursor must include both its timestamp and event ID.");
        }
        if (afterEventId is not null && string.IsNullOrWhiteSpace(afterEventId))
        {
            throw new ArgumentException("The dependency-event cursor ID must not be empty.", nameof(afterEventId));
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EventId, RunId, ParentBatchId, ChildStepId, PayloadJson, PrerequisiteBatchIdsJson, CreatedUtc
            FROM DependencyEvents
            WHERE RunId = $runId AND State = 'Pending'
              AND (
                $afterCreatedUtc IS NULL
                OR CreatedUtc > $afterCreatedUtc
                OR (CreatedUtc = $afterCreatedUtc AND EventId > $afterEventId)
              )
            ORDER BY CreatedUtc, EventId
            LIMIT $limit;
            """;
        AddParameter(command, "$runId", runId);
        AddParameter(command, "$limit", limit);
        AddParameter(
            command,
            "$afterCreatedUtc",
            afterCreatedUtc.HasValue ? FormatUtc(afterCreatedUtc.Value) : null);
        AddParameter(command, "$afterEventId", afterEventId);

        using var reader = command.ExecuteReader();
        var events = new List<StoredDependencyEvent>();
        while (reader.Read())
        {
            events.Add(new StoredDependencyEvent(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                DeserializeBatchIds(reader.GetString(5)),
                ParseUtc(reader.GetString(6))));
        }

        return events;
    }

    public DependencyBatchMaterialization? TryMaterializeDependencyEvent(
        string runId,
        string eventId,
        DateTimeOffset? nowUtc = null)
    {
        using var writeLock = SqliteDatabaseWriteGate.Enter(DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        var now = FormatUtc(nowUtc ?? DateTimeOffset.UtcNow);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);

        string parentBatchId;
        string childTable;
        string payloadJson;
        IReadOnlyList<string> prerequisiteBatchIds;
        string state;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT ParentBatchId, ChildStepId, PayloadJson, PrerequisiteBatchIdsJson, State
                FROM DependencyEvents
                WHERE RunId = $runId AND EventId = $eventId;
                """;
            AddParameter(read, "$runId", runId);
            AddParameter(read, "$eventId", eventId);
            using var reader = read.ExecuteReader();
            if (!reader.Read())
            {
                throw new KeyNotFoundException($"Dependency event '{eventId}' was not found for run '{runId}'.");
            }

            parentBatchId = reader.GetString(0);
            childTable = reader.GetString(1);
            payloadJson = reader.GetString(2);
            prerequisiteBatchIds = DeserializeBatchIds(reader.GetString(3));
            state = reader.GetString(4);
        }

        if (state == "Completed")
        {
            var completedBatch = FindBatchById(connection, transaction, eventId)
                ?? throw new InvalidOperationException(
                    $"Completed dependency event '{eventId}' has no materialized batch.");
            if (!string.Equals(completedBatch.RunId, runId, StringComparison.Ordinal) ||
                !string.Equals(completedBatch.StepId, childTable, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Completed dependency event '{eventId}' conflicts with its materialized batch.");
            }

            transaction.Commit();
            return new DependencyBatchMaterialization(completedBatch, WasCreated: false);
        }

        if (state != "Pending")
        {
            throw new InvalidOperationException($"Dependency event '{eventId}' has unknown state '{state}'.");
        }

        var parentBatch = FindBatchById(connection, transaction, parentBatchId)
            ?? throw new InvalidOperationException(
                $"Dependency event '{eventId}' references missing parent batch '{parentBatchId}'.");
        if (!string.Equals(parentBatch.RunId, runId, StringComparison.Ordinal) ||
            parentBatch.State != MigrationBatchState.Succeeded)
        {
            throw new InvalidOperationException(
                $"Dependency event '{eventId}' cannot be released because parent batch '{parentBatchId}' did not succeed.");
        }

        foreach (var prerequisiteBatchId in prerequisiteBatchIds)
        {
            var prerequisiteBatch = FindBatchById(connection, transaction, prerequisiteBatchId);
            if (prerequisiteBatch is null)
            {
                transaction.Rollback();
                return null;
            }
            if (!string.Equals(prerequisiteBatch.RunId, runId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Dependency event '{eventId}' references a prerequisite from another run.");
            }
            if (prerequisiteBatch.State == MigrationBatchState.CompletedWithErrors)
            {
                throw new InvalidOperationException(
                    $"Dependency event '{eventId}' is blocked by failed prerequisite batch '{prerequisiteBatchId}'.");
            }
            if (prerequisiteBatch.State != MigrationBatchState.Succeeded)
            {
                transaction.Rollback();
                return null;
            }
        }

        var checkpointNow = FormatUtc(nowUtc ?? DateTimeOffset.UtcNow);
        using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.Transaction = transaction;
            checkpoint.CommandText = """
                INSERT INTO StepCheckpoints (RunId, StepId, LastCompletedSequence, CursorJson, UpdatedUtc)
                VALUES ($runId, $stepId, -1, NULL, $now)
                ON CONFLICT (RunId, StepId) DO NOTHING;
                """;
            AddParameter(checkpoint, "$runId", runId);
            AddParameter(checkpoint, "$stepId", childTable);
            AddParameter(checkpoint, "$now", checkpointNow);
            checkpoint.ExecuteNonQuery();
        }

        var sequence = GetNextSequence(connection, transaction, runId, childTable);
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO Batches (
                    BatchId, RunId, StepId, Sequence, CursorAfterJson, PayloadHash, PayloadJson,
                    State, Attempts, CreatedUtc, UpdatedUtc, InputRows)
                VALUES (
                    $batchId, $runId, $stepId, $sequence, NULL, $payloadHash, $payloadJson,
                    'Pending', 0, $now, $now, $inputRows);
                """;
            AddParameter(insert, "$batchId", eventId);
            AddParameter(insert, "$runId", runId);
            AddParameter(insert, "$stepId", childTable);
            AddParameter(insert, "$sequence", sequence);
            AddParameter(insert, "$payloadHash", Hash(payloadJson));
            AddParameter(insert, "$payloadJson", payloadJson);
            AddParameter(insert, "$now", checkpointNow);
            AddParameter(insert, "$inputRows", GetInputRowCount(payloadJson));
            insert.ExecuteNonQuery();
        }

        using (var complete = connection.CreateCommand())
        {
            complete.Transaction = transaction;
            complete.CommandText = """
                UPDATE DependencyEvents
                SET State = 'Completed', UpdatedUtc = $now
                WHERE RunId = $runId AND EventId = $eventId AND State = 'Pending';
                """;
            AddParameter(complete, "$now", checkpointNow);
            AddParameter(complete, "$runId", runId);
            AddParameter(complete, "$eventId", eventId);
            if (complete.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    $"Dependency event '{eventId}' was concurrently materialized.");
            }
        }

        var batch = FindBatchById(connection, transaction, eventId)
            ?? throw new InvalidOperationException(
                $"Dependency event '{eventId}' could not be read after materialization.");
        transaction.Commit();
        return new DependencyBatchMaterialization(batch, WasCreated: true);
    }

    public void CompleteDependencyEvent(string runId, string eventId, DateTimeOffset? nowUtc = null)
    {
        using var writeLock = SqliteDatabaseWriteGate.Enter(DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE DependencyEvents
            SET State = 'Completed', UpdatedUtc = $now
            WHERE RunId = $runId AND EventId = $eventId AND State = 'Pending';
            """;
        AddParameter(command, "$now", FormatUtc(nowUtc ?? DateTimeOffset.UtcNow));
        AddParameter(command, "$runId", runId);
        AddParameter(command, "$eventId", eventId);

        if (command.ExecuteNonQuery() == 1)
        {
            return;
        }

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT State FROM DependencyEvents WHERE RunId = $runId AND EventId = $eventId;";
        AddParameter(read, "$runId", runId);
        AddParameter(read, "$eventId", eventId);
        var state = read.ExecuteScalar()?.ToString();
        if (!string.Equals(state, "Completed", StringComparison.Ordinal))
        {
            throw new KeyNotFoundException($"Pending dependency event '{eventId}' was not found for run '{runId}'.");
        }
    }

    private void Initialize()
    {
        using var writeLock = SqliteDatabaseWriteGate.Enter(DatabasePath);
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        ConfigureConnection(connection);

        using (var journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode = WAL;";
            var mode = journal.ExecuteScalar()?.ToString();
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"SQLite WAL mode could not be enabled for '{DatabasePath}'.");
            }
        }

        var migrationTableExists = TableExists(connection, "SchemaMigrations");
        if (!migrationTableExists && HasApplicationTables(connection))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has no schema migration history. Create a fresh database; untracked schemas are not supported.");
        }

        ExecuteNonQuery(connection, null, """
            CREATE TABLE IF NOT EXISTS SchemaMigrations (
                Version INTEGER NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL UNIQUE,
                AppliedUtc TEXT NOT NULL
            );
            """);

        var appliedMigrations = ReadAppliedMigrations(connection);
        if (migrationTableExists && appliedMigrations.Count == 0 && HasApplicationTables(connection))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' contains tables but no applied migration records. Create a fresh database.");
        }
        if (appliedMigrations.Keys.Any(version => version > 14))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has a schema migration newer than this engine supports.");
        }

        if (appliedMigrations.TryGetValue(1, out var initialName) &&
            !string.Equals(initialName, "001-initial-store", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has an unknown schema migration at version 1.");
        }
        if (appliedMigrations.TryGetValue(2, out var stepName) &&
            !string.Equals(stepName, "002-top-level-steps", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has an unknown schema migration at version 2.");
        }
        if (appliedMigrations.TryGetValue(3, out var metricsName) &&
            !string.Equals(metricsName, "003-step-runtime-metrics", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has an unknown schema migration at version 3.");
        }
        if (appliedMigrations.TryGetValue(4, out var targetRecordIdsName) &&
            !string.Equals(targetRecordIdsName, "004-target-record-ids", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has an unknown schema migration at version 4.");
        }
        if (appliedMigrations.TryGetValue(5, out var targetRecordContextsName) &&
            !string.Equals(targetRecordContextsName, "005-target-record-contexts", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has an unknown schema migration at version 5.");
        }
        if (appliedMigrations.TryGetValue(6, out var streamingJoinName) &&
            !string.Equals(streamingJoinName, "006-streaming-join-staging", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has an unknown schema migration at version 6.");
        }
        if (appliedMigrations.TryGetValue(7, out var recordProgressName) &&
            !string.Equals(recordProgressName, "007-record-progress", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has an unknown schema migration at version 7.");
        }
        if (appliedMigrations.TryGetValue(8, out var activityPeriodsName) &&
            !string.Equals(activityPeriodsName, "008-step-activity-periods", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has an unknown schema migration at version 8.");
        }
        if (appliedMigrations.TryGetValue(9, out var sourceKeyIndexName) &&
            !string.Equals(sourceKeyIndexName, "009-source-key-index", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has an unknown schema migration at version 9.");
        }
        if (appliedMigrations.TryGetValue(10, out var joinInputKeyIndexName) &&
            !string.Equals(joinInputKeyIndexName, "010-streaming-join-key-index", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has an unknown schema migration at version 10.");
        }
        if (appliedMigrations.TryGetValue(11, out var debugSnapshotRetentionIndexName) &&
            !string.Equals(debugSnapshotRetentionIndexName, "011-debug-snapshot-retention-index", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has an unknown schema migration at version 11.");
        }
        if (appliedMigrations.TryGetValue(12, out var dataverseTargetTuningName) &&
            !string.Equals(dataverseTargetTuningName, "012-dataverse-target-tuning-profiles", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has an unknown schema migration at version 12.");
        }
        if (appliedMigrations.TryGetValue(13, out var pipelineTuningName) &&
            !string.Equals(pipelineTuningName, "013-pipeline-aware-tuning-profiles", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has an unknown schema migration at version 13.");
        }
        if (appliedMigrations.TryGetValue(14, out var connectionTuningName) &&
            !string.Equals(connectionTuningName, "014-connection-scoped-tuning-profiles", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has an unknown schema migration at version 14.");
        }
        if (appliedMigrations.ContainsKey(2) && !appliedMigrations.ContainsKey(1))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has an incomplete schema migration history.");
        }
        if (appliedMigrations.ContainsKey(3) && !appliedMigrations.ContainsKey(2))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has an incomplete schema migration history.");
        }
        if (appliedMigrations.ContainsKey(4) && !appliedMigrations.ContainsKey(3))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has an incomplete schema migration history.");
        }
        if (appliedMigrations.ContainsKey(5) && !appliedMigrations.ContainsKey(4))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has an incomplete schema migration history.");
        }
        if (appliedMigrations.ContainsKey(8) && !appliedMigrations.ContainsKey(7))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has an incomplete schema migration history.");
        }
        if (appliedMigrations.ContainsKey(9) && !appliedMigrations.ContainsKey(8))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has an incomplete schema migration history.");
        }
        if (appliedMigrations.ContainsKey(10) && !appliedMigrations.ContainsKey(9))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has an incomplete schema migration history.");
        }
        if (appliedMigrations.ContainsKey(11) && !appliedMigrations.ContainsKey(10))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has an incomplete schema migration history.");
        }
        if (appliedMigrations.ContainsKey(12) && !appliedMigrations.ContainsKey(11))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has an incomplete schema migration history.");
        }
        if (appliedMigrations.ContainsKey(13) && !appliedMigrations.ContainsKey(12))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has an incomplete schema migration history.");
        }
        if (appliedMigrations.ContainsKey(14) && !appliedMigrations.ContainsKey(13))
        {
            throw new InvalidOperationException(
                $"Database '{DatabasePath}' has an incomplete schema migration history.");
        }

        if (!appliedMigrations.ContainsKey(1))
        {
            using var initialTransaction = connection.BeginTransaction();
            ExecuteNonQuery(connection, initialTransaction, """
                INSERT INTO SchemaMigrations (Version, Name, AppliedUtc)
                VALUES (1, '001-initial-store', 'initializing');
                """);

            ExecuteNonQuery(connection, initialTransaction, """
            CREATE TABLE IF NOT EXISTS Runs (
                RunId TEXT NOT NULL PRIMARY KEY,
                JobName TEXT NOT NULL,
                JobHash TEXT NOT NULL,
                Status TEXT NOT NULL CHECK (Status IN ('Running', 'Completed', 'Failed', 'Cancelled')),
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                SchedulerLeaseOwner TEXT NULL,
                SchedulerLeaseExpiresUtc TEXT NULL,
                PipelineJson TEXT NOT NULL DEFAULT '[]',
                DebugCaptureEnabled INTEGER NOT NULL DEFAULT 0,
                FinishedUtc TEXT NULL,
                DebugCaptureRetentionDays INTEGER NOT NULL DEFAULT 30,
                ConfigurationJson TEXT NOT NULL DEFAULT 'null',
                ConnectionInstancesJson TEXT NOT NULL DEFAULT '[]'
            );

            CREATE TABLE IF NOT EXISTS TableCheckpoints (
                RunId TEXT NOT NULL,
                TableName TEXT NOT NULL,
                LastCompletedSequence INTEGER NOT NULL DEFAULT -1,
                CursorJson TEXT NULL,
                UpdatedUtc TEXT NOT NULL,
                PRIMARY KEY (RunId, TableName),
                FOREIGN KEY (RunId) REFERENCES Runs (RunId) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS SourceFeeders (
                RunId TEXT NOT NULL,
                JobName TEXT NOT NULL,
                Completed INTEGER NOT NULL DEFAULT 0 CHECK (Completed IN (0, 1)),
                UpdatedUtc TEXT NOT NULL,
                PRIMARY KEY (RunId, JobName),
                FOREIGN KEY (RunId) REFERENCES Runs (RunId) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS Batches (
                BatchId TEXT NOT NULL PRIMARY KEY,
                RunId TEXT NOT NULL,
                TableName TEXT NOT NULL,
                Sequence INTEGER NOT NULL CHECK (Sequence >= 0),
                CursorAfterJson TEXT NULL,
                PayloadHash TEXT NOT NULL,
                PayloadJson TEXT NULL,
                State TEXT NOT NULL CHECK (State IN ('Pending', 'InFlight', 'Succeeded', 'CompletedWithErrors')),
                Attempts INTEGER NOT NULL DEFAULT 0 CHECK (Attempts >= 0),
                LeaseOwner TEXT NULL,
                LeaseExpiresUtc TEXT NULL,
                OutcomesJson TEXT NULL,
                LastError TEXT NULL,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                UNIQUE (RunId, TableName, Sequence),
                FOREIGN KEY (RunId) REFERENCES Runs (RunId) ON DELETE CASCADE,
                CHECK (
                    (State = 'Succeeded' AND PayloadJson IS NULL AND OutcomesJson IS NOT NULL)
                    OR (State = 'CompletedWithErrors' AND PayloadJson IS NOT NULL AND OutcomesJson IS NOT NULL)
                    OR (State IN ('Pending', 'InFlight') AND PayloadJson IS NOT NULL AND OutcomesJson IS NULL)
                ),
                CHECK (
                    (State = 'InFlight' AND LeaseOwner IS NOT NULL AND LeaseExpiresUtc IS NOT NULL)
                    OR (State <> 'InFlight' AND LeaseOwner IS NULL AND LeaseExpiresUtc IS NULL)
                )
            );

            CREATE TABLE IF NOT EXISTS DependencyEvents (
                EventId TEXT NOT NULL PRIMARY KEY,
                RunId TEXT NOT NULL,
                ParentBatchId TEXT NOT NULL,
                ChildTable TEXT NOT NULL,
                PayloadJson TEXT NOT NULL,
                PrerequisiteBatchIdsJson TEXT NOT NULL DEFAULT '[]',
                State TEXT NOT NULL CHECK (State IN ('Pending', 'Completed')),
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                FOREIGN KEY (RunId) REFERENCES Runs (RunId) ON DELETE CASCADE,
                FOREIGN KEY (ParentBatchId) REFERENCES Batches (BatchId) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS DebugSnapshots (
                SnapshotId INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                RunId TEXT NOT NULL,
                BatchId TEXT NOT NULL,
                JobName TEXT NOT NULL,
                StageName TEXT NOT NULL,
                RecordIndex INTEGER NOT NULL,
                Attempt INTEGER NOT NULL,
                LineageId TEXT NOT NULL,
                ParentLineageIdsJson TEXT NOT NULL,
                PayloadJson TEXT NOT NULL,
                CapturedUtc TEXT NOT NULL,
                FOREIGN KEY (RunId) REFERENCES Runs (RunId) ON DELETE CASCADE,
                FOREIGN KEY (BatchId) REFERENCES Batches (BatchId) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS DebugBatchInputs (
                RunId TEXT NOT NULL,
                BatchId TEXT NOT NULL,
                RecordIndex INTEGER NOT NULL CHECK (RecordIndex >= 0),
                LineageId TEXT NOT NULL,
                PRIMARY KEY (RunId, BatchId, RecordIndex),
                FOREIGN KEY (RunId) REFERENCES Runs (RunId) ON DELETE CASCADE,
                FOREIGN KEY (BatchId) REFERENCES Batches (BatchId) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_Batches_Lease
                ON Batches (RunId, State, LeaseExpiresUtc, CreatedUtc);
            CREATE INDEX IF NOT EXISTS IX_Batches_Sequence
                ON Batches (RunId, TableName, Sequence);
            CREATE INDEX IF NOT EXISTS IX_DependencyEvents_Pending
                ON DependencyEvents (RunId, State, CreatedUtc);
            CREATE INDEX IF NOT EXISTS IX_DebugSnapshots_Lineage
                ON DebugSnapshots (RunId, LineageId, CapturedUtc);
            CREATE INDEX IF NOT EXISTS IX_DebugSnapshots_Batch
                ON DebugSnapshots (RunId, BatchId, CapturedUtc);
            CREATE INDEX IF NOT EXISTS IX_DebugSnapshots_Job_Captured
                ON DebugSnapshots (RunId, JobName, CapturedUtc DESC, SnapshotId DESC);
            """);
            UpdateMigrationAppliedUtc(connection, initialTransaction, 1);
            initialTransaction.Commit();
        }

        if (!appliedMigrations.ContainsKey(2))
        {
            using var stepTransaction = connection.BeginTransaction();
            ExecuteNonQuery(connection, stepTransaction, """
                ALTER TABLE Runs RENAME COLUMN JobName TO RunName;
                ALTER TABLE Runs RENAME COLUMN JobHash TO StepHash;
                ALTER TABLE TableCheckpoints RENAME COLUMN TableName TO StepId;
                ALTER TABLE TableCheckpoints RENAME TO StepCheckpoints;
                ALTER TABLE Batches RENAME COLUMN TableName TO StepId;
                ALTER TABLE DependencyEvents RENAME COLUMN ChildTable TO ChildStepId;
                ALTER TABLE SourceFeeders RENAME COLUMN JobName TO StepId;
                ALTER TABLE DebugSnapshots RENAME COLUMN JobName TO StepId;

                DROP INDEX IF EXISTS IX_Batches_Sequence;
                CREATE INDEX IX_Batches_Sequence ON Batches (RunId, StepId, Sequence);
                DROP INDEX IF EXISTS IX_DebugSnapshots_Job_Captured;
                CREATE INDEX IX_DebugSnapshots_Step_Captured
                    ON DebugSnapshots (RunId, StepId, CapturedUtc DESC, SnapshotId DESC);

                INSERT INTO SchemaMigrations (Version, Name, AppliedUtc)
                VALUES (2, '002-top-level-steps', 'initializing');
                """);
            UpdateMigrationAppliedUtc(connection, stepTransaction, 2);
            stepTransaction.Commit();
        }

        if (!appliedMigrations.ContainsKey(3))
        {
            using var metricsTransaction = connection.BeginTransaction();
            ExecuteNonQuery(connection, metricsTransaction, """
                ALTER TABLE Batches ADD COLUMN SucceededRows INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE Batches ADD COLUMN FailedRows INTEGER NOT NULL DEFAULT 0;

                UPDATE Batches
                SET SucceededRows = CASE
                        WHEN json_type(OutcomesJson, '$.succeeded') IN ('integer', 'real')
                            THEN CAST(json_extract(OutcomesJson, '$.succeeded') AS INTEGER)
                        WHEN json_type(OutcomesJson, '$.succeeded') = 'array'
                            THEN json_array_length(OutcomesJson, '$.succeeded')
                        WHEN json_type(OutcomesJson, '$.succeeded') = 'object' THEN 1
                        WHEN json_type(OutcomesJson, '$.success') IN ('integer', 'real')
                            THEN CAST(json_extract(OutcomesJson, '$.success') AS INTEGER)
                        WHEN json_type(OutcomesJson, '$.success') = 'array'
                            THEN json_array_length(OutcomesJson, '$.success')
                        WHEN json_type(OutcomesJson, '$.success') = 'object' THEN 1
                        ELSE 0
                    END,
                    FailedRows = CASE
                        WHEN json_type(OutcomesJson, '$.failed') IN ('integer', 'real')
                            THEN CAST(json_extract(OutcomesJson, '$.failed') AS INTEGER)
                        WHEN json_type(OutcomesJson, '$.failed') = 'array'
                            THEN json_array_length(OutcomesJson, '$.failed')
                        WHEN json_type(OutcomesJson, '$.failed') = 'object' THEN 1
                        WHEN json_type(OutcomesJson, '$.errors') IN ('integer', 'real')
                            THEN CAST(json_extract(OutcomesJson, '$.errors') AS INTEGER)
                        WHEN json_type(OutcomesJson, '$.errors') = 'array'
                            THEN json_array_length(OutcomesJson, '$.errors')
                        WHEN json_type(OutcomesJson, '$.errors') = 'object' THEN 1
                        ELSE 0
                    END
                WHERE OutcomesJson IS NOT NULL;

                CREATE TABLE StepActivity (
                    RunId TEXT NOT NULL,
                    StepId TEXT NOT NULL,
                    StartedUtc TEXT NOT NULL,
                    ActiveMilliseconds INTEGER NOT NULL DEFAULT 0 CHECK (ActiveMilliseconds >= 0),
                    IdleMilliseconds INTEGER NOT NULL DEFAULT 0 CHECK (IdleMilliseconds >= 0),
                    ActiveCount INTEGER NOT NULL DEFAULT 0 CHECK (ActiveCount >= 0),
                    ActiveSinceUtc TEXT NULL,
                    IdleSinceUtc TEXT NULL,
                    CompletedUtc TEXT NULL,
                    PRIMARY KEY (RunId, StepId),
                    FOREIGN KEY (RunId) REFERENCES Runs (RunId) ON DELETE CASCADE
                );

                INSERT INTO SchemaMigrations (Version, Name, AppliedUtc)
                VALUES (3, '003-step-runtime-metrics', 'initializing');
                """);
            UpdateMigrationAppliedUtc(connection, metricsTransaction, 3);
            metricsTransaction.Commit();
        }

        if (!appliedMigrations.ContainsKey(4))
        {
            using var targetRecordIdsTransaction = connection.BeginTransaction();
            ExecuteNonQuery(connection, targetRecordIdsTransaction, """
                CREATE TABLE TargetRecordIds (
                    RunId TEXT NOT NULL,
                    ConnectionId TEXT NOT NULL,
                    TableName TEXT NOT NULL,
                    KeyColumn TEXT NOT NULL,
                    KeyValue TEXT NOT NULL,
                    RecordId TEXT NOT NULL,
                    UpdatedUtc TEXT NOT NULL,
                    PRIMARY KEY (RunId, ConnectionId, TableName, KeyColumn, KeyValue),
                    FOREIGN KEY (RunId) REFERENCES Runs (RunId) ON DELETE CASCADE
                );

                INSERT INTO SchemaMigrations (Version, Name, AppliedUtc)
                VALUES (4, '004-target-record-ids', 'initializing');
                """);
            UpdateMigrationAppliedUtc(connection, targetRecordIdsTransaction, 4);
            targetRecordIdsTransaction.Commit();
        }

        if (!appliedMigrations.ContainsKey(5))
        {
            using var targetRecordContextsTransaction = connection.BeginTransaction();
            ExecuteNonQuery(connection, targetRecordContextsTransaction, """
                CREATE TABLE TargetRecordContexts (
                    RunId TEXT NOT NULL,
                    StepId TEXT NOT NULL,
                    ConnectionId TEXT NOT NULL,
                    TableName TEXT NOT NULL,
                    KeyColumn TEXT NOT NULL,
                    KeyValue TEXT NOT NULL,
                    RecordJson TEXT NOT NULL,
                    PRIMARY KEY (RunId, StepId, ConnectionId, TableName, KeyColumn, KeyValue),
                    FOREIGN KEY (RunId) REFERENCES Runs (RunId) ON DELETE CASCADE
                );

                CREATE INDEX IX_TargetRecordContexts_ParentLookup
                    ON TargetRecordContexts (RunId, StepId, ConnectionId, TableName, KeyColumn, KeyValue);

                INSERT INTO SchemaMigrations (Version, Name, AppliedUtc)
                VALUES (5, '005-target-record-contexts', 'initializing');
                """);
            UpdateMigrationAppliedUtc(connection, targetRecordContextsTransaction, 5);
            targetRecordContextsTransaction.Commit();
        }

        if (!appliedMigrations.ContainsKey(6))
        {
            using var joinStagingTransaction = connection.BeginTransaction();
            ExecuteNonQuery(connection, joinStagingTransaction, """
                CREATE TABLE JoinInputRecords (
                    RunId TEXT NOT NULL,
                    JoinStepId TEXT NOT NULL,
                    InputStepId TEXT NOT NULL,
                    Sequence INTEGER NOT NULL,
                    RecordIndex INTEGER NOT NULL,
                    RecordJson TEXT NOT NULL,
                    OriginBatchId TEXT NOT NULL,
                    OriginRecordIndex INTEGER NOT NULL,
                    PRIMARY KEY (RunId, JoinStepId, InputStepId, Sequence, RecordIndex),
                    FOREIGN KEY (RunId) REFERENCES Runs (RunId) ON DELETE CASCADE
                );

                CREATE INDEX IX_JoinInputRecords_Read
                    ON JoinInputRecords (RunId, JoinStepId, InputStepId, Sequence, RecordIndex);

                CREATE TABLE JoinRelationshipMatches (
                    RunId TEXT NOT NULL,
                    JoinStepId TEXT NOT NULL,
                    Sequence INTEGER NOT NULL,
                    RecordIndex INTEGER NOT NULL,
                    Alias TEXT NOT NULL,
                    ParentInputStepId TEXT NULL,
                    ParentSequence INTEGER NULL,
                    ParentRecordIndex INTEGER NULL,
                    PRIMARY KEY (RunId, JoinStepId, Sequence, RecordIndex, Alias),
                    FOREIGN KEY (RunId) REFERENCES Runs (RunId) ON DELETE CASCADE
                );

                CREATE TABLE BatchParentReferences (
                    RunId TEXT NOT NULL,
                    BatchId TEXT NOT NULL,
                    ParentBatchId TEXT NOT NULL,
                    PRIMARY KEY (RunId, BatchId, ParentBatchId),
                    FOREIGN KEY (RunId) REFERENCES Runs (RunId) ON DELETE CASCADE,
                    FOREIGN KEY (BatchId) REFERENCES Batches (BatchId) ON DELETE CASCADE,
                    FOREIGN KEY (ParentBatchId) REFERENCES Batches (BatchId) ON DELETE CASCADE
                );

                INSERT INTO SchemaMigrations (Version, Name, AppliedUtc)
                VALUES (6, '006-streaming-join-staging', 'initializing');
                """);
            UpdateMigrationAppliedUtc(connection, joinStagingTransaction, 6);
            joinStagingTransaction.Commit();
        }

        if (!appliedMigrations.ContainsKey(7))
        {
            using var recordProgressTransaction = connection.BeginTransaction();
            ExecuteNonQuery(connection, recordProgressTransaction, """
                ALTER TABLE Batches ADD COLUMN InputRows INTEGER NULL
                    CHECK (InputRows IS NULL OR InputRows >= 0);
                ALTER TABLE Batches ADD COLUMN FilteredRows INTEGER NOT NULL DEFAULT 0
                    CHECK (FilteredRows >= 0);

                UPDATE Batches
                SET InputRows = CASE
                    WHEN PayloadJson IS NOT NULL
                         AND json_type(PayloadJson, '$.Records') = 'array'
                        THEN json_array_length(PayloadJson, '$.Records')
                    ELSE NULL
                END;
                """);
            ExecuteNonQuery(connection, recordProgressTransaction, """
                INSERT INTO SchemaMigrations (Version, Name, AppliedUtc)
                VALUES (7, '007-record-progress', 'initializing');
                """);
            UpdateMigrationAppliedUtc(connection, recordProgressTransaction, 7);
            recordProgressTransaction.Commit();
        }

        if (!appliedMigrations.ContainsKey(8))
        {
            using var activityPeriodsTransaction = connection.BeginTransaction();
            ExecuteNonQuery(connection, activityPeriodsTransaction, """
                CREATE TABLE StepActivityPeriods (
                    PeriodId INTEGER PRIMARY KEY AUTOINCREMENT,
                    RunId TEXT NOT NULL,
                    StepId TEXT NOT NULL,
                    State TEXT NOT NULL CHECK (State IN ('Working', 'Waiting')),
                    StartedUtc TEXT NOT NULL,
                    EndedUtc TEXT NULL CHECK (EndedUtc IS NULL OR EndedUtc >= StartedUtc),
                    FOREIGN KEY (RunId, StepId)
                        REFERENCES StepActivity (RunId, StepId) ON DELETE CASCADE
                );
                CREATE INDEX IX_StepActivityPeriods_RunStepStarted
                    ON StepActivityPeriods (RunId, StepId, StartedUtc);
                CREATE UNIQUE INDEX IX_StepActivityPeriods_Open
                    ON StepActivityPeriods (RunId, StepId)
                    WHERE EndedUtc IS NULL;

                INSERT INTO SchemaMigrations (Version, Name, AppliedUtc)
                VALUES (8, '008-step-activity-periods', 'initializing');
                """);
            UpdateMigrationAppliedUtc(connection, activityPeriodsTransaction, 8);
            activityPeriodsTransaction.Commit();
        }

        if (!appliedMigrations.ContainsKey(9))
        {
            using var sourceKeyIndexTransaction = connection.BeginTransaction();
            ExecuteNonQuery(connection, sourceKeyIndexTransaction, """
                CREATE TABLE SourceKeyIndex (
                    RunId TEXT NOT NULL,
                    StepId TEXT NOT NULL,
                    SourceKey TEXT NOT NULL,
                    BatchId TEXT NOT NULL,
                    RecordIndex INTEGER NOT NULL CHECK (RecordIndex >= 0),
                    PRIMARY KEY (RunId, StepId, SourceKey),
                    FOREIGN KEY (RunId) REFERENCES Runs (RunId) ON DELETE CASCADE,
                    FOREIGN KEY (BatchId) REFERENCES Batches (BatchId) ON DELETE CASCADE
                );
                CREATE INDEX IX_SourceKeyIndex_Batch
                    ON SourceKeyIndex (RunId, StepId, BatchId);

                INSERT INTO SchemaMigrations (Version, Name, AppliedUtc)
                VALUES (9, '009-source-key-index', 'initializing');
                """);
            UpdateMigrationAppliedUtc(connection, sourceKeyIndexTransaction, 9);
            sourceKeyIndexTransaction.Commit();
        }

        if (!appliedMigrations.ContainsKey(10))
        {
            using var joinInputKeyIndexTransaction = connection.BeginTransaction();
            ExecuteNonQuery(connection, joinInputKeyIndexTransaction, """
                CREATE TABLE JoinInputKeyIndex (
                    RunId TEXT NOT NULL,
                    JoinStepId TEXT NOT NULL,
                    InputStepId TEXT NOT NULL,
                    Sequence INTEGER NOT NULL,
                    RecordIndex INTEGER NOT NULL CHECK (RecordIndex >= 0),
                    KeyJson TEXT,
                    PRIMARY KEY (RunId, JoinStepId, InputStepId, Sequence, RecordIndex),
                    FOREIGN KEY (RunId, JoinStepId, InputStepId, Sequence, RecordIndex)
                        REFERENCES JoinInputRecords (
                            RunId, JoinStepId, InputStepId, Sequence, RecordIndex)
                        ON DELETE CASCADE
                );
                CREATE INDEX IX_JoinInputKeyIndex_Lookup
                    ON JoinInputKeyIndex (RunId, JoinStepId, InputStepId, KeyJson, Sequence, RecordIndex);

                INSERT INTO SchemaMigrations (Version, Name, AppliedUtc)
                VALUES (10, '010-streaming-join-key-index', 'initializing');
                """);
            UpdateMigrationAppliedUtc(connection, joinInputKeyIndexTransaction, 10);
            joinInputKeyIndexTransaction.Commit();
        }

        if (!appliedMigrations.ContainsKey(11))
        {
            using var debugSnapshotRetentionIndexTransaction = connection.BeginTransaction();
            ExecuteNonQuery(connection, debugSnapshotRetentionIndexTransaction, """
                CREATE INDEX IX_DebugSnapshots_Run_Captured
                    ON DebugSnapshots (RunId, CapturedUtc);

                INSERT INTO SchemaMigrations (Version, Name, AppliedUtc)
                VALUES (11, '011-debug-snapshot-retention-index', 'initializing');
                """);
            UpdateMigrationAppliedUtc(connection, debugSnapshotRetentionIndexTransaction, 11);
            debugSnapshotRetentionIndexTransaction.Commit();
        }

        if (!appliedMigrations.ContainsKey(12))
        {
            using var dataverseTargetTuningTransaction = connection.BeginTransaction();
            ExecuteNonQuery(connection, dataverseTargetTuningTransaction, """
                CREATE TABLE DataverseTargetTuningProfiles (
                    StepId TEXT NOT NULL PRIMARY KEY,
                    BatchSize INTEGER NULL CHECK (BatchSize IS NULL OR BatchSize > 0),
                    MaxConcurrency INTEGER NULL CHECK (MaxConcurrency IS NULL OR MaxConcurrency > 0),
                    UpdatedUtc TEXT NOT NULL,
                    CHECK (BatchSize IS NOT NULL OR MaxConcurrency IS NOT NULL)
                );

                INSERT INTO SchemaMigrations (Version, Name, AppliedUtc)
                VALUES (12, '012-dataverse-target-tuning-profiles', 'initializing');
                """);
            UpdateMigrationAppliedUtc(connection, dataverseTargetTuningTransaction, 12);
            dataverseTargetTuningTransaction.Commit();
        }

        if (!appliedMigrations.ContainsKey(13))
        {
            using var pipelineTuningTransaction = connection.BeginTransaction();
            ExecuteNonQuery(connection, pipelineTuningTransaction, """
                ALTER TABLE DataverseTargetTuningProfiles
                    ADD COLUMN AlgorithmVersion INTEGER NOT NULL DEFAULT 1
                    CHECK (AlgorithmVersion > 0);

                INSERT INTO SchemaMigrations (Version, Name, AppliedUtc)
                VALUES (13, '013-pipeline-aware-tuning-profiles', 'initializing');
                """);
            UpdateMigrationAppliedUtc(connection, pipelineTuningTransaction, 13);
            pipelineTuningTransaction.Commit();
        }

        if (!appliedMigrations.ContainsKey(14))
        {
            using var connectionTuningTransaction = connection.BeginTransaction();
            ExecuteNonQuery(connection, connectionTuningTransaction, """
                ALTER TABLE DataverseTargetTuningProfiles
                    ADD COLUMN ConnectionId TEXT NULL;

                INSERT INTO SchemaMigrations (Version, Name, AppliedUtc)
                VALUES (14, '014-connection-scoped-tuning-profiles', 'initializing');
                """);
            UpdateMigrationAppliedUtc(connection, connectionTuningTransaction, 14);
            connectionTuningTransaction.Commit();
        }
    }

    private static void EnsureStepActivity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string stepId,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO StepActivity (RunId, StepId, StartedUtc, IdleSinceUtc)
            VALUES ($runId, $stepId, $now, $now)
            ON CONFLICT (RunId, StepId) DO NOTHING;
            """;
        AddParameter(command, "$runId", runId);
        AddParameter(command, "$stepId", stepId);
        AddParameter(command, "$now", FormatUtc(now));
        if (command.ExecuteNonQuery() == 1)
        {
            InsertStepActivityPeriod(connection, transaction, runId, stepId, "Waiting", now);
        }
    }

    private static void InsertStepActivityPeriod(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string stepId,
        string state,
        DateTimeOffset startedUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO StepActivityPeriods (RunId, StepId, State, StartedUtc)
            VALUES ($runId, $stepId, $state, $startedUtc);
            """;
        AddParameter(command, "$runId", runId);
        AddParameter(command, "$stepId", stepId);
        AddParameter(command, "$state", state);
        AddParameter(command, "$startedUtc", FormatUtc(startedUtc));
        command.ExecuteNonQuery();
    }

    private static void CloseStepActivityPeriod(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string stepId,
        string state,
        DateTimeOffset endedUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE StepActivityPeriods
            SET EndedUtc = CASE
                WHEN $endedUtc < StartedUtc THEN StartedUtc
                ELSE $endedUtc
            END
            WHERE RunId = $runId
              AND StepId = $stepId
              AND State = $state
              AND EndedUtc IS NULL;
            """;
        AddParameter(command, "$runId", runId);
        AddParameter(command, "$stepId", stepId);
        AddParameter(command, "$state", state);
        AddParameter(command, "$endedUtc", FormatUtc(endedUtc));
        command.ExecuteNonQuery();
    }

    private static void EnterStepActivity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string stepId,
        DateTimeOffset now)
    {
        var previousActivity = ReadStepActivity(connection, transaction, runId, stepId);
        if (previousActivity?.CompletedUtc.HasValue == true)
        {
            return;
        }

        var startsWorkingPeriod = previousActivity is null || previousActivity.ActiveCount == 0;
        if (startsWorkingPeriod)
        {
            now = NotBefore(now, previousActivity?.IdleSinceUtc);
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO StepActivity (
                RunId, StepId, StartedUtc, ActiveCount, ActiveSinceUtc)
            VALUES ($runId, $stepId, $now, 1, $now)
            ON CONFLICT (RunId, StepId) DO UPDATE
            SET IdleMilliseconds = StepActivity.IdleMilliseconds +
                    CASE WHEN StepActivity.ActiveCount = 0 AND StepActivity.IdleSinceUtc IS NOT NULL
                        THEN MAX(0, CAST(ROUND(
                            (julianday($now) - julianday(StepActivity.IdleSinceUtc)) * 86400000
                        ) AS INTEGER))
                        ELSE 0
                    END,
                ActiveCount = StepActivity.ActiveCount + 1,
                ActiveSinceUtc = CASE
                    WHEN StepActivity.ActiveCount = 0 THEN $now
                    ELSE StepActivity.ActiveSinceUtc
                END,
                IdleSinceUtc = CASE
                    WHEN StepActivity.ActiveCount = 0 THEN NULL
                    ELSE StepActivity.IdleSinceUtc
                END
            WHERE StepActivity.CompletedUtc IS NULL;
            """;
        AddParameter(command, "$now", FormatUtc(now));
        AddParameter(command, "$runId", runId);
        AddParameter(command, "$stepId", stepId);
        if (command.ExecuteNonQuery() == 1 && startsWorkingPeriod)
        {
            if (previousActivity?.IdleSinceUtc.HasValue == true)
            {
                CloseStepActivityPeriod(connection, transaction, runId, stepId, "Waiting", now);
            }
            InsertStepActivityPeriod(connection, transaction, runId, stepId, "Working", now);
        }
    }

    private static void ExitStepActivity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string stepId,
        DateTimeOffset now)
    {
        var previousActivity = ReadStepActivity(connection, transaction, runId, stepId);
        if (previousActivity?.ActiveCount == 1)
        {
            now = NotBefore(now, previousActivity.ActiveSinceUtc);
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE StepActivity
            SET ActiveMilliseconds = ActiveMilliseconds +
                    CASE WHEN ActiveCount = 1 AND ActiveSinceUtc IS NOT NULL
                        THEN MAX(0, CAST(ROUND(
                            (julianday($now) - julianday(ActiveSinceUtc)) * 86400000
                        ) AS INTEGER))
                        ELSE 0
                    END,
                ActiveCount = ActiveCount - 1,
                ActiveSinceUtc = CASE WHEN ActiveCount = 1 THEN NULL ELSE ActiveSinceUtc END,
                IdleSinceUtc = CASE WHEN ActiveCount = 1 THEN $now ELSE IdleSinceUtc END
            WHERE RunId = $runId AND StepId = $stepId AND ActiveCount > 0 AND CompletedUtc IS NULL;
            """;
        AddParameter(command, "$now", FormatUtc(now));
        AddParameter(command, "$runId", runId);
        AddParameter(command, "$stepId", stepId);
        var updated = command.ExecuteNonQuery();
        if (updated == 1 && previousActivity?.ActiveCount == 1)
        {
            CloseStepActivityPeriod(connection, transaction, runId, stepId, "Working", now);
            InsertStepActivityPeriod(connection, transaction, runId, stepId, "Waiting", now);
        }
        if (updated == 0)
        {
            var activity = ReadStepActivity(connection, transaction, runId, stepId);
            if (activity is null || !activity.CompletedUtc.HasValue)
            {
                throw new InvalidOperationException($"Runtime activity for step '{runId}/{stepId}' is not active.");
            }
        }
    }

    private static void CompleteStepActivity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string stepId,
        DateTimeOffset now)
    {
        var activity = ReadStepActivity(connection, transaction, runId, stepId);
        if (activity is null || activity.CompletedUtc.HasValue)
        {
            return;
        }

        var activeMilliseconds = activity.ActiveMilliseconds;
        var idleMilliseconds = activity.IdleMilliseconds;
        if (activity.ActiveCount > 0 && activity.ActiveSinceUtc.HasValue)
        {
            now = NotBefore(now, activity.ActiveSinceUtc);
            activeMilliseconds += ElapsedMilliseconds(activity.ActiveSinceUtc.Value, now);
            CloseStepActivityPeriod(connection, transaction, runId, stepId, "Working", now);
        }
        else if (activity.IdleSinceUtc.HasValue)
        {
            now = NotBefore(now, activity.IdleSinceUtc);
            idleMilliseconds += ElapsedMilliseconds(activity.IdleSinceUtc.Value, now);
            CloseStepActivityPeriod(connection, transaction, runId, stepId, "Waiting", now);
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE StepActivity
            SET ActiveMilliseconds = $activeMilliseconds,
                IdleMilliseconds = $idleMilliseconds,
                ActiveCount = 0,
                ActiveSinceUtc = NULL,
                IdleSinceUtc = NULL,
                CompletedUtc = $now
            WHERE RunId = $runId AND StepId = $stepId AND CompletedUtc IS NULL;
            """;
        AddParameter(command, "$activeMilliseconds", activeMilliseconds);
        AddParameter(command, "$idleMilliseconds", idleMilliseconds);
        AddParameter(command, "$now", FormatUtc(now));
        AddParameter(command, "$runId", runId);
        AddParameter(command, "$stepId", stepId);
        command.ExecuteNonQuery();
    }

    private static void RecoverStepActivities(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        DateTimeOffset now)
    {
        foreach (var (stepId, activity) in ReadStepActivities(connection, transaction, runId))
        {
            if (activity.CompletedUtc.HasValue)
            {
                continue;
            }

            var activeMilliseconds = activity.ActiveMilliseconds;
            var idleMilliseconds = activity.IdleMilliseconds;
            var transitionUtc = activity.ActiveCount > 0
                ? NotBefore(now, activity.ActiveSinceUtc)
                : NotBefore(now, activity.IdleSinceUtc);
            if (activity.ActiveCount > 0 && activity.ActiveSinceUtc.HasValue)
            {
                activeMilliseconds += ElapsedMilliseconds(activity.ActiveSinceUtc.Value, transitionUtc);
                CloseStepActivityPeriod(connection, transaction, runId, stepId, "Working", transitionUtc);
            }
            else if (activity.IdleSinceUtc.HasValue)
            {
                idleMilliseconds += ElapsedMilliseconds(activity.IdleSinceUtc.Value, transitionUtc);
                CloseStepActivityPeriod(connection, transaction, runId, stepId, "Waiting", transitionUtc);
            }
            InsertStepActivityPeriod(connection, transaction, runId, stepId, "Waiting", transitionUtc);

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE StepActivity
                SET ActiveMilliseconds = $activeMilliseconds,
                    IdleMilliseconds = $idleMilliseconds,
                    ActiveCount = 0,
                    ActiveSinceUtc = NULL,
                    IdleSinceUtc = $now
                WHERE RunId = $runId AND StepId = $stepId AND CompletedUtc IS NULL;
                """;
            AddParameter(command, "$activeMilliseconds", activeMilliseconds);
            AddParameter(command, "$idleMilliseconds", idleMilliseconds);
            AddParameter(command, "$now", FormatUtc(transitionUtc));
            AddParameter(command, "$runId", runId);
            AddParameter(command, "$stepId", stepId);
            command.ExecuteNonQuery();
        }
    }

    private static void FinalizeStepActivities(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        DateTimeOffset now)
    {
        foreach (var (stepId, activity) in ReadStepActivities(connection, transaction, runId))
        {
            var transitionUtc = activity.ActiveCount > 0
                ? NotBefore(now, activity.ActiveSinceUtc)
                : NotBefore(now, activity.IdleSinceUtc);
            var activeMilliseconds = activity.ActiveMilliseconds;
            var idleMilliseconds = activity.IdleMilliseconds;
            if (activity.ActiveCount > 0 && activity.ActiveSinceUtc.HasValue)
            {
                activeMilliseconds += ElapsedMilliseconds(activity.ActiveSinceUtc.Value, transitionUtc);
                CloseStepActivityPeriod(connection, transaction, runId, stepId, "Working", transitionUtc);
            }
            else if (activity.IdleSinceUtc.HasValue)
            {
                idleMilliseconds += ElapsedMilliseconds(activity.IdleSinceUtc.Value, transitionUtc);
                CloseStepActivityPeriod(connection, transaction, runId, stepId, "Waiting", transitionUtc);
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE StepActivity
                SET ActiveMilliseconds = $activeMilliseconds,
                    IdleMilliseconds = $idleMilliseconds,
                    ActiveCount = 0,
                    ActiveSinceUtc = NULL,
                    IdleSinceUtc = NULL
                WHERE RunId = $runId AND StepId = $stepId;
                """;
            AddParameter(command, "$activeMilliseconds", activeMilliseconds);
            AddParameter(command, "$idleMilliseconds", idleMilliseconds);
            AddParameter(command, "$runId", runId);
            AddParameter(command, "$stepId", stepId);
            command.ExecuteNonQuery();
        }
    }

    private static IReadOnlyList<(string StepId, StepActivityState Activity)> ReadStepActivities(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT StepId, ActiveMilliseconds, IdleMilliseconds, ActiveCount,
                   ActiveSinceUtc, IdleSinceUtc, CompletedUtc
            FROM StepActivity
            WHERE RunId = $runId;
            """;
        AddParameter(command, "$runId", runId);
        using var reader = command.ExecuteReader();
        var activities = new List<(string StepId, StepActivityState Activity)>();
        while (reader.Read())
        {
            activities.Add((
                reader.GetString(0),
                new StepActivityState(
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : ParseUtc(reader.GetString(4)),
                    reader.IsDBNull(5) ? null : ParseUtc(reader.GetString(5)),
                    reader.IsDBNull(6) ? null : ParseUtc(reader.GetString(6)))));
        }

        return activities;
    }

    private static StepActivityState? ReadStepActivity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string stepId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ActiveMilliseconds, IdleMilliseconds, ActiveCount,
                   ActiveSinceUtc, IdleSinceUtc, CompletedUtc
            FROM StepActivity
            WHERE RunId = $runId AND StepId = $stepId;
            """;
        AddParameter(command, "$runId", runId);
        AddParameter(command, "$stepId", stepId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new StepActivityState(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : ParseUtc(reader.GetString(3)),
                reader.IsDBNull(4) ? null : ParseUtc(reader.GetString(4)),
                reader.IsDBNull(5) ? null : ParseUtc(reader.GetString(5)))
            : null;
    }

    private static long ElapsedMilliseconds(DateTimeOffset start, DateTimeOffset end) =>
        Math.Max(0, (long)(end - start).TotalMilliseconds);

    private static DateTimeOffset NotBefore(DateTimeOffset timestamp, DateTimeOffset? lowerBound) =>
        lowerBound.HasValue && timestamp < lowerBound.Value ? lowerBound.Value : timestamp;

    private sealed record StepActivityState(
        long ActiveMilliseconds,
        long IdleMilliseconds,
        int ActiveCount,
        DateTimeOffset? ActiveSinceUtc,
        DateTimeOffset? IdleSinceUtc,
        DateTimeOffset? CompletedUtc);

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM sqlite_master
                WHERE type = 'table' AND name = $tableName
            );
            """;
        AddParameter(command, "$tableName", tableName);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static bool HasApplicationTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM sqlite_master
                WHERE type = 'table'
                  AND name NOT LIKE 'sqlite_%'
                  AND name <> 'SchemaMigrations'
            );
            """;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static Dictionary<int, string> ReadAppliedMigrations(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Version, Name FROM SchemaMigrations ORDER BY Version;";
        using var reader = command.ExecuteReader();
        var migrations = new Dictionary<int, string>();
        while (reader.Read())
        {
            if (!migrations.TryAdd(reader.GetInt32(0), reader.GetString(1)))
            {
                throw new InvalidOperationException("The schema migration history contains duplicate versions.");
            }
        }

        return migrations;
    }

    private static void UpdateMigrationAppliedUtc(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE SchemaMigrations
            SET AppliedUtc = $appliedUtc
            WHERE Version = $version;
            """;
        AddParameter(command, "$appliedUtc", FormatUtc(DateTimeOffset.UtcNow));
        AddParameter(command, "$version", version);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException($"Schema migration {version} was not recorded.");
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        var configured = false;
        try
        {
            connection.Open();
            ConfigureConnection(connection);
            configured = true;
            return connection;
        }
        finally
        {
            if (!configured)
            {
                connection.Dispose();
            }
        }
    }

    private static void ConfigureConnection(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, null, "PRAGMA foreign_keys = ON;");
        ExecuteNonQuery(connection, null, "PRAGMA synchronous = FULL;");
        ExecuteNonQuery(connection, null, "PRAGMA busy_timeout = 30000;");
    }

    private static StoredBatch? FindBatchById(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string batchId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT BatchId, RunId, StepId, Sequence, CursorAfterJson, PayloadJson, State,
                   Attempts, LeaseOwner, LeaseExpiresUtc, OutcomesJson, LastError, CreatedUtc, UpdatedUtc
            FROM Batches
            WHERE BatchId = $batchId;
            """;
        AddParameter(command, "$batchId", batchId);
        using var reader = command.ExecuteReader();
        var batch = reader.Read() ? ReadBatch(reader) : null;
        reader.Close();
        return batch is null ? null : AttachDependencyParents(connection, transaction, batch);
    }

    private static StoredBatch AttachDependencyParents(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        StoredBatch batch)
    {
        var parentBatchIds = new HashSet<string>(StringComparer.Ordinal);
        using (var references = connection.CreateCommand())
        {
            references.Transaction = transaction;
            references.CommandText = """
                SELECT ParentBatchId
                FROM BatchParentReferences
                WHERE RunId = $runId AND BatchId = $batchId;
                """;
            AddParameter(references, "$runId", batch.RunId);
            AddParameter(references, "$batchId", batch.BatchId);
            using var referenceReader = references.ExecuteReader();
            while (referenceReader.Read())
            {
                parentBatchIds.Add(referenceReader.GetString(0));
            }
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ParentBatchId, PrerequisiteBatchIdsJson
            FROM DependencyEvents
            WHERE RunId = $runId AND EventId = $batchId;
            """;
        AddParameter(command, "$runId", batch.RunId);
        AddParameter(command, "$batchId", batch.BatchId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return batch with { ParentBatchIds = parentBatchIds.ToArray() };
        }

        parentBatchIds.Add(reader.GetString(0));
        foreach (var requiredBatchId in DeserializeBatchIds(reader.GetString(1)))
        {
            parentBatchIds.Add(requiredBatchId);
        }
        return batch with
        {
            ParentBatchIds = parentBatchIds.ToArray()
        };
    }

    private static ExistingBatch? FindBatchByIdentity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string tableName,
        long sequence,
        string batchId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT BatchId, RunId, StepId, Sequence, PayloadHash, CursorAfterJson
            FROM Batches
            WHERE BatchId = $batchId
               OR (RunId = $runId AND StepId = $stepId AND Sequence = $sequence)
            LIMIT 1;
            """;
        AddParameter(command, "$batchId", batchId);
        AddParameter(command, "$runId", runId);
        AddParameter(command, "$stepId", tableName);
        AddParameter(command, "$sequence", sequence);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new ExistingBatch(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4),
                ReadNullableString(reader, 5))
            : null;
    }

    private static long GetNextSequence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string tableName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(MAX(Sequence) + 1, 0)
            FROM Batches
            WHERE RunId = $runId AND StepId = $stepId;
            """;
        AddParameter(command, "$runId", runId);
        AddParameter(command, "$stepId", tableName);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static MigrationStepCheckpoint AdvanceCheckpoint(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string tableName,
        string now)
    {
        var checkpoint = ReadCheckpoint(connection, transaction, runId, tableName)
            ?? throw new InvalidOperationException($"Checkpoint '{runId}/{tableName}' was not initialized.");
        var lastCompletedSequence = checkpoint.LastCompletedSequence;
        var cursorJson = checkpoint.CursorJson;

        while (true)
        {
            using var batch = connection.CreateCommand();
            batch.Transaction = transaction;
            batch.CommandText = """
                SELECT State, CursorAfterJson
                FROM Batches
                WHERE RunId = $runId AND StepId = $stepId AND Sequence = $sequence;
                """;
            AddParameter(batch, "$runId", runId);
            AddParameter(batch, "$stepId", tableName);
            AddParameter(batch, "$sequence", lastCompletedSequence + 1);

            using var reader = batch.ExecuteReader();
            if (!reader.Read())
            {
                break;
            }

            var state = reader.GetString(0);
            if (state is not ("Succeeded" or "CompletedWithErrors"))
            {
                break;
            }

            cursorJson = ReadNullableString(reader, 1);
            lastCompletedSequence++;
        }

        if (lastCompletedSequence == checkpoint.LastCompletedSequence)
        {
            return checkpoint;
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE StepCheckpoints
                SET LastCompletedSequence = $sequence,
                    CursorJson = $cursorJson,
                    UpdatedUtc = $now
                WHERE RunId = $runId AND StepId = $stepId;
                """;
            AddParameter(update, "$sequence", lastCompletedSequence);
            AddParameter(update, "$cursorJson", cursorJson);
            AddParameter(update, "$now", now);
            AddParameter(update, "$runId", runId);
            AddParameter(update, "$stepId", tableName);
            update.ExecuteNonQuery();
        }

        return new MigrationStepCheckpoint(runId, tableName, lastCompletedSequence, cursorJson, ParseUtc(now));
    }

    private static MigrationStepCheckpoint? ReadCheckpoint(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string runId,
        string tableName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT RunId, StepId, LastCompletedSequence, CursorJson, UpdatedUtc
            FROM StepCheckpoints
            WHERE RunId = $runId AND StepId = $stepId;
            """;
        AddParameter(command, "$runId", runId);
        AddParameter(command, "$stepId", tableName);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new MigrationStepCheckpoint(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                ReadNullableString(reader, 3),
                ParseUtc(reader.GetString(4)))
            : null;
    }

    private static StoredBatch ReadBatch(SqliteDataReader reader)
    {
        if (!Enum.TryParse<MigrationBatchState>(reader.GetString(6), out var state))
        {
            throw new InvalidOperationException($"Unknown batch state '{reader.GetString(6)}'.");
        }

        return new StoredBatch(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            ReadNullableString(reader, 4),
            ReadNullableString(reader, 5),
            state,
            reader.GetInt32(7),
            ReadNullableString(reader, 8),
            reader.IsDBNull(9) ? null : ParseUtc(reader.GetString(9)),
            ReadNullableString(reader, 10),
            ReadNullableString(reader, 11),
            ParseUtc(reader.GetString(12)),
            ParseUtc(reader.GetString(13)));
    }

    private static void InsertDependencyEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredBatch parentBatch,
        DependencyEvent dependencyEvent,
        string now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dependencyEvent.EventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(dependencyEvent.ChildStepId);
        ValidateJson(dependencyEvent.PayloadJson, nameof(dependencyEvent.PayloadJson));
        var prerequisiteBatchIds = dependencyEvent.RequiredBatchIds
            .Distinct(StringComparer.Ordinal)
            .OrderBy(batchId => batchId, StringComparer.Ordinal)
            .ToArray();
        if (prerequisiteBatchIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Prerequisite batch IDs must not be empty.",
                nameof(dependencyEvent));
        }
        var prerequisiteBatchIdsJson = JsonSerializer.Serialize(prerequisiteBatchIds);

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO DependencyEvents (
                    EventId, RunId, ParentBatchId, ChildStepId, PayloadJson, PrerequisiteBatchIdsJson,
                    State, CreatedUtc, UpdatedUtc)
                VALUES (
                    $eventId, $runId, $parentBatchId, $childStepId, $payloadJson, $prerequisiteBatchIdsJson,
                    'Pending', $now, $now)
                ON CONFLICT (EventId) DO NOTHING;
                """;
            AddParameter(insert, "$eventId", dependencyEvent.EventId);
            AddParameter(insert, "$runId", parentBatch.RunId);
            AddParameter(insert, "$parentBatchId", parentBatch.BatchId);
            AddParameter(insert, "$childStepId", dependencyEvent.ChildStepId);
            AddParameter(insert, "$payloadJson", dependencyEvent.PayloadJson);
            AddParameter(insert, "$prerequisiteBatchIdsJson", prerequisiteBatchIdsJson);
            AddParameter(insert, "$now", now);
            if (insert.ExecuteNonQuery() == 1)
            {
                EnsureStepActivity(
                    connection,
                    transaction,
                    parentBatch.RunId,
                    dependencyEvent.ChildStepId,
                    ParseUtc(now));
                return;
            }
        }

        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = """
            SELECT RunId, ParentBatchId, ChildStepId, PayloadJson, PrerequisiteBatchIdsJson
            FROM DependencyEvents
            WHERE EventId = $eventId;
            """;
        AddParameter(read, "$eventId", dependencyEvent.EventId);
        using var reader = read.ExecuteReader();
        if (!reader.Read() ||
            !string.Equals(reader.GetString(0), parentBatch.RunId, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(1), parentBatch.BatchId, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(2), dependencyEvent.ChildStepId, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(3), dependencyEvent.PayloadJson, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(4), prerequisiteBatchIdsJson, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Dependency event ID '{dependencyEvent.EventId}' was reused with different content.");
        }
    }

    private static IReadOnlyList<string> DeserializeBatchIds(string json)
    {
        return JsonSerializer.Deserialize<string[]>(json)
            ?? throw new InvalidOperationException("A dependency event contains invalid prerequisite batch IDs.");
    }

    private static void ValidateJson(string json, string parameterName)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("A valid JSON value is required.", parameterName, exception);
        }
    }

    private static long? GetInputRowCount(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, "Records", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.Array)
            {
                return property.Value.GetArrayLength();
            }
        }

        return null;
    }

    private static string Hash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static void TouchRun(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string runId,
        string now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE Runs SET UpdatedUtc = $now WHERE RunId = $runId;";
        AddParameter(command, "$now", now);
        AddParameter(command, "$runId", runId);
        command.ExecuteNonQuery();
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseUtc(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static object ToDatabaseValue(object? value)
    {
        return value ?? DBNull.Value;
    }

    private static void AddParameter(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, ToDatabaseValue(value));
    }

    private static void InsertSourceKeyIndex(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string stepId,
        string batchId,
        IReadOnlyList<SourceKeyEntry> sourceKeys)
    {
        foreach (var sourceKey in sourceKeys)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO SourceKeyIndex (RunId, StepId, SourceKey, BatchId, RecordIndex)
                VALUES ($runId, $stepId, $sourceKey, $batchId, $recordIndex)
                ON CONFLICT (RunId, StepId, SourceKey) DO NOTHING;
                """;
            AddParameter(insert, "$runId", runId);
            AddParameter(insert, "$stepId", stepId);
            AddParameter(insert, "$sourceKey", sourceKey.SourceKey);
            AddParameter(insert, "$batchId", batchId);
            AddParameter(insert, "$recordIndex", sourceKey.RecordIndex);
            insert.ExecuteNonQuery();

            using var verify = connection.CreateCommand();
            verify.Transaction = transaction;
            verify.CommandText = """
                SELECT BatchId, RecordIndex
                FROM SourceKeyIndex
                WHERE RunId = $runId AND StepId = $stepId AND SourceKey = $sourceKey;
                """;
            AddParameter(verify, "$runId", runId);
            AddParameter(verify, "$stepId", stepId);
            AddParameter(verify, "$sourceKey", sourceKey.SourceKey);
            using var reader = verify.ExecuteReader();
            if (!reader.Read() ||
                !string.Equals(reader.GetString(0), batchId, StringComparison.Ordinal) ||
                reader.GetInt32(1) != sourceKey.RecordIndex)
            {
                throw new InvalidDataException(
                    $"Source key '{sourceKey.SourceKey}' is duplicated or changed in source step '{stepId}'.");
            }
        }
    }

    private static object? ExecuteScalar(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static int ExecuteNonQuery(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteNonQuery();
    }

    private sealed record ExistingBatch(
        string BatchId,
        string RunId,
        string StepId,
        long Sequence,
        string PayloadHash,
        string? CursorAfterJson);
}
