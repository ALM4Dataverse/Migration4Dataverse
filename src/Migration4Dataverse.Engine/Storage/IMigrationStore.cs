namespace Migration4Dataverse.Engine.Storage;

public interface IMigrationStore
{
    void CreateRun(
        string runId,
        string runName,
        string stepHash,
        DateTimeOffset? nowUtc = null,
        string pipelineJson = "[]",
        bool debugCaptureEnabled = false,
        int debugCaptureRetentionDays = 30,
        string configurationJson = "null",
        string connectionInstancesJson = "[]");
    void UpdateConnectionInstanceSnapshots(string runId, string snapshotsJson);
    void SetRunStatus(string runId, MigrationRunStatus status, DateTimeOffset? nowUtc = null);
    void SaveTargetRecordIds(string runId, IReadOnlyList<TargetRecordIdMapping> mappings);
    IReadOnlyDictionary<TargetRecordIdLookup, Guid> GetTargetRecordIds(
        string runId,
        string connectionId,
        IReadOnlyList<TargetRecordIdLookup> lookups);
    void SaveTargetRecordContexts(string runId, IReadOnlyList<TargetRecordContextMapping> contexts);
    string? GetTargetRecordContext(
        string runId,
        string stepId,
        string connectionId,
        string tableName,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> keyValues,
        bool required = true);
    IReadOnlyDictionary<string, string> GetTargetRecordContexts(
        string runId,
        string stepId,
        string connectionId,
        string tableName,
        string keyColumnsJson,
        IReadOnlyList<string> keyValuesJson);
    void SaveJoinInputRecords(string runId, IReadOnlyList<JoinInputRecordMapping> records);
    IReadOnlyList<JoinInputRecord> ReadJoinInputRecordPage(
        string runId,
        string joinStepId,
        string inputStepId,
        long afterSequence,
        int afterRecordIndex,
        int pageSize,
        long? throughSequence = null);
    JoinInputRecord? FindJoinInputRecordByKey(
        string runId,
        string joinStepId,
        string inputStepId,
        string keyJson,
        long throughSequence);
    JoinInputRecord? GetJoinInputRecord(
        string runId,
        string joinStepId,
        string inputStepId,
        long sequence,
        int recordIndex);
    void SaveJoinRelationshipMatches(
        string runId,
        string joinStepId,
        IReadOnlyList<JoinRelationshipMatch> matches);
    IReadOnlyDictionary<JoinInputRecordKey, IReadOnlyDictionary<string, JoinRelationshipMatch>>
        ReadJoinRelationshipMatches(
            string runId,
            string joinStepId,
            IReadOnlyList<string> aliases,
            JoinInputRecordKey first,
            JoinInputRecordKey last);
    IReadOnlyDictionary<JoinInputRecordKey, JoinInputRecord> ReadJoinInputRecordsByKeys(
        string runId,
        string joinStepId,
        string inputStepId,
        IReadOnlyList<JoinInputRecordKey> keys);
    void ClearJoinStaging(string runId);
    void StartStepActivity(string runId, string stepId, DateTimeOffset? nowUtc = null);
    void EnterStepActivity(string runId, string stepId, DateTimeOffset? nowUtc = null);
    void ExitStepActivity(string runId, string stepId, DateTimeOffset? nowUtc = null);
    void CompleteStepActivity(string runId, string stepId, DateTimeOffset? nowUtc = null);
    bool TryAcquireRunLease(
        string runId,
        string workerId,
        TimeSpan leaseDuration,
        DateTimeOffset? nowUtc = null);
    void ReleaseRunLease(string runId, string workerId, DateTimeOffset? nowUtc = null);
    MigrationRunProgress GetRunProgress(string runId);
    IReadOnlyDictionary<string, long> GetNonTerminalBatchCountsByStep(string runId);
    IReadOnlyList<PendingBatchStepCandidate> GetPendingBatchStepCandidates(
        string runId,
        DateTimeOffset? nowUtc = null);
    long GetPendingDependencyEventCountWithoutPrerequisites(string runId);
    IReadOnlyDictionary<string, long> GetPendingDependencyEventCountsWithoutPrerequisitesByStep(string runId);
    IReadOnlyDictionary<string, long> GetPendingDependencyEventCountsByStep(string runId);
    string EnqueueBatch(
        string runId,
        string tableName,
        long sequence,
        string batchId,
        string? cursorAfterJson,
        string payloadJson,
        DateTimeOffset? nowUtc = null,
        IReadOnlyList<string>? parentBatchIds = null,
        long filteredRows = 0,
        IReadOnlyList<SourceKeyEntry>? sourceKeys = null);
    SourceKeyBatchReference? GetSourceKeyBatchReference(
        string runId,
        string sourceStepId,
        string sourceKey);
    IEnumerable<SourceKeyBatchReference> EnumerateSourceKeyBatchReferences(
        string runId,
        string sourceStepId);
    StoredBatch? TryLeaseNextBatch(
        string runId,
        string workerId,
        TimeSpan leaseDuration,
        DateTimeOffset? nowUtc = null,
        string? stepId = null);
    void AbandonBatch(
        string batchId,
        string workerId,
        string error,
        DateTimeOffset? nowUtc = null);
    bool TryAbandonBatch(
        string batchId,
        string workerId,
        string error,
        DateTimeOffset? nowUtc = null);
    bool RenewBatchLease(
        string batchId,
        string workerId,
        TimeSpan leaseDuration,
        DateTimeOffset? nowUtc = null);
    MigrationStepCheckpoint CompleteBatch(
        string batchId,
        string workerId,
        string outcomesJson,
        bool hasErrors,
        IReadOnlyCollection<DependencyEvent>? dependencyEvents = null,
        string? lastError = null,
        DateTimeOffset? nowUtc = null,
        long filteredRows = 0);
    MigrationStepCheckpoint? TryCompleteBatch(
        string batchId,
        string workerId,
        string outcomesJson,
        bool hasErrors,
        IReadOnlyCollection<DependencyEvent>? dependencyEvents = null,
        string? lastError = null,
        DateTimeOffset? nowUtc = null,
        long filteredRows = 0);
    StoredBatch? GetBatch(string batchId);
    MigrationStepCheckpoint GetStepCheckpoint(string runId, string tableName);
    MigrationStepCheckpoint GetStepSourceCheckpoint(string runId, string stepId);
    IReadOnlyList<StoredDependencyEvent> GetPendingDependencyEvents(
        string runId,
        int limit = 100,
        DateTimeOffset? afterCreatedUtc = null,
        string? afterEventId = null);
    DependencyBatchMaterialization? TryMaterializeDependencyEvent(
        string runId,
        string eventId,
        DateTimeOffset? nowUtc = null);
    void CompleteDependencyEvent(string runId, string eventId, DateTimeOffset? nowUtc = null);
    long PurgeExpiredDebugSnapshots();
    IReadOnlyList<MigrationRunSummary> GetDashboardRuns(int limit = 50);
    MigrationRunSummary GetDashboardRun(string runId);
    IReadOnlyList<MigrationDashboardBatch> GetDashboardBatches(
        string runId,
        string stepId,
        int limit = 100);
    IReadOnlyList<MigrationDashboardBatch> GetDashboardBatchErrors(
        string runId,
        string stepId,
        int limit = 100,
        int offset = 0);
    IReadOnlyList<MigrationDebugSnapshot> GetDebugSnapshots(
        string runId,
        string? lineageId = null,
        string? batchId = null,
        int limit = 500);
    MigrationDebugSnapshot? GetDebugSnapshot(
        string runId,
        string stepId,
        string batchId,
        string stageName,
        int recordIndex,
        int attempt,
        string lineageId,
        DateTimeOffset capturedUtc);
    IReadOnlyList<string> GetBatchInputLineageIds(string runId, string batchId);
    IReadOnlyList<string> GetBatchOutputLineageIds(string runId, string batchId);
    MigrationDebugSnapshotPage GetDebugSnapshotPage(
        string runId,
        string stepId,
        string? search = null,
        int limit = 100,
        int offset = 0,
        string? stageName = null,
        string sortColumn = "record",
        string sortDirection = "descending",
        IReadOnlyDictionary<string, string>? columnFilters = null);
    void MarkSourceFeederStarted(string runId, string stepId);
    void MarkSourceFeederCompleted(string runId, string stepId);
    void CaptureDebugSnapshots(IEnumerable<MigrationDebugSnapshot> snapshots);
    DataverseTargetTuningProfile? GetDataverseTargetTuningProfile(string stepId);
    void SaveDataverseTargetTuningProfile(
        string stepId,
        int? batchSize,
        int? maxConcurrency,
        int algorithmVersion = 1,
        string? connectionId = null);
    long ClearDataverseTargetTuningProfile(string stepId);
    long ClearDataverseTargetTuningProfiles();
}
