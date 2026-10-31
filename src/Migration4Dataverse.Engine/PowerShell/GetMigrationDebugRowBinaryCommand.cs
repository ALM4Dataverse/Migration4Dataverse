using System.Management.Automation;
using System.Text.Json;
using Migration4Dataverse.Engine.Connectors;

namespace Migration4Dataverse.Engine.PowerShell;

[Cmdlet(VerbsCommon.Get, "M4dMigrationDebugRowBinary")]
[OutputType(typeof(MigrationBinaryValue))]
public sealed class GetMigrationDebugRowBinaryCommand : MigrationStoreCmdletBase
{
    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string RunId { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string StepId { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string BatchId { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string StageName { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    [ValidateRange(0, int.MaxValue)]
    public int RecordIndex { get; set; }

    [Parameter(Mandatory = true)]
    [ValidateRange(1, int.MaxValue)]
    public int Attempt { get; set; }

    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string LineageId { get; set; } = string.Empty;

    [Parameter(Mandatory = true)]
    public DateTimeOffset CapturedUtc { get; set; }

    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string PropertyName { get; set; } = string.Empty;

    protected override void ProcessRecord()
    {
        var snapshot = OpenStore().GetDebugSnapshot(
            RunId,
            StepId,
            BatchId,
            StageName,
            RecordIndex,
            Attempt,
            LineageId,
            CapturedUtc);
        if (snapshot is null)
        {
            throw new ItemNotFoundException("The captured row snapshot was not found or has expired.");
        }

        using var payload = JsonDocument.Parse(snapshot.PayloadJson);
        if (payload.RootElement.ValueKind != JsonValueKind.Object ||
            !payload.RootElement.TryGetProperty(PropertyName, out var propertyValue))
        {
            throw new ItemNotFoundException($"The captured row has no property '{PropertyName}'.");
        }

        var transportValue = JsonSerializer.Deserialize<object?>(propertyValue.GetRawText());
        var binaryValue = MigrationBinaryValue.RestoreFromTransport(
            transportValue,
            MigrationBinaryValue.GetRunStagingRoot(GetDatabasePath(), RunId)) as MigrationBinaryValue;
        if (binaryValue is null)
        {
            throw new InvalidDataException($"Captured property '{PropertyName}' is not a binary artifact.");
        }

        WriteObject(binaryValue);
    }
}
