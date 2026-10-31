using System.Management.Automation;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.PowerShell;

public sealed record MigrationRecoveryResult(
    string RunId,
    string Action,
    int QueuedBatchCount,
    string? QueuedBatchId,
    bool? ContinuationAllowed);

[Cmdlet(
    VerbsLifecycle.Invoke,
    "M4dMigrationRecovery",
    DefaultParameterSetName = RetryBatchesParameterSet,
    SupportsShouldProcess = true,
    ConfirmImpact = ConfirmImpact.Medium)]
[OutputType(typeof(MigrationRecoveryResult))]
public sealed class InvokeMigrationRecoveryCommand : MigrationStoreCmdletBase
{
    private const string RetryBatchesParameterSet = "RetryBatches";
    private const string RetryRowParameterSet = "RetryRow";
    private const string ContinueStepParameterSet = "ContinueStep";

    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string RunId { get; set; } = string.Empty;

    [Parameter(Mandatory = true, ParameterSetName = RetryBatchesParameterSet)]
    public SwitchParameter RetryFailedBatches { get; set; }

    [Parameter(Mandatory = true, ParameterSetName = RetryRowParameterSet)]
    [Parameter(Mandatory = true, ParameterSetName = ContinueStepParameterSet)]
    [ValidateNotNullOrEmpty]
    public string StepId { get; set; } = string.Empty;

    [Parameter(Mandatory = true, ParameterSetName = RetryRowParameterSet)]
    [ValidateNotNullOrEmpty]
    public string BatchId { get; set; } = string.Empty;

    [Parameter(Mandatory = true, ParameterSetName = RetryRowParameterSet)]
    [ValidateRange(0, int.MaxValue)]
    public int RecordIndex { get; set; }

    [Parameter(Mandatory = true, ParameterSetName = RetryRowParameterSet)]
    [ValidateNotNullOrEmpty]
    public string PayloadJson { get; set; } = string.Empty;

    [Parameter(Mandatory = true, ParameterSetName = ContinueStepParameterSet)]
    public SwitchParameter ContinueAfterFailure { get; set; }

    protected override void ProcessRecord()
    {
        var action = ParameterSetName switch
        {
            RetryBatchesParameterSet => "Queue all failed batches for replay",
            RetryRowParameterSet => $"Queue row {RecordIndex} from batch '{BatchId}' for retry",
            ContinueStepParameterSet => $"Approve continuation past step '{StepId}'",
            _ => throw new InvalidOperationException($"Unsupported recovery action '{ParameterSetName}'.")
        };
        var target = $"run '{RunId}' in '{GetDatabasePath()}'";
        if (!ShouldProcess(target, action))
        {
            return;
        }

        var store = MigrationStoreFactory.Open(GetDatabasePath());
        var result = ParameterSetName switch
        {
            RetryBatchesParameterSet => new MigrationRecoveryResult(
                RunId,
                action,
                store.QueueFailedBatchesForRetry(RunId),
                null,
                null),
            RetryRowParameterSet => new MigrationRecoveryResult(
                RunId,
                action,
                1,
                store.QueueFailedRowForRetry(RunId, StepId, BatchId, RecordIndex, PayloadJson),
                null),
            ContinueStepParameterSet => new MigrationRecoveryResult(
                RunId,
                action,
                0,
                null,
                store.AllowStepFailureContinuation(RunId, StepId)),
            _ => throw new InvalidOperationException($"Unsupported recovery action '{ParameterSetName}'.")
        };
        WriteObject(result);
    }
}
