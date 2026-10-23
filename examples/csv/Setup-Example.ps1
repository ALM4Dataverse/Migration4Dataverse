Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$dataDirectory = Join-Path $PSScriptRoot 'generated-data'
$null = [System.IO.Directory]::CreateDirectory($dataDirectory)
$encoding = [System.Text.UTF8Encoding]::new($false)
$accounts = [System.Collections.Generic.List[object]]::new()
$contacts = [System.Collections.Generic.List[object]]::new()
$projects = [System.Collections.Generic.List[object]]::new()
$products = [System.Collections.Generic.List[object]]::new()
$priceListItems = [System.Collections.Generic.List[object]]::new()
$productSummaries = [System.Collections.Generic.List[object]]::new()

for ($accountNumber = 1; $accountNumber -le 3; $accountNumber++) {
    $accountSourceId = 'A{0:D10}' -f $accountNumber
    $accountId = [guid]::ParseExact(('00000000-0000-0000-0000-{0:D12}' -f $accountNumber), 'D')
    $accounts.Add([pscustomobject]@{
        SourceId = $accountSourceId
        DataverseId = $accountId
        Name = "Sample Account $accountNumber"
        Region = "Region-$accountNumber"
        Revenue = [decimal](1000 + $accountNumber)
    })

    for ($contactNumber = 1; $contactNumber -le 2; $contactNumber++) {
        $sequence = (($accountNumber - 1) * 2) + $contactNumber
        $contactSourceId = 'C{0:D12}' -f $sequence
        $contactId = [guid]::ParseExact(('10000000-0000-0000-0000-{0:D12}' -f $sequence), 'D')
        $contacts.Add([pscustomobject]@{
            SourceId = $contactSourceId
            DataverseId = $contactId
            AccountSourceId = $accountSourceId
            AccountDataverseId = $accountId
            FirstName = "Given$sequence"
            LastName = "Family$sequence"
            Email = "user$sequence@example.test"
        })

        for ($projectNumber = 1; $projectNumber -le 2; $projectNumber++) {
            $projectSequence = (($sequence - 1) * 2) + $projectNumber
            $projectSourceId = 'P{0:D12}' -f $projectSequence
            $projectId = [guid]::ParseExact(('20000000-0000-0000-0000-{0:D12}' -f $projectSequence), 'D')
            $projects.Add([pscustomobject]@{
                SourceId = $projectSourceId
                DataverseId = $projectId
                ContactSourceId = $contactSourceId
                ContactDataverseId = $contactId
                Name = "Migration Project $projectSequence"
                Budget = [decimal](1000 + $projectSequence)
            })
        }
    }
}

for ($productNumber = 1; $productNumber -le 3; $productNumber++) {
    $productSourceId = 'D{0:D10}' -f $productNumber
    $productId = [guid]::ParseExact(('40000000-0000-0000-0000-{0:D12}' -f $productNumber), 'D')
    $products.Add([pscustomobject]@{
        SourceId = $productSourceId
        DataverseId = $productId
        Name = "Sample Product $productNumber"
        Sku = "SKU-$productNumber"
        UnitPrice = [decimal](50 + $productNumber)
    })

    for ($itemNumber = 1; $itemNumber -le 2; $itemNumber++) {
        $sequence = (($productNumber - 1) * 2) + $itemNumber
        $itemId = [guid]::ParseExact(('50000000-0000-0000-0000-{0:D12}' -f $sequence), 'D')
        $priceListItems.Add([pscustomobject]@{
            SourceId = 'L{0:D10}' -f $sequence
            DataverseId = $itemId
            ProductSourceId = $productSourceId
            ProductDataverseId = $productId
            PriceListName = "Example Price List $itemNumber"
            Amount = [decimal](45 + $sequence)
        })
    }
}

for ($summaryNumber = 1; $summaryNumber -le 2; $summaryNumber++) {
    $productSummaries.Add([pscustomobject]@{
        SourceId = 'S{0:D10}' -f $summaryNumber
        DataverseId = [guid]::ParseExact(('60000000-0000-0000-0000-{0:D12}' -f $summaryNumber), 'D')
        Name = "Product Summary $summaryNumber"
        ProductCount = 3
        Period = "2026-Q$summaryNumber"
    })
}

foreach ($table in @(
    @{ Name = 'accounts'; Rows = $accounts }
    @{ Name = 'contacts'; Rows = $contacts }
    @{ Name = 'projects'; Rows = $projects }
    @{ Name = 'products'; Rows = $products }
    @{ Name = 'priceListItems'; Rows = $priceListItems }
    @{ Name = 'productSummary'; Rows = $productSummaries }
)) {
    $path = Join-Path $dataDirectory "$($table.Name).jsonl"
    $lines = foreach ($row in $table.Rows) {
        ConvertTo-Json -InputObject $row -Depth 10 -Compress
    }
    [System.IO.File]::WriteAllLines($path, [string[]]$lines, $encoding)
}

$engineAssemblyPath = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '../../src/Migration4Dataverse.Engine/bin/Release/net10.0/Migration4Dataverse.Engine.dll'))
if (-not (Test-Path -LiteralPath $engineAssemblyPath -PathType Leaf)) {
    throw "Build the Migration4Dataverse.Engine Release configuration before running the transformation examples: '$engineAssemblyPath'."
}
[void][System.Reflection.Assembly]::LoadFrom($engineAssemblyPath)
$filterTransformPipeline = [ordered]@{
    Sources = @(
        @{ Id = 'products'; Type = 'jsonl'; ConnectionId = 'example-files'; Path = (Join-Path $dataDirectory 'products.jsonl') }
        @{ Id = 'accounts'; Type = 'jsonl'; ConnectionId = 'example-files'; Path = (Join-Path $dataDirectory 'accounts.jsonl') }
    )
    Nodes = @(
        @{ Id = 'products-source'; Type = 'source'; SourceId = 'products' }
        @{ Id = 'accounts-source'; Type = 'source'; SourceId = 'accounts' }
        @{
            Id = 'filter-priced-products'
            Type = 'filter'
            Inputs = @('products-source')
            Script = {
                param($row)
                [decimal]$row.UnitPrice -ge 52
            }
        }
        @{
            Id = 'normalize-products'
            Type = 'transform'
            Inputs = @('filter-priced-products')
            Script = {
                param($row)
                [pscustomobject][ordered]@{
                    SourceId = [string]$row.SourceId
                    Name = ([string]$row.Name).ToUpperInvariant()
                    Sku = [string]$row.Sku
                    UnitPrice = [decimal]$row.UnitPrice
                }
            }
        }
        @{ Id = 'append-products-and-accounts'; Type = 'append'; Inputs = @('normalize-products', 'accounts-source') }
        @{ Id = 'sort-by-source-id'; Type = 'sort'; Inputs = @('append-products-and-accounts'); Keys = @(@{ Name = 'SourceId'; Type = 'string' }) }
    )
    OutputNodeId = 'sort-by-source-id'
}
[Migration4Dataverse.Engine.Transformations.DataFlowPipeline]::WriteJsonLinesAsync(
    $filterTransformPipeline,
    (Join-Path $dataDirectory 'transforms/filter-transform-demo.jsonl'),
    [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
$mergePipeline = [ordered]@{
    Sources = @(
        @{ Id = 'products'; Type = 'jsonl'; ConnectionId = 'example-files'; Path = (Join-Path $dataDirectory 'products.jsonl') }
        @{ Id = 'price-list-items'; Type = 'jsonl'; ConnectionId = 'example-files'; Path = (Join-Path $dataDirectory 'priceListItems.jsonl') }
    )
    Nodes = @(
        @{ Id = 'products-source'; Type = 'source'; SourceId = 'products' }
        @{ Id = 'price-list-items-source'; Type = 'source'; SourceId = 'price-list-items' }
        @{ Id = 'merge-products-and-items'; Type = 'merge'; Inputs = @('products-source', 'price-list-items-source'); Keys = @(@{ Name = 'SourceId'; Type = 'string' }) }
    )
    OutputNodeId = 'merge-products-and-items'
}
[Migration4Dataverse.Engine.Transformations.DataFlowPipeline]::WriteJsonLinesAsync(
    $mergePipeline,
    (Join-Path $dataDirectory 'transforms/merge-demo.jsonl'),
    [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()

$engineAssemblyPath = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '../../src/Migration4Dataverse.Engine/bin/Release/net10.0/Migration4Dataverse.Engine.dll'))
if (-not (Test-Path -LiteralPath $engineAssemblyPath -PathType Leaf)) {
    throw "Build the Migration4Dataverse.Engine Release configuration before creating CSV fixtures: '$engineAssemblyPath'."
}
[void][System.Reflection.Assembly]::LoadFrom($engineAssemblyPath)
$sourceOptions = @{
    HasHeaderRecord = $true
    Encoding = 'utf-8'
    Culture = 'InvariantCulture'
    SourceColumnsByStep = @{
        Accounts = @('SourceId', 'DataverseId', 'Name', 'Region', 'Revenue')
        Contacts = @('SourceId', 'DataverseId', 'AccountSourceId', 'AccountDataverseId', 'FirstName', 'LastName', 'Email')
        Projects = @('SourceId', 'DataverseId', 'ContactSourceId', 'ContactDataverseId', 'Name', 'Budget')
        Products = @('SourceId', 'DataverseId', 'Name', 'Sku', 'UnitPrice')
        PriceListItems = @('SourceId', 'DataverseId', 'ProductSourceId', 'ProductDataverseId', 'PriceListName', 'Amount')
        ProductSummary = @('SourceId', 'DataverseId', 'Name', 'ProductCount', 'Period')
    }
    ColumnTypes = @{
        DataverseId = 'guid'
        AccountDataverseId = 'guid'
        ContactDataverseId = 'guid'
        ProductDataverseId = 'guid'
        Revenue = 'decimal'
        Budget = 'decimal'
        UnitPrice = 'decimal'
        Amount = 'decimal'
        ProductCount = 'int'
    }
}
$sourceOptionsJson = ConvertTo-Json -InputObject $sourceOptions -Depth 10 -Compress
foreach ($table in @(
    @{ Name = 'Accounts'; FileName = 'accounts' }
    @{ Name = 'Contacts'; FileName = 'contacts' }
    @{ Name = 'Projects'; FileName = 'projects' }
    @{ Name = 'Products'; FileName = 'products' }
    @{ Name = 'PriceListItems'; FileName = 'priceListItems' }
    @{ Name = 'ProductSummary'; FileName = 'productSummary' }
)) {
    [Migration4Dataverse.Engine.Connectors.StreamingSourceFileConverter]::ConvertJsonLinesToCsv(
        (Join-Path $dataDirectory "$($table.FileName).jsonl"),
        (Join-Path $dataDirectory "$($table.FileName).csv"),
        $table.Name,
        $sourceOptionsJson)
}

$manifest = [ordered]@{
    SchemaVersion = 4
    SourceType = 'csv'
    AccountCount = 3
    ContactsPerAccount = 2
    ProjectsPerContact = 2
    AccountRows = 3
    ContactRows = 6
    ProjectRows = 12
    ProductRows = $products.Count
    PriceListItemRows = $priceListItems.Count
    ProductSummaryRows = $productSummaries.Count
}
[System.IO.File]::WriteAllText(
    (Join-Path $dataDirectory 'data-manifest.json'),
    (ConvertTo-Json -InputObject $manifest -Depth 3),
    $encoding)

Write-Host "Created source fixtures for Accounts, Contacts, Projects, Products, PriceListItems, and ProductSummary in '$dataDirectory'."
