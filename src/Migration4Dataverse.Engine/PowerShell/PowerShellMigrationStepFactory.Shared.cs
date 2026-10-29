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

        function ConvertTo-FileTargetBatch {
            param($Records, [string]$TargetPath, [string]$StepId, [string]$RunId, [string]$BatchId, [long]$Sequence)
            $recordsJson = ConvertTo-Json -InputObject $Records -Depth 100 -Compress -ErrorAction Stop
            [Migration4Dataverse.Engine.Connectors.FileBatchTargetWriter]::WriteSingleFileTargetBatch(
                $TargetPath, $RunId, $StepId, $BatchId, $Sequence, $recordsJson)
        }

        $payload = ConvertFrom-Json -InputObject $batch.PayloadJson -AsHashtable -Depth 100 -ErrorAction Stop
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
                        $recordsJson = ConvertTo-Json -InputObject $inputRecords -Depth 100 -Compress -ErrorAction Stop
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
                        for ($index = 0; $index -lt $inputRecords.Count; $index++) {
                            try {
                                $values = if ($hasConnectedTransform) {
                                    @(& $script $inputRecords[$index] $transformConnection)
                                }
                                else {
                                    @(& $script $inputRecords[$index])
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
                        if ($context.JoinInputConsumers.Count -gt 0) {
                            if ($null -eq $context.Store) {
                                throw "Target step '$($context.StepId)' must have a migration store to stage Join input records."
                            }
                            $targetParameters.PassThru = $true
                            $writtenRecords = @($records | Set-DataverseRecord @targetParameters)
                            if ($writtenRecords.Count -ne $records.Count) {
                                throw "Set-DataverseRecord -PassThru returned $($writtenRecords.Count) records for $($records.Count) inputs in step '$($context.StepId)'."
                            }

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
                                foreach ($field in $records[$recordIndex].Keys) {
                                    $recordFields[[string]$field] = $records[$recordIndex][$field]
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
                    'jsonl' {
                        ConvertTo-FileTargetBatch -Records $records -TargetPath $context.TargetPath `
                            -StepId $context.StepId -RunId $batch.RunId `
                            -BatchId $batch.BatchId -Sequence $batch.Sequence
                    }
                    'csv' {
                        ConvertTo-FileTargetBatch -Records $records -TargetPath $context.TargetPath `
                            -StepId $context.StepId -RunId $batch.RunId `
                            -BatchId $batch.BatchId -Sequence $batch.Sequence
                    }
                    'parquet' {
                        ConvertTo-FileTargetBatch -Records $records -TargetPath $context.TargetPath `
                            -StepId $context.StepId -RunId $batch.RunId `
                            -BatchId $batch.BatchId -Sequence $batch.Sequence
                    }
                    'sqlite' {
                        $recordsJson = ConvertTo-Json -InputObject $records -Depth 100 -Compress -ErrorAction Stop
                        [Migration4Dataverse.Engine.Connectors.DatabaseBatchTargetWriter]::WriteBatch(
                            $targetConnection, 'sqlite', $context.TableName, $context.StepId,
                            $batch.RunId, $batch.BatchId, $recordsJson, $context.TargetOptionsJson)
                    }
                    'duckdb' {
                        $recordsJson = ConvertTo-Json -InputObject $records -Depth 100 -Compress -ErrorAction Stop
                        [Migration4Dataverse.Engine.Connectors.DatabaseBatchTargetWriter]::WriteBatch(
                            $targetConnection, 'duckdb', $context.TableName, $context.StepId,
                            $batch.RunId, $batch.BatchId, $recordsJson, $context.TargetOptionsJson)
                    }
                    'sqlserver' {
                        $recordsJson = ConvertTo-Json -InputObject $records -Depth 100 -Compress -ErrorAction Stop
                        [Migration4Dataverse.Engine.Connectors.DatabaseBatchTargetWriter]::WriteBatch(
                            $targetConnection, 'sqlserver', $context.TableName, $context.StepId,
                            $batch.RunId, $batch.BatchId, $recordsJson, $context.TargetOptionsJson)
                    }
                    'postgresql' {
                        $recordsJson = ConvertTo-Json -InputObject $records -Depth 100 -Compress -ErrorAction Stop
                        [Migration4Dataverse.Engine.Connectors.DatabaseBatchTargetWriter]::WriteBatch(
                            $targetConnection, 'postgresql', $context.TableName, $context.StepId,
                            $batch.RunId, $batch.BatchId, $recordsJson, $context.TargetOptionsJson)
                    }
                    'mysql' {
                        $recordsJson = ConvertTo-Json -InputObject $records -Depth 100 -Compress -ErrorAction Stop
                        [Migration4Dataverse.Engine.Connectors.DatabaseBatchTargetWriter]::WriteBatch(
                            $targetConnection, 'mysql', $context.TableName, $context.StepId,
                            $batch.RunId, $batch.BatchId, $recordsJson, $context.TargetOptionsJson)
                    }
                    'oracle' {
                        $recordsJson = ConvertTo-Json -InputObject $records -Depth 100 -Compress -ErrorAction Stop
                        [Migration4Dataverse.Engine.Connectors.DatabaseBatchTargetWriter]::WriteBatch(
                            $targetConnection, 'oracle', $context.TableName, $context.StepId,
                            $batch.RunId, $batch.BatchId, $recordsJson, $context.TargetOptionsJson)
                    }
                    'azureblob' {
                        $recordsJson = ConvertTo-Json -InputObject $records -Depth 100 -Compress -ErrorAction Stop
                        [Migration4Dataverse.Engine.Connectors.ObjectStorageTargetWriter]::WriteBatch(
                            $targetConnection, 'azureblob', $context.TargetPath, $context.StepId,
                            $batch.RunId, $batch.BatchId, $batch.Sequence, $recordsJson,
                            $context.TargetOptionsJson, $context.StagingRoot)
                    }
                    's3' {
                        $recordsJson = ConvertTo-Json -InputObject $records -Depth 100 -Compress -ErrorAction Stop
                        [Migration4Dataverse.Engine.Connectors.ObjectStorageTargetWriter]::WriteBatch(
                            $targetConnection, 's3', $context.TargetPath, $context.StepId,
                            $batch.RunId, $batch.BatchId, $batch.Sequence, $recordsJson,
                            $context.TargetOptionsJson, $context.StagingRoot)
                    }
                    'adlsgen2' {
                        $recordsJson = ConvertTo-Json -InputObject $records -Depth 100 -Compress -ErrorAction Stop
                        [Migration4Dataverse.Engine.Connectors.ObjectStorageTargetWriter]::WriteBatch(
                            $targetConnection, 'adlsgen2', $context.TargetPath, $context.StepId,
                            $batch.RunId, $batch.BatchId, $batch.Sequence, $recordsJson,
                            $context.TargetOptionsJson, $context.StagingRoot)
                    }
                    'gcs' {
                        $recordsJson = ConvertTo-Json -InputObject $records -Depth 100 -Compress -ErrorAction Stop
                        [Migration4Dataverse.Engine.Connectors.ObjectStorageTargetWriter]::WriteBatch(
                            $targetConnection, 'gcs', $context.TargetPath, $context.StepId,
                            $batch.RunId, $batch.BatchId, $batch.Sequence, $recordsJson,
                            $context.TargetOptionsJson, $context.StagingRoot)
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
                    $recordJson = ConvertTo-Json -InputObject $joinInputRecords[$index] `
                        -Depth 100 -Compress -ErrorAction Stop
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
            $recordsJson = ConvertTo-Json -InputObject $outputRecords.ToArray() -Depth 100 -Compress -ErrorAction Stop
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
                    $childPayload = ConvertTo-Json -InputObject ([ordered]@{
                        Records = $chunk
                        __LineageReferences = $lineageChunk
                    }) -Depth 100 -Compress -ErrorAction Stop
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
                $childPayload = ConvertTo-Json -InputObject ([ordered]@{
                    Records = $chunk
                    __LineageReferences = $lineageReferences.ToArray()
                }) -Depth 100 -Compress -ErrorAction Stop
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
                "IncludeSystemColumns", "IncludeFileAndImageMetadataColumns", "Unpublished"
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

        if (connectorType is "jsonl" or "csv" or "parquet" or "excel")
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
                    $"The {connectorType} connector requires a DirectoryInfo or FileInfo connection for {role}.");
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
