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
Import-Module Rnwood.Dataverse.Data.PowerShell -ErrorAction Stop
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
            Type = 'csv'
            ConnectionId = 'example-files'
            Path = 'user.csv'
            Key = 'id'
            Options = @{ EmptyFieldAsNull = $true }
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
                new_salesforceid = $sourceId
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
            MaxConcurrency = 'auto'
            Table = 'new_m4dsalesuser'
            Operation = 'Upsert'
            BatchSize = 'auto'
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
            Type = 'csv'
            ConnectionId = 'example-files'
            Path = 'account.csv'
            Key = 'id'
            Options = @{ EmptyFieldAsNull = $true }
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
                new_salesforceid = $sourceId
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
            MaxConcurrency = 'auto'
            Table = 'account'
            Operation = 'Upsert'
            BatchSize = 'auto'
            Options = @{
                MatchOn = ,@('new_salesforceid')
            }
        }
    }
    @{
        # Reads lead records as an independent root stream.
        Id = 'LeadsSource'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'csv'
            ConnectionId = 'example-files'
            Path = 'lead.csv'
            Key = 'id'
            Options = @{ EmptyFieldAsNull = $true }
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
                new_salesforceid = $sourceId
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
            MaxConcurrency = 'auto'
            Table = 'lead'
            Operation = 'Upsert'
            BatchSize = 'auto'
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
            Type = 'csv'
            ConnectionId = 'example-files'
            Path = 'contact.csv'
            Key = 'id'
            Options = @{ EmptyFieldAsNull = $true }
        }
    }
    @{
        # Left-joins each contact to its account while retaining contacts without an account key.
        Id = 'JoinContacts'
        Type = 'join'
        Inputs = @(
            @{ Job = 'ContactsSource'; As = 'Contact' }
            @{
                Job = 'AccountsTarget'
                As = 'Account'
                Keys = @(@{ Child = 'account_id'; Parent = 'new_salesforceid' })
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
                new_salesforceid = $sourceId
                new_sourceownerid = [string]$row.Contact.owner_id
                new_sourcerowjson = ConvertTo-Json -InputObject $row.Contact -Depth 8 -Compress
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
            MaxConcurrency = 'auto'
            Table = 'contact'
            Operation = 'Upsert'
            BatchSize = 'auto'
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
            Type = 'csv'
            ConnectionId = 'example-files'
            Path = 'opportunity.csv'
            Key = 'id'
            Options = @{ EmptyFieldAsNull = $true }
        }
    }
    @{
        # Resolves each opportunity's account while preserving unmatched opportunities.
        Id = 'JoinOpportunityAccounts'
        Type = 'join'
        Inputs = @(
            @{ Job = 'OpportunitiesSource'; As = 'Opportunity' }
            @{
                Job = 'AccountsTarget'
                As = 'Account'
                Keys = @(@{ Child = 'account_id'; Parent = 'new_salesforceid' })
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
                Keys = @(@{ Child = 'Opportunity.lead_source_id'; Parent = 'new_salesforceid' })
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
                new_salesforceid = $sourceId
                new_sourceownerid = [string]$opportunity.owner_id
                new_sourcerowjson = ConvertTo-Json -InputObject $opportunity -Depth 8 -Compress
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
            MaxConcurrency = 'auto'
            Table = 'opportunity'
            Operation = 'Upsert'
            BatchSize = 'auto'
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
            Type = 'csv'
            ConnectionId = 'example-files'
            Path = 'order.csv'
            Key = 'id'
            Options = @{ EmptyFieldAsNull = $true }
        }
    }
    @{
        # Resolves each order's account while preserving rows with no account match.
        Id = 'JoinOrderAccounts'
        Type = 'join'
        Inputs = @(
            @{ Job = 'OrdersSource'; As = 'Order' }
            @{
                Job = 'AccountsTarget'
                As = 'Account'
                Keys = @(@{ Child = 'account_id'; Parent = 'new_salesforceid' })
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
                Keys = @(@{ Child = 'Order.opportunity_id'; Parent = 'new_salesforceid' })
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
                new_salesforceid = $sourceId
                new_sourceownerid = [string]$order.owner_id
                new_sourcerowjson = ConvertTo-Json -InputObject $order -Depth 8 -Compress
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
            MaxConcurrency = 'auto'
            Table = 'salesorder'
            Operation = 'Upsert'
            BatchSize = 'auto'
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
            Type = 'csv'
            ConnectionId = 'example-files'
            Path = 'order_items.csv'
            Key = 'id'
            Options = @{ EmptyFieldAsNull = $true }
        }
    }
    @{
        # Keeps only order items whose parent order was successfully written.
        Id = 'JoinOrderItems'
        Type = 'join'
        Inputs = @(
            @{ Job = 'OrderItemsSource'; As = 'OrderItem' }
            @{
                Job = 'OrdersTarget'
                As = 'Order'
                Keys = @(@{ Child = 'order_id'; Parent = 'new_salesforceid' })
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
                new_salesforceid = $sourceId
                new_sourcerowjson = ConvertTo-Json -InputObject $row.OrderItem -Depth 8 -Compress
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
            MaxConcurrency = 'auto'
            Table = 'salesorderdetail'
            Operation = 'Upsert'
            BatchSize = 'auto'
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
            Type = 'csv'
            ConnectionId = 'example-files'
            Path = 'tasks.csv'
            Key = 'id'
            Options = @{ EmptyFieldAsNull = $true }
        }
    }
    @{
        # Resolves task account references while retaining tasks without an account match.
        Id = 'JoinTaskAccounts'
        Type = 'join'
        Inputs = @(
            @{ Job = 'TasksSource'; As = 'Task' }
            @{
                Job = 'AccountsTarget'
                As = 'Account'
                Keys = @(@{ Child = 'what_id'; Parent = 'new_salesforceid' })
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
                Keys = @(@{ Child = 'Task.what_id'; Parent = 'new_salesforceid' })
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
            $fields = [ordered]@{
                new_salesforceid = [string]$task.id
                new_sourceownerid = [string]$task.owner_id
                new_sourcerowjson = ConvertTo-Json -InputObject $task -Depth 8 -Compress
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
            MaxConcurrency = 'auto'
            Table = 'task'
            Operation = 'Upsert'
            BatchSize = 'auto'
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
            Type = 'csv'
            ConnectionId = 'example-files'
            Path = 'event.csv'
            Key = 'id'
            Options = @{ EmptyFieldAsNull = $true }
        }
    }
    @{
        # Resolves event account references while retaining events without an account match.
        Id = 'JoinEventAccounts'
        Type = 'join'
        Inputs = @(
            @{ Job = 'EventsSource'; As = 'Event' }
            @{
                Job = 'AccountsTarget'
                As = 'Account'
                Keys = @(@{ Child = 'what_id'; Parent = 'new_salesforceid' })
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
                Keys = @(@{ Child = 'Event.what_id'; Parent = 'new_salesforceid' })
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
            $fields = [ordered]@{
                new_salesforceid = [string]$event.id
                new_sourceownerid = [string]$event.owner_id
                new_sourcerowjson = ConvertTo-Json -InputObject $event -Depth 8 -Compress
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
            MaxConcurrency = 'auto'
            Table = 'appointment'
            Operation = 'Upsert'
            BatchSize = 'auto'
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
    # Auto-tuned Dataverse limits are reused by target step ID from this database on later runs.
    # Clear one or all saved profiles with Clear-MigrationTargetTuning when retuning is needed.
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
