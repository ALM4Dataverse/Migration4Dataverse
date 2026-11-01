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
        'New-M4dMigrationJobStore'
        'New-M4dMigrationFile'
        'Invoke-M4dMigration'
        'Complete-M4dMigrationBatch'
        'Set-M4dMigrationRunStatus'
        'Start-M4dMigrationDashboard'
        'Stop-M4dMigrationDashboard'
    )
    CmdletsToExport = @(
        'Invoke-M4dMigrationGraph'
        'Get-M4dMigrationRun'
        'Get-M4dMigrationStep'
        'Get-M4dMigrationBatch'
        'Get-M4dMigrationError'
        'Get-M4dMigrationRetryHistory'
        'Get-M4dMigrationDebugRow'
        'Get-M4dMigrationDebugRowBinary'
        'Get-M4dMigrationConfigurationPreview'
        'Get-M4dMigrationComponent'
        'Get-M4dMigrationProcessMetric'
        'Get-M4dMigrationDataverseConnectionThroughput'
        'Invoke-M4dMigrationRecovery'
        'Clear-M4dMigrationTargetTuning'
        'Start-M4dMigrationDatabaseServer'
        'New-M4dMigrationBinaryValue'
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
