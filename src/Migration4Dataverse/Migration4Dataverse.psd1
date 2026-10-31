@{
    RootModule = 'Migration4Dataverse.psm1'
    ModuleVersion = '0.1.0'
    GUID = '8f8b9a47-4af3-4f72-a7c9-905fce580a9a'
    Author = 'Migration4Dataverse contributors'
    CompanyName = 'Migration4Dataverse'
    Copyright = '(c) 2026 Migration4Dataverse contributors. MIT License.'
    Description = 'PowerShell migrations for Microsoft Dataverse and local data pipelines.'
    PowerShellVersion = '7.6'
    CompatiblePSEditions = @('Core')
    RequiredModules = @(
        @{
            ModuleName = 'Rnwood.Dataverse.Data.PowerShell'
            ModuleVersion = '3.3.0'
        }
    )
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
            Tags = @('Dataverse', 'Migration', 'PowerShell', 'ETL', 'DataMigration')
            LicenseUri = 'https://github.com/rnwood/Migration4Dataverse/blob/main/LICENSE'
            ProjectUri = 'https://github.com/rnwood/Migration4Dataverse'
            ReleaseNotes = 'https://github.com/rnwood/Migration4Dataverse/releases'
        }
    }
}
