using System.Management.Automation;
using Migration4Dataverse.Engine.Connectors;

namespace Migration4Dataverse.Engine.PowerShell;

[Cmdlet(VerbsCommon.New, "M4dMigrationBinaryValue", DefaultParameterSetName = Base64ParameterSet)]
[OutputType(typeof(MigrationBinaryValue))]
public sealed class NewMigrationBinaryValueCommand : PSCmdlet
{
    private const string Base64ParameterSet = "Base64";
    private const string BytesParameterSet = "Bytes";
    private const string FileParameterSet = "File";
    private const string StreamParameterSet = "Stream";

    [Parameter(Mandatory = true, ParameterSetName = Base64ParameterSet)]
    public string Base64 { get; set; } = string.Empty;

    [Parameter(Mandatory = true, ParameterSetName = BytesParameterSet)]
    public byte[] Bytes { get; set; } = [];

    [Parameter(Mandatory = true, ParameterSetName = FileParameterSet)]
    [ValidateNotNullOrEmpty]
    public string FilePath { get; set; } = string.Empty;

    [Parameter(Mandatory = true, ParameterSetName = StreamParameterSet)]
    [ValidateNotNull]
    public Stream InputStream { get; set; } = Stream.Null;

    [Parameter(Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string StagingRoot { get; set; } = string.Empty;

    [Parameter]
    public string? FileName { get; set; }

    [Parameter]
    public string? ContentType { get; set; }

    protected override void ProcessRecord()
    {
        var binaryValue = ParameterSetName switch
        {
            Base64ParameterSet => MigrationBinaryValue.FromBase64(
                Base64,
                StagingRoot,
                FileName,
                ContentType),
            BytesParameterSet => MigrationBinaryValue.FromBytes(
                Bytes,
                StagingRoot,
                FileName,
                ContentType),
            FileParameterSet => MigrationBinaryValue.FromFile(
                FilePath,
                StagingRoot,
                FileName,
                ContentType),
            StreamParameterSet => MigrationBinaryValue.FromStream(
                InputStream,
                StagingRoot,
                FileName,
                ContentType),
            _ => throw new InvalidOperationException($"Unsupported parameter set '{ParameterSetName}'.")
        };
        WriteObject(binaryValue);
    }
}
