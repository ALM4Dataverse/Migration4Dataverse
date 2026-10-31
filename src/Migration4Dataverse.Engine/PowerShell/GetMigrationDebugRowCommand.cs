using System.Collections;
using System.Globalization;
using System.Management.Automation;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.PowerShell;

[Cmdlet(VerbsCommon.Get, "M4dMigrationDebugRow")]
[OutputType(typeof(MigrationDebugSnapshotPage))]
[OutputType(typeof(MigrationDebugSnapshot))]
public sealed class GetMigrationDebugRowCommand : MigrationStoreCmdletBase
{
    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string RunId { get; set; } = string.Empty;

    [Parameter]
    [ValidateNotNullOrEmpty]
    public string? StepId { get; set; }

    [Parameter]
    [ValidateNotNullOrEmpty]
    public string? LineageId { get; set; }

    [Parameter]
    [ValidateNotNullOrEmpty]
    public string? BatchId { get; set; }

    [Parameter]
    public SwitchParameter Trace { get; set; }

    [Parameter]
    public string? Search { get; set; }

    [Parameter]
    public string? StageName { get; set; }

    [Parameter]
    [ValidateRange(1, 2000)]
    public int Limit { get; set; } = 100;

    [Parameter]
    [ValidateRange(0, int.MaxValue)]
    public int Offset { get; set; }

    [Parameter]
    [ValidateNotNullOrEmpty]
    public string SortColumn { get; set; } = "record";

    [Parameter]
    [ValidateSet("ascending", "descending")]
    public string SortDirection { get; set; } = "descending";

    [Parameter]
    public IDictionary? ColumnFilters { get; set; }

    protected override void ProcessRecord()
    {
        var store = OpenStore();
        if (Trace)
        {
            if (string.IsNullOrWhiteSpace(LineageId) || !string.IsNullOrWhiteSpace(StepId))
            {
                throw new ArgumentException("Trace requires LineageId and cannot be combined with StepId.");
            }

            WriteObject(
                store.GetDebugSnapshots(RunId, LineageId, BatchId, Limit),
                enumerateCollection: true);
            return;
        }

        if (!string.IsNullOrWhiteSpace(StepId))
        {
            if (!string.IsNullOrWhiteSpace(LineageId) || !string.IsNullOrWhiteSpace(BatchId))
            {
                throw new ArgumentException("StepId cannot be combined with LineageId or BatchId.");
            }
            if (Limit > 500)
            {
                throw new ArgumentOutOfRangeException(nameof(Limit), "A debug-row page can contain at most 500 rows.");
            }

            WriteObject(store.GetDebugSnapshotPage(
                RunId,
                StepId,
                Search,
                Limit,
                Offset,
                StageName,
                SortColumn,
                SortDirection,
                ConvertFilters(ColumnFilters)));
            return;
        }

        if (string.IsNullOrWhiteSpace(LineageId) && string.IsNullOrWhiteSpace(BatchId))
        {
            throw new ArgumentException("Supply StepId, BatchId, or -Trace with LineageId.");
        }

        WriteObject(
            store.GetDebugSnapshots(RunId, LineageId, BatchId, Limit),
            enumerateCollection: true);
    }

    private static IReadOnlyDictionary<string, string>? ConvertFilters(IDictionary? filters)
    {
        if (filters is null)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in filters)
        {
            var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Debug-row filter keys must not be empty.", nameof(filters));
            }
            if (!result.TryAdd(
                    key,
                    Convert.ToString(entry.Value, CultureInfo.InvariantCulture) ?? string.Empty))
            {
                throw new ArgumentException($"Debug-row filter '{key}' was supplied more than once.", nameof(filters));
            }
        }

        return result;
    }
}
