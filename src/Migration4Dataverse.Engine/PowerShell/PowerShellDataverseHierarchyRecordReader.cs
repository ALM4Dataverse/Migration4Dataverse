using System.Globalization;
using System.Text.Json;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.PowerShell;

public sealed class PowerShellDataverseHierarchyRecordReader : IHierarchyRecordReader
{
    private const string ReadRecordsScript = """
        param($QueryType, $Options, $Connection, $KeyProperty, $ParentKeyProperty, $IdProperty, $CountOnly, $BinaryStagingRoot, $RecordWriter)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'

        function Get-QueryOption {
            param([string]$Name)
            foreach ($key in $Options.Keys) {
                if ([string]::Equals([string]$key, $Name, [System.StringComparison]::OrdinalIgnoreCase)) {
                    return $Options[$key]
                }
            }
            return $null
        }

        function Get-DataverseFileColumnNames {
            $value = Get-QueryOption -Name 'FileColumns'
            if ($null -eq $value) {
                return @()
            }
            return @($value | ForEach-Object { [string]$_ } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        }

        function Add-QueryOptions {
            param(
                [System.Collections.IDictionary]$Parameters,
                [string[]]$Names
            )
            foreach ($name in $Names) {
                $value = Get-QueryOption -Name $name
                if ($null -ne $value) {
                    [void]($Parameters[$name] = $value)
                }
            }
        }

        function Read-DataverseFileColumn {
            param($Record, [string]$ColumnName)

            if ([string]::IsNullOrWhiteSpace([string]$BinaryStagingRoot)) {
                throw "Dataverse source step requires a binary staging directory to read file column '$ColumnName'."
            }
            if ([string]::IsNullOrWhiteSpace([string]$IdProperty)) {
                throw "Dataverse source step requires Source.Id to read file column '$ColumnName'."
            }
            $idValue = if ($Record -is [System.Collections.IDictionary]) {
                $Record[$IdProperty]
            }
            else {
                $property = $Record.PSObject.Properties[$IdProperty]
                if ($null -eq $property) { $null } else { $property.Value }
            }
            if ($null -eq $idValue -or [string]::IsNullOrWhiteSpace([string]$idValue)) {
                throw "Dataverse source row is missing record ID property '$IdProperty' required for file column '$ColumnName'."
            }
            $recordId = [guid]$idValue
            if ($recordId -eq [guid]::Empty) {
                throw "Dataverse source row has an empty record ID in property '$IdProperty'."
            }

            $tableName = [string](Get-QueryOption -Name 'TableName')
            $initialize = [Microsoft.Crm.Sdk.Messages.InitializeFileBlocksDownloadRequest]::new()
            $initialize.Target = [Microsoft.Xrm.Sdk.EntityReference]::new($tableName, $recordId)
            $initialize.FileAttributeName = $ColumnName
            $fileInfo = $Connection.Execute($initialize)
            $fileSize = [long]$fileInfo.FileSizeInBytes
            if ($fileSize -eq 0) {
                return $null
            }

            $blockLength = if ($fileInfo.IsChunkingSupported) {
                [long][Math]::Min(4MB, $fileSize)
            }
            else {
                if ($fileSize -gt [int]::MaxValue) {
                    throw "Dataverse file column '$ColumnName' on record '$recordId' does not support chunked downloads and exceeds the maximum block size."
                }
                $fileSize
            }
            $writer = [Migration4Dataverse.Engine.Connectors.MigrationBinaryValue]::CreateWriter(
                [string]$BinaryStagingRoot,
                [string]$fileInfo.FileName)
            try {
                $offset = [long]0
                while ($offset -lt $fileSize) {
                    $currentBlockLength = [long][Math]::Min($blockLength, $fileSize - $offset)
                    $request = [Microsoft.Xrm.Sdk.OrganizationRequest]::new('DownloadBlock')
                    $request.Parameters['FileContinuationToken'] = $fileInfo.FileContinuationToken
                    $request.Parameters['Offset'] = $offset
                    $request.Parameters['BlockLength'] = $currentBlockLength
                    $response = $Connection.Execute($request)
                    $data = $response.Results['Data'] -as [byte[]]
                    if ($null -eq $data -or $data.Length -eq 0) {
                        throw "Dataverse returned no data while downloading file column '$ColumnName' on record '$recordId' at offset $offset."
                    }
                    if ($data.Length -gt $currentBlockLength) {
                        throw "Dataverse returned an oversized block while downloading file column '$ColumnName' on record '$recordId'."
                    }
                    $writer.Stream.Write($data, 0, $data.Length)
                    $offset += $data.Length
                }
                if ($offset -ne $fileSize) {
                    throw "Dataverse returned $offset of $fileSize bytes for file column '$ColumnName' on record '$recordId'."
                }
                $writer.Complete()
            }
            finally {
                $writer.Dispose()
            }
        }

        function Add-DataverseFileColumns {
            param($Record)
            $fileColumns = @(Get-DataverseFileColumnNames)
            foreach ($columnName in $fileColumns) {
                $binaryValue = Read-DataverseFileColumn -Record $Record -ColumnName ([string]$columnName)
                if ($Record -is [System.Collections.IDictionary]) {
                    $Record[[string]$columnName] = $binaryValue
                }
                else {
                    $Record | Add-Member -NotePropertyName ([string]$columnName) `
                        -NotePropertyValue $binaryValue -Force
                }
            }
            return $Record
        }

        function Get-DataverseRecordValue {
            param($Record, [string]$Name)
            if ($Record -is [System.Collections.IDictionary]) {
                foreach ($key in $Record.Keys) {
                    if ([string]::Equals(
                        [string]$key,
                        $Name,
                        [System.StringComparison]::OrdinalIgnoreCase)) {
                        return $Record[$key]
                    }
                }
                return $null
            }
            $property = $Record.PSObject.Properties[$Name]
            if ($null -eq $property) { return $null }
            return $property.Value
        }

        function Add-DataverseAttachmentContent {
            param($Record)
            $tableName = [string](Get-QueryOption -Name 'TableName')
            $contentColumn = switch ($tableName.ToLowerInvariant()) {
                'annotation' { 'documentbody' }
                'activitymimeattachment' { 'body' }
                default { return $Record }
            }
            $content = Get-DataverseRecordValue -Record $Record -Name $contentColumn
            if ($content -isnot [string] -or [string]::IsNullOrWhiteSpace($content)) {
                return $Record
            }
            if ([string]::IsNullOrWhiteSpace([string]$BinaryStagingRoot)) {
                throw "Dataverse source step requires a binary staging directory to read attachment content from '$contentColumn'."
            }
            $fileName = [string](Get-DataverseRecordValue -Record $Record -Name 'filename')
            $contentType = [string](Get-DataverseRecordValue -Record $Record -Name 'mimetype')
            $binaryValue = [Migration4Dataverse.Engine.Connectors.MigrationBinaryValue]::FromBase64(
                $content,
                [string]$BinaryStagingRoot,
                $fileName,
                $contentType)
            if ($Record -is [System.Collections.IDictionary]) {
                foreach ($key in $Record.Keys) {
                    if ([string]::Equals(
                        [string]$key,
                        $contentColumn,
                        [System.StringComparison]::OrdinalIgnoreCase)) {
                        $Record[$key] = $binaryValue
                        break
                    }
                }
            }
            else {
                $Record | Add-Member -NotePropertyName $contentColumn -NotePropertyValue $binaryValue -Force
            }
            return $Record
        }

        function Write-QueryRecords {
            process {
                if ($CountOnly) {
                    $RecordWriter.Invoke([System.Management.Automation.PSObject]::AsPSObject($_))
                }
                else {
                    $record = $_
                    if ($QueryType -eq 'simple') {
                        $fileColumns = @(Get-DataverseFileColumnNames)
                        if ($fileColumns.Count -gt 0) {
                            $record = Add-DataverseFileColumns -Record $record
                        }
                        $record = Add-DataverseAttachmentContent -Record $record
                    }
                    if ([Migration4Dataverse.Engine.Connectors.MigrationBinaryValue]::ContainsBinaryValues($record)) {
                        if ([string]::IsNullOrWhiteSpace([string]$BinaryStagingRoot)) {
                            throw "Dataverse source step returned binary content without a staging directory."
                        }
                        $record = [Migration4Dataverse.Engine.Connectors.MigrationBinaryValue]::PrepareForTransport(
                            $record,
                            [string]$BinaryStagingRoot)
                    }
                    $json = ConvertTo-Json -InputObject $record -Depth 100 -Compress -ErrorAction Stop
                    $RecordWriter.Invoke([System.Management.Automation.PSObject]::AsPSObject($json))
                }
            }
        }

        switch ($QueryType) {
            'simple' {
                $parameters = @{
                    TableName = [string](Get-QueryOption -Name 'TableName')
                    Connection = $Connection
                    ErrorAction = 'Stop'
                }

                $orderByValue = Get-QueryOption -Name 'OrderBy'
                $requiredOrder = if ([string]::IsNullOrWhiteSpace([string]$ParentKeyProperty)) {
                    [string[]]@($KeyProperty)
                }
                else {
                    [string[]]@($ParentKeyProperty, $KeyProperty)
                }
                $requiredOrderText = [string]::Join("', then '", $requiredOrder)
                if ($null -eq $orderByValue) {
                    $parameters['OrderBy'] = $requiredOrder
                }
                else {
                    $orderBy = @($orderByValue | ForEach-Object { [string]$_ })
                    for ($index = 0; $index -lt $requiredOrder.Length; $index++) {
                        if ($orderBy.Length -le $index -or
                            $orderBy[$index].EndsWith('-', [System.StringComparison]::Ordinal) -or
                            -not [string]::Equals(
                                $orderBy[$index],
                                $requiredOrder[$index],
                                [System.StringComparison]::OrdinalIgnoreCase)) {
                            throw "OrderBy for a Dataverse source must begin with ascending '$requiredOrderText'."
                        }
                    }
                    $parameters['OrderBy'] = [string[]]$orderBy
                }
                Add-QueryOptions -Parameters $parameters -Names @(
                    'VerboseRecordCount', 'RecordCount', 'FilterValues', 'Criteria', 'Links',
                    'ExcludeFilterValues', 'ExcludeFilterOr', 'ActiveOnly', 'Id', 'Name',
                    'ExcludeId', 'Columns', 'ExcludeColumns', 'Top', 'PageSize',
                    'LookupValuesReturnName', 'IncludeSystemColumns',
                    'IncludeFileAndImageMetadataColumns', 'Unpublished'
                )
                if ($CountOnly) { $parameters['RecordCount'] = $true }
                Get-DataverseRecord @parameters | Write-QueryRecords
            }
            'fetchxml' {
                $parameters = @{
                    FetchXml = [string](Get-QueryOption -Name 'FetchXml')
                    Connection = $Connection
                    ErrorAction = 'Stop'
                }
                Add-QueryOptions -Parameters $parameters -Names @(
                    'VerboseRecordCount', 'RecordCount', 'LookupValuesReturnName', 'Unpublished'
                )
                if ($CountOnly) { $parameters['RecordCount'] = $true }
                Get-DataverseRecord @parameters | Write-QueryRecords
            }
            'cds4sql' {
                $parameters = @{
                    Sql = [string](Get-QueryOption -Name 'Sql')
                    Connection = $Connection
                    ErrorAction = 'Stop'
                }
                Add-QueryOptions -Parameters $parameters -Names @(
                    'UseTdsEndpoint', 'Timeout', 'Parameters', 'BatchSize',
                    'MaxDegreeOfParallelism', 'BypassCustomPluginExecution', 'UseBulkDelete',
                    'ReturnEntityReferenceAsGuid', 'UseLocalTimezone', 'DataSourceName'
                )
                if ($CountOnly) {
                    throw "Dataverse query type '$QueryType' does not support source count preflight."
                }
                Invoke-DataverseSql @parameters | Write-QueryRecords
            }
            default {
                throw "Unsupported Dataverse source query type '$QueryType'."
            }
        }
        """;

    private readonly HierarchySourceLevel _level;
    private readonly IAsyncEnumerable<System.Management.Automation.PSObject> _recordStream;
    private readonly string? _resumeKey;
    private bool _resumeKeyFound;
    private IAsyncEnumerator<System.Management.Automation.PSObject>? _records;
    private JsonElement? _lookahead;
    private bool _disposed;

    public PowerShellDataverseHierarchyRecordReader(
        PowerShellScriptExecutor executor,
        HierarchySourceLevel level,
        PowerShellDataverseSourceDefinition definition,
        string? resumeKey = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(definition);
        if (level.Type != "dataverse")
        {
            throw new ArgumentException(
                "A Dataverse reader requires a source level with type 'dataverse'.",
                nameof(level));
        }

        _level = level;
        _resumeKey = resumeKey;
        _resumeKeyFound = resumeKey is null;
        _recordStream = executor.InvokeStreamingAsync(
            ReadRecordsScript,
            [
                definition.QueryType,
                definition.Options,
                definition.Connection,
                level.KeyProperty,
                level.ParentKeyProperty,
                level.IdProperty,
                false,
                level.BinaryStagingRoot
            ]);
    }

    public static async Task<long> CountRecordsAsync(
            PowerShellScriptExecutor executor,
            string stepId,
            string keyProperty,
            PowerShellDataverseSourceDefinition definition,
            CancellationToken cancellationToken = default)
    {
            ArgumentNullException.ThrowIfNull(executor);
            ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
            ArgumentException.ThrowIfNullOrWhiteSpace(keyProperty);
            ArgumentNullException.ThrowIfNull(definition);
            if (definition.QueryType is not ("simple" or "fetchxml"))
            {
                throw new ArgumentException(
                    "Dataverse count preflight requires a Simple or FetchXml query.",
                    nameof(definition));
            }

            var output = new List<System.Management.Automation.PSObject>(1);
            await foreach (var value in executor.InvokeStreamingAsync(
                               ReadRecordsScript,
                               [
                                   definition.QueryType,
                                   definition.Options,
                                   definition.Connection,
                                   keyProperty,
                                   null,
                                   null,
                                   true,
                                   null
                               ],
                               cancellationToken).ConfigureAwait(false))
            {
                output.Add(value);
            }

            if (output.Count != 1)
            {
                throw new InvalidDataException(
                    $"Dataverse source '{stepId}' RecordCount returned {output.Count} count values; expected exactly one.");
            }

            var countValue = output[0].BaseObject;
            if (countValue is not (byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal))
            {
                throw new InvalidDataException(
                    $"Dataverse source '{stepId}' RecordCount must return a non-negative integer.");
            }

            decimal count;
            try
            {
                count = Convert.ToDecimal(countValue, CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (
                exception is InvalidCastException or FormatException or OverflowException)
            {
                throw new InvalidDataException(
                    $"Dataverse source '{stepId}' RecordCount must return a non-negative integer.",
                    exception);
            }

            if (count < 0 || decimal.Truncate(count) != count || count > long.MaxValue)
            {
                throw new InvalidDataException(
                    $"Dataverse source '{stepId}' RecordCount must return a non-negative integer.");
            }

            return (long)count;
    }

    public long RecordCount { get; private set; }

    public async ValueTask<JsonElement?> PeekAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lookahead.HasValue)
        {
            return _lookahead.Value;
        }

        _records ??= _recordStream.GetAsyncEnumerator(cancellationToken);
        while (await _records.MoveNextAsync().ConfigureAwait(false))
        {
            if (_records.Current.BaseObject is not string json)
            {
                throw new InvalidDataException(
                    $"Dataverse source for job '{_level.StepId}' returned a record that was not serialized as JSON.");
            }

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"Dataverse source for job '{_level.StepId}' returned a record that is not a JSON object.");
            }

            var record = document.RootElement.Clone();
            RecordCount++;
            if (!_resumeKeyFound)
            {
                var recordKey = GetKey(record);
                var comparison = string.Compare(recordKey, _resumeKey, StringComparison.OrdinalIgnoreCase);
                if (comparison < 0)
                {
                    continue;
                }
                if (comparison == 0)
                {
                    _resumeKeyFound = true;
                    continue;
                }

                throw new InvalidDataException(
                    $"Checkpoint key '{_resumeKey}' is missing from Dataverse source '{_level.StepId}'.");
            }

            _lookahead = record;
            return _lookahead;
        }

        if (!_resumeKeyFound)
        {
            throw new InvalidDataException(
                $"Checkpoint key '{_resumeKey}' is missing from Dataverse source '{_level.StepId}'.");
        }

        return null;
    }

    public JsonElement Take()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = _lookahead
            ?? throw new InvalidOperationException($"Dataverse source for job '{_level.StepId}' has no buffered record.");
        _lookahead = null;
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            if (_records is not null)
            {
                await _records.DisposeAsync().ConfigureAwait(false);
            }
            _disposed = true;
        }
    }

    private string GetKey(JsonElement record)
    {
        if (!record.TryGetProperty(_level.KeyProperty, out var keyElement))
        {
            throw new InvalidDataException(
                $"A source row for job '{_level.StepId}' is missing key property '{_level.KeyProperty}'.");
        }

        var key = keyElement.ValueKind switch
        {
            JsonValueKind.String => keyElement.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => keyElement.ToString(),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidDataException(
                $"A source row for job '{_level.StepId}' has an invalid key in property '{_level.KeyProperty}'.");
        }

        return key;
    }
}
