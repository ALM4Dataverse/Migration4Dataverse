using System.Collections;
using System.Management.Automation;
using System.Text.Json;
using System.Text.RegularExpressions;
using Migration4Dataverse.Engine.Connectors;
using Migration4Dataverse.Engine.Scheduling;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.PowerShell;

internal sealed class MigrationStepSet : IDisposable
{
    private readonly IReadOnlyList<SemaphoreSlim> _semaphores;
    private readonly IReadOnlyList<System.Data.Common.DbConnection> _ownedConnections;
    private readonly IReadOnlyList<DataverseConnectionPool> _dataversePools;

    public MigrationStepSet(
        IReadOnlyList<MigrationStepDefinition> steps,
        IReadOnlyList<SemaphoreSlim> semaphores,
        IReadOnlyList<PipelineDataverseConnectionController> targetControllers,
        PipelineThroughputMeter throughputMeter,
        IReadOnlyList<System.Data.Common.DbConnection> ownedConnections,
        IReadOnlyList<DataverseConnectionPool> dataversePools)
    {
        Steps = steps;
        _semaphores = semaphores;
        TargetControllers = targetControllers;
        ThroughputMeter = throughputMeter;
        _ownedConnections = ownedConnections;
        _dataversePools = dataversePools;
    }

    public IReadOnlyList<MigrationStepDefinition> Steps { get; }

    public IReadOnlyList<PipelineDataverseConnectionController> TargetControllers { get; }

    public PipelineThroughputMeter ThroughputMeter { get; }

    public void Dispose()
    {
        foreach (var targetController in TargetControllers)
        {
            targetController.Dispose();
        }
        foreach (var pool in _dataversePools)
        {
            pool.Dispose();
        }
        foreach (var semaphore in _semaphores)
        {
            semaphore.Dispose();
        }
        foreach (var connection in _ownedConnections)
        {
            connection.Dispose();
        }
    }
}

internal static partial class PowerShellMigrationStepFactory
{
    private const string NodeHandlerScript = """
        param($batch, $sourceConnection, $targetConnection, $transformConnection, $context)
        Set-StrictMode -Version Latest
        $ErrorActionPreference = 'Stop'

        function ConvertTo-MigrationFields {
            param($Value, [string]$StepId)
            if ($Value -is [System.Collections.IDictionary]) {
                return $Value
            }
            if ($Value -is [pscustomobject]) {
                $fields = [ordered]@{}
                foreach ($property in $Value.PSObject.Properties) {
                    if ($property.IsGettable -and $property.MemberType -ne [System.Management.Automation.PSMemberTypes]::Method) {
                        $fields[$property.Name] = $property.Value
                    }
                }
                return $fields
            }
            throw "Transform step '$StepId' must emit a dictionary or custom object."
        }

        function ConvertTo-MigrationJson {
            param($Value, [int]$Depth = 100)
            if ([Migration4Dataverse.Engine.Connectors.MigrationBinaryValue]::ContainsBinaryValues($Value)) {
                if ([string]::IsNullOrWhiteSpace([string]$context.BinaryStagingRoot)) {
                    throw "Step '$($context.StepId)' cannot transport binary values without a staging directory."
                }
                $transportValue = [Migration4Dataverse.Engine.Connectors.MigrationBinaryValue]::PrepareForTransport(
                    $Value,
                    [string]$context.BinaryStagingRoot)
            }
            else {
                $transportValue = $Value
            }
            ConvertTo-Json -InputObject $transportValue -Depth $Depth -Compress -ErrorAction Stop
        }

        function Upload-DataverseFileColumn {
            param($Connection, [string]$TableName, [guid]$RecordId, [string]$ColumnName, $Value)

            if ($Value.Length -eq 0) {
                throw "Dataverse file column '$ColumnName' cannot be uploaded with empty content."
            }
            $fileName = if ([string]::IsNullOrWhiteSpace([string]$Value.FileName)) {
                "$ColumnName.bin"
            }
            else {
                [System.IO.Path]::GetFileName([string]$Value.FileName)
            }
            $initialize = [Microsoft.Crm.Sdk.Messages.InitializeFileBlocksUploadRequest]::new()
            $initialize.Target = [Microsoft.Xrm.Sdk.EntityReference]::new($TableName, $RecordId)
            $initialize.FileAttributeName = $ColumnName
            $initialize.FileName = $fileName
            $upload = $Connection.Execute($initialize)
            $blockIds = [System.Collections.Generic.List[string]]::new()
            $blockSize = 4MB
            $offset = [long]0
            $blockNumber = 0
            $stream = $Value.OpenRead()
            try {
                while ($offset -lt $Value.Length) {
                    $currentBlockLength = [int][Math]::Min($blockSize, $Value.Length - $offset)
                    $blockData = [byte[]]::new($currentBlockLength)
                    $read = 0
                    while ($read -lt $currentBlockLength) {
                        $count = $stream.Read($blockData, $read, $currentBlockLength - $read)
                        if ($count -eq 0) {
                            throw "Binary value '$fileName' ended before its recorded length of $($Value.Length) bytes."
                        }
                        $read += $count
                    }

                    $blockId = [Convert]::ToBase64String(
                        [System.Text.Encoding]::UTF8.GetBytes(('block-{0:D6}' -f $blockNumber)))
                    $blockIds.Add($blockId)
                    $request = [Microsoft.Xrm.Sdk.OrganizationRequest]::new('UploadBlock')
                    $request.Parameters['BlockId'] = $blockId
                    $request.Parameters['BlockData'] = $blockData
                    $request.Parameters['FileContinuationToken'] = $upload.FileContinuationToken
                    $null = $Connection.Execute($request)
                    $offset += $currentBlockLength
                    $blockNumber++
                }
            }
            finally {
                $stream.Dispose()
            }

            $commit = [Microsoft.Crm.Sdk.Messages.CommitFileBlocksUploadRequest]::new()
            $commit.FileContinuationToken = $upload.FileContinuationToken
            $commit.BlockList = [string[]]$blockIds.ToArray()
            $commit.FileName = $fileName
            $commit.MimeType = if ([string]::IsNullOrWhiteSpace([string]$Value.ContentType)) {
                $Value.DetectMimeType()
            }
            else {
                [string]$Value.ContentType
            }
            $null = $Connection.Execute($commit)
        }

        function Prepare-DataverseBinaryRecords {
            param([object[]]$Records, [string]$TableName)

            $preparedRecords = [object[]]::new($Records.Count)
            $fileValues = [System.Collections.Generic.List[object]]::new()
            $isAttachmentTable = $TableName -in @('annotation', 'activitymimeattachment')
            for ($recordIndex = 0; $recordIndex -lt $Records.Count; $recordIndex++) {
                $sourceRecord = ConvertTo-MigrationFields -Value $Records[$recordIndex] -StepId $context.StepId
                $record = [ordered]@{}
                foreach ($field in $sourceRecord.Keys) {
                    $record[[string]$field] = $sourceRecord[$field]
                }
                foreach ($field in @($record.Keys)) {
                    $value = $record[$field]
                    if ($value -isnot [Migration4Dataverse.Engine.Connectors.MigrationBinaryValue]) {
                        continue
                    }
                    if ($isAttachmentTable) {
                        $contentColumn = if ($TableName -eq 'annotation') { 'documentbody' } else { 'body' }
                        if (-not [string]::Equals(
                            [string]$field,
                            $contentColumn,
                            [System.StringComparison]::OrdinalIgnoreCase)) {
                            throw "Binary values on Dataverse attachment table '$TableName' must map to '$contentColumn'."
                        }
                        $record[$field] = $value.ToBase64String()
                        continue
                    }
                    if ($value.Representation -eq 'base64') {
                        $value = $value.DecodeBase64()
                    }
                    $fileValues.Add([pscustomobject]@{
                        RecordIndex = $recordIndex
                        ColumnName = [string]$field
                        Value = $value
                    })
                    $record.Remove($field)
                }
                $preparedRecords[$recordIndex] = $record
            }
            [pscustomobject]@{
                Records = $preparedRecords
                FileValues = $fileValues
            }
        }

        function ConvertTo-FileTargetBatch {
            param($Records, [string]$TargetPath, [string]$StepId, [string]$RunId, [string]$BatchId, [long]$Sequence)
            $recordsJson = ConvertTo-MigrationJson -Value $Records
            [Migration4Dataverse.Engine.Connectors.FileBatchTargetWriter]::WriteSingleFileTargetBatch(
                $TargetPath, $RunId, $StepId, $BatchId, $Sequence, $recordsJson)
        }

        function ConvertTo-ExternalBinaryTargetRecords {
            param([object[]]$Records, [long]$Sequence)

            $configuredColumns = [Migration4Dataverse.Engine.Connectors.BinaryFileReferenceSupport]::GetConfiguredBinaryColumns(
                $context.TargetFormatOptionsJson,
                $context.StepId)
            $configuredColumnSet = [System.Collections.Generic.HashSet[string]]::new(
                [System.StringComparer]::OrdinalIgnoreCase)
            foreach ($column in $configuredColumns) {
                [void]$configuredColumnSet.Add($column)
            }
            $referenceCallback = if ([string]::IsNullOrWhiteSpace($context.TargetBinaryReferenceText)) {
                $null
            }
            else {
                [scriptblock]::Create($context.TargetBinaryReferenceText)
            }
            $preparedRecords = [object[]]::new($Records.Count)
            for ($recordIndex = 0; $recordIndex -lt $Records.Count; $recordIndex++) {
                $record = $Records[$recordIndex]
                $fields = ConvertTo-MigrationFields -Value $record -StepId $context.StepId
                $columns = if ($configuredColumns.Count -gt 0) {
                    $configuredColumns
                }
                else {
                    @($fields.Keys | Where-Object {
                        $value = $fields[$_]
                        $value -is [Migration4Dataverse.Engine.Connectors.MigrationBinaryValue] -or
                            $value -is [byte[]] -or $value -is [System.IO.Stream]
                    })
                }
                foreach ($columnName in $columns) {
                    if (-not $fields.Contains($columnName)) {
                        throw "Binary target column '$columnName' is missing from record $recordIndex in step '$($context.StepId)'."
                    }
                    $value = $fields[$columnName]
                    if ($null -eq $value) {
                        continue
                    }
                    if ($value -isnot [Migration4Dataverse.Engine.Connectors.MigrationBinaryValue] -and
                        $value -isnot [byte[]] -and $value -isnot [System.IO.Stream]) {
                        throw "Binary target column '$columnName' in step '$($context.StepId)' must contain a MigrationBinaryValue, byte array, stream, or null."
                    }
                    if ($value -is [System.IO.Stream]) {
                        try {
                            $binaryValue = [Migration4Dataverse.Engine.Connectors.BinaryFileReferenceSupport]::NormalizeBinaryValue(
                                $value,
                                [string]$context.BinaryStagingRoot)
                        }
                        finally {
                            $value.Dispose()
                        }
                    }
                    else {
                        $binaryValue = [Migration4Dataverse.Engine.Connectors.BinaryFileReferenceSupport]::NormalizeBinaryValue(
                            $value,
                            [string]$context.BinaryStagingRoot)
                    }
                    $defaultRelativePath = [Migration4Dataverse.Engine.Connectors.BinaryFileReferenceSupport]::GetDefaultRelativePath(
                        $Sequence,
                        $recordIndex,
                        [string]$columnName,
                        $binaryValue)
                    $relativePath = $defaultRelativePath
                    if ($null -ne $referenceCallback) {
                        $binaryContext = [pscustomobject]@{
                            StepId = $context.StepId
                            RunId = $batch.RunId
                            Sequence = $Sequence
                            RecordIndex = $recordIndex
                            TargetPath = $context.TargetPath
                            StagingRoot = $context.BinaryStagingRoot
                        }
                        $callbackOutput = @(& $referenceCallback `
                            $fields `
                            ([string]$columnName) `
                            $binaryValue `
                            $defaultRelativePath `
                            $binaryContext)
                        if ($callbackOutput.Count -ne 1 -or $callbackOutput[0] -isnot [string]) {
                            throw "CreateBinaryReference for target step '$($context.StepId)' must return exactly one relative path string."
                        }
                        $relativePath = $callbackOutput[0]
                    }
                    $fields[$columnName] = [Migration4Dataverse.Engine.Connectors.BinaryFileReferenceSupport]::CreateTransportReference(
                        $binaryValue,
                        [string]$batch.RunId,
                        $relativePath)
                }

                foreach ($fieldName in $fields.Keys) {
                    $fieldValue = $fields[$fieldName]
                    if ($fieldValue -is [System.Collections.IDictionary] -and
                        ([System.Collections.IDictionary]$fieldValue).Contains(
                            [Migration4Dataverse.Engine.Connectors.BinaryFileReferenceSupport]::TransportMarker)) {
                        continue
                    }
                    $isDirectBinary = $fieldValue -is [Migration4Dataverse.Engine.Connectors.MigrationBinaryValue] -or
                        $fieldValue -is [byte[]] -or $fieldValue -is [System.IO.Stream]
                    if ($isDirectBinary -and
                        $configuredColumns.Count -gt 0 -and
                        -not $configuredColumnSet.Contains([string]$fieldName)) {
                        throw "Binary value in target column '$fieldName' is not listed in Options.BinaryColumns for step '$($context.StepId)'."
                    }
                    if (-not $isDirectBinary -and
                        [Migration4Dataverse.Engine.Connectors.MigrationBinaryValue]::ContainsBinaryValues($fieldValue)) {
                        throw "Binary values in target step '$($context.StepId)' must be mapped to top-level columns before sidecar output."
                    }
                }
                $preparedRecords[$recordIndex] = [pscustomobject]$fields
            }
            return ,$preparedRecords
        }

        $payload = ConvertFrom-Json -InputObject $batch.PayloadJson -AsHashtable -Depth 100 -ErrorAction Stop
        $payload = [Migration4Dataverse.Engine.Connectors.MigrationBinaryValue]::RestoreFromTransport(
            $payload,
            [string]$context.BinaryStagingRoot)
        if ($payload -isnot [System.Collections.IDictionary] -or -not $payload.Contains('Records')) {
            throw "Batch '$($batch.BatchId)' does not contain a Records array."
        }
        $inputRecords = @($payload.Records)
        $outputRecords = [System.Collections.Generic.List[object]]::new()
        $parentRecordIndices = [System.Collections.Generic.List[System.Collections.Generic.IReadOnlyList[int]]]::new()
        $targetOutputRecords = $null
        $filteredRows = 0

        switch ($context.Type) {
            'source' {
                $binaryColumns = [Migration4Dataverse.Engine.Connectors.BinaryFileReferenceSupport]::GetConfiguredBinaryColumns(
                    $context.SourceFormatOptionsJson,
                    $context.StepId)
                $binaryResolver = if ([string]::IsNullOrWhiteSpace($context.SourceBinaryResolverText)) {
                    $null
                }
                else {
                    [scriptblock]::Create($context.SourceBinaryResolverText)
                }
                for ($index = 0; $index -lt $inputRecords.Count; $index++) {
                    $wrapper = $inputRecords[$index]
                    if ($wrapper -isnot [System.Collections.IDictionary] -or
                        $wrapper.Source -isnot [System.Collections.IDictionary]) {
                        throw "Source step '$($context.StepId)' received an invalid connector row."
                    }
                    $record = [ordered]@{}
                    foreach ($field in $wrapper.Source.Keys) {
                        $record[[string]$field] = $wrapper.Source[$field]
                    }
                    foreach ($columnName in $binaryColumns) {
                        $sourceColumn = @($wrapper.Source.Keys | Where-Object {
                            [string]::Equals(
                                [string]$_,
                                $columnName,
                                [System.StringComparison]::OrdinalIgnoreCase)
                        }) | Select-Object -First 1
                        if ($null -eq $sourceColumn) {
                            throw "Binary source column '$columnName' is missing from record $index in step '$($context.StepId)'."
                        }
                        $reference = $wrapper.Source[$sourceColumn]
                        if ($null -eq $reference) {
                            continue
                        }
                        if ($null -ne $binaryResolver) {
                            $binaryContext = [pscustomobject]@{
                                StepId = $context.StepId
                                RunId = $batch.RunId
                                ColumnName = [string]$sourceColumn
                                SourcePath = $context.SourcePath
                                StagingRoot = $context.BinaryStagingRoot
                            }
                            $resolverOutput = @(& $binaryResolver `
                                $reference `
                                $wrapper.Source `
                                ([string]$sourceColumn) `
                                $binaryContext)
                            if ($resolverOutput.Count -ne 1) {
                                throw "ResolveBinaryReference for source step '$($context.StepId)' must return exactly one value."
                            }
                            $binaryValue = [Migration4Dataverse.Engine.Connectors.BinaryFileReferenceSupport]::FromResolverResult(
                                $resolverOutput[0],
                                [string]$context.SourcePath,
                                [string]$context.BinaryStagingRoot)
                        }
                        else {
                            if ($reference -isnot [string]) {
                                throw "Binary source column '$sourceColumn' in step '$($context.StepId)' must contain a relative file reference string."
                            }
                            $binaryValue = [Migration4Dataverse.Engine.Connectors.BinaryFileReferenceSupport]::FromFileReference(
                                $reference,
                                [string]$context.SourcePath,
                                [string]$context.BinaryStagingRoot)
                        }
                        $record[[string]$sourceColumn] = $binaryValue
                    }
                    if (-not [string]::IsNullOrWhiteSpace($context.IdProperty) -and
                        $wrapper.Source.Contains($context.IdProperty)) {
                        $record.Id = [guid]$wrapper.Source[$context.IdProperty]
                    }
                    $outputRecords.Add([pscustomobject]$record)
                    $parentRecordIndices.Add([int[]]@($index))
                }
            }
            'transform' {
                $hasDatabaseTransform = $context.TransformType -in @('sqlite', 'duckdb')
                $hasConnectedTransform = $hasDatabaseTransform -or $context.TransformType -eq 'dataverse'
                $transformConnectionLease = $null
                $openedTransformConnection = $false
                if ($hasDatabaseTransform -and [string]::IsNullOrWhiteSpace($context.TransformSql)) {
                    $transformConnectionLease = [Migration4Dataverse.Engine.Connectors.DatabaseConnectionGate]::Acquire(
                        $transformConnection)
                }
                try {
                    if ($hasDatabaseTransform -and
                        [string]::IsNullOrWhiteSpace($context.TransformSql) -and
                        $transformConnection.State -ne [System.Data.ConnectionState]::Open) {
                        $transformConnection.Open()
                        $openedTransformConnection = $true
                    }
                    if ($context.TransformMode -eq 'whole_dataset') {
                        for ($index = 0; $index -lt $inputRecords.Count; $index++) {
                            $record = $inputRecords[$index]
                            if ($record -is [System.Collections.IDictionary] -and
                                $record.Contains('Source')) {
                                $record = $record.Source
                            }
                            $fields = ConvertTo-MigrationFields -Value $record -StepId $context.StepId
                            $outputRecords.Add([pscustomobject]$fields)
                            $parentRecordIndices.Add([int[]]@($index))
                        }
                    }
                    elseif (-not [string]::IsNullOrWhiteSpace($context.TransformSql)) {
                        $recordsJson = ConvertTo-MigrationJson -Value $inputRecords
                        if ($context.TransformMode -eq 'per_batch') {
                            if ($context.TransformType -eq 'sqlite') {
                                $outputsJson = [Migration4Dataverse.Engine.Connectors.PreparedDatabaseTransformRunner]::ExecuteSqliteBatch(
                                    $transformConnection,
                                    $context.TransformSql,
                                    $context.TransformParametersJson,
                                    $recordsJson,
                                    $context.TransformCommandTimeoutSeconds)
                            }
                            else {
                                $outputsJson = [Migration4Dataverse.Engine.Connectors.PreparedDatabaseTransformRunner]::ExecuteDuckDbBatch(
                                    $transformConnection,
                                    $context.TransformSql,
                                    $context.TransformParametersJson,
                                    $recordsJson,
                                    $context.TransformCommandTimeoutSeconds)
                            }
                        }
                        else {
                            $outputsJson = [Migration4Dataverse.Engine.Connectors.PreparedDatabaseTransformRunner]::ExecutePreparedBatch(
                                $transformConnection,
                                $context.TransformType,
                                $context.TransformSql,
                                $context.TransformParametersJson,
                                $recordsJson)
                        }
                        $preparedOutputs = ConvertFrom-Json -InputObject $outputsJson -AsHashtable -Depth 100 -ErrorAction Stop
                        foreach ($preparedOutput in $preparedOutputs) {
                            if ($preparedOutput -is [System.Collections.IDictionary] -and
                                $preparedOutput.Contains('ParentIndices')) {
                                $parentIndices = [int[]]@($preparedOutput['ParentIndices'])
                            }
                            else {
                                $parentIndices = [int[]]@([int]$preparedOutput.ParentIndex)
                            }
                            $parentIndex = $parentIndices[0]
                            $fields = ConvertTo-MigrationFields -Value $preparedOutput.Record -StepId $context.StepId
                            if (-not $fields.Contains('Id') -and
                                $inputRecords[$parentIndex] -is [System.Collections.IDictionary] -and
                                $inputRecords[$parentIndex].Contains('Id')) {
                                $fields = [ordered]@{ Id = $inputRecords[$parentIndex].Id } + $fields
                            }
                            $outputRecords.Add([pscustomobject]$fields)
                            $parentRecordIndices.Add([int[]]@($parentIndices))
                        }
                    }
                    else {
                        $script = [scriptblock]::Create($context.ScriptText)
                        $binaryContext = [pscustomobject]@{
                            StepId = $context.StepId
                            RunId = $batch.RunId
                            StagingRoot = $context.BinaryStagingRoot
                        }
                        for ($index = 0; $index -lt $inputRecords.Count; $index++) {
                            try {
                                $values = if ($hasConnectedTransform) {
                                    @(& $script $inputRecords[$index] $transformConnection $binaryContext)
                                }
                                else {
                                    @(& $script $inputRecords[$index] $binaryContext)
                                }
                            }
                            catch {
                                throw "Transform step '$($context.StepId)' failed: $($_.Exception.Message)"
                            }
                            foreach ($value in $values) {
                                $fields = ConvertTo-MigrationFields -Value $value -StepId $context.StepId
                                if (-not $fields.Contains('Id') -and
                                    $inputRecords[$index] -is [System.Collections.IDictionary] -and
                                    $inputRecords[$index].Contains('Id')) {
                                    $fields = [ordered]@{ Id = $inputRecords[$index].Id } + $fields
                                }
                                $outputRecords.Add([pscustomobject]$fields)
                                $parentRecordIndices.Add([int[]]@($index))
                            }
                        }
                    }
                }
                finally {
                    try {
                        if ($openedTransformConnection) {
                            $transformConnection.Close()
                        }
                    }
                    finally {
                        if ($null -ne $transformConnectionLease) {
                            $transformConnectionLease.Dispose()
                        }
                    }
                }
            }
            'filter' {
                $script = [scriptblock]::Create($context.ScriptText)
                for ($index = 0; $index -lt $inputRecords.Count; $index++) {
                    try {
                        $values = @(& $script $inputRecords[$index])
                    }
                    catch {
                        throw "Filter step '$($context.StepId)' failed: $($_.Exception.Message)"
                    }
                    $keep = $false
                    if ($values.Count -eq 1) {
                        $keep = [System.Management.Automation.LanguagePrimitives]::IsTrue($values[0])
                    }
                    elseif ($values.Count -gt 1) {
                        $keep = [System.Management.Automation.LanguagePrimitives]::IsTrue($values)
                    }
                    if ($keep) {
                        $outputRecords.Add($inputRecords[$index])
                        $parentRecordIndices.Add([int[]]@($index))
                    }
                    else {
                        $filteredRows++
                    }
                }
            }
            'append' {
                foreach ($index in 0..($inputRecords.Count - 1)) {
                    $outputRecords.Add($inputRecords[$index])
                    $parentRecordIndices.Add([int[]]@($index))
                }
            }
            'join' {
                for ($index = 0; $index -lt $inputRecords.Count; $index++) {
                    $outputRecords.Add($inputRecords[$index])
                    $parentRecordIndices.Add([int[]]@($index))
                }
            }
            'sort' {
                throw "Sort step '$($context.StepId)' requires stream-wide materialization and is not supported by this execution path."
            }
            'merge' {
                throw "Merge step '$($context.StepId)' requires coordinated sorted inputs and is not supported by this execution path."
            }
            'target' {
                $outputRecords.AddRange([object[]]$inputRecords)
                for ($index = 0; $index -lt $inputRecords.Count; $index++) {
                    $parentRecordIndices.Add([int[]]@($index))
                }
            }
            default {
                throw "Unsupported migration step type '$($context.Type)'."
            }
        }

        if ($context.Type -eq 'transform' -and $inputRecords.Count -gt 0) {
            $contributingInputIndices = [System.Collections.Generic.HashSet[int]]::new()
            foreach ($indices in $parentRecordIndices) {
                foreach ($index in $indices) {
                    [void]$contributingInputIndices.Add($index)
                }
            }
            $filteredRows += $inputRecords.Count - $contributingInputIndices.Count
        }

        if ($context.Type -eq 'target' -and $outputRecords.Count -gt 0) {
            $records = $outputRecords.ToArray()
            if ($context.TargetType -eq 'file' -and
                $context.TargetFormatType -in @('csv', 'jsonl', 'parquet')) {
                $records = ConvertTo-ExternalBinaryTargetRecords `
                    -Records $records `
                    -Sequence $batch.Sequence
            }
            $targetExecutionLease = $null
            $targetDataverseLease = $null
            if (-not $context.WhatIf -and $context.TargetType -in @('dataverse', 'custom', 'sqlite', 'duckdb', 'sqlserver', 'postgresql', 'mysql', 'oracle', 'azureblob', 's3', 'adlsgen2', 'gcs', 'mongodb')) {
                if ($null -ne $context.TargetController) {
                    $targetExecutionLease = $context.ExecutionLease
                    if ($null -eq $targetExecutionLease) {
                        throw "Target step '$($context.StepId)' reached execution without its Dataverse connection lease."
                    }
                    $targetDataverseLease = $targetExecutionLease
                }
                elseif ($null -eq $context.ExecutionLease) {
                    throw "Target step '$($context.StepId)' reached execution without its concurrency lease."
                }
            }
            try {
                if (-not $context.WhatIf) {
                    switch ($context.TargetType) {
                    'dataverse' {
                        $targetOptions = ConvertFrom-Json -InputObject $context.TargetOptionsJson `
                            -AsHashtable -Depth 100 -ErrorAction Stop
                        if ($null -eq $targetOptions) {
                            $targetOptions = @{}
                        }
                        if ($targetOptions -isnot [System.Collections.IDictionary]) {
                            throw "Target Options for step '$($context.StepId)' must be a hashtable or dictionary."
                        }
                        $originalRecords = $records
                        $binaryPreparation = Prepare-DataverseBinaryRecords `
                            -Records $records -TableName $context.TableName
                        $records = [object[]]$binaryPreparation.Records
                        $dataverseFileValues = $binaryPreparation.FileValues
                        $targetParameters = @{
                            TableName = $context.TableName
                            Connection = $targetConnection
                            BatchSize = if ($null -ne $targetDataverseLease) {
                                $targetDataverseLease.BatchSize
                            }
                            else {
                                $context.BatchSize
                            }
                            MaxDegreeOfParallelism = 1
                            Retries = if ($context.DisableDataverseRetriesForDiagnostics) { 0 } else { 5 }
                            InitialRetryDelay = 2
                            ErrorAction = 'Stop'
                        }
                        $targetOperation = $context.TargetOperation
                        if ($targetOperation -eq 'Create') {
                            $targetParameters.CreateOnly = $true
                        }
                        elseif ($targetOperation -eq 'Update') {
                            $targetParameters.NoCreate = $true
                        }
                        if ($targetOperation -eq 'Upsert' -and -not $targetOptions.Contains('MatchOn')) {
                            $targetParameters.Upsert = $true
                        }
                        elseif ($targetOptions.Contains('MatchOn') -and $targetOperation -notin @('Create', 'Delete')) {
                            $targetParameters.MatchOn = [string[][]]$targetOptions.MatchOn
                        }
                        if ($targetOptions.Contains('LookupColumns')) {
                            $targetParameters.LookupColumns = $targetOptions.LookupColumns
                        }
                        if ($targetOperation -ne 'Delete' -and
                            (-not $targetOptions.Contains('NoLookup') -or [bool]$targetOptions.NoLookup)) {
                            $targetParameters.NoLookup = $true
                        }
                        if ($targetOptions.Contains('BypassBusinessLogicExecution')) {
                            $targetParameters.BypassBusinessLogicExecution =
                                $targetOptions.BypassBusinessLogicExecution
                        }
                        if ($targetOptions.Contains('BypassBusinessLogicExecutionStepIds')) {
                            $targetParameters.BypassBusinessLogicExecutionStepIds =
                                $targetOptions.BypassBusinessLogicExecutionStepIds
                        }
                        $records = [object[]][Migration4Dataverse.Engine.PowerShell.DataverseLookupReferenceResolver]::Resolve($records)

                        if ($targetOperation -eq 'Delete' -and $context.JoinInputConsumers.Count -gt 0) {
                            throw "Dataverse Delete target step '$($context.StepId)' cannot provide parent records to Join steps."
                        }
                        if ($targetOperation -eq 'Delete' -and $dataverseFileValues.Count -gt 0) {
                            throw "Dataverse Delete target step '$($context.StepId)' cannot write file column content."
                        }
                        if ($context.JoinInputConsumers.Count -gt 0 -or $dataverseFileValues.Count -gt 0) {
                            if ($context.JoinInputConsumers.Count -gt 0 -and $null -eq $context.Store) {
                                throw "Target step '$($context.StepId)' must have a migration store to stage Join input records."
                            }
                            $targetParameters.PassThru = $true
                            $writtenRecords = @($records | Set-DataverseRecord @targetParameters)
                            if ($writtenRecords.Count -ne $records.Count) {
                                throw "Set-DataverseRecord -PassThru returned $($writtenRecords.Count) records for $($records.Count) inputs in step '$($context.StepId)'."
                            }

                            foreach ($fileValue in $dataverseFileValues) {
                                $writtenRecord = $writtenRecords[[int]$fileValue.RecordIndex]
                                $idProperty = $writtenRecord.PSObject.Properties['Id']
                                if ($null -eq $idProperty -or $null -eq $idProperty.Value) {
                                    throw "Set-DataverseRecord -PassThru did not return a primary-key Id for file upload in step '$($context.StepId)'."
                                }
                                $recordId = [guid]$idProperty.Value
                                if ($recordId -eq [guid]::Empty) {
                                    throw "Set-DataverseRecord -PassThru returned an empty Id for file upload in step '$($context.StepId)'."
                                }
                                Upload-DataverseFileColumn `
                                    -Connection $targetConnection `
                                    -TableName $context.TableName `
                                    -RecordId $recordId `
                                    -ColumnName ([string]$fileValue.ColumnName) `
                                    -Value $fileValue.Value
                            }

                            if ($context.JoinInputConsumers.Count -gt 0) {
                                $targetOutputRecords = [System.Collections.Generic.List[object]]::new()
                                for ($recordIndex = 0; $recordIndex -lt $writtenRecords.Count; $recordIndex++) {
                                    $writtenRecord = $writtenRecords[$recordIndex]
                                    $idProperty = $writtenRecord.PSObject.Properties['Id']
                                    if ($null -eq $idProperty -or $null -eq $idProperty.Value) {
                                        throw "Set-DataverseRecord -PassThru did not return a primary-key Id for step '$($context.StepId)'."
                                    }
                                    $recordId = [guid]$idProperty.Value
                                    if ($recordId -eq [guid]::Empty) {
                                        throw "Set-DataverseRecord -PassThru returned an empty Id for step '$($context.StepId)'."
                                    }

                                    $recordFields = [ordered]@{}
                                    foreach ($field in $originalRecords[$recordIndex].Keys) {
                                        $recordFields[[string]$field] = $originalRecords[$recordIndex][$field]
                                    }
                                    foreach ($property in $writtenRecord.PSObject.Properties) {
                                        if ($property.IsGettable -and
                                            $property.MemberType -ne [System.Management.Automation.PSMemberTypes]::Method) {
                                            $recordFields[$property.Name] = $property.Value
                                        }
                                    }
                                    $recordFields.Id = $recordId
                                    $targetOutputRecords.Add([pscustomobject]$recordFields)
                                }
                            }
                        }
                        elseif ($targetOperation -eq 'Delete') {
                            $deleteParameters = @{
                                TableName = $context.TableName
                                Connection = $targetConnection
                                BatchSize = if ($null -ne $targetDataverseLease) {
                                    $targetDataverseLease.BatchSize
                                }
                                else {
                                    $context.BatchSize
                                }
                                MaxDegreeOfParallelism = 1
                                Retries = if ($context.DisableDataverseRetriesForDiagnostics) { 0 } else { 5 }
                                InitialRetryDelay = 2
                                ErrorAction = 'Stop'
                            }
                            if ($targetOptions.Contains('MatchOn')) {
                                $deleteParameters.MatchOn = [string[][]]$targetOptions.MatchOn
                            }
                            $null = $records | Remove-DataverseRecord @deleteParameters
                        }
                        else {
                            $null = $records | Set-DataverseRecord @targetParameters
                        }
                    }
                    'file' {
                        ConvertTo-FileTargetBatch -Records $records -TargetPath $context.TargetPath `
                            -StepId $context.StepId -RunId $batch.RunId `
                            -BatchId $batch.BatchId -Sequence $batch.Sequence
                    }
                    'sqlite' {
                        $recordsJson = ConvertTo-MigrationJson -Value $records
                        [Migration4Dataverse.Engine.Connectors.DatabaseBatchTargetWriter]::WriteBatch(
                            $targetConnection, 'sqlite', $context.TableName, $context.StepId,
                            $batch.RunId, $batch.BatchId, $recordsJson, $context.TargetOptionsJson)
                    }
                    'duckdb' {
                        $recordsJson = ConvertTo-MigrationJson -Value $records
                        [Migration4Dataverse.Engine.Connectors.DatabaseBatchTargetWriter]::WriteBatch(
                            $targetConnection, 'duckdb', $context.TableName, $context.StepId,
                            $batch.RunId, $batch.BatchId, $recordsJson, $context.TargetOptionsJson)
                    }
                    'sqlserver' {
                        $recordsJson = ConvertTo-MigrationJson -Value $records
                        [Migration4Dataverse.Engine.Connectors.DatabaseBatchTargetWriter]::WriteBatch(
                            $targetConnection, 'sqlserver', $context.TableName, $context.StepId,
                            $batch.RunId, $batch.BatchId, $recordsJson, $context.TargetOptionsJson)
                    }
                    'postgresql' {
                        $recordsJson = ConvertTo-MigrationJson -Value $records
                        [Migration4Dataverse.Engine.Connectors.DatabaseBatchTargetWriter]::WriteBatch(
                            $targetConnection, 'postgresql', $context.TableName, $context.StepId,
                            $batch.RunId, $batch.BatchId, $recordsJson, $context.TargetOptionsJson)
                    }
                    'mysql' {
                        $recordsJson = ConvertTo-MigrationJson -Value $records
                        [Migration4Dataverse.Engine.Connectors.DatabaseBatchTargetWriter]::WriteBatch(
                            $targetConnection, 'mysql', $context.TableName, $context.StepId,
                            $batch.RunId, $batch.BatchId, $recordsJson, $context.TargetOptionsJson)
                    }
                    'oracle' {
                        $recordsJson = ConvertTo-MigrationJson -Value $records
                        [Migration4Dataverse.Engine.Connectors.DatabaseBatchTargetWriter]::WriteBatch(
                            $targetConnection, 'oracle', $context.TableName, $context.StepId,
                            $batch.RunId, $batch.BatchId, $recordsJson, $context.TargetOptionsJson)
                    }
                    'azureblob' {
                        $recordsJson = ConvertTo-MigrationJson -Value $records
                        [Migration4Dataverse.Engine.Connectors.ObjectStorageTargetWriter]::WriteBatch(
                            $targetConnection, 'azureblob', $context.TargetPath, $context.TargetFormatType,
                            [string[]]$context.TargetFormatExtensions, $context.StepId,
                            $batch.RunId, $batch.BatchId, $batch.Sequence, $recordsJson,
                            $context.TargetOptionsJson, $context.TargetFormatOptionsJson, $context.StagingRoot)
                    }
                    's3' {
                        $recordsJson = ConvertTo-MigrationJson -Value $records
                        [Migration4Dataverse.Engine.Connectors.ObjectStorageTargetWriter]::WriteBatch(
                            $targetConnection, 's3', $context.TargetPath, $context.TargetFormatType,
                            [string[]]$context.TargetFormatExtensions, $context.StepId,
                            $batch.RunId, $batch.BatchId, $batch.Sequence, $recordsJson,
                            $context.TargetOptionsJson, $context.TargetFormatOptionsJson, $context.StagingRoot)
                    }
                    'adlsgen2' {
                        $recordsJson = ConvertTo-MigrationJson -Value $records
                        [Migration4Dataverse.Engine.Connectors.ObjectStorageTargetWriter]::WriteBatch(
                            $targetConnection, 'adlsgen2', $context.TargetPath, $context.TargetFormatType,
                            [string[]]$context.TargetFormatExtensions, $context.StepId,
                            $batch.RunId, $batch.BatchId, $batch.Sequence, $recordsJson,
                            $context.TargetOptionsJson, $context.TargetFormatOptionsJson, $context.StagingRoot)
                    }
                    'gcs' {
                        $recordsJson = ConvertTo-MigrationJson -Value $records
                        [Migration4Dataverse.Engine.Connectors.ObjectStorageTargetWriter]::WriteBatch(
                            $targetConnection, 'gcs', $context.TargetPath, $context.TargetFormatType,
                            [string[]]$context.TargetFormatExtensions, $context.StepId,
                            $batch.RunId, $batch.BatchId, $batch.Sequence, $recordsJson,
                            $context.TargetOptionsJson, $context.TargetFormatOptionsJson, $context.StagingRoot)
                    }
                    'sharepoint' {
                        $recordsJson = ConvertTo-MigrationJson -Value $records
                        [Migration4Dataverse.Engine.Connectors.SharePointTargetWriter]::WriteBatch(
                            $targetConnection, $context.StepId, $batch.RunId, $batch.BatchId,
                            $batch.Sequence, $recordsJson, $context.TargetOptionsJson,
                            $context.StagingRoot)
                    }
                    'mongodb' {
                        $recordsJson = ConvertTo-Json -InputObject $records -Depth 100 -Compress -ErrorAction Stop
                        [Migration4Dataverse.Engine.Connectors.MongoTargetWriter]::WriteBatch(
                            $targetConnection, $context.TableName, $context.StepId,
                            $batch.RunId, $batch.BatchId, $recordsJson, $context.TargetOptionsJson)
                    }
                    'custom' {
                        $callback = [scriptblock]::Create($context.TargetCallbackText)
                        $stepContext = [pscustomobject]@{
                            StepId = $context.StepId
                            TableName = $context.TableName
                            RunId = $batch.RunId
                            BatchId = $batch.BatchId
                            Sequence = $batch.Sequence
                            TargetPath = $context.TargetPath
                            BinaryStagingRoot = $context.BinaryStagingRoot
                        }
                        $result = @(& $callback $targetConnection $records $stepContext)
                        if ($result.Count -ne 1 -or $result[0] -isnot [bool] -or -not $result[0]) {
                            throw "Custom target callback for step '$($context.StepId)' must return Boolean true."
                        }
                    }
                        default {
                            throw "Unsupported target connector type '$($context.TargetType)'."
                        }
                    }
                }
                if (-not $context.WhatIf) {
                    $context.PipelineThroughputMeter.Record([long]$records.Count)
                }
            }
            finally {
                if ($null -ne $targetExecutionLease) {
                    $context.TargetController.Complete($targetExecutionLease)
                }
            }
        }

        if ($context.JoinInputConsumers.Count -gt 0) {
            $joinInputRecords = [System.Collections.Generic.List[object]]::new()
            if ($null -ne $targetOutputRecords) {
                $joinInputRecords.AddRange($targetOutputRecords)
            }
            else {
                $joinInputRecords.AddRange($outputRecords)
            }
            foreach ($joinConsumer in $context.JoinInputConsumers) {
                $stagedRecords = [System.Collections.Generic.List[Migration4Dataverse.Engine.Storage.JoinInputRecordMapping]]::new()
                for ($index = 0; $index -lt $joinInputRecords.Count; $index++) {
                    $originRecordIndex = $index
                    if ($index -lt $parentRecordIndices.Count -and $parentRecordIndices[$index].Count -gt 0) {
                        $originRecordIndex = [int]$parentRecordIndices[$index][0]
                    }
                    $recordJson = ConvertTo-MigrationJson -Value $joinInputRecords[$index]
                    $stagedRecords.Add([Migration4Dataverse.Engine.Storage.JoinInputRecordMapping]::new(
                        $joinConsumer.JoinStepId,
                        $context.StepId,
                        [long]$batch.Sequence,
                        [int]($index + 1),
                        $recordJson,
                        [string]$batch.BatchId,
                        $originRecordIndex,
                        [string[]]$joinConsumer.KeyColumns))
                }
                $context.Store.SaveJoinInputRecords(
                    $batch.RunId,
                    [Migration4Dataverse.Engine.Storage.JoinInputRecordMapping[]]$stagedRecords.ToArray())
            }
            if ($null -ne $targetOutputRecords) {
                $outputRecords.Clear()
                $outputRecords.AddRange([object[]]$targetOutputRecords.ToArray())
            }
        }

        $debugStages = [System.Collections.Generic.List[Migration4Dataverse.Engine.Storage.MigrationDebugStage]]::new()
        if ($context.CaptureDebugData) {
            $recordsJson = ConvertTo-MigrationJson -Value $outputRecords.ToArray()
            if ($context.Type -eq 'source') {
                if ($payload.Contains('__LineageReferences')) {
                    $debugStages.Add([Migration4Dataverse.Engine.Storage.MigrationDebugStage]::new(
                        $context.StepId, $recordsJson, $parentRecordIndices))
                }
                elseif ($context.Type -eq 'join' -and $payload.Contains('__LineageReferences')) {
                    $referencesByRecord = [System.Collections.Generic.List[System.Collections.Generic.IReadOnlyList[Migration4Dataverse.Engine.Storage.MigrationDebugParentRecordReference]]]::new()
                    foreach ($recordReferences in $payload.__LineageReferences) {
                        $references = [System.Collections.Generic.List[Migration4Dataverse.Engine.Storage.MigrationDebugParentRecordReference]]::new()
                        foreach ($reference in $recordReferences) {
                            $references.Add([Migration4Dataverse.Engine.Storage.MigrationDebugParentRecordReference]::new(
                                [string]$reference.BatchId,
                                [int]$reference.RecordIndex))
                        }
                        $referencesByRecord.Add([Migration4Dataverse.Engine.Storage.MigrationDebugParentRecordReference[]]$references.ToArray())
                    }
                    $debugStages.Add([Migration4Dataverse.Engine.Storage.MigrationDebugStage]::new(
                        $context.StepId,
                        $recordsJson,
                        $null,
                        $referencesByRecord))
                }
                else {
                    $debugStages.Add([Migration4Dataverse.Engine.Storage.MigrationDebugStage]::new($context.StepId, $recordsJson))
                }
            }
            else {
                $debugStages.Add([Migration4Dataverse.Engine.Storage.MigrationDebugStage]::new(
                    $context.StepId, $recordsJson, $parentRecordIndices))
            }
        }

        $dependencyOutputs = [System.Collections.Generic.List[Migration4Dataverse.Engine.Scheduling.MigrationDependencyOutput]]::new()
        foreach ($child in $context.Children) {
            if ($child.IsRowLinked) {
                $childRows = [System.Collections.Generic.List[object]]::new()
                $lineageReferences = [System.Collections.Generic.List[object]]::new()
                for ($recordIndex = 0; $recordIndex -lt $inputRecords.Count; $recordIndex++) {
                    $wrapper = $inputRecords[$recordIndex]
                    if ($wrapper -isnot [System.Collections.IDictionary] -or
                        $wrapper.Children -isnot [System.Collections.IDictionary] -or
                        -not $wrapper.Children.Contains($child.StepId)) {
                        continue
                    }
                    foreach ($childRow in $wrapper.Children[$child.StepId]) {
                        $childRows.Add($childRow)
                        $lineageReferences.Add([object[]]@([ordered]@{
                            BatchId = $batch.BatchId
                            RecordIndex = $recordIndex
                        }))
                    }
                }

                for ($offset = 0; $offset -lt $childRows.Count; $offset += $child.BatchSize) {
                    $count = [Math]::Min($child.BatchSize, $childRows.Count - $offset)
                    $chunk = [object[]]$childRows.GetRange($offset, $count).ToArray()
                    $lineageChunk = [object[]]$lineageReferences.GetRange($offset, $count).ToArray()
                    $childPayload = ConvertTo-MigrationJson -Value ([ordered]@{
                        Records = $chunk
                        __LineageReferences = $lineageChunk
                    })
                    $dependencyOutputs.Add([Migration4Dataverse.Engine.Scheduling.MigrationDependencyOutput]::new(
                        $child.StepId, $childPayload))
                }
                continue
            }

            for ($offset = 0; $offset -lt $outputRecords.Count; $offset += $child.BatchSize) {
                $count = [Math]::Min($child.BatchSize, $outputRecords.Count - $offset)
                $chunk = [object[]]$outputRecords.GetRange($offset, $count).ToArray()
                $lineageReferences = [System.Collections.Generic.List[object]]::new()
                for ($recordOffset = 0; $recordOffset -lt $count; $recordOffset++) {
                    $lineageReferences.Add([object[]]@([ordered]@{
                        BatchId = $batch.BatchId
                        RecordIndex = $offset + $recordOffset
                    }))
                }
                $childPayload = ConvertTo-MigrationJson -Value ([ordered]@{
                    Records = $chunk
                    __LineageReferences = $lineageReferences.ToArray()
                })
                $dependencyOutputs.Add([Migration4Dataverse.Engine.Scheduling.MigrationDependencyOutput]::new(
                    $child.StepId, $childPayload))
            }
        }

        $outcomeData = if ($context.WhatIf -and $context.Type -eq 'target') {
            [ordered]@{ Succeeded = 0; Failed = 0; Previewed = $outputRecords.Count }
        }
        else {
            [ordered]@{ Succeeded = $outputRecords.Count; Failed = 0 }
        }
        if ($context.TargetType -eq 'dataverse' -and $null -ne $context.TargetController) {
            $outcomeData.SpecialistInfo = $context.TargetController.GetDashboardState($context.StepId)
        }
        $outcomes = ConvertTo-Json -InputObject $outcomeData -Depth 10 -Compress -ErrorAction Stop
        [Migration4Dataverse.Engine.Scheduling.MigrationBatchResult]::new(
            $outcomes,
            $false,
            [Migration4Dataverse.Engine.Scheduling.MigrationDependencyOutput[]]$dependencyOutputs.ToArray(),
            $null,
            [Migration4Dataverse.Engine.Storage.MigrationDebugStage[]]$debugStages.ToArray(),
            [long]$filteredRows)
        """;

    private static IReadOnlyList<GraphDependencyInput> ParseDependencies(object? value, string stepId)
    {
        if (value is null)
        {
            return Array.Empty<GraphDependencyInput>();
        }

        var entries = value is string || GetMap(value, "DependsOn entry", required: false) is not null
            ? new[] { value }
            : LanguagePrimitives.GetEnumerable(value)?.Cast<object?>().ToArray() ?? new[] { value };
        var dependencies = new List<GraphDependencyInput>();
        foreach (var rawEntry in entries)
        {
            if (Unwrap(rawEntry) is string name)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(name);
                dependencies.Add(new GraphDependencyInput(name, null, 0));
                continue;
            }

            throw new ArgumentException(
                $"DependsOn on step '{stepId}' accepts only step IDs for completion barriers. " +
                "Declare keyed relationships in Inputs with explicit Child and Parent key columns.");
        }

        return dependencies;
    }

    private static string ValidateDataverseSourceOptions(IDictionary? options, string stepId)
    {
        if (options is null)
        {
            throw new ArgumentException(
                $"Dataverse source step '{stepId}' must declare Options with QueryType and query parameters.");
        }

        var queryType = GetRequiredString(options, "QueryType");
        var normalizedType = queryType.Trim().ToLowerInvariant();
        string[] allowedOptions = normalizedType switch
        {
            "simple" =>
            [
                "QueryType", "TableName", "VerboseRecordCount", "RecordCount", "FilterValues", "Criteria",
                "Links", "ExcludeFilterValues", "ExcludeFilterOr", "ActiveOnly", "Id", "Name", "ExcludeId",
                "Columns", "ExcludeColumns", "OrderBy", "Top", "PageSize", "LookupValuesReturnName",
                "IncludeSystemColumns", "IncludeFileAndImageMetadataColumns", "Unpublished", "FileColumns"
            ],
            "fetchxml" =>
            [
                "QueryType", "FetchXml", "VerboseRecordCount", "RecordCount", "LookupValuesReturnName", "Unpublished"
            ],
            "cds4sql" =>
            [
                "QueryType", "Sql", "UseTdsEndpoint", "Timeout", "Parameters", "BatchSize",
                "MaxDegreeOfParallelism", "BypassCustomPluginExecution", "UseBulkDelete",
                "ReturnEntityReferenceAsGuid", "UseLocalTimezone", "DataSourceName"
            ],
            _ => throw new ArgumentException(
                $"Dataverse source step '{stepId}' has unsupported QueryType '{queryType}'. " +
                "Use Simple, FetchXml, or Cds4Sql.")
        };
        var allowed = new HashSet<string>(allowedOptions, StringComparer.OrdinalIgnoreCase);
        foreach (var key in options.Keys)
        {
            var optionName = key?.ToString() ?? string.Empty;
            if (!allowed.Contains(optionName))
            {
                throw new ArgumentException(
                    $"Dataverse source step '{stepId}' option '{optionName}' is not supported for query type '{queryType}'.");
            }
        }

        if (normalizedType != "simple" && GetProperty(options, "FileColumns") is not null)
        {
            throw new ArgumentException(
                $"Dataverse source step '{stepId}' supports Options.FileColumns only with QueryType 'Simple'.");
        }

        if (normalizedType == "simple")
        {
            _ = GetRequiredString(options, "TableName");
            if (GetProperty(options, "RecordCount") is { } recordCount &&
                LanguagePrimitives.ConvertTo<bool>(Unwrap(recordCount)))
            {
                throw new ArgumentException(
                    $"Dataverse source step '{stepId}' cannot set RecordCount because it replaces the record output with a count.");
            }
        }
        else if (normalizedType == "fetchxml")
        {
            _ = GetRequiredString(options, "FetchXml");
            if (GetProperty(options, "RecordCount") is { } recordCount &&
                LanguagePrimitives.ConvertTo<bool>(Unwrap(recordCount)))
            {
                throw new ArgumentException(
                    $"Dataverse source step '{stepId}' cannot set RecordCount because it replaces the record output with a count.");
            }
        }
        else
        {
            var sql = GetRequiredString(options, "Sql");
            ValidateReadOnlySql(sql, stepId);
        }

        return normalizedType;
    }

    private static void ValidateReadOnlySql(
        string sql,
        string stepId,
        string sourceDescription = "Dataverse SQL source step")
    {
        var remaining = sql.TrimStart();
        while (remaining.StartsWith("--", StringComparison.Ordinal) ||
               remaining.StartsWith("/*", StringComparison.Ordinal))
        {
            if (remaining.StartsWith("--", StringComparison.Ordinal))
            {
                var lineEnd = remaining.IndexOfAny(['\r', '\n']);
                remaining = lineEnd < 0 ? string.Empty : remaining[(lineEnd + 1)..].TrimStart();
                continue;
            }

            var commentEnd = remaining.IndexOf("*/", StringComparison.Ordinal);
            if (commentEnd < 0)
            {
                throw new ArgumentException(
                    $"{sourceDescription} '{stepId}' contains an unterminated SQL comment.");
            }
            remaining = remaining[(commentEnd + 2)..].TrimStart();
        }

        if (!remaining.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
            (remaining.Length > "SELECT".Length &&
             !char.IsWhiteSpace(remaining["SELECT".Length]) &&
             remaining["SELECT".Length] is not ('(' or '/' or '-')))
        {
            throw new ArgumentException(
                $"{sourceDescription} '{stepId}' must use a read-only SELECT query.");
        }

        var inSingleQuotedString = false;
        var inDoubleQuotedIdentifier = false;
        var inBracketedIdentifier = false;
        var inLineComment = false;
        var inBlockComment = false;
        var semicolonIndex = -1;
        for (var index = 0; index < remaining.Length; index++)
        {
            var current = remaining[index];
            var next = index + 1 < remaining.Length ? remaining[index + 1] : '\0';
            if (inLineComment)
            {
                if (current is '\r' or '\n')
                {
                    inLineComment = false;
                }
                continue;
            }
            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    index++;
                }
                continue;
            }
            if (inSingleQuotedString)
            {
                if (current == '\'' && next == '\'')
                {
                    index++;
                }
                else if (current == '\'')
                {
                    inSingleQuotedString = false;
                }
                continue;
            }
            if (inDoubleQuotedIdentifier)
            {
                if (current == '"' && next == '"')
                {
                    index++;
                }
                else if (current == '"')
                {
                    inDoubleQuotedIdentifier = false;
                }
                continue;
            }
            if (inBracketedIdentifier)
            {
                if (current == ']' && next == ']')
                {
                    index++;
                }
                else if (current == ']')
                {
                    inBracketedIdentifier = false;
                }
                continue;
            }

            if (current == '-' && next == '-')
            {
                inLineComment = true;
                index++;
            }
            else if (current == '/' && next == '*')
            {
                inBlockComment = true;
                index++;
            }
            else if (current == '\'')
            {
                inSingleQuotedString = true;
            }
            else if (current == '"')
            {
                inDoubleQuotedIdentifier = true;
            }
            else if (current == '[')
            {
                inBracketedIdentifier = true;
            }
            else if (current == ';')
            {
                if (semicolonIndex >= 0)
                {
                    throw new ArgumentException(
                        $"{sourceDescription} '{stepId}' must contain a single SELECT statement.");
                }
                semicolonIndex = index;
            }
        }

        if (semicolonIndex >= 0)
        {
            var trailingContent = remaining[(semicolonIndex + 1)..];
            var onlyCommentsOrWhitespace = Regex.Replace(
                trailingContent,
                @"(?s)/\*.*?\*/|--[^\r\n]*|[\s]",
                string.Empty);
            if (onlyCommentsOrWhitespace.Length != 0)
            {
                throw new ArgumentException(
                    $"{sourceDescription} '{stepId}' must contain a single SELECT statement.");
            }
        }
    }

    private static string ResolveConnectorPath(
        object? connection,
        string configuredPath,
        string connectorType,
        string role)
    {
        if (connectorType is "http" or "odata")
        {
            return configuredPath;
        }

        if (connectorType is "salesforce" or "sharepoint" or "hubspot" or "zendesk" or "servicenow")
        {
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                throw new ArgumentException(
                    $"{role} must omit Path for the {connectorType} connector.");
            }
            RestJsonConnectorSupport.GetHttpClient(connection, connectorType, role);
            return string.Empty;
        }

        if (connectorType is "azureblob" or "s3" or "adlsgen2" or "gcs")
        {
            if (string.IsNullOrWhiteSpace(configuredPath) &&
                role.StartsWith("Target", StringComparison.Ordinal))
            {
                throw new ArgumentException($"{role} must declare a Path.");
            }
            return configuredPath;
        }

        if (connectorType == "mongodb")
        {
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                throw new ArgumentException(
                    $"{role} must omit Path when its connection is a MongoDB connection.");
            }
            MongoConnectionSupport.ValidateConnection(connection!, role);
            return string.Empty;
        }

        if (connectorType is "sqlserver" or "postgresql" or "mysql" or "oracle")
        {
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                throw new ArgumentException(
                    $"{role} must omit Path when its connection is a database connection.");
            }
            if (connection is not System.Data.Common.DbConnection)
            {
                throw new ArgumentException(
                    $"The {connectorType} connector requires a DbConnection for {role}.");
            }
            return string.Empty;
        }

        if (connectorType is "sqlite" or "duckdb")
        {
            if (connection is FileInfo databaseFile)
            {
                if (!string.IsNullOrWhiteSpace(configuredPath))
                {
                    throw new ArgumentException($"{role} must omit Path when its connection is a FileInfo.");
                }
                return Path.GetFullPath(databaseFile.FullName);
            }

            if (connection is not DirectoryInfo databaseDirectory)
            {
                throw new ArgumentException(
                    $"The {connectorType} connector requires a DirectoryInfo or FileInfo connection for {role}.");
            }
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                throw new ArgumentException($"{role} must declare Path when its connection is a DirectoryInfo.");
            }

            return ResolveRelativePath(databaseDirectory, configuredPath, role);
        }

        if (connectorType == "file")
        {
            if (connection is FileInfo file)
            {
                if (!string.IsNullOrWhiteSpace(configuredPath))
                {
                    throw new ArgumentException(
                        $"{role} must omit Path when its connection is a FileInfo.");
                }
                return Path.GetFullPath(file.FullName);
            }

            if (connection is not DirectoryInfo fileDirectory)
            {
                throw new ArgumentException(
                    $"The file connector requires a DirectoryInfo or FileInfo connection for {role}.");
            }
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                throw new ArgumentException($"{role} must declare Path when its connection is a DirectoryInfo.");
            }

            return ResolveRelativePath(fileDirectory, configuredPath, role);
        }

        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return string.Empty;
        }
        if (connection is not DirectoryInfo directory)
        {
            return configuredPath;
        }

        return ResolveRelativePath(directory, configuredPath, role);
    }

    private static string ResolveRelativePath(DirectoryInfo directory, string configuredPath, string role)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            throw new ArgumentException($"{role} must be relative to its directory connection.");
        }

        var root = Path.GetFullPath(directory.FullName);
        var resolved = Path.GetFullPath(Path.Combine(root, configuredPath));
        var relative = Path.GetRelativePath(root, resolved);
        if (relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw new ArgumentException($"{role} escapes its directory connection.");
        }
        return resolved;
    }

    private static void AddCustomModules(HashSet<string> modules, IDictionary? connector, string propertyName)
    {
        if (connector is null)
        {
            return;
        }
        var custom = GetMap(GetProperty(connector, propertyName), propertyName, required: false);
        if (custom is null)
        {
            return;
        }
        foreach (var module in GetStrings(GetProperty(custom, "Modules")))
        {
            modules.Add(module);
        }
    }

    private static string[] GetStrings(object? value)
    {
        if (value is null)
        {
            return Array.Empty<string>();
        }
        if (Unwrap(value) is string text)
        {
            return new[] { text };
        }
        var values = LanguagePrimitives.GetEnumerable(value);
        if (values is null)
        {
            return new[] { value.ToString() ?? string.Empty };
        }
        return values.Cast<object?>()
            .Select(item => Unwrap(item)?.ToString() ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static object? ResolveConnection(IDictionary connections, string connectionId, string stepId, string role)
    {
        if (!connections.Contains(connectionId))
        {
            throw new ArgumentException(
                $"Step '{stepId}' references unknown {role} connection ID '{connectionId}'.");
        }
        return Unwrap(connections[connectionId])
            ?? throw new ArgumentException(
                $"Connection ID '{connectionId}' for step '{stepId}' resolved to null.");
    }

    private static string SerializeJson(object? value)
    {
        var normalized = NormalizeForJson(value);
        return normalized is null ? "{}" : JsonSerializer.Serialize(normalized);
    }

    private static object? NormalizeForJson(object? value)
    {
        if (value is PSObject psObject)
        {
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in psObject.Properties)
            {
                if (property.IsGettable)
                {
                    properties[property.Name] = NormalizeForJson(property.Value);
                }
            }
            return properties;
        }
        value = Unwrap(value);
        if (value is null || value is string || value.GetType().IsPrimitive || value is decimal or Guid or DateTime or DateTimeOffset)
        {
            return value;
        }
        if (value is IDictionary dictionary)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dictionary)
            {
                result[entry.Key?.ToString() ?? string.Empty] = NormalizeForJson(entry.Value);
            }
            return result;
        }
        if (value is IEnumerable enumerable)
        {
            return enumerable.Cast<object?>().Select(NormalizeForJson).ToArray();
        }
        return value.ToString();
    }

    private static IDictionary? GetMap(object? value, string description, bool required = true)
    {
        if (value is PSObject psObject)
        {
            if (psObject.BaseObject is IDictionary baseDictionary)
            {
                return baseDictionary;
            }
            if (psObject.BaseObject is IEnumerable and not string &&
                !psObject.Properties.Any(property => property.MemberType == PSMemberTypes.NoteProperty))
            {
                if (required)
                {
                    throw new ArgumentException($"{description} must be a dictionary or object with named properties.");
                }
                return null;
            }
            return GetPropertiesAsMap(psObject);
        }
        if (value is IDictionary dictionary)
        {
            return dictionary;
        }
        if (value is IEnumerable and not string)
        {
            if (required)
            {
                throw new ArgumentException($"{description} must be a dictionary or object with named properties.");
            }
            return null;
        }
        if (value is not null and not (string or ScriptBlock) &&
            !value.GetType().IsPrimitive &&
            value is not decimal and not Guid)
        {
            var objectProperties = PSObject.AsPSObject(value);
            if (objectProperties.Properties.Any())
            {
                return GetPropertiesAsMap(objectProperties);
            }
        }
        if (required)
        {
            throw new ArgumentException($"{description} must be a dictionary or object with named properties.");
        }
        return null;
    }

    private static object? GetProperty(object? input, string name)
    {
        if (input is IDictionary dictionary)
        {
            if (dictionary.Contains(name))
            {
                return dictionary[name];
            }
            foreach (DictionaryEntry entry in dictionary)
            {
                if (string.Equals(entry.Key?.ToString(), name, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Value;
                }
            }
            return null;
        }
        if (input is PSObject psObject)
        {
            if (psObject.BaseObject is IDictionary baseDictionary)
            {
                return GetProperty(baseDictionary, name);
            }
            return psObject.Properties[name]?.Value;
        }
        if (input is not null and not (string or ScriptBlock) &&
            !input.GetType().IsPrimitive &&
            input is not decimal and not Guid)
        {
            var objectProperties = PSObject.AsPSObject(input);
            var property = objectProperties.Properties[name];
            if (property is not null)
            {
                return property.Value;
            }
        }
        return null;
    }

    private static IDictionary GetPropertiesAsMap(PSObject psObject)
    {
        if (psObject.BaseObject is IDictionary dictionary)
        {
            return dictionary;
        }

        var properties = new Hashtable(StringComparer.OrdinalIgnoreCase);
        foreach (var property in psObject.Properties)
        {
            if (property.IsGettable && !property.MemberType.HasFlag(PSMemberTypes.Method))
            {
                properties[property.Name] = property.Value;
            }
        }
        return properties;
    }

    private static string? GetString(IDictionary input, string name, bool required)
    {
        var value = Unwrap(GetProperty(input, name));
        if (value is null)
        {
            if (required)
            {
                throw new ArgumentException($"Required property '{name}' is missing.");
            }
            return null;
        }
        if (value is not string text || (required && string.IsNullOrWhiteSpace(text)))
        {
            throw new ArgumentException($"Property '{name}' must be a non-empty string.");
        }
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string GetRequiredString(IDictionary input, string name)
    {
        return GetString(input, name, required: true)
            ?? throw new ArgumentException($"Required property '{name}' is missing.");
    }

    private static string GetString(PSObject input, string name, bool required)
    {
        return GetString(GetMap(input, $"Job property '{name}'")!, name, required)
            ?? (required ? throw new ArgumentException($"Required property '{name}' is missing.") : string.Empty);
    }

    private static ScriptBlock GetScriptBlock(IDictionary input, string name, bool required)
    {
        var value = Unwrap(GetProperty(input, name));
        if (value is ScriptBlock scriptBlock)
        {
            return scriptBlock;
        }
        throw new ArgumentException(
            required
                ? $"Required property '{name}' must be a PowerShell script block."
                : $"Property '{name}' must be a PowerShell script block when provided.");
    }

    private static ScriptBlock GetScriptBlock(PSObject input, string name, bool required)
    {
        var value = Unwrap(GetProperty(input, name));
        if (value is ScriptBlock scriptBlock)
        {
            return scriptBlock;
        }
        if (required)
        {
            throw new ArgumentException($"Required property '{name}' must be a PowerShell script block.");
        }
        throw new ArgumentException($"Property '{name}' must be a PowerShell script block when provided.");
    }

    private static int GetInt(IDictionary input, string name, int defaultValue)
    {
        var value = Unwrap(GetProperty(input, name));
        return value is null ? defaultValue : LanguagePrimitives.ConvertTo<int>(value);
    }

    private static long GetLong(IDictionary input, string name, long defaultValue)
    {
        var value = Unwrap(GetProperty(input, name));
        return value is null ? defaultValue : LanguagePrimitives.ConvertTo<long>(value);
    }

    private static object? Unwrap(object? value)
    {
        return value is PSObject psObject ? psObject.BaseObject : value;
    }

    private static string NormalizeScriptText(ScriptBlock scriptBlock)
    {
        var text = scriptBlock.ToString().Trim();
        if (text.StartsWith('{') && text.EndsWith('}'))
        {
            text = text[1..^1].Trim();
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("A PowerShell script block cannot be empty.");
        }
        return text;
    }

    private static CustomConnector ParseCustomConnector(object? value, string stepId, string role)
    {
        var custom = GetMap(value, $"Custom {role} for step '{stepId}'", required: true)!;
        var callback = GetScriptBlock(custom, "Callback", required: true);
        var countCallbackValue = GetProperty(custom, "CountCallback");
        if (role != "source" && countCallbackValue is not null)
        {
            throw new ArgumentException(
                $"Custom {role} for step '{stepId}' cannot declare CountCallback.");
        }
        var countCallbackText = countCallbackValue is null
            ? null
            : NormalizeScriptText(GetScriptBlock(custom, "CountCallback", required: true));
        var identity = GetRequiredString(custom, "Identity");
        var pageSize = GetInt(custom, "PageSize", 100);
        var maxConcurrency = GetInt(custom, "MaxConcurrency", 1);
        if (pageSize < 1 || maxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"Custom {role} for step '{stepId}' must have positive PageSize and MaxConcurrency values.");
        }
        return new CustomConnector(
            identity,
            NormalizeScriptText(callback),
            countCallbackText,
            pageSize,
            maxConcurrency);
    }

    private sealed record GraphDependencyInput(string StepId, string? ParentKey, int RowsPerParent);

    private sealed record CustomConnector(
        string Identity,
        string CallbackText,
        string? CountCallbackText,
        int PageSize,
        int MaxConcurrency);


}
