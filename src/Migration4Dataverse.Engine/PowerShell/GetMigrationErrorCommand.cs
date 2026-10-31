using System.Management.Automation;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.PowerShell;

public sealed record MigrationErrorDetail(
    string RunId,
    string Scope,
    string? StepId,
    string? BatchId,
    long? Sequence,
    string Message,
    int Attempts,
    string? OutcomesJson,
    IReadOnlyList<MigrationDashboardRowError> RowErrors,
    MigrationRunFailure? RunFailure);

[Cmdlet(VerbsCommon.Get, "M4dMigrationError")]
[OutputType(typeof(MigrationErrorDetail))]
public sealed class GetMigrationErrorCommand : MigrationStoreCmdletBase
{
    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string RunId { get; set; } = string.Empty;

    [Parameter]
    [ValidateNotNullOrEmpty]
    public string? StepId { get; set; }

    [Parameter]
    [ValidateRange(1, 1000)]
    public int Limit { get; set; } = 100;

    [Parameter]
    [ValidateRange(0, int.MaxValue)]
    public int Offset { get; set; }

    protected override void ProcessRecord()
    {
        var store = OpenStore();
        var run = store.GetDashboardRun(RunId);
        if (StepId is not null &&
            !run.Steps.Any(step => string.Equals(step.Id, StepId, StringComparison.Ordinal)))
        {
            throw new ItemNotFoundException($"Run '{RunId}' has no step '{StepId}'.");
        }

        if (run.Failure is { } failure &&
            (StepId is null || string.Equals(failure.StepId, StepId, StringComparison.Ordinal)))
        {
            WriteObject(new MigrationErrorDetail(
                RunId,
                "Run",
                failure.StepId,
                null,
                null,
                failure.Message,
                failure.Attempts,
                null,
                Array.Empty<MigrationDashboardRowError>(),
                failure));
        }

        foreach (var step in run.Steps)
        {
            if (StepId is not null && !string.Equals(step.Id, StepId, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var batch in store.GetDashboardBatchErrors(RunId, step.Id, Limit, Offset))
            {
                var message = batch.LastError;
                if (string.IsNullOrWhiteSpace(message))
                {
                    message = string.Join(
                        Environment.NewLine,
                        batch.RowErrors.Select(rowError => rowError.Error));
                }

                WriteObject(new MigrationErrorDetail(
                    RunId,
                    "Batch",
                    batch.StepId,
                    batch.BatchId,
                    batch.Sequence,
                    message ?? "The batch failed without recorded error text.",
                    batch.Attempts,
                    batch.OutcomesJson,
                    batch.RowErrors,
                    null));
            }
        }
    }
}
