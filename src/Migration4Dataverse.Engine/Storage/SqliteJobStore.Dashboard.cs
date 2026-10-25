using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Migration4Dataverse.Engine.Storage;

public sealed partial class SqliteMigrationStore
{
    public long PurgeExpiredDebugSnapshots()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM DebugSnapshots
            WHERE EXISTS (
                SELECT 1
                FROM Runs
                WHERE Runs.RunId = DebugSnapshots.RunId
                  AND DebugSnapshots.CapturedUtc < strftime(
                    '%Y-%m-%dT%H:%M:%f+00:00',
                    'now',
                    '-' || Runs.DebugCaptureRetentionDays || ' days')
            );
            """;
        var deletedSnapshots = command.ExecuteNonQuery();
        using var lineageCleanup = connection.CreateCommand();
        lineageCleanup.CommandText = """
            DELETE FROM DebugBatchInputs
            WHERE EXISTS (
                SELECT 1
                FROM Runs
                WHERE Runs.RunId = DebugBatchInputs.RunId
                  AND Runs.Status <> 'Running'
                  AND Runs.FinishedUtc IS NOT NULL
                  AND Runs.FinishedUtc < strftime(
                    '%Y-%m-%dT%H:%M:%f+00:00',
                    'now',
                    '-' || Runs.DebugCaptureRetentionDays || ' days')
            );
            """;
        lineageCleanup.ExecuteNonQuery();
        return deletedSnapshots;
    }

    public IReadOnlyList<MigrationRunSummary> GetDashboardRuns(int limit = 50)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT RunId
            FROM Runs
            ORDER BY CreatedUtc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var runIds = new List<string>();
        while (reader.Read())
        {
            runIds.Add(reader.GetString(0));
        }

        return runIds.Select(GetDashboardRun).ToArray();
    }

    public MigrationRunSummary GetDashboardRun(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        using var connection = OpenConnection();
        string runName;
        string status;
        DateTimeOffset createdUtc;
        DateTimeOffset updatedUtc;
        bool debugCaptureEnabled;
        int debugCaptureRetentionDays;
        string pipelineJson;
        string configurationJson;
        string connectionInstancesJson;
        DateTimeOffset? schedulerLeaseExpiresUtc;
        using (var run = connection.CreateCommand())
        {
            run.CommandText = """
                SELECT RunName, Status, CreatedUtc, UpdatedUtc, DebugCaptureEnabled,
                       DebugCaptureRetentionDays, PipelineJson, ConfigurationJson,
                       ConnectionInstancesJson, SchedulerLeaseExpiresUtc
                FROM Runs
                WHERE RunId = $runId;
                """;
            run.Parameters.AddWithValue("$runId", runId);
            using var reader = run.ExecuteReader();
            if (!reader.Read())
            {
                throw new KeyNotFoundException($"Run '{runId}' was not found.");
            }

            runName = reader.GetString(0);
            status = reader.GetString(1);
            createdUtc = ParseUtc(reader.GetString(2));
            updatedUtc = ParseUtc(reader.GetString(3));
            debugCaptureEnabled = reader.GetInt32(4) != 0;
            debugCaptureRetentionDays = reader.GetInt32(5);
            pipelineJson = reader.GetString(6);
            configurationJson = reader.GetString(7);
            connectionInstancesJson = reader.GetString(8);
            schedulerLeaseExpiresUtc = reader.IsDBNull(9) ? null : ParseUtc(reader.GetString(9));
        }

        var steps = ReadPipeline(pipelineJson);
        var stepIndex = steps.ToDictionary(step => step.Id, StringComparer.Ordinal);
        using (var batches = connection.CreateCommand())
        {
            batches.CommandText = """
                SELECT StepId,
                       SUM(CASE WHEN State = 'Pending' THEN 1 ELSE 0 END),
                       SUM(CASE WHEN State = 'InFlight' THEN 1 ELSE 0 END),
                       SUM(CASE WHEN State = 'Succeeded' THEN 1 ELSE 0 END),
                       SUM(CASE WHEN State = 'CompletedWithErrors' THEN 1 ELSE 0 END),
                       MIN(CreatedUtc),
                       MAX(UpdatedUtc),
                       SUM(SucceededRows),
                       SUM(FailedRows),
                       SUM(CASE WHEN State IN ('Succeeded', 'CompletedWithErrors')
                                THEN COALESCE(InputRows, 0) ELSE 0 END),
                       SUM(CASE WHEN State = 'Pending' THEN COALESCE(InputRows, 0) ELSE 0 END),
                       SUM(CASE WHEN State = 'InFlight' THEN COALESCE(InputRows, 0) ELSE 0 END),
                       SUM(FilteredRows),
                       SUM(CASE WHEN InputRows IS NULL THEN 1 ELSE 0 END)
                FROM Batches
                WHERE RunId = $runId
                GROUP BY StepId;
                """;
            batches.Parameters.AddWithValue("$runId", runId);
            using var reader = batches.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                if (!stepIndex.TryGetValue(name, out var step))
                {
                    step = new MutableDashboardStep(name, [], hasSource: false, type: "unknown");
                    stepIndex.Add(name, step);
                    steps.Add(step);
                }

                step.PendingBatches = reader.GetInt64(1);
                step.InFlightBatches = reader.GetInt64(2);
                step.SucceededBatches = reader.GetInt64(3);
                step.BatchesWithErrors = reader.GetInt64(4);
                step.FirstBatchUtc = reader.IsDBNull(5) ? null : ParseUtc(reader.GetString(5));
                step.LastBatchUtc = reader.IsDBNull(6) ? null : ParseUtc(reader.GetString(6));
                step.SucceededRows = reader.GetInt64(7);
                step.FailedRows = reader.GetInt64(8);
                step.InputRowsProcessed = reader.GetInt64(9);
                step.InputRowsPending = reader.GetInt64(10);
                step.InputRowsInFlight = reader.GetInt64(11);
                step.FilteredRows = reader.GetInt64(12);
                step.BatchesWithUnknownInputRows = reader.GetInt64(13);
            }
        }

        using (var activity = connection.CreateCommand())
        {
            activity.CommandText = """
                SELECT batch.StepId, batch.OutcomesJson
                FROM Batches AS batch
                WHERE batch.RunId = $runId
                  AND batch.OutcomesJson IS NOT NULL
                  AND batch.Sequence = (
                      SELECT MAX(latest.Sequence)
                      FROM Batches AS latest
                      WHERE latest.RunId = batch.RunId
                        AND latest.StepId = batch.StepId
                        AND latest.OutcomesJson IS NOT NULL
                  );
                """;
            activity.Parameters.AddWithValue("$runId", runId);
            using var reader = activity.ExecuteReader();
            while (reader.Read())
            {
                if (stepIndex.TryGetValue(reader.GetString(0), out var step))
                {
                    step.SpecialistInfo = ReadSpecialistInfo(reader.GetString(1));
                }
            }
        }

        using (var throughput = connection.CreateCommand())
        {
            throughput.CommandText = """
                SELECT StepId, UpdatedUtc, SucceededRows, FailedRows
                FROM Batches
                WHERE RunId = $runId
                  AND State IN ('Succeeded', 'CompletedWithErrors')
                ORDER BY UpdatedUtc;
                """;
            throughput.Parameters.AddWithValue("$runId", runId);
            using var reader = throughput.ExecuteReader();
            while (reader.Read())
            {
                if (stepIndex.TryGetValue(reader.GetString(0), out var step))
                {
                    step.CompletedBatchRows.Add(new CompletedBatchRows(
                        ParseUtc(reader.GetString(1)),
                        checked(reader.GetInt64(2) + reader.GetInt64(3))));
                }
            }
        }

        using (var activity = connection.CreateCommand())
        {
            activity.CommandText = """
                SELECT StepId, ActiveMilliseconds, IdleMilliseconds,
                       ActiveSinceUtc, IdleSinceUtc
                FROM StepActivity
                WHERE RunId = $runId;
                """;
            activity.Parameters.AddWithValue("$runId", runId);
            using var reader = activity.ExecuteReader();
            while (reader.Read())
            {
                if (!stepIndex.TryGetValue(reader.GetString(0), out var step))
                {
                    continue;
                }

                step.ActiveMilliseconds = reader.GetInt64(1);
                step.IdleMilliseconds = reader.GetInt64(2);
                step.ActiveSinceUtc = reader.IsDBNull(3) ? null : ParseUtc(reader.GetString(3));
                step.IdleSinceUtc = reader.IsDBNull(4) ? null : ParseUtc(reader.GetString(4));
            }
        }

        using (var activityPeriods = connection.CreateCommand())
        {
            activityPeriods.CommandText = """
                SELECT StepId, State, StartedUtc, EndedUtc
                FROM StepActivityPeriods
                WHERE RunId = $runId
                ORDER BY StartedUtc, PeriodId;
                """;
            activityPeriods.Parameters.AddWithValue("$runId", runId);
            using var reader = activityPeriods.ExecuteReader();
            while (reader.Read())
            {
                if (stepIndex.TryGetValue(reader.GetString(0), out var step))
                {
                    step.ActivityPeriods.Add(new MigrationDashboardActivityPeriod(
                        reader.GetString(1),
                        ParseUtc(reader.GetString(2)),
                        reader.IsDBNull(3) ? null : ParseUtc(reader.GetString(3))));
                }
            }
        }

        using (var feeders = connection.CreateCommand())
        {
            feeders.CommandText = """
                SELECT StepId, Completed
                FROM SourceFeeders
                WHERE RunId = $runId;
                """;
            feeders.Parameters.AddWithValue("$runId", runId);
            using var reader = feeders.ExecuteReader();
            while (reader.Read())
            {
                if (stepIndex.TryGetValue(reader.GetString(0), out var step))
                {
                    step.SourceFeederCompleted = reader.GetInt32(1) != 0;
                }
            }
        }

        using (var dependencyEvents = connection.CreateCommand())
        {
            dependencyEvents.CommandText = """
                SELECT ChildStepId, COUNT(*)
                FROM DependencyEvents
                WHERE RunId = $runId AND State = 'Pending'
                GROUP BY ChildStepId;
                """;
            dependencyEvents.Parameters.AddWithValue("$runId", runId);
            using var reader = dependencyEvents.ExecuteReader();
            while (reader.Read())
            {
                if (stepIndex.TryGetValue(reader.GetString(0), out var step))
                {
                    step.PendingDependencyEvents = reader.GetInt64(1);
                }
            }
        }

        var allComplete = string.Equals(status, MigrationRunStatus.Completed.ToString(), StringComparison.Ordinal);
        var metricTime = DateTimeOffset.UtcNow;
        var runIsActive = status == MigrationRunStatus.Running.ToString() &&
                          (schedulerLeaseExpiresUtc is null || schedulerLeaseExpiresUtc > metricTime);
        var pendingBatches = steps.Sum(step => step.PendingBatches);
        var inFlightBatches = steps.Sum(step => step.InFlightBatches);
        var succeededBatches = steps.Sum(step => step.SucceededBatches);
        var batchesWithErrors = steps.Sum(step => step.BatchesWithErrors);
        var completionCache = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool IsComplete(MutableDashboardStep step, HashSet<string>? visiting = null)
        {
            if (allComplete)
            {
                return true;
            }

            if (completionCache.TryGetValue(step.Id, out var cached))
            {
                return cached;
            }

            visiting ??= new HashSet<string>(StringComparer.Ordinal);
            if (!visiting.Add(step.Id))
            {
                return false;
            }

            if (step.BatchesWithErrors > 0 ||
                step.PendingBatches > 0 ||
                step.InFlightBatches > 0 ||
                step.PendingDependencyEvents > 0)
            {
                visiting.Remove(step.Id);
                return false;
            }

            var complete = step.HasSource
                ? step.SourceFeederCompleted
                : step.Inputs.Count > 0 &&
                  step.Inputs.All(input => stepIndex.TryGetValue(input, out var inputStep) &&
                                           IsComplete(inputStep, visiting));
            visiting.Remove(step.Id);
            completionCache[step.Id] = complete;
            return complete;
        }

        foreach (var step in steps)
        {
            step.RowsPerSecondSamples = BuildRowsPerSecondSamples(
                step.ActivityPeriods,
                step.CompletedBatchRows,
                IsComplete(step) || !runIsActive,
                metricTime);
        }

        return new MigrationRunSummary(
            runId,
            runName,
            status,
            createdUtc,
            updatedUtc,
            debugCaptureEnabled,
            debugCaptureRetentionDays,
            pendingBatches,
            inFlightBatches,
            succeededBatches,
            batchesWithErrors,
            steps.Sum(step => step.SucceededRows),
            steps.Sum(step => step.FailedRows),
            steps.Select(step => step.ToRecord(
                IsComplete(step),
                step.HasSource
                    ? step.SourceFeederCompleted
                    : step.Inputs.Count > 0 &&
                      step.PendingDependencyEvents == 0 &&
                      step.Inputs.All(input =>
                          stepIndex.TryGetValue(input, out var inputStep) && IsComplete(inputStep)),
                metricTime,
                runIsActive)).ToArray(),
            ParseOptionalJson(configurationJson),
            ParseOptionalJson(connectionInstancesJson),
            schedulerLeaseExpiresUtc);
    }

    private static IReadOnlyList<MigrationDashboardRateSample> BuildRowsPerSecondSamples(
        IReadOnlyList<MigrationDashboardActivityPeriod> activityPeriods,
        IReadOnlyList<CompletedBatchRows> completedBatchRows,
        bool isComplete,
        DateTimeOffset nowUtc)
    {
        var firstWorkingPeriod = activityPeriods
            .Where(period => period.State.Equals("Working", StringComparison.Ordinal))
            .MinBy(period => period.StartedUtc);
        if (firstWorkingPeriod is null)
        {
            return Array.Empty<MigrationDashboardRateSample>();
        }

        var startedUtc = firstWorkingPeriod.StartedUtc;
        var endedUtc = isComplete
            ? activityPeriods
                .Where(period => period.EndedUtc.HasValue)
                .Select(period => period.EndedUtc!.Value)
                .DefaultIfEmpty(startedUtc)
                .Max()
            : nowUtc;
        var elapsed = endedUtc - startedUtc;
        if (elapsed <= TimeSpan.Zero)
        {
            return Array.Empty<MigrationDashboardRateSample>();
        }

        const int maximumSamples = 24;
        var sampleCount = Math.Clamp((int)Math.Ceiling(elapsed.TotalSeconds / 5), 1, maximumSamples);
        var rowsPerSample = new long[sampleCount];
        foreach (var batch in completedBatchRows)
        {
            if (batch.CompletedUtc < startedUtc || batch.CompletedUtc > endedUtc)
            {
                continue;
            }

            var elapsedRatio = (batch.CompletedUtc - startedUtc).TotalSeconds / elapsed.TotalSeconds;
            var sampleIndex = Math.Min((int)(elapsedRatio * sampleCount), sampleCount - 1);
            rowsPerSample[sampleIndex] = checked(rowsPerSample[sampleIndex] + batch.Rows);
        }

        return Enumerable.Range(0, sampleCount)
            .Select(index =>
            {
                var elapsedTicks = elapsed.Ticks / sampleCount * index +
                                   elapsed.Ticks % sampleCount * index / sampleCount;
                var periodStartedUtc = startedUtc.AddTicks(elapsedTicks);
                var periodEndedUtc = index == sampleCount - 1
                    ? endedUtc
                    : startedUtc.AddTicks(elapsed.Ticks / sampleCount * (index + 1) +
                                          elapsed.Ticks % sampleCount * (index + 1) / sampleCount);
                return new MigrationDashboardRateSample(
                    periodStartedUtc,
                    periodEndedUtc,
                    rowsPerSample[index] / (periodEndedUtc - periodStartedUtc).TotalSeconds);
            })
            .ToArray();
    }

    public IReadOnlyList<MigrationDashboardBatch> GetDashboardBatches(
        string runId,
        string stepId,
        int limit = 100)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
                   SELECT BatchId, RunId, StepId, Sequence, State, Attempts, LastError,
                   OutcomesJson, CreatedUtc, UpdatedUtc
            FROM Batches
                   WHERE RunId = $runId AND StepId = $stepId
            ORDER BY Sequence DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$stepId", stepId);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var batches = new List<MigrationDashboardBatch>();
        while (reader.Read())
        {
            var outcomes = reader.IsDBNull(7) ? null : reader.GetString(7);
            var rowCounts = outcomes is null ? default : ReadRowCounts(outcomes);
            batches.Add(new MigrationDashboardBatch(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                outcomes,
                ParseUtc(reader.GetString(8)),
                ParseUtc(reader.GetString(9)),
                rowCounts.Succeeded,
                rowCounts.Failed));
        }

        return batches;
    }

    public IReadOnlyList<MigrationDashboardBatch> GetDashboardBatchErrors(
        string runId,
        string stepId,
        int limit = 100,
        int offset = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT BatchId, RunId, StepId, Sequence, State, Attempts, LastError,
                   OutcomesJson, CreatedUtc, UpdatedUtc
            FROM Batches
            WHERE RunId = $runId AND StepId = $stepId AND State = 'CompletedWithErrors'
            ORDER BY Sequence DESC
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$stepId", stepId);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        using var reader = command.ExecuteReader();
        var batches = new List<MigrationDashboardBatch>();
        while (reader.Read())
        {
            var outcomes = reader.IsDBNull(7) ? null : reader.GetString(7);
            var rowCounts = outcomes is null ? default : ReadRowCounts(outcomes);
            batches.Add(new MigrationDashboardBatch(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                outcomes,
                ParseUtc(reader.GetString(8)),
                ParseUtc(reader.GetString(9)),
                rowCounts.Succeeded,
                rowCounts.Failed));
        }

        return batches;
    }

    public IReadOnlyList<MigrationDebugSnapshot> GetDebugSnapshots(
        string runId,
        string? lineageId = null,
        string? batchId = null,
        int limit = 500)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var retentionDays = GetDashboardRun(runId).DebugCaptureRetentionDays;
        var retentionStart = FormatUtc(DateTimeOffset.UtcNow.AddDays(-retentionDays));
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE TraceLineage(LineageId) AS (
                SELECT $lineageId
                UNION
                SELECT snapshots.LineageId
                FROM DebugSnapshots AS snapshots
                JOIN TraceLineage AS parent
                  ON EXISTS (
                    SELECT 1
                    FROM json_each(snapshots.ParentLineageIdsJson)
                    WHERE json_each.value = parent.LineageId
                  )
                WHERE snapshots.RunId = $runId
                  AND snapshots.CapturedUtc >= $retentionStart
                UNION
                SELECT parent.value
                FROM DebugSnapshots AS snapshots
                JOIN TraceLineage AS current
                  ON snapshots.LineageId = current.LineageId
                JOIN json_each(snapshots.ParentLineageIdsJson) AS parent
                WHERE snapshots.RunId = $runId
                  AND snapshots.CapturedUtc >= $retentionStart
            )
            SELECT RunId, BatchId, StepId, StageName, RecordIndex, Attempt,
                   LineageId, ParentLineageIdsJson, PayloadJson, CapturedUtc
            FROM DebugSnapshots
            WHERE RunId = $runId
              AND CapturedUtc >= $retentionStart
              AND ($lineageId IS NULL OR LineageId IN (SELECT LineageId FROM TraceLineage))
              AND ($batchId IS NULL OR BatchId = $batchId)
            ORDER BY CapturedUtc, SnapshotId
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$retentionStart", retentionStart);
        command.Parameters.AddWithValue("$lineageId", (object?)lineageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$batchId", (object?)batchId ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var snapshots = new List<MigrationDebugSnapshot>();
        while (reader.Read())
        {
            var parentLineageIds = JsonSerializer.Deserialize<string[]>(reader.GetString(7))
                ?? Array.Empty<string>();
            snapshots.Add(new MigrationDebugSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetString(6),
                parentLineageIds,
                reader.GetString(8),
                ParseUtc(reader.GetString(9))));
        }

        return snapshots;
    }

    internal IReadOnlyList<string> GetBatchInputLineageIds(string runId, string batchId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT RecordIndex, LineageId
            FROM DebugBatchInputs
            WHERE RunId = $runId
              AND BatchId = $batchId
            ORDER BY RecordIndex;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$batchId", batchId);
        var lineageIds = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var recordIndex = reader.GetInt32(0);
            if (recordIndex != lineageIds.Count)
            {
                throw new InvalidOperationException(
                    $"Debug input lineage metadata for batch '{batchId}' has a missing record index.");
            }

            lineageIds.Add(reader.GetString(1));
        }

        return lineageIds;
    }

    internal IReadOnlyList<string> GetBatchOutputLineageIds(string runId, string batchId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT RecordIndex, LineageId
            FROM DebugSnapshots
            WHERE RunId = $runId
              AND BatchId = $batchId
              AND RecordIndex >= 0
              AND StageName = (
                  SELECT StepId
                  FROM Batches
                  WHERE Batches.BatchId = $batchId
              )
            ORDER BY SnapshotId;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$batchId", batchId);
        var lineageIds = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var recordIndex = reader.GetInt32(0);
            if (recordIndex != lineageIds.Count)
            {
                throw new InvalidOperationException(
                    $"Debug output lineage metadata for batch '{batchId}' has a missing record index.");
            }

            lineageIds.Add(reader.GetString(1));
        }

        return lineageIds;
    }

    public MigrationDebugSnapshotPage GetDebugSnapshotPage(
        string runId,
        string stepId,
        string? search = null,
        int limit = 100,
        int offset = 0,
        string? stageName = null,
        string sortColumn = "record",
        string sortDirection = "descending",
        IReadOnlyDictionary<string, string>? columnFilters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sortColumn);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 500);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (stageName is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stageName);
        }

        var directionSql = sortDirection.Trim().ToLowerInvariant() switch
        {
            "ascending" => "ASC",
            "descending" => "DESC",
            _ => throw new ArgumentException("Sort direction must be 'ascending' or 'descending'.", nameof(sortDirection))
        };
        if (columnFilters is { Count: > 64 })
        {
            throw new ArgumentOutOfRangeException(nameof(columnFilters), "At most 64 snapshot column filters may be supplied.");
        }

        var sortColumnId = sortColumn.Trim();
        var sortExpression = sortColumnId switch
        {
            "record" => "RecordIndex",
            "attempt" => "Attempt",
            "captured" => "CapturedUtc",
            "batch" => "BatchId",
            "lineage" => "LineageId",
            "stage" => "StageName",
            "payload-json" => "PayloadJson",
            _ when sortColumnId.StartsWith("payload:", StringComparison.Ordinal) &&
                   sortColumnId.Length > "payload:".Length =>
                "(SELECT value FROM json_each(DebugSnapshots.PayloadJson) WHERE key = $sortProperty LIMIT 1)",
            _ => throw new ArgumentException($"Unsupported snapshot sort column '{sortColumn}'.", nameof(sortColumn))
        };
        var retentionDays = GetDashboardRun(runId).DebugCaptureRetentionDays;
        var retentionStart = FormatUtc(DateTimeOffset.UtcNow.AddDays(-retentionDays));
        var searchPattern = string.IsNullOrWhiteSpace(search)
            ? null
            : $"%{EscapeLikePattern(search.Trim())}%";
        var whereClauses = new List<string>
        {
            "RunId = $runId",
            "StepId = $stepId",
            "StageName <> 'BatchOutcome'",
            "($stageName IS NULL OR StageName = $stageName)",
            "CapturedUtc >= $retentionStart",
            """
            (
              $search IS NULL
              OR StageName LIKE $search ESCAPE '\'
              OR LineageId LIKE $search ESCAPE '\'
              OR PayloadJson LIKE $search ESCAPE '\'
            )
            """
        };
        var queryParameters = new List<(string Name, object Value)>
        {
            ("$runId", runId),
            ("$stepId", stepId),
            ("$stageName", (object?)stageName ?? DBNull.Value),
            ("$retentionStart", retentionStart),
            ("$search", (object?)searchPattern ?? DBNull.Value)
        };
        var filterIndex = 0;
        foreach (var (columnId, value) in columnFilters ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (value.Length > 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(columnFilters), "Snapshot column filters cannot exceed 1024 characters.");
            }

            var patternParameter = $"$filter{filterIndex}";
            var filterPattern = $"%{EscapeLikePattern(value.Trim())}%";
            var filterExpression = columnId switch
            {
                "record" => "CAST(RecordIndex AS TEXT)",
                "attempt" => "CAST(Attempt AS TEXT)",
                "captured" => "CapturedUtc",
                "batch" => "BatchId",
                "lineage" => "LineageId",
                "stage" => "StageName",
                "payload-json" => "PayloadJson",
                _ when columnId.StartsWith("payload:", StringComparison.Ordinal) &&
                       columnId.Length > "payload:".Length => null,
                _ => throw new ArgumentException($"Unsupported snapshot filter column '{columnId}'.", nameof(columnFilters))
            };

            if (filterExpression is null)
            {
                var propertyParameter = $"$filterProperty{filterIndex}";
                whereClauses.Add(
                    $"""
                    EXISTS (
                        SELECT 1
                        FROM json_each(DebugSnapshots.PayloadJson) AS filterProperty
                        WHERE filterProperty.key = {propertyParameter}
                          AND CAST(filterProperty.value AS TEXT) LIKE {patternParameter} ESCAPE '\'
                    )
                    """);
                queryParameters.Add((propertyParameter, columnId["payload:".Length..]));
            }
            else
            {
                whereClauses.Add($"{filterExpression} LIKE {patternParameter} ESCAPE '\\'");
            }

            queryParameters.Add((patternParameter, filterPattern));
            filterIndex++;
        }

        var whereSql = string.Join(" AND ", whereClauses);

        void AddQueryParameters(SqliteCommand command)
        {
            foreach (var (name, value) in queryParameters)
            {
                command.Parameters.AddWithValue(name, value);
            }
        }

        using var connection = OpenConnection();
        long totalCount;
        using (var count = connection.CreateCommand())
        {
            count.CommandText = $"""
                SELECT COUNT(*)
                FROM DebugSnapshots
                WHERE {whereSql};
                """;
            AddQueryParameters(count);
            totalCount = (long)(count.ExecuteScalar() ?? 0L);
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT RunId, BatchId, StepId, StageName, RecordIndex, Attempt,
                   LineageId, ParentLineageIdsJson, PayloadJson, CapturedUtc
            FROM DebugSnapshots
            WHERE {whereSql}
            ORDER BY {sortExpression} {directionSql}, CapturedUtc DESC, SnapshotId DESC
            LIMIT $limit OFFSET $offset;
            """;
        AddQueryParameters(command);
        if (sortColumnId.StartsWith("payload:", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("$sortProperty", sortColumnId["payload:".Length..]);
        }
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        var snapshots = new List<MigrationDebugSnapshot>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                snapshots.Add(ReadDebugSnapshot(reader));
            }
        }

        var stageCounts = new List<MigrationDebugSnapshotStageCount>();
        using (var counts = connection.CreateCommand())
        {
            counts.CommandText = """
                SELECT StageName, COUNT(*)
                FROM DebugSnapshots
                WHERE RunId = $runId
                  AND StepId = $stepId
                  AND StageName <> 'BatchOutcome'
                  AND CapturedUtc >= $retentionStart
                GROUP BY StageName
                ORDER BY StageName;
                """;
            counts.Parameters.AddWithValue("$runId", runId);
            counts.Parameters.AddWithValue("$stepId", stepId);
            counts.Parameters.AddWithValue("$retentionStart", retentionStart);
            using var countReader = counts.ExecuteReader();
            while (countReader.Read())
            {
                stageCounts.Add(new MigrationDebugSnapshotStageCount(
                    countReader.GetString(0),
                    countReader.GetInt64(1)));
            }
        }

        var payloadColumns = new List<string>();
        using (var columns = connection.CreateCommand())
        {
            columns.CommandText = """
                SELECT DISTINCT payloadProperty.key
                FROM DebugSnapshots AS snapshots
                JOIN json_each(snapshots.PayloadJson) AS payloadProperty
                WHERE snapshots.RunId = $runId
                  AND snapshots.StepId = $stepId
                  AND snapshots.StageName <> 'BatchOutcome'
                  AND snapshots.CapturedUtc >= $retentionStart
                  AND ($stageName IS NULL OR snapshots.StageName = $stageName)
                ORDER BY payloadProperty.key COLLATE NOCASE;
                """;
            columns.Parameters.AddWithValue("$runId", runId);
            columns.Parameters.AddWithValue("$stepId", stepId);
            columns.Parameters.AddWithValue("$retentionStart", retentionStart);
            columns.Parameters.AddWithValue("$stageName", (object?)stageName ?? DBNull.Value);
            using var columnsReader = columns.ExecuteReader();
            while (columnsReader.Read())
            {
                payloadColumns.Add(columnsReader.GetString(0));
            }
        }

        return new MigrationDebugSnapshotPage(
            snapshots,
            totalCount,
            offset + snapshots.Count < totalCount,
            stageCounts,
            payloadColumns);
    }

    public void MarkSourceFeederStarted(string runId, string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SourceFeeders (RunId, StepId, Completed, UpdatedUtc)
            VALUES ($runId, $stepId, 0, $updatedUtc)
            ON CONFLICT (RunId, StepId) DO UPDATE SET
                Completed = 0,
                UpdatedUtc = excluded.UpdatedUtc;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$stepId", stepId);
        command.Parameters.AddWithValue("$updatedUtc", FormatUtc(DateTimeOffset.UtcNow));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                $"Source feeder state for step '{stepId}' in run '{runId}' could not be recorded.");
        }
    }

    public void MarkSourceFeederCompleted(string runId, string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE SourceFeeders
            SET Completed = 1, UpdatedUtc = $updatedUtc
            WHERE RunId = $runId AND StepId = $stepId;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$stepId", stepId);
        command.Parameters.AddWithValue("$updatedUtc", FormatUtc(DateTimeOffset.UtcNow));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                $"Source feeder state for step '{stepId}' in run '{runId}' was not initialized.");
        }
    }

    public void CaptureDebugSnapshots(IEnumerable<MigrationDebugSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var records = snapshots.ToArray();
        if (records.Length == 0)
        {
            return;
        }

        foreach (var snapshot in records)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.RunId);
            ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.BatchId);
            ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.StepId);
            ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.StageName);
            ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.LineageId);
            ValidateJson(snapshot.PayloadJson, nameof(snapshots));
        }

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText = """
                DELETE FROM DebugSnapshots
                WHERE RunId = $runId
                  AND CapturedUtc < (
                    SELECT strftime(
                        '%Y-%m-%dT%H:%M:%f+00:00',
                        'now',
                        '-' || DebugCaptureRetentionDays || ' days')
                    FROM Runs
                    WHERE RunId = $runId
                  );
                DELETE FROM DebugBatchInputs
                WHERE RunId = $runId
                  AND EXISTS (
                    SELECT 1
                    FROM Runs
                    WHERE Runs.RunId = DebugBatchInputs.RunId
                      AND Runs.Status <> 'Running'
                      AND Runs.FinishedUtc IS NOT NULL
                      AND Runs.FinishedUtc < strftime(
                        '%Y-%m-%dT%H:%M:%f+00:00',
                        'now',
                        '-' || Runs.DebugCaptureRetentionDays || ' days')
                  );
                """;
            var runIdParameter = cleanup.Parameters.AddWithValue("$runId", string.Empty);
            foreach (var runId in records.Select(snapshot => snapshot.RunId).Distinct(StringComparer.Ordinal))
            {
                runIdParameter.Value = runId;
                cleanup.ExecuteNonQuery();
            }
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO DebugSnapshots (
                RunId, BatchId, StepId, StageName, RecordIndex, Attempt,
                LineageId, ParentLineageIdsJson, PayloadJson, CapturedUtc)
            SELECT
                $runId, $batchId, $stepId, $stageName, $recordIndex, $attempt,
                $lineageId, $parentLineageIdsJson, $payloadJson, $capturedUtc
            WHERE EXISTS (
                SELECT 1
                FROM Runs
                WHERE RunId = $runId AND DebugCaptureEnabled = 1
            );
            """;
        var insertRunId = insert.Parameters.AddWithValue("$runId", string.Empty);
        var insertBatchId = insert.Parameters.AddWithValue("$batchId", string.Empty);
        var insertStepId = insert.Parameters.AddWithValue("$stepId", string.Empty);
        var insertStageName = insert.Parameters.AddWithValue("$stageName", string.Empty);
        var insertRecordIndex = insert.Parameters.AddWithValue("$recordIndex", 0);
        var insertAttempt = insert.Parameters.AddWithValue("$attempt", 0);
        var insertLineageId = insert.Parameters.AddWithValue("$lineageId", string.Empty);
        var insertParentLineageIdsJson = insert.Parameters.AddWithValue("$parentLineageIdsJson", string.Empty);
        var insertPayloadJson = insert.Parameters.AddWithValue("$payloadJson", string.Empty);
        var insertCapturedUtc = insert.Parameters.AddWithValue("$capturedUtc", string.Empty);
        insert.Prepare();

        using var lineageInsert = connection.CreateCommand();
        lineageInsert.Transaction = transaction;
        lineageInsert.CommandText = """
            INSERT INTO DebugBatchInputs (RunId, BatchId, RecordIndex, LineageId)
            VALUES ($runId, $batchId, $recordIndex, $lineageId)
            ON CONFLICT (RunId, BatchId, RecordIndex) DO UPDATE
            SET LineageId = excluded.LineageId
            WHERE DebugBatchInputs.LineageId = excluded.LineageId;
            """;
        var lineageRunId = lineageInsert.Parameters.AddWithValue("$runId", string.Empty);
        var lineageBatchId = lineageInsert.Parameters.AddWithValue("$batchId", string.Empty);
        var lineageRecordIndex = lineageInsert.Parameters.AddWithValue("$recordIndex", 0);
        var lineageId = lineageInsert.Parameters.AddWithValue("$lineageId", string.Empty);
        lineageInsert.Prepare();

        foreach (var snapshot in records)
        {
            insertRunId.Value = snapshot.RunId;
            insertBatchId.Value = snapshot.BatchId;
            insertStepId.Value = snapshot.StepId;
            insertStageName.Value = snapshot.StageName;
            insertRecordIndex.Value = snapshot.RecordIndex;
            insertAttempt.Value = snapshot.Attempt;
            insertLineageId.Value = snapshot.LineageId;
            insertParentLineageIdsJson.Value = JsonSerializer.Serialize(snapshot.ParentLineageIds);
            insertPayloadJson.Value = snapshot.PayloadJson;
            insertCapturedUtc.Value = FormatUtc(snapshot.CapturedUtc);
            if (insert.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    $"Debug capture is disabled or run '{snapshot.RunId}' does not exist.");
            }

            if ((snapshot.StageName is "BatchInput" or "SourceInput") && snapshot.RecordIndex >= 0)
            {
                lineageRunId.Value = snapshot.RunId;
                lineageBatchId.Value = snapshot.BatchId;
                lineageRecordIndex.Value = snapshot.RecordIndex;
                lineageId.Value = snapshot.LineageId;
                if (lineageInsert.ExecuteNonQuery() != 1)
                {
                    throw new InvalidOperationException(
                        $"Batch '{snapshot.BatchId}' produced conflicting lineage IDs for input record {snapshot.RecordIndex}.");
                }
            }
        }

        transaction.Commit();
    }

    private static MigrationDebugSnapshot ReadDebugSnapshot(SqliteDataReader reader)
    {
        var parentLineageIds = JsonSerializer.Deserialize<string[]>(reader.GetString(7))
            ?? Array.Empty<string>();
        return new MigrationDebugSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetString(6),
            parentLineageIds,
            reader.GetString(8),
            ParseUtc(reader.GetString(9)));
    }

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static List<MutableDashboardStep> ReadPipeline(string pipelineJson)
    {
        using var document = JsonDocument.Parse(pipelineJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Stored migration pipeline metadata must be a JSON array.");
        }

        var steps = new List<MutableDashboardStep>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var id = item.GetProperty("Id").GetString()
                ?? throw new InvalidDataException("Stored migration step ID was null.");
            var inputs = item.GetProperty("Inputs")
                .EnumerateArray()
                .Select(value => value.GetString() ?? string.Empty)
                .ToArray();
            var completionDependencies = item.TryGetProperty("CompletionDependencies", out var completionProperty)
                ? completionProperty.EnumerateArray()
                    .Select(value => value.GetString() ?? string.Empty)
                    .ToArray()
                : Array.Empty<string>();
            var hasSource = item.GetProperty("HasSource").GetBoolean();
            var type = item.TryGetProperty("Type", out var typeProperty)
                ? typeProperty.GetString() ?? "unknown"
                : "unknown";
            var expectedRecordCount = item.TryGetProperty("ExpectedRecordCount", out var expectedProperty) &&
                                      expectedProperty.ValueKind == JsonValueKind.Number &&
                                      expectedProperty.TryGetInt64(out var expectedCount) &&
                                      expectedCount >= 0
                ? expectedCount
                : (long?)null;
            steps.Add(new MutableDashboardStep(
                id,
                inputs,
                hasSource,
                type,
                completionDependencies,
                expectedRecordCount));
        }

        return steps;
    }

    private static (long Succeeded, long Failed) ReadRowCounts(string outcomesJson)
    {
        using var document = JsonDocument.Parse(outcomesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        long succeeded = 0;
        long failed = 0;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.Equals("succeeded", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("success", StringComparison.OrdinalIgnoreCase))
            {
                succeeded = ReadCount(property.Value);
            }
            else if (property.Name.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
                     property.Name.Equals("errors", StringComparison.OrdinalIgnoreCase))
            {
                failed = ReadCount(property.Value);
            }
        }

        return (succeeded, failed);
    }

    private static JsonElement? ReadSpecialistInfo(string outcomesJson)
    {
        using var document = JsonDocument.Parse(outcomesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.Equals("SpecialistInfo", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.Object)
            {
                return property.Value.Clone();
            }
        }

        return null;
    }

    private static long ReadCount(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var count) => count,
            JsonValueKind.Array => value.GetArrayLength(),
            JsonValueKind.Object => 1,
            _ => 0
        };
    }

    private sealed record CompletedBatchRows(DateTimeOffset CompletedUtc, long Rows);

    private static JsonElement? ParseOptionalJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Null
            ? null
            : document.RootElement.Clone();
    }

    private sealed class MutableDashboardStep(
        string id,
        IReadOnlyList<string> inputs,
        bool hasSource,
        string type,
        IReadOnlyList<string>? completionDependencies = null,
        long? configuredExpectedRows = null)
    {
        public string Id { get; } = id;
        public IReadOnlyList<string> Inputs { get; } = inputs;
        public IReadOnlyList<string> CompletionDependencies { get; } =
            completionDependencies ?? Array.Empty<string>();
        public bool HasSource { get; } = hasSource;
        public string Type { get; } = type;
        public long? ConfiguredExpectedRows { get; } = configuredExpectedRows;
        public bool SourceFeederCompleted { get; set; }
        public long PendingBatches { get; set; }
        public long InFlightBatches { get; set; }
        public long PendingDependencyEvents { get; set; }
        public long SucceededBatches { get; set; }
        public long BatchesWithErrors { get; set; }
        public long SucceededRows { get; set; }
        public long FailedRows { get; set; }
        public long InputRowsProcessed { get; set; }
        public long InputRowsPending { get; set; }
        public long InputRowsInFlight { get; set; }
        public long FilteredRows { get; set; }
        public long BatchesWithUnknownInputRows { get; set; }
        public JsonElement? SpecialistInfo { get; set; }
        public List<CompletedBatchRows> CompletedBatchRows { get; } = [];
        public IReadOnlyList<MigrationDashboardRateSample> RowsPerSecondSamples { get; set; } =
            Array.Empty<MigrationDashboardRateSample>();
        public DateTimeOffset? FirstBatchUtc { get; set; }
        public DateTimeOffset? LastBatchUtc { get; set; }
        public long ActiveMilliseconds { get; set; }
        public long IdleMilliseconds { get; set; }
        public DateTimeOffset? ActiveSinceUtc { get; set; }
        public DateTimeOffset? IdleSinceUtc { get; set; }
        public List<MigrationDashboardActivityPeriod> ActivityPeriods { get; } = [];

        public MigrationDashboardStep ToRecord(
            bool isComplete,
            bool inputFinalized,
            DateTimeOffset now,
            bool isRunRunning)
        {
            var activeMilliseconds = ActiveMilliseconds;
            var idleMilliseconds = IdleMilliseconds;
            if (isRunRunning && !isComplete)
            {
                if (ActiveSinceUtc.HasValue)
                {
                    activeMilliseconds += Math.Max(0, (long)(now - ActiveSinceUtc.Value).TotalMilliseconds);
                }
                else if (IdleSinceUtc.HasValue)
                {
                    idleMilliseconds += Math.Max(0, (long)(now - IdleSinceUtc.Value).TotalMilliseconds);
                }
            }

            var totalRows = SucceededRows + FailedRows;
            var rowsPerSecond = activeMilliseconds > 0
                ? totalRows / (activeMilliseconds / 1000d)
                : (double?)null;
            return new MigrationDashboardStep(
                Id,
                Inputs,
                CompletionDependencies,
                HasSource,
                Type,
                isComplete,
                PendingBatches,
                InFlightBatches,
                PendingDependencyEvents,
                SucceededBatches,
                BatchesWithErrors,
                SucceededRows,
                FailedRows,
                FirstBatchUtc,
                LastBatchUtc,
                rowsPerSecond,
                idleMilliseconds)
            {
                ActiveMilliseconds = activeMilliseconds,
                InputRowsProcessed = InputRowsProcessed,
                InputRowsPending = InputRowsPending,
                InputRowsInFlight = InputRowsInFlight,
                FilteredRows = FilteredRows,
                ExpectedRows = HasSource && ConfiguredExpectedRows.HasValue
                    ? ConfiguredExpectedRows
                    : inputFinalized && BatchesWithUnknownInputRows == 0
                        ? InputRowsProcessed + InputRowsPending + InputRowsInFlight
                        : null,
                InputFinalized = inputFinalized,
                BatchesWithUnknownInputRows = BatchesWithUnknownInputRows,
                SpecialistInfo = SpecialistInfo,
                RowsPerSecondSamples = RowsPerSecondSamples,
                ActivityPeriods = ActivityPeriods.ToArray()
            };
        }
    }
}
