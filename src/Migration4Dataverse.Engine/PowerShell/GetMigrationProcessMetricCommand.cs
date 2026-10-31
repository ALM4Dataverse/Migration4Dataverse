using System.Diagnostics;
using System.Management.Automation;

namespace Migration4Dataverse.Engine.PowerShell;

public sealed record MigrationProcessMetricSnapshot(
    double CpuTimeMilliseconds,
    int LogicalProcessorCount,
    long WorkingSetBytes,
    long ManagedHeapBytes,
    long? MemoryLimitBytes,
    DateTimeOffset CapturedUtc);

[Cmdlet(VerbsCommon.Get, "MigrationProcessMetric")]
[OutputType(typeof(MigrationProcessMetricSnapshot))]
public sealed class GetMigrationProcessMetricCommand : PSCmdlet
{
    protected override void ProcessRecord()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var memoryLimitBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        WriteObject(new MigrationProcessMetricSnapshot(
            process.TotalProcessorTime.TotalMilliseconds,
            Math.Max(1, Environment.ProcessorCount),
            process.WorkingSet64,
            GC.GetTotalMemory(forceFullCollection: false),
            memoryLimitBytes > 0 ? memoryLimitBytes : null,
            DateTimeOffset.UtcNow));
    }
}
