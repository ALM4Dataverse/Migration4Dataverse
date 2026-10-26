using Microsoft.Data.Sqlite;

namespace Migration4Dataverse.Engine.Storage;

internal sealed partial class SqliteMigrationStore
{
    public DataverseTargetTuningProfile? GetDataverseTargetTuningProfile(string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT StepId, BatchSize, MaxConcurrency, UpdatedUtc, AlgorithmVersion, ConnectionId
            FROM DataverseTargetTuningProfiles
            WHERE StepId = $stepId;
            """;
        command.Parameters.AddWithValue("$stepId", stepId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new DataverseTargetTuningProfile(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            ParseUtc(reader.GetString(3)),
            reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    public void SaveDataverseTargetTuningProfile(
        string stepId,
        int? batchSize,
        int? maxConcurrency,
        int algorithmVersion = 1,
        string? connectionId = null)
    {
        using var writeLock = SqliteDatabaseWriteGate.Enter(DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        if (batchSize is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }
        if (maxConcurrency is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(algorithmVersion);
        if (connectionId is not null && string.IsNullOrWhiteSpace(connectionId))
        {
            throw new ArgumentException("Connection ID must not be empty when provided.", nameof(connectionId));
        }
        if (batchSize is null && maxConcurrency is null)
        {
            throw new ArgumentException("At least one tuned value must be provided.");
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO DataverseTargetTuningProfiles (
                StepId, BatchSize, MaxConcurrency, UpdatedUtc, AlgorithmVersion, ConnectionId)
            VALUES ($stepId, $batchSize, $maxConcurrency, $updatedUtc, $algorithmVersion, $connectionId)
            ON CONFLICT (StepId) DO UPDATE SET
                BatchSize = COALESCE(excluded.BatchSize, DataverseTargetTuningProfiles.BatchSize),
                MaxConcurrency = COALESCE(excluded.MaxConcurrency, DataverseTargetTuningProfiles.MaxConcurrency),
                AlgorithmVersion = excluded.AlgorithmVersion,
                ConnectionId = excluded.ConnectionId,
                UpdatedUtc = excluded.UpdatedUtc;
            """;
        command.Parameters.AddWithValue("$stepId", stepId);
        command.Parameters.AddWithValue("$batchSize", (object?)batchSize ?? DBNull.Value);
        command.Parameters.AddWithValue("$maxConcurrency", (object?)maxConcurrency ?? DBNull.Value);
        command.Parameters.AddWithValue("$algorithmVersion", algorithmVersion);
        command.Parameters.AddWithValue("$connectionId", (object?)connectionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedUtc", FormatUtc(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    public long ClearDataverseTargetTuningProfile(string stepId)
    {
        using var writeLock = SqliteDatabaseWriteGate.Enter(DatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM DataverseTargetTuningProfiles
            WHERE StepId = $stepId;
            """;
        command.Parameters.AddWithValue("$stepId", stepId);
        return command.ExecuteNonQuery();
    }

    public long ClearDataverseTargetTuningProfiles()
    {
        using var writeLock = SqliteDatabaseWriteGate.Enter(DatabasePath);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM DataverseTargetTuningProfiles;";
        return command.ExecuteNonQuery();
    }
}
