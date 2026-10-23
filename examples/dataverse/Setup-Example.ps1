Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$dataDirectory = Join-Path $PSScriptRoot 'generated-data'
$null = [System.IO.Directory]::CreateDirectory($dataDirectory)
$datasetUrl = 'https://www.kaggle.com/api/v1/datasets/download/sushicatsan/sample-sales-crm-data?datasetVersionNumber=1'
$datasetFiles = @(
    [pscustomobject]@{ Name = 'user.csv'; Rows = 275 }
    [pscustomobject]@{ Name = 'account.csv'; Rows = 10000 }
    [pscustomobject]@{ Name = 'lead.csv'; Rows = 14000 }
    [pscustomobject]@{ Name = 'contact.csv'; Rows = 18500 }
    [pscustomobject]@{ Name = 'opportunity.csv'; Rows = 16000 }
    [pscustomobject]@{ Name = 'order.csv'; Rows = 9200 }
    [pscustomobject]@{ Name = 'order_items.csv'; Rows = 41400 }
    [pscustomobject]@{ Name = 'tasks.csv'; Rows = 110000 }
    [pscustomobject]@{ Name = 'event.csv'; Rows = 38000 }
)

$missingFiles = @(
    $datasetFiles |
        Where-Object { -not (Test-Path -LiteralPath (Join-Path $dataDirectory $_.Name) -PathType Leaf) }
)
if ($missingFiles.Count -gt 0) {
    $archivePath = [System.IO.Path]::GetTempFileName()
    try {
        Write-Host 'Downloading the version-pinned Sample Sales CRM Data archive from Kaggle...'
        Invoke-WebRequest -Uri $datasetUrl -OutFile $archivePath -ErrorAction Stop
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            foreach ($file in $datasetFiles) {
                $entry = $archive.GetEntry($file.Name)
                if ($null -eq $entry) {
                    throw "Kaggle dataset archive does not contain '$($file.Name)'."
                }
                $destination = Join-Path $dataDirectory $file.Name
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $true)
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
    }
}

foreach ($file in $datasetFiles) {
    $path = Join-Path $dataDirectory $file.Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "Expected dataset file '$path' to exist and be non-empty."
    }
}

$sortDefinitions = @(
    @{ Name = 'user.csv'; Columns = @('id') }
    @{ Name = 'account.csv'; Columns = @('id') }
    @{ Name = 'lead.csv'; Columns = @('id') }
    @{ Name = 'contact.csv'; Columns = @('id') }
    @{ Name = 'opportunity.csv'; Columns = @('id') }
    @{ Name = 'order.csv'; Columns = @('id') }
    @{ Name = 'order_items.csv'; Columns = @('id') }
    @{ Name = 'tasks.csv'; Columns = @('id') }
    @{ Name = 'event.csv'; Columns = @('id') }
)
foreach ($sortDefinition in $sortDefinitions) {
    $path = Join-Path $dataDirectory $sortDefinition.Name
    [object[]]$rows = @(Import-Csv -LiteralPath $path -ErrorAction Stop)
    $columns = [string[]]$sortDefinition.Columns
    foreach ($row in $rows) {
        foreach ($column in $columns) {
            if ([string]::IsNullOrWhiteSpace([string]$row.$column)) {
                throw "Dataset '$($sortDefinition.Name)' contains a row with an empty '$column' value."
            }
        }
    }
    $comparisonScript = {
        param($left, $right)
        foreach ($column in $columns) {
            $result = [System.StringComparer]::Ordinal.Compare(
                [string]$left.$column,
                [string]$right.$column)
            if ($result -ne 0) {
                return $result
            }
        }
        return 0
    }.GetNewClosure()
    $comparison = [System.Comparison[object]]$comparisonScript
    [System.Array]::Sort($rows, $comparison)
    $rows | Export-Csv -LiteralPath $path -NoTypeInformation -Encoding utf8NoBOM -ErrorAction Stop
}

$manifest = [ordered]@{
    Dataset = 'Sample Sales CRM Data'
    DatasetUrl = 'https://www.kaggle.com/datasets/sushicatsan/sample-sales-crm-data'
    DatasetVersion = 1
    License = 'CC0: Public Domain'
    TotalRows = 257375
    Files = [ordered]@{}
}
foreach ($file in $datasetFiles) {
    $manifest.Files[$file.Name] = $file.Rows
}
[System.IO.File]::WriteAllText(
    (Join-Path $dataDirectory 'data-manifest.json'),
    (ConvertTo-Json -InputObject $manifest -Depth 4),
    [System.Text.UTF8Encoding]::new($false))
Write-Host "Verified the complete 257,375-row Kaggle dataset in '$dataDirectory'."

$dataverseUrl = [uri]'https://org2aa8aa8c.crm11.dynamics.com'
$connectionProfileName = 'Migration4DataverseExample'

Import-Module Rnwood.Dataverse.Data.PowerShell -ErrorAction Stop
$connection = Get-DataverseConnection `
    -Url $dataverseUrl `
    -DeviceCode `
    -Name $connectionProfileName `
    -DisableAffinityCookie `
    -ErrorAction Stop

$requiredStandardTables = @(
    'account'
    'contact'
    'lead'
    'opportunity'
    'salesorder'
    'salesorderdetail'
    'task'
    'appointment'
)
$existingEntities = @{}
foreach ($entity in @(Get-DataverseEntityMetadata -ExcludeAttributes -ExcludeRelationships -Connection $connection -ErrorAction Stop)) {
    $existingEntities[[string]$entity.LogicalName] = $true
}
foreach ($tableName in $requiredStandardTables) {
    if (-not $existingEntities.ContainsKey($tableName)) {
        throw "Required standard Dataverse table '$tableName' is not available in this environment."
    }
}

$sourceUserTable = 'new_m4dsalesuser'
$changedEntities = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
if (-not $existingEntities.ContainsKey($sourceUserTable)) {
    Set-DataverseEntityMetadata `
        -EntityName $sourceUserTable `
        -SchemaName 'new_M4DSalesUser' `
        -DisplayName 'Salesforce Source User' `
        -DisplayCollectionName 'Salesforce Source Users' `
        -OwnershipType OrganizationOwned `
        -PrimaryAttributeSchemaName 'new_Name' `
        -PrimaryAttributeDisplayName 'Name' `
        -PrimaryAttributeMaxLength 200 `
        -Connection $connection `
        -ErrorAction Stop | Out-Null
    $existingEntities[$sourceUserTable] = $true
    $null = $changedEntities.Add($sourceUserTable)
}

$tableAttributes = @{
    account = @(
        @{ Name = 'new_salesforceid'; Schema = 'new_SalesforceId'; Display = 'Salesforce ID'; Type = 'String'; MaxLength = 100 }
        @{ Name = 'new_sourceownerid'; Schema = 'new_SourceOwnerId'; Display = 'Source Owner ID'; Type = 'String'; MaxLength = 100 }
        @{ Name = 'new_sourcerowjson'; Schema = 'new_SourceRowJson'; Display = 'Source Row JSON'; Type = 'Memo'; MaxLength = 1048576 }
    )
    contact = @(
        @{ Name = 'new_salesforceid'; Schema = 'new_SalesforceId'; Display = 'Salesforce ID'; Type = 'String'; MaxLength = 100 }
        @{ Name = 'new_sourceownerid'; Schema = 'new_SourceOwnerId'; Display = 'Source Owner ID'; Type = 'String'; MaxLength = 100 }
        @{ Name = 'new_sourcerowjson'; Schema = 'new_SourceRowJson'; Display = 'Source Row JSON'; Type = 'Memo'; MaxLength = 1048576 }
    )
    lead = @(
        @{ Name = 'new_salesforceid'; Schema = 'new_SalesforceId'; Display = 'Salesforce ID'; Type = 'String'; MaxLength = 100 }
        @{ Name = 'new_sourceownerid'; Schema = 'new_SourceOwnerId'; Display = 'Source Owner ID'; Type = 'String'; MaxLength = 100 }
        @{ Name = 'new_sourcerowjson'; Schema = 'new_SourceRowJson'; Display = 'Source Row JSON'; Type = 'Memo'; MaxLength = 1048576 }
    )
    opportunity = @(
        @{ Name = 'new_salesforceid'; Schema = 'new_SalesforceId'; Display = 'Salesforce ID'; Type = 'String'; MaxLength = 100 }
        @{ Name = 'new_sourceownerid'; Schema = 'new_SourceOwnerId'; Display = 'Source Owner ID'; Type = 'String'; MaxLength = 100 }
        @{ Name = 'new_sourcerowjson'; Schema = 'new_SourceRowJson'; Display = 'Source Row JSON'; Type = 'Memo'; MaxLength = 1048576 }
    )
    salesorder = @(
        @{ Name = 'new_salesforceid'; Schema = 'new_SalesforceId'; Display = 'Salesforce ID'; Type = 'String'; MaxLength = 100 }
        @{ Name = 'new_sourceownerid'; Schema = 'new_SourceOwnerId'; Display = 'Source Owner ID'; Type = 'String'; MaxLength = 100 }
        @{ Name = 'new_sourcerowjson'; Schema = 'new_SourceRowJson'; Display = 'Source Row JSON'; Type = 'Memo'; MaxLength = 1048576 }
    )
    salesorderdetail = @(
        @{ Name = 'new_salesforceid'; Schema = 'new_SalesforceId'; Display = 'Salesforce ID'; Type = 'String'; MaxLength = 100 }
        @{ Name = 'new_sourcerowjson'; Schema = 'new_SourceRowJson'; Display = 'Source Row JSON'; Type = 'Memo'; MaxLength = 1048576 }
    )
    task = @(
        @{ Name = 'new_salesforceid'; Schema = 'new_SalesforceId'; Display = 'Salesforce ID'; Type = 'String'; MaxLength = 100 }
        @{ Name = 'new_sourceownerid'; Schema = 'new_SourceOwnerId'; Display = 'Source Owner ID'; Type = 'String'; MaxLength = 100 }
        @{ Name = 'new_sourcerowjson'; Schema = 'new_SourceRowJson'; Display = 'Source Row JSON'; Type = 'Memo'; MaxLength = 1048576 }
    )
    appointment = @(
        @{ Name = 'new_salesforceid'; Schema = 'new_SalesforceId'; Display = 'Salesforce ID'; Type = 'String'; MaxLength = 100 }
        @{ Name = 'new_sourceownerid'; Schema = 'new_SourceOwnerId'; Display = 'Source Owner ID'; Type = 'String'; MaxLength = 100 }
        @{ Name = 'new_sourcerowjson'; Schema = 'new_SourceRowJson'; Display = 'Source Row JSON'; Type = 'Memo'; MaxLength = 1048576 }
    )
    new_m4dsalesuser = @(
        @{ Name = 'new_salesforceid'; Schema = 'new_SalesforceId'; Display = 'Salesforce ID'; Type = 'String'; MaxLength = 100 }
        @{ Name = 'new_sourcerowjson'; Schema = 'new_SourceRowJson'; Display = 'Source Row JSON'; Type = 'Memo'; MaxLength = 1048576 }
    )
}

foreach ($tableName in @($requiredStandardTables) + @($sourceUserTable)) {
    $metadata = Get-DataverseEntityMetadata -EntityName $tableName -Connection $connection -ErrorAction Stop
    $existingAttributes = @{}
    foreach ($attribute in @($metadata.Attributes)) {
        $existingAttributes[[string]$attribute.LogicalName] = $true
    }
    foreach ($attribute in $tableAttributes[$tableName]) {
        if ($existingAttributes.ContainsKey($attribute.Name)) {
            continue
        }

        $parameters = @{
            EntityName = $tableName
            AttributeName = $attribute.Name
            SchemaName = $attribute.Schema
            DisplayName = $attribute.Display
            AttributeType = $attribute.Type
            Connection = $connection
            ErrorAction = 'Stop'
        }
        if ($attribute.ContainsKey('MaxLength')) {
            $parameters.MaxLength = $attribute.MaxLength
        }
        Set-DataverseAttributeMetadata @parameters | Out-Null
        $null = $changedEntities.Add($tableName)
    }
}

foreach ($entityName in $changedEntities) {
    Publish-DataverseCustomizations -EntityName $entityName -Connection $connection -ErrorAction Stop | Out-Null
}

$alternateKeyTables = @($tableAttributes.Keys)
foreach ($tableName in $alternateKeyTables) {
    $hasSalesforceIdKey = $false
    foreach ($key in @(Get-DataverseEntityKeyMetadata -EntityName $tableName -Connection $connection -ErrorAction Stop)) {
        $keyAttributes = @($key.KeyAttributes | ForEach-Object { [string]$_ })
        if ($keyAttributes.Count -eq 1 -and
            [string]::Equals($keyAttributes[0], 'new_salesforceid', [StringComparison]::OrdinalIgnoreCase)) {
            $hasSalesforceIdKey = $true
            break
        }
    }
    if ($hasSalesforceIdKey) {
        continue
    }

    $schemaName = "new_${tableName}_SalesforceIdKey"
    Set-DataverseEntityKeyMetadata `
        -EntityName $tableName `
        -SchemaName $schemaName `
        -DisplayName 'Salesforce ID' `
        -KeyAttributes @('new_salesforceid') `
        -Connection $connection `
        -Publish `
        -ErrorAction Stop | Out-Null
}

Write-Host "Created or verified the Salesforce ID alternate keys and source fields. Saved the connection as '$connectionProfileName'."
