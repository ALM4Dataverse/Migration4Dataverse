# Standalone CLI

The Migration4Dataverse CLI is a single-file executable for Windows x64/ARM64, Linux x64/ARM64, and macOS ARM64. Each release bundles the .NET 10.0.13 runtime, PowerShell SDK 7.6.7, the versioned Migration4Dataverse module, and `Rnwood.Dataverse.Data.PowerShell` 3.3.0. No .NET or PowerShell installation, module installation, or runtime download is needed on the target machine; after downloading the matching executable, it can run without internet access unless the migration itself uses an online service.

> [!TIP]
> The self-contained CLI is the recommended way to get started and run ordinary migrations. Use the PowerShell Gallery module when you need an interactive PowerShell workflow, custom orchestration, or direct access to the cmdlet API.

## Get started

Download the executable matching the operating system and CPU architecture from [GitHub Releases](https://github.com/rnwood/Migration4Dataverse/releases), then make it executable on Unix-like systems if needed:

```sh
chmod +x ./Migration4Dataverse.Cli-linux-x64
./Migration4Dataverse.Cli-linux-x64 --help
```

The examples below use `m4d` as the executable name. Rename the downloaded file to `m4d`, put it on `PATH`, or substitute its full path in each command.

`--help` lists user-facing commands and their options. Lower-level graph, store, and value-construction commands are grouped under `advanced`. Command and parameter descriptions come from the bundled PowerShell `Get-Help` content. CLI names remove the `M4dMigration` noun prefix; for example, `Invoke-M4dMigration` is `run`, and `Get-M4dMigrationRun` is `get-run`.

### Advanced: PowerShell module

The module is for users who want to call Migration4Dataverse from PowerShell or build custom orchestration around its commands. It requires PowerShell 7.6 or later, which runs on the .NET 10 runtime. The Gallery installation resolves the Dataverse PowerShell module dependency:

```powershell
Install-Module Migration4Dataverse -Scope CurrentUser
Invoke-M4dMigration -Path ./migration-job.m4d
```

Building from a source checkout has additional contributor prerequisites; see the [developer release guide](./dev/releasing.md).

## Run a migration

Define a migration in a `.m4d` file. It must assign a root `$Migration` hashtable and does not call `Invoke-M4dMigration`:

```powershell
$Migration = @{
    Steps = @(
        # Define the migration graph here.
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

```sh
m4d run ./migration-job.m4d \
  --database-path ./migration.sqlite3 --run-name "Nightly import"
```

Values in `RunOptions` are defaults; explicitly supplied CLI options take precedence. Without either, `DatabasePath` defaults to `./migration.sqlite3` and `RunName` defaults to the file name. `PreRun` and `PostRun` receive the same context object. They run for every invocation, including `--what-if` previews and resumes. `PostRun` is attempted even if pre-run or migration execution fails; when both migration and teardown fail, both errors are reported.

`run` starts the local dashboard by default using the effective database path and the resolved steps/connections (including connections initialized by `PreRun`). It prints the dashboard URL, runs the migration, then keeps the dashboard available until Ctrl+C; after you close it, the command returns the migration's exit code. Use `--no-dashboard` for automated or headless runs. Ctrl+C during the migration cancels the migration.

The same file can be run from PowerShell with `Invoke-M4dMigration -Path ./migration-job.m4d`. `New-M4dMigrationFile -Path ./migration-job.m4d` creates a commented template; the CLI equivalent is `m4d new-file --path ./migration-job.m4d`. A `.m4d` file is executable PowerShell, not a restricted `.psd1` data file; only load definitions you trust.

## Commands and common options

All commands exported by the bundled module are available:

| PowerShell command | CLI command |
|---|---|
| `Invoke-M4dMigration` | `run` |
| `New-M4dMigrationFile` | `new-file` |
| `Get-M4dMigrationRun` | `get-run` |
| `Get-M4dMigrationStep` | `get-step` |
| `Get-M4dMigrationBatch` | `get-batch` |
| `Get-M4dMigrationError` | `get-error` |
| `Get-M4dMigrationRetryHistory` | `get-retry-history` |
| `Get-M4dMigrationDebugRow` | `get-debug-row` |
| `Get-M4dMigrationDebugRowBinary` | `get-debug-row-binary` |
| `Get-M4dMigrationConfigurationPreview` | `get-configuration-preview` |
| `Get-M4dMigrationProcessMetric` | `get-process-metric` |
| `Get-M4dMigrationDataverseConnectionThroughput` | `get-dataverse-connection-throughput` |
| `Invoke-M4dMigrationRecovery` | `invoke-recovery` |
| `Clear-M4dMigrationTargetTuning` | `clear-target-tuning` |
| `Start-M4dMigrationDatabaseServer` | `start-database-server` |
| `Start-M4dMigrationDashboard` | `start-dashboard` |
| `Stop-M4dMigrationDashboard` | `stop-dashboard` |
| `New-M4dMigrationBinaryValue` | `advanced new-binary-value` |
| `New-M4dMigrationJobStore` | `advanced new-job-store` |
| `Complete-M4dMigrationBatch` | `advanced complete-batch` |
| `Set-M4dMigrationRunStatus` | `advanced set-run-status` |
| `Invoke-M4dMigrationGraph` | `advanced invoke-graph` |

Every command accepts `--json` to emit newline-delimited JSON events. `run` takes the `.m4d` file path as its positional argument; the CLI does not execute `.ps1` setup scripts. Commands that accept `DatabasePath` expose `--database-path`; lower-level commands under `advanced` that accept the migration store expose the same option and the CLI opens the store for them. Command-specific options follow the PowerShell parameter names in kebab-case. Run a command's `--help` for its options.

`start-dashboard` starts a standalone dashboard and stays in the foreground until Ctrl+C, then stops it. A dashboard handle is process-local, so `stop-dashboard` is useful when a setup script creates and retains the handle; a separate CLI process cannot stop another process's dashboard.

## Output and exit codes

Without `--json`, command output is human-readable. With `--json`, standard output contains one JSON object per line, each with a `type` and UTC `timestamp`; output, information, warning, progress, error, started, and completed events are represented as separate lines.

| Exit code | Meaning |
|---:|---|
| `0` | Success |
| `1` | Migration or command execution failed |
| `2` | Invalid arguments, script, or job definition |
| `3` | Cancelled with Ctrl+C |
| `4` | Bundled runtime or prerequisite startup failed |

The `--json` option keeps these events on standard output; diagnostics in human-readable mode are written to standard error.
