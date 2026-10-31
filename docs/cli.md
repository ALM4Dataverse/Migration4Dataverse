# Standalone CLI

The Migration4Dataverse CLI is a single-file executable for Windows x64/ARM64, Linux x64/ARM64, and macOS ARM64. Each release bundles the .NET 10.0.13 runtime, PowerShell SDK 7.6.7, the versioned Migration4Dataverse module, and `Rnwood.Dataverse.Data.PowerShell` 3.3.0. No .NET or PowerShell installation, module installation, or runtime download is needed on the target machine; after downloading the matching executable, it can run without internet access unless the migration itself uses an online service.

## Get started

Download the executable matching the operating system and CPU architecture from [GitHub Releases](https://github.com/rnwood/Migration4Dataverse/releases), then make it executable on Unix-like systems if needed:

```sh
chmod +x ./Migration4Dataverse.Cli-linux-x64
./Migration4Dataverse.Cli-linux-x64 --help
```

`--help` lists the exported module commands and their options. Command and parameter descriptions come from the bundled PowerShell `Get-Help` content. CLI names remove the `M4dMigration` noun prefix; for example, `Invoke-M4dMigration` is `run`, and `Get-M4dMigrationRun` is `get-run`.

## Run a migration

The simplest script defines `$Steps` and `$Connections`, and the CLI invokes the migration:

```powershell
# migration-job.ps1
$Steps = @(
    # Define the migration graph here.
)
$Connections = @{
    # Supply the connection objects required by the graph.
}
```

```sh
m4d run --script ./migration-job.ps1 \
  --database-path ./migration.sqlite3 --run-name "Nightly import"
```

Scripts may also invoke `Invoke-M4dMigration` themselves. CLI options are supplied as PowerShell parameter defaults during script execution: they apply when the script omits a parameter, while explicit arguments in the script take precedence. For scripts that only define `$Steps` and `$Connections`, command options are passed directly to the CLI invocation.

Complex PowerShell objects such as connections are best created in the setup script. Where a command parameter is a JSON-compatible array or dictionary, the CLI also exposes a `--<parameter>-json` option, such as `--steps-json`. A `--script` can provide live .NET, PowerShell, or connector objects that JSON cannot represent.

## Commands and common options

All commands exported by the bundled module are available:

| PowerShell command | CLI command |
|---|---|
| `Invoke-M4dMigration` | `run` |
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
| `New-M4dMigrationBinaryValue` | `new-binary-value` |
| `New-M4dMigrationJobStore` | `new-job-store` |
| `Complete-M4dMigrationBatch` | `complete-batch` |
| `Set-M4dMigrationRunStatus` | `set-run-status` |
| `Start-M4dMigrationDashboard` | `start-dashboard` |
| `Stop-M4dMigrationDashboard` | `stop-dashboard` |

Every command accepts `--script` to run a setup script first and `--json` to emit newline-delimited JSON events. Commands that accept `DatabasePath` expose `--database-path`; lower-level functions that accept the migration store expose the same option and the CLI opens the store for them. Command-specific options follow the PowerShell parameter names in kebab-case. Run a command's `--help` for its options.

`start-dashboard` stays in the foreground until Ctrl+C, then stops the dashboard. A dashboard handle is process-local, so `stop-dashboard` is useful when a setup script creates and retains the handle; a separate CLI process cannot stop another process's dashboard.

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
