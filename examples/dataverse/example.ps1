Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSVersion -lt [version]'7.6') {
    throw 'Migration4Dataverse targets PowerShell 7.6 or later (.NET 10).'
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$dataDirectory = Join-Path $PSScriptRoot 'generated-data'
$runId = $env:M4D_RUN_ID
$databasePath = Join-Path $dataDirectory 'migration.sqlite3'
$null = [System.IO.Directory]::CreateDirectory($dataDirectory)
$requiredFiles = @(
    'user.csv'
    'account.csv'
    'lead.csv'
    'contact.csv'
    'opportunity.csv'
    'order.csv'
    'order_items.csv'
    'tasks.csv'
    'event.csv'
    'account-attachments.jsonl'
    'account-attachments.jsonl.attachments/migration-example-contract.txt'
)
foreach ($fileName in $requiredFiles) {
    $path = Join-Path $dataDirectory $fileName
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Run 'Setup-Example.ps1' first; the required dataset file '$path' is missing."
    }
}

$moduleManifest = Join-Path $repositoryRoot 'src/Migration4Dataverse.PowerShell/Migration4Dataverse.PowerShell.psd1'
Import-Module -Name $moduleManifest -Force -ErrorAction Stop
$connections = @{
    'example-files' = [System.IO.DirectoryInfo]::new($dataDirectory)
}
$connectionProfileName = 'Migration4DataverseExample'
Import-Module Rnwood.Dataverse.Data.PowerShell -MinimumVersion '3.3.0' -ErrorAction Stop
$connections['example-dataverse'] = Get-DataverseConnection `
    -Name $connectionProfileName `
    -DisableAffinityCookie `
    -ErrorAction Stop

$steps = @(
    @{
        # Reads user records independently so this source can run alongside the other roots.
        Id = 'SourceUsers'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'file'
            Format = @{ Type = 'csv'; Options = @{ EmptyFieldAsNull = $true } }
            ConnectionId = 'example-files'
            Path = 'user.csv'
            Key = 'id'
        }
    }
    @{
        # Maps source user identifiers and names into the migration-owned user table.
        Id = 'MapSourceUsers'
        Type = 'transform'
        Inputs = @('SourceUsers')
        Script = {
            param($row)
            $sourceId = [string]$row.id
            @{
                new_salesforceid = "run2-$sourceId"
                new_name = [string]$row.name
                new_sourcerowjson = ConvertTo-Json -InputObject $row -Depth 8 -Compress
            }
        }
    }
    @{
        # Upserts the mapped user records into Dataverse.
        Id = 'SourceUsersTarget'
        Type = 'target'
        Inputs = @('MapSourceUsers')
        Target = @{
            Type = 'dataverse'
            ConnectionId = 'example-dataverse'
            Table = 'new_m4dsalesuser'
            Operation = 'Upsert'
            BatchSize = 10
            Options = @{
                MatchOn = ,@('new_salesforceid')
            }
        }
    }
    @{
        # Reads account records as an independent root stream.
        Id = 'AccountsSource'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'file'
            Format = @{ Type = 'csv'; Options = @{ EmptyFieldAsNull = $true } }
            ConnectionId = 'example-files'
            Path = 'account.csv'
            Key = 'id'
        }
    }
    @{
        # Converts source account fields into Dataverse account columns.
        Id = 'MapAccounts'
        Type = 'transform'
        Inputs = @('AccountsSource')
        Script = {
            param($row)
            $sourceId = [string]$row.id
            @{
                new_salesforceid = "run2-$sourceId"
                new_sourceownerid = [string]$row.owner_id
                new_sourcerowjson = ConvertTo-Json -InputObject $row -Depth 8 -Compress
                name = [string]$row.name
                accountnumber = [string]$row.account_number
                revenue = [decimal]$row.annual_revenue
                numberofemployees = [int]$row.employee_count
                address1_city = [string]$row.billing_city
                address1_stateorprovince = [string]$row.billing_state
                address1_country = [string]$row.billing_country
            }
        }
    }
    @{
        # Upserts mapped accounts and exposes their target rows for downstream joins.
        Id = 'AccountsTarget'
        Type = 'target'
        Inputs = @('MapAccounts')
        Target = @{
            Type = 'dataverse'
            ConnectionId = 'example-dataverse'
            Table = 'account'
            Operation = 'Upsert'
            BatchSize = 10
            Options = @{
                MatchOn = ,@('new_salesforceid')
            }
        }
    }
    @{
        # Resolves Content from the JSONL record to its sibling sidecar file.
        Id = 'AccountAttachmentsSource'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'file'
            Format = @{
                Type = 'jsonl'
                Options = @{ BinaryColumns = @('Content') }
            }
            ConnectionId = 'example-files'
            Path = 'account-attachments.jsonl'
            Key = 'Id'
        }
    }
    @{
        # Builds the same account key used by AccountsTarget.
        Id = 'AccountAttachmentsJoinKeys'
        Type = 'transform'
        Inputs = @('AccountAttachmentsSource')
        Script = {
            param($row)
            $fields = [ordered]@{}
            foreach ($property in $row.PSObject.Properties) {
                $fields[$property.Name] = $property.Value
            }
            $fields.run2_account_id = "run2-$([string]$row.AccountSourceId)"
            $fields
        }
    }
    @{
        # Associates the sidecar attachment with its newly upserted Dataverse account.
        Id = 'JoinAccountAttachments'
        Type = 'join'
        Inputs = @(
            @{ Job = 'AccountAttachmentsJoinKeys'; As = 'Attachment' }
            @{
                Job = 'AccountsTarget'
                As = 'Account'
                Keys = @(@{ Child = 'run2_account_id'; Parent = 'new_salesforceid' })
            }
        )
        JoinType = 'inner'
    }
    @{
        # Detects MIME type and stages a replacement by streaming the original bytes.
        Id = 'MapAccountAttachment'
        Type = 'transform'
        Inputs = @('JoinAccountAttachments')
        Script = {
            param($row)
            $attachment = $row.Attachment.Content
            if ($attachment -isnot [Migration4Dataverse.Engine.Connectors.MigrationBinaryValue]) {
                throw 'The account attachment source did not resolve Content to a MigrationBinaryValue.'
            }

            $writer = $attachment.CreateWriter($attachment.FileName, $attachment.DetectMimeType())
            try {
                $sourceStream = $attachment.OpenRead()
                try {
                    $sourceStream.CopyTo($writer.Stream)
                }
                finally {
                    $sourceStream.Dispose()
                }
                $mappedAttachment = $writer.Complete()
            }
            finally {
                $writer.Dispose()
            }

            @{
                Id = [string]$row.Account.Id
                new_m4dexamplefile = $mappedAttachment
            }
        }
    }
    @{
        # Uploads the file-backed value to the account File column in Dataverse.
        Id = 'AccountAttachmentTarget'
        Type = 'target'
        Inputs = @('MapAccountAttachment')
        Target = @{
            Type = 'dataverse'
            ConnectionId = 'example-dataverse'
            Table = 'account'
            Operation = 'Update'
            BatchSize = 1
        }
    }
    @{
        # Reads lead records as an independent root stream.
        Id = 'LeadsSource'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'file'
            Format = @{ Type = 'csv'; Options = @{ EmptyFieldAsNull = $true } }
            ConnectionId = 'example-files'
            Path = 'lead.csv'
            Key = 'id'
        }
    }
    @{
        # Converts source lead fields into Dataverse lead columns.
        Id = 'MapLeads'
        Type = 'transform'
        Inputs = @('LeadsSource')
        Script = {
            param($row)
            $sourceId = [string]$row.id
            $subject = '{0} {1} - {2}' -f $row.first_name, $row.last_name, $row.company
            @{
                new_salesforceid = "run2-$sourceId"
                new_sourceownerid = [string]$row.owner_id
                new_sourcerowjson = ConvertTo-Json -InputObject $row -Depth 8 -Compress
                subject = $subject
                firstname = [string]$row.first_name
                lastname = [string]$row.last_name
                companyname = [string]$row.company
                emailaddress1 = [string]$row.email
                telephone1 = [string]$row.phone
            }
        }
    }
    @{
        # Upserts mapped leads and exposes their target rows for downstream joins.
        Id = 'LeadsTarget'
        Type = 'target'
        Inputs = @('MapLeads')
        Target = @{
            Type = 'dataverse'
            ConnectionId = 'example-dataverse'
            Table = 'lead'
            Operation = 'Upsert'
            BatchSize = 10
            Options = @{
                MatchOn = ,@('new_salesforceid')
            }
        }
    }
)

$steps += @(
    @{
        # Reads contacts independently; account resolution happens in the following join.
        Id = 'ContactsSource'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'file'
            Format = @{ Type = 'csv'; Options = @{ EmptyFieldAsNull = $true } }
            ConnectionId = 'example-files'
            Path = 'contact.csv'
            Key = 'id'
        }
    }
    @{
        # Builds run2 lookup keys without altering the captured source row.
        Id = 'ContactsJoinKeys'
        Type = 'transform'
        Inputs = @('ContactsSource')
        Script = {
            param($row)
            $fields = [ordered]@{}
            foreach ($property in $row.PSObject.Properties) {
                $fields[$property.Name] = $property.Value
            }
            $fields._sourceRowJson = ConvertTo-Json -InputObject $row -Depth 8 -Compress
            $accountId = [string]$row.account_id
            $fields.run2_account_id = if ([string]::IsNullOrWhiteSpace($accountId)) { $null } else { "run2-$accountId" }
            $fields
        }
    }
    @{
        # Left-joins each contact to its account while retaining contacts without an account key.
        Id = 'JoinContacts'
        Type = 'join'
        Inputs = @(
            @{ Job = 'ContactsJoinKeys'; As = 'Contact' }
            @{
                Job = 'AccountsTarget'
                As = 'Account'
                Keys = @(@{ Child = 'run2_account_id'; Parent = 'new_salesforceid' })
            }
        )
        JoinType = 'left'
    }
    @{
        # Maps contact fields and attaches a Dataverse account reference when matched.
        Id = 'MapContacts'
        Type = 'transform'
        Inputs = @('JoinContacts')
        Script = {
            param($row)
            $sourceId = [string]$row.Contact.id
            $accountId = [string]$row.Contact.account_id
            $fields = [ordered]@{
                new_salesforceid = "run2-$sourceId"
                new_sourceownerid = [string]$row.Contact.owner_id
                new_sourcerowjson = [string]$row.Contact._sourceRowJson
                firstname = [string]$row.Contact.first_name
                lastname = [string]$row.Contact.last_name
                emailaddress1 = [string]$row.Contact.email
                telephone1 = [string]$row.Contact.phone
                jobtitle = [string]$row.Contact.title
                department = [string]$row.Contact.department
            }
            if (-not [string]::IsNullOrWhiteSpace($accountId)) {
                if ($null -eq $row.Account) {
                    throw "Contact '$sourceId' references an account that was not matched by JoinContacts."
                }
                $fields.parentcustomerid = @{
                    TableName = 'account'
                    Id = [string]$row.Account.Id
                }
            }
            $fields
        }
    }
    @{
        # Upserts the mapped contacts into Dataverse.
        Id = 'ContactsTarget'
        Type = 'target'
        Inputs = @('MapContacts')
        Target = @{
            Type = 'dataverse'
            ConnectionId = 'example-dataverse'
            Table = 'contact'
            Operation = 'Upsert'
            BatchSize = 10
            Options = @{
                MatchOn = ,@('new_salesforceid')
            }
        }
    }
    @{
        # Reads opportunities independently from the account and lead streams.
        Id = 'OpportunitiesSource'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'file'
            Format = @{ Type = 'csv'; Options = @{ EmptyFieldAsNull = $true } }
            ConnectionId = 'example-files'
            Path = 'opportunity.csv'
            Key = 'id'
        }
    }
    @{
        # Builds run2 lookup keys without altering the captured source row.
        Id = 'OpportunitiesJoinKeys'
        Type = 'transform'
        Inputs = @('OpportunitiesSource')
        Script = {
            param($row)
            $fields = [ordered]@{}
            foreach ($property in $row.PSObject.Properties) {
                $fields[$property.Name] = $property.Value
            }
            $fields._sourceRowJson = ConvertTo-Json -InputObject $row -Depth 8 -Compress
            $accountId = [string]$row.account_id
            $leadId = [string]$row.lead_source_id
            $fields.run2_account_id = if ([string]::IsNullOrWhiteSpace($accountId)) { $null } else { "run2-$accountId" }
            $fields.run2_lead_source_id = if ([string]::IsNullOrWhiteSpace($leadId)) { $null } else { "run2-$leadId" }
            $fields
        }
    }
    @{
        # Resolves each opportunity's account while preserving unmatched opportunities.
        Id = 'JoinOpportunityAccounts'
        Type = 'join'
        Inputs = @(
            @{ Job = 'OpportunitiesJoinKeys'; As = 'Opportunity' }
            @{
                Job = 'AccountsTarget'
                As = 'Account'
                Keys = @(@{ Child = 'run2_account_id'; Parent = 'new_salesforceid' })
            }
        )
        JoinType = 'left'
    }
    @{
        # Adds the opportunity's lead lookup to the already joined account data.
        Id = 'JoinOpportunities'
        Type = 'join'
        Inputs = @(
            @{ Job = 'JoinOpportunityAccounts'; As = 'OpportunityAccount' }
            @{
                Job = 'LeadsTarget'
                As = 'Lead'
                Keys = @(@{ Child = 'Opportunity.run2_lead_source_id'; Parent = 'new_salesforceid' })
            }
        )
        JoinType = 'left'
    }
    @{
        # Maps opportunity fields and creates account and lead references when present.
        Id = 'MapOpportunities'
        Type = 'transform'
        Inputs = @('JoinOpportunities')
        Script = {
            param($row)
            $opportunity = $row.OpportunityAccount.Opportunity
            $account = $row.OpportunityAccount.Account
            $sourceId = [string]$opportunity.id
            $accountId = [string]$opportunity.account_id
            $fields = [ordered]@{
                new_salesforceid = "run2-$sourceId"
                new_sourceownerid = [string]$opportunity.owner_id
                new_sourcerowjson = [string]$opportunity._sourceRowJson
                name = [string]$opportunity.name
                estimatedvalue = [decimal]$opportunity.amount
                closeprobability = [int]$opportunity.probability
                estimatedclosedate = [datetime]::ParseExact([string]$opportunity.close_date, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
            }
            if (-not [string]::IsNullOrWhiteSpace($accountId)) {
                if ($null -eq $account) {
                    throw "Opportunity '$sourceId' references an account that was not matched by JoinOpportunities."
                }
                $fields.accountid = @{
                    TableName = 'account'
                    Id = [string]$account.Id
                }
            }
            $leadId = [string]$opportunity.lead_source_id
            if (-not [string]::IsNullOrWhiteSpace($leadId)) {
                if ($null -eq $row.Lead) {
                    throw "Opportunity '$sourceId' references a lead that was not matched by JoinOpportunities."
                }
                $fields.originatingleadid = @{
                    TableName = 'lead'
                    Id = [string]$row.Lead.Id
                }
            }
            $fields
        }
    }
    @{
        # Upserts the mapped opportunities into Dataverse.
        Id = 'OpportunitiesTarget'
        Type = 'target'
        Inputs = @('MapOpportunities')
        Target = @{
            Type = 'dataverse'
            ConnectionId = 'example-dataverse'
            Table = 'opportunity'
            Operation = 'Upsert'
            BatchSize = 10
            Options = @{
                MatchOn = ,@('new_salesforceid')
            }
        }
    }
)

$steps += @(
    @{
        # Reads orders independently; related account and opportunity rows are joined later.
        Id = 'OrdersSource'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'file'
            Format = @{ Type = 'csv'; Options = @{ EmptyFieldAsNull = $true } }
            ConnectionId = 'example-files'
            Path = 'order.csv'
            Key = 'id'
        }
    }
    @{
        # Builds run2 lookup keys without altering the captured source row.
        Id = 'OrdersJoinKeys'
        Type = 'transform'
        Inputs = @('OrdersSource')
        Script = {
            param($row)
            $fields = [ordered]@{}
            foreach ($property in $row.PSObject.Properties) {
                $fields[$property.Name] = $property.Value
            }
            $fields._sourceRowJson = ConvertTo-Json -InputObject $row -Depth 8 -Compress
            $accountId = [string]$row.account_id
            $opportunityId = [string]$row.opportunity_id
            $fields.run2_account_id = if ([string]::IsNullOrWhiteSpace($accountId)) { $null } else { "run2-$accountId" }
            $fields.run2_opportunity_id = if ([string]::IsNullOrWhiteSpace($opportunityId)) { $null } else { "run2-$opportunityId" }
            $fields
        }
    }
    @{
        # Resolves each order's account while preserving rows with no account match.
        Id = 'JoinOrderAccounts'
        Type = 'join'
        Inputs = @(
            @{ Job = 'OrdersJoinKeys'; As = 'Order' }
            @{
                Job = 'AccountsTarget'
                As = 'Account'
                Keys = @(@{ Child = 'run2_account_id'; Parent = 'new_salesforceid' })
            }
        )
        JoinType = 'left'
    }
    @{
        # Adds the order's opportunity lookup to the joined account data.
        Id = 'JoinOrders'
        Type = 'join'
        Inputs = @(
            @{ Job = 'JoinOrderAccounts'; As = 'OrderAccount' }
            @{
                Job = 'OpportunitiesTarget'
                As = 'Opportunity'
                Keys = @(@{ Child = 'Order.run2_opportunity_id'; Parent = 'new_salesforceid' })
            }
        )
        JoinType = 'left'
    }
    @{
        # Maps order fields and creates references to the matched account and opportunity.
        Id = 'MapOrders'
        Type = 'transform'
        Inputs = @('JoinOrders')
        Script = {
            param($row)
            $order = $row.OrderAccount.Order
            $account = $row.OrderAccount.Account
            $sourceId = [string]$order.id
            $accountId = [string]$order.account_id
            if ([string]::IsNullOrWhiteSpace($accountId)) {
                throw "Salesforce order '$sourceId' has no account_id and cannot be mapped to a Dataverse order."
            }
            if ($null -eq $account) {
                throw "Salesforce order '$sourceId' references an account that was not matched by JoinOrders."
            }
            $orderFields = [ordered]@{
                new_salesforceid = "run2-$sourceId"
                new_sourceownerid = [string]$order.owner_id
                new_sourcerowjson = [string]$order._sourceRowJson
                name = [string]$order.order_number
                customerid = @{
                    TableName = 'account'
                    Id = [string]$account.Id
                }
            }
            $opportunityId = [string]$order.opportunity_id
            if ($null -ne $row.Opportunity) {
                $orderFields.opportunityid = @{
                    TableName = 'opportunity'
                    Id = [string]$row.Opportunity.Id
                }
            }
            # The configured Dataverse organization has one active price list.
            $priceListId = [guid]::Parse('65029c08-f01f-eb11-a812-000d3a33e825')
            $orderFields.pricelevelid = @{
                TableName = 'pricelevel'
                Id = $priceListId
            }
            $orderFields
        }
    }
    @{
        # Upserts the mapped orders into Dataverse.
        Id = 'OrdersTarget'
        Type = 'target'
        Inputs = @('MapOrders')
        Target = @{
            Type = 'dataverse'
            ConnectionId = 'example-dataverse'
            Table = 'salesorder'
            Operation = 'Upsert'
            BatchSize = 10
            Options = @{
                MatchOn = ,@('new_salesforceid')
            }
        }
    }
)

$steps += @(
    @{
        # Reads order-item rows independently of the order stream.
        Id = 'OrderItemsSource'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'file'
            Format = @{ Type = 'csv'; Options = @{ EmptyFieldAsNull = $true } }
            ConnectionId = 'example-files'
            Path = 'order_items.csv'
            Key = 'id'
        }
    }
    @{
        # Builds run2 lookup keys without altering the captured source row.
        Id = 'OrderItemsJoinKeys'
        Type = 'transform'
        Inputs = @('OrderItemsSource')
        Script = {
            param($row)
            $fields = [ordered]@{}
            foreach ($property in $row.PSObject.Properties) {
                $fields[$property.Name] = $property.Value
            }
            $fields._sourceRowJson = ConvertTo-Json -InputObject $row -Depth 8 -Compress
            $orderId = [string]$row.order_id
            $fields.run2_order_id = if ([string]::IsNullOrWhiteSpace($orderId)) { $null } else { "run2-$orderId" }
            $fields
        }
    }
    @{
        # Keeps only order items whose parent order was successfully written.
        Id = 'JoinOrderItems'
        Type = 'join'
        Inputs = @(
            @{ Job = 'OrderItemsJoinKeys'; As = 'OrderItem' }
            @{
                Job = 'OrdersTarget'
                As = 'Order'
                Keys = @(@{ Child = 'run2_order_id'; Parent = 'new_salesforceid' })
            }
        )
        JoinType = 'inner'
    }
    @{
        # Maps each order item and adds the matched Dataverse sales-order reference.
        Id = 'MapOrderItems'
        Type = 'transform'
        Inputs = @('JoinOrderItems')
        Script = {
            param($row)
            $sourceId = [string]$row.OrderItem.id
            @{
                new_salesforceid = "run2-$sourceId"
                new_sourcerowjson = [string]$row.OrderItem._sourceRowJson
                salesorderid = @{
                    TableName = 'salesorder'
                    Id = [string]$row.Order.Id
                }
                isproductoverridden = $true
                productdescription = [string]$row.OrderItem.product_name
                quantity = [decimal]$row.OrderItem.quantity
                priceperunit = [decimal]$row.OrderItem.unit_price
            }
        }
    }
    @{
        # Upserts mapped order items into the Dataverse sales-order-detail table.
        Id = 'OrderItemsTarget'
        Type = 'target'
        Inputs = @('MapOrderItems')
        Target = @{
            Type = 'dataverse'
            ConnectionId = 'example-dataverse'
            Table = 'salesorderdetail'
            Operation = 'Upsert'
            BatchSize = 10
            Options = @{
                MatchOn = ,@('new_salesforceid')
            }
        }
    }
)

$steps += @(
    @{
        # Reads task rows independently from account and opportunity processing.
        Id = 'TasksSource'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'file'
            Format = @{ Type = 'csv'; Options = @{ EmptyFieldAsNull = $true } }
            ConnectionId = 'example-files'
            Path = 'tasks.csv'
            Key = 'id'
        }
    }
    @{
        # Builds run2 lookup keys without altering the captured source row.
        Id = 'TasksJoinKeys'
        Type = 'transform'
        Inputs = @('TasksSource')
        Script = {
            param($row)
            $fields = [ordered]@{}
            foreach ($property in $row.PSObject.Properties) {
                $fields[$property.Name] = $property.Value
            }
            $fields._sourceRowJson = ConvertTo-Json -InputObject $row -Depth 8 -Compress
            $whatId = [string]$row.what_id
            $fields.run2_what_id = if ([string]::IsNullOrWhiteSpace($whatId)) { $null } else { "run2-$whatId" }
            $fields
        }
    }
    @{
        # Resolves task account references while retaining tasks without an account match.
        Id = 'JoinTaskAccounts'
        Type = 'join'
        Inputs = @(
            @{ Job = 'TasksJoinKeys'; As = 'Task' }
            @{
                Job = 'AccountsTarget'
                As = 'Account'
                Keys = @(@{ Child = 'run2_what_id'; Parent = 'new_salesforceid' })
            }
        )
        JoinType = 'left'
    }
    @{
        # Adds task opportunity references to the joined task and account data.
        Id = 'JoinTasks'
        Type = 'join'
        Inputs = @(
            @{ Job = 'JoinTaskAccounts'; As = 'TaskAccount' }
            @{
                Job = 'OpportunitiesTarget'
                As = 'Opportunity'
                Keys = @(@{ Child = 'TaskAccount.Task.run2_what_id'; Parent = 'new_salesforceid' })
            }
        )
        JoinType = 'left'
    }
    @{
        # Maps task fields and resolves its regarding reference from the matched parent.
        Id = 'MapTasks'
        Type = 'transform'
        Inputs = @('JoinTasks')
        Script = {
            param($row)
            $task = $row.TaskAccount.Task
            $sourceId = [string]$task.id
            $fields = [ordered]@{
                new_salesforceid = "run2-$sourceId"
                new_sourceownerid = [string]$task.owner_id
                new_sourcerowjson = [string]$task._sourceRowJson
                subject = [string]$task.subject
                prioritycode = [string]$task.priority
                scheduledend = [datetime]::ParseExact([string]$task.activity_date, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
            }
            $regardingId = [string]$task.what_id
            if ($regardingId.StartsWith('001', [StringComparison]::Ordinal)) {
                $regardingTable = 'account'
            }
            elseif ($regardingId.StartsWith('006', [StringComparison]::Ordinal)) {
                $regardingTable = 'opportunity'
            }
            else {
                $regardingTable = $null
            }
            if ($null -ne $regardingTable) {
                $parent = if ($regardingTable -eq 'account') { $row.TaskAccount.Account } else { $row.Opportunity }
                if ($null -eq $parent) {
                    throw "Task '$($task.id)' references a parent that was not captured by its target step."
                }
                $fields.regardingobjectid = @{
                    TableName = $regardingTable
                    Id = [string]$parent.Id
                }
            }
            $fields
        }
    }
    @{
        # Upserts the mapped tasks into Dataverse.
        Id = 'TasksTarget'
        Type = 'target'
        Inputs = @('MapTasks')
        Target = @{
            Type = 'dataverse'
            ConnectionId = 'example-dataverse'
            Table = 'task'
            Operation = 'Upsert'
            BatchSize = 10
            Options = @{
                MatchOn = ,@('new_salesforceid')
            }
        }
    }
    @{
        # Reads event rows independently from account and opportunity processing.
        Id = 'EventsSource'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'file'
            Format = @{ Type = 'csv'; Options = @{ EmptyFieldAsNull = $true } }
            ConnectionId = 'example-files'
            Path = 'event.csv'
            Key = 'id'
        }
    }
    @{
        # Builds run2 lookup keys without altering the captured source row.
        Id = 'EventsJoinKeys'
        Type = 'transform'
        Inputs = @('EventsSource')
        Script = {
            param($row)
            $fields = [ordered]@{}
            foreach ($property in $row.PSObject.Properties) {
                $fields[$property.Name] = $property.Value
            }
            $fields._sourceRowJson = ConvertTo-Json -InputObject $row -Depth 8 -Compress
            $whatId = [string]$row.what_id
            $fields.run2_what_id = if ([string]::IsNullOrWhiteSpace($whatId)) { $null } else { "run2-$whatId" }
            $fields
        }
    }
    @{
        # Resolves event account references while retaining events without an account match.
        Id = 'JoinEventAccounts'
        Type = 'join'
        Inputs = @(
            @{ Job = 'EventsJoinKeys'; As = 'Event' }
            @{
                Job = 'AccountsTarget'
                As = 'Account'
                Keys = @(@{ Child = 'run2_what_id'; Parent = 'new_salesforceid' })
            }
        )
        JoinType = 'left'
    }
    @{
        # Adds event opportunity references to the joined event and account data.
        Id = 'JoinEvents'
        Type = 'join'
        Inputs = @(
            @{ Job = 'JoinEventAccounts'; As = 'EventAccount' }
            @{
                Job = 'OpportunitiesTarget'
                As = 'Opportunity'
                Keys = @(@{ Child = 'EventAccount.Event.run2_what_id'; Parent = 'new_salesforceid' })
            }
        )
        JoinType = 'left'
    }
    @{
        # Maps event fields and resolves the appropriate account or opportunity reference.
        Id = 'MapEvents'
        Type = 'transform'
        Inputs = @('JoinEvents')
        Script = {
            param($row)
            $event = $row.EventAccount.Event
            $sourceId = [string]$event.id
            $fields = [ordered]@{
                new_salesforceid = "run2-$sourceId"
                new_sourceownerid = [string]$event.owner_id
                new_sourcerowjson = [string]$event._sourceRowJson
                subject = [string]$event.subject
                scheduledstart = [datetime]::Parse([string]$event.start_datetime, [Globalization.CultureInfo]::InvariantCulture)
                scheduledend = [datetime]::Parse([string]$event.end_datetime, [Globalization.CultureInfo]::InvariantCulture)
                location = [string]$event.location
                isalldayevent = [bool]::Parse([string]$event.is_all_day_event)
            }
            $regardingId = [string]$event.what_id
            if ($regardingId.StartsWith('001', [StringComparison]::Ordinal)) {
                $regardingTable = 'account'
            }
            elseif ($regardingId.StartsWith('006', [StringComparison]::Ordinal)) {
                $regardingTable = 'opportunity'
            }
            else {
                $regardingTable = $null
            }
            if ($null -ne $regardingTable) {
                $parent = if ($regardingTable -eq 'account') { $row.EventAccount.Account } else { $row.Opportunity }
                if ($null -eq $parent) {
                    throw "Event '$($event.id)' references a parent that was not captured by its target step."
                }
                $fields.regardingobjectid = @{
                    TableName = $regardingTable
                    Id = [string]$parent.Id
                }
            }
            $fields
        }
    }
    @{
        # Upserts the mapped events into Dataverse.
        Id = 'EventsTarget'
        Type = 'target'
        Inputs = @('MapEvents')
        Target = @{
            Type = 'dataverse'
            ConnectionId = 'example-dataverse'
            Table = 'appointment'
            Operation = 'Upsert'
            BatchSize = 10
            Options = @{
                MatchOn = ,@('new_salesforceid')
            }
        }
    }
)

$runName = 'Salesforce Kaggle sample dataset'

$dashboard = Start-MigrationDashboard -DatabasePath $databasePath
try {
    Write-Host 'Migration dashboard is available at:'
    Write-Host "  $($dashboard.Url)"
    # Dataverse target batch size and concurrency are fixed for the run; legacy tuning profiles are ignored.
    # Clear legacy profiles from older versions with Clear-MigrationTargetTuning if needed.
    Invoke-Migration `
        -DatabasePath $databasePath `
        -RunName $runName `
        -RunId $runId `
        -Steps $steps `
        -Connections $connections `
        -BatchSize 250 `
        -MaxDegreeOfParallelism 32 `
        -EnableDebugCapture `
        -Verbose

    if ([Console]::IsInputRedirected) {
        Write-Host 'Migration completed.'
    }
    else {
        Write-Host 'Migration completed. The dashboard remains open for run and row-trace inspection.'
        Write-Host 'Press Enter to stop the dashboard server.'
        $null = Read-Host
    }
}
catch {
    if (-not [Console]::IsInputRedirected) {
        Write-Host 'Migration failed. The dashboard remains open so you can inspect the failed run.'
        Write-Host "  $($dashboard.Url)"
    }
    Write-Host $_
    if (-not [Console]::IsInputRedirected) {
        Write-Host 'Press Enter to stop the dashboard server.'
        $null = Read-Host
    }
    throw
}
finally {
    Stop-MigrationDashboard -Dashboard $dashboard
}
