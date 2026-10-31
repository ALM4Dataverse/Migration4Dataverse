@{
    RootModule = 'Migration4Dataverse.PowerShell.psm1'
    ModuleVersion = '0.1.0'
    GUID = '8f8b9a47-4af3-4f72-a7c9-905fce580a9a'
    Author = 'Migration4Dataverse contributors'
    Description = 'PowerShell entry points for the Migration4Dataverse C# engine.'
    PowerShellVersion = '7.6'
    CompatiblePSEditions = @('Core')
    FunctionsToExport = @(
        'New-MigrationJobStore'
        'Complete-MigrationBatch'
        'Set-MigrationRunStatus'
        'Start-MigrationDashboard'
        'Stop-MigrationDashboard'
    )
    CmdletsToExport = @(
        'Invoke-Migration'
        'Get-MigrationRun'
        'Get-MigrationStep'
        'Get-MigrationBatch'
        'Get-MigrationError'
        'Get-MigrationRetryHistory'
        'Get-MigrationDebugRow'
        'Get-MigrationDebugRowBinary'
        'Get-MigrationConfigurationPreview'
        'Get-MigrationProcessMetric'
        'Get-MigrationDataverseConnectionThroughput'
        'Invoke-MigrationRecovery'
        'Clear-MigrationTargetTuning'
        'Start-MigrationDatabaseServer'
        'New-MigrationBinaryValue'
    )
    VariablesToExport = @()
    AliasesToExport = @()
    PrivateData = @{
        PSData = @{
            Tags = @('Dataverse', 'Migration', 'PowerShell')
        }
    }
}
