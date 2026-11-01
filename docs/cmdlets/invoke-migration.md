# Invoke-M4dMigration

Runs a migration defined in an executable PowerShell `.m4d` file. The file assigns one root `$Migration` hashtable and does not call `Invoke-M4dMigration` itself.

The recommended CLI command is:

```sh
m4d run ./migration-job.m4d --database-path ./migration.sqlite3 --run-name "Nightly import"
```

The PowerShell module interface is intended for advanced interactive use and integrations:

```powershell
Invoke-M4dMigration -Path './migration-job.m4d' `
    -DatabasePath './migration.sqlite3' `
    -RunName 'Nightly import'
```

Create a complete commented starter file with [`New-M4dMigrationFile`](./new-migration-file.md).

## Definition shape

```powershell
$Migration = @{
    Steps = @(
        # Migration graph nodes
    )
    Connections = @{}
    RunOptions = @{
        RunName = 'Nightly import'
    }
    PreRun = {
        param($context)
        # Initialize live connections on $context.Connections.
    }
    PostRun = {
        param($context)
        # Release resources or inspect $context.Result / $context.Error.
    }
}
```

`Steps` is required. `Connections` defaults to an empty hashtable. See [migration step configuration](../configuration.md) for node types and connector options.

`PreRun` and `PostRun` are optional scriptblocks receiving the same context object, with `Path`, `Migration`, `Parameters`, `Connections`, `Result`, `Error`, and `PostRunError` properties. `PreRun` can initialize or replace `context.Connections`. `PostRun` is attempted after pre-run was attempted, including when pre-run or migration execution fails. Both hooks run on every invocation, including previews and resumes. If execution and teardown both fail, the reported exception includes both failures and keeps the migration error primary.

## Run options and precedence

Values in `RunOptions` are defaults. Parameters explicitly supplied to `Invoke-M4dMigration` or options explicitly supplied to `m4d run` override matching file defaults. Supported direct options are:

| Parameter | Purpose |
|---|---|
| `DatabasePath` | SQLite migration database path; defaults to `migration.sqlite3` in the current directory. |
| `RunName` | Human-readable run name; defaults to the `.m4d` file name without its extension. |
| `RunId`, `StepHash` | Resume an existing compatible run. |
| `MaxDegreeOfParallelism`, `MaxQueuedBatches`, `MaxQueuedBatchesPerStep` | Bound scheduler concurrency and queued work. |
| `MaxAttempts`, `MaxErrorRatePercent`, `SourceReadMaxAttempts`, `SourceReadRetryDelaySeconds` | Configure retry and row-error handling. |
| `DataverseNoRetryDiagnosticMode` | Disable retries for diagnostic runs. |
| `ModulesToImport`, `RequiredModules`, `ScriptsToLoad`, `SharedScripts` | Load modules and helper code for migration execution. |
| `BatchSize` | Set a batch-size default for supported steps and targets. |
| `EnableDebugCapture`, `DebugCaptureRetentionDays` | Configure persisted debug-data capture and retention. |
| `WhatIf` | Read and transform rows while skipping target writes and batch callbacks. |

The `.m4d` source is included in the default resume-compatibility hash, so changes to top-level helper code and lifecycle hooks prevent silently resuming with changed behavior.

> [!WARNING]
> A `.m4d` file is executable PowerShell, not a restricted `.psd1` data file. Loading it executes its top-level statements. Only load definitions you trust.

> [!NOTE]
> The lower-level graph scheduler is an internal engine implementation. `Invoke-M4dMigration` is the supported file-based PowerShell entry point.
