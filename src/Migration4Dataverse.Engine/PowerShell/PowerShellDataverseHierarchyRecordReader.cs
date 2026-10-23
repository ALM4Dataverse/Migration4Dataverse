using System.Globalization;
using System.Text.Json;
using Migration4Dataverse.Engine.Scheduling;

namespace Migration4Dataverse.Engine.PowerShell;

public sealed class PowerShellDataverseHierarchyRecordReader : IHierarchyRecordReader
{
    private const string ReadRecordsScript = """
        param($QueryType, $Options, $Connection, $KeyProperty, $ParentKeyProperty, $CountOnly, $RecordWriter)
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

        function Write-QueryRecords {
            process {
                if ($CountOnly) {
                    $RecordWriter.Invoke([System.Management.Automation.PSObject]::AsPSObject($_))
                }
                else {
                    $json = ConvertTo-Json -InputObject $_ -Depth 100 -Compress -ErrorAction Stop
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
                if ($CountOnly) { $parameters['TotalRecordCount'] = $true }
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
                if ($CountOnly) { $parameters['TotalRecordCount'] = $true }
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
    private IAsyncEnumerator<System.Management.Automation.PSObject>? _records;
    private JsonElement? _lookahead;
    private bool _disposed;

    public PowerShellDataverseHierarchyRecordReader(
        PowerShellScriptExecutor executor,
        HierarchySourceLevel level,
        PowerShellDataverseSourceDefinition definition)
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
        _recordStream = executor.InvokeStreamingAsync(
            ReadRecordsScript,
            [
                definition.QueryType,
                definition.Options,
                definition.Connection,
                level.KeyProperty,
                level.ParentKeyProperty,
                false
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
                                   true
                               ],
                               cancellationToken).ConfigureAwait(false))
            {
                output.Add(value);
            }

            if (output.Count != 1)
            {
                throw new InvalidDataException(
                    $"Dataverse source '{stepId}' TotalRecordCount returned {output.Count} count values; expected exactly one.");
            }

            var countValue = output[0].BaseObject;
            if (countValue is not (byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal))
            {
                throw new InvalidDataException(
                    $"Dataverse source '{stepId}' TotalRecordCount must return a non-negative integer.");
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
                    $"Dataverse source '{stepId}' TotalRecordCount must return a non-negative integer.",
                    exception);
            }

            if (count < 0 || decimal.Truncate(count) != count || count > long.MaxValue)
            {
                throw new InvalidDataException(
                    $"Dataverse source '{stepId}' TotalRecordCount must return a non-negative integer.");
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
        if (!await _records.MoveNextAsync().ConfigureAwait(false))
        {
            return null;
        }

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

        _lookahead = document.RootElement.Clone();
        RecordCount++;
        return _lookahead;
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
}
