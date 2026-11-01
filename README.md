<p align="center">
  <img src="assets/logo.png" alt="Migration4Dataverse logo" width="180">
</p>

# Migration4Dataverse

**A flexible, fast, zero-prerequisite migration engine optimized for Microsoft Dataverse.**

> [!WARNING]
> This repository is an early proof of concept, not a production-ready migration product. Review the examples and test them against a non-production environment before using them with real data.

Migration4Dataverse lets you describe a migration in a readable `.m4d` file and run it with the self-contained `m4d` CLI. Source and target connections are supplied separately from the step definitions. The engine handles batching, dependencies, local run history, and recovery.

Supported connectors include local files (CSV, JSON Lines, Parquet, Excel, binary, and custom formats), Azure Blob, Amazon S3, Azure Data Lake Storage Gen2, Google Cloud Storage, SQLite, DuckDB, SQL Server, PostgreSQL, MySQL, Oracle, HTTP/OData, Salesforce Sales Cloud, SharePoint, HubSpot, Zendesk, ServiceNow, MongoDB, Dataverse, and custom PowerShell callbacks. A local dashboard can show run progress, outcomes, and optional captured-row lineage.

## Why Migration4Dataverse?

Migration4Dataverse is being built as a flexible, fast, and easy-to-install engine optimized for Microsoft Dataverse, while also supporting local files, databases, APIs, and other services. The standalone CLI bundles its runtimes and modules, so users can run migrations without first installing .NET, PowerShell, or a Migration4Dataverse module.

Dataverse migrations are often specific to an organization's data model: each job may need its own mappings, table dependencies, and custom handling. `.m4d` definitions keep that logic transparent and adaptable without asking each project to build its own orchestration and recovery machinery. The format is executable PowerShell, so mappings and callbacks remain easy to review and extend without compiling an extension for each transformation.

The project is designed for the age of AI coding agents as well as human developers: migration files are plain-text, structured, and generated from a complete commented template, making them straightforward to create, inspect, review, and modify. The runtime supplies bounded batching, dependency coordination, persisted run state, checkpoints, and recovery so that those mechanics do not need to be reimplemented in every migration.

Migrations run locally without SSIS, a separate SQL Server, or an Azure-hosted execution service. Embedded SQLite and DuckDB can provide local staging and SQL-based processing.

> [!NOTE]
> This is a single-machine migration tool, not a distributed scale-out platform.

## Features

- Process records incrementally with bounded queues and parallel workers; stream large files through disk-backed staging instead of buffering whole values in memory.
- Choose from file, object-storage, database, API, SaaS, SharePoint, Dataverse, and custom connectors, with file formats selected independently of local or object storage.
- Define mappings, filters, keyed joins, and callbacks as PowerShell steps that can be reviewed and adapted to each migration.
- Extend the engine with custom PowerShell connectors and streaming file-format readers and writers.
- Move binary attachments through file-backed values, sidecars, and Dataverse File/Image or document connectors; mappings can pass values through or apply Base64, JSON-stream, and MIME-type transformations.

## Recommended: install the standalone CLI

Download the executable matching your operating system and CPU architecture from [GitHub Releases](https://github.com/rnwood/Migration4Dataverse/releases). It includes .NET, PowerShell, Migration4Dataverse, and the Dataverse connector module; no runtime or module installation is needed on the target machine.

```sh
# On Linux/macOS, make the downloaded executable runnable.
chmod +x ./Migration4Dataverse.Cli-linux-x64
./Migration4Dataverse.Cli-linux-x64 --help
./Migration4Dataverse.Cli-linux-x64 new-file --path ./migration-job.m4d
./Migration4Dataverse.Cli-linux-x64 run ./migration-job.m4d
```

See the [standalone CLI guide](./docs/cli.md) for supported platforms, commands, JSON events, and exit codes.
Release assets have platform-specific filenames; these examples use the Linux x64 asset. Rename it to `m4d` or substitute the matching full path for your platform.

### Advanced: PowerShell module

Use the PowerShell Gallery module when you need an interactive PowerShell workflow, custom orchestration, or direct cmdlet access. It requires PowerShell 7.6 or later (.NET 10); its Dataverse module dependency is installed automatically:

```powershell
Install-Module Migration4Dataverse -Scope CurrentUser
Invoke-M4dMigration -Path ./migration-job.m4d
```

## Quick start

Create a commented starter file, edit its `$Migration` definition, and run it:

```sh
./Migration4Dataverse.Cli-linux-x64 new-file --path ./migration-job.m4d
# Edit migration-job.m4d to define its source, steps, and target.
./Migration4Dataverse.Cli-linux-x64 run ./migration-job.m4d --database-path ./migration.sqlite3
```

No separate PowerShell, .NET, or module installation is required for these CLI commands. See the [CLI guide](./docs/cli.md) for options and the `.m4d` file contract.

## Define a migration

Think of a migration as rows moving from a source, through optional processing, to a target:

| Part | In simple terms |
|---|---|
| **Source** | Reads rows from somewhere, such as a file, database, or Dataverse. |
| **Transform** | Changes the values or shape of a row, for example renaming fields or converting a date. |
| **Filter** | Decides whether a row should continue. Rows that pass keep their original values. |
| **Target** | Writes the rows to somewhere, such as another file, database, or Dataverse. |

Steps are ordinary PowerShell hashtables. `Inputs` connects steps and says where their rows come from. A simple migration might read a CSV, map its rows, and upsert them to Dataverse:

```powershell
$steps = @(
    @{
        Id = 'AccountsSource'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'file'
            Format = @{ Type = 'csv' }
            ConnectionId = 'files'
            Path = 'account.csv'
            Key = 'id'
        }
    }
    @{
        Id = 'MapAccounts'
        Type = 'transform'
        Inputs = @('AccountsSource')
        Script = {
            param($row)
            @{
                new_salesforceid = [string]$row.id
                name = [string]$row.name
            }
        }
    }
    @{
        Id = 'AccountsTarget'
        Type = 'target'
        Inputs = @('MapAccounts')
        Target = @{
            Type = 'dataverse'
            ConnectionId = 'crm'
            Table = 'account'
            Operation = 'Upsert'
            Options = @{ MatchOn = ,@('new_salesforceid') }
        }
    }
)

$connections = @{
    files = [System.IO.DirectoryInfo]'/data/import'
    crm = $dataverseConnection
}

$Migration = @{
    Steps = $steps
    Connections = $connections
    RunOptions = @{
        DatabasePath = './migration.sqlite3'
        RunName = 'Account import'
    }
}
```

Save this definition as `migration-job.m4d`, then run it with the CLI:

```sh
m4d run ./migration-job.m4d
```

The [Dataverse example](./examples/dataverse/example.m4d) shows how a `PreRun` hook loads a saved connection profile.

The `ConnectionId` values in the steps are names, not credentials or open connections. The actual values are passed separately in `-Connections`: here, `files` points to a folder and `crm` is an authenticated Dataverse connection. This keeps runtime connection details separate from the migration definition.

Start with the [migration basics](./docs/concepts/migration-basics.md) for a plain-language overview. The [step configuration guide](./docs/configuration.md) explains the supported graph shape and exact behavior; the [source](./docs/connectors/sources/README.md) and [target](./docs/connectors/targets/README.md) connector guides describe where rows can come from and go. For Dataverse-specific query, write, authentication, and connection options, see the [Dataverse connector guides](./docs/connectors/sources/dataverse.md) and [upstream module documentation](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell).

## Example migrations

These runnable examples use the supported `.m4d` contract and the CLI.

The Dataverse examples use the full [Sample Sales CRM Data](https://www.kaggle.com/datasets/sushicatsan/sample-sales-crm-data) dataset. Setup also generates a roughly 1 MB text attachment beside a JSONL reference, keeping the file bytes out of the structured record.

| Target | Example | Setup | Demonstrates |
|---|---|---|---|
| Dataverse | [example.m4d](./examples/dataverse/example.m4d) | [Setup-Example.ps1](./examples/dataverse/Setup-Example.ps1) | Imports a multi-table dataset with keyed joins; resolves a JSONL sidecar into a file-backed value, detects its MIME type, and uploads a streamed replacement to a Dataverse File column. |
| Dataverse (sequential) | [example-sequential.m4d](./examples/dataverse/example-sequential.m4d) | [Setup-Example.ps1](./examples/dataverse/Setup-Example.ps1) | Uses completion dependencies and alternate keys, then passes the same sidecar-backed value unchanged to a Dataverse File column. |
| Local files (offline) | [example.m4d](./examples/error-recovery/example.m4d) | Optional [Setup-Example.ps1](./examples/error-recovery/Setup-Example.ps1) | Captures a Transform row error for inspection and retry through CLI recovery commands. |
| Local files (offline) | [example-filter.m4d](./examples/error-recovery/example-filter.m4d) | Optional [Setup-Example.ps1](./examples/error-recovery/Setup-Example.ps1) | Captures a Filter row error for inspection and retry through CLI recovery commands. |

Run the local example without an external service or PowerShell setup step:

```sh
./Migration4Dataverse.Cli-linux-x64 run ./examples/error-recovery/example.m4d --run-id error-recovery-demo
./Migration4Dataverse.Cli-linux-x64 get-error --database-path ./examples/error-recovery/generated-data/migration.sqlite3 \
  --run-id error-recovery-demo
./Migration4Dataverse.Cli-linux-x64 run ./examples/error-recovery/example-filter.m4d --run-id filter-recovery-demo
```

Use `get-error` to identify the failed batch and row, then `invoke-recovery` to queue a corrected payload. Rerun the same `.m4d` file with the same `--run-id`.

For Dataverse, first prepare a test organization and sample dataset with the setup script, then run either migration file:

```powershell
# Dataset/org setup is a separate PowerShell preparation script, not a migration file.
Install-Module Rnwood.Dataverse.Data.PowerShell -MinimumVersion 3.3.0 -Scope CurrentUser
pwsh ./examples/dataverse/Setup-Example.ps1
```

```sh
./Migration4Dataverse.Cli-linux-x64 run ./examples/dataverse/example.m4d
./Migration4Dataverse.Cli-linux-x64 run ./examples/dataverse/example-sequential.m4d
```

The setup uses PowerShell 7.6 or later and the Dataverse PowerShell module because it provisions the connection profile and Dataverse metadata. The self-contained CLI needs no runtime prerequisites to run the migration itself. The Dataverse setup downloads the pinned sample dataset and stores a device-code-authenticated connection profile named `Migration4DataverseExample`. The migration imports nine CSV files and writes records to the configured organization. It also creates or customizes Dataverse tables, alternate keys, and an account File column, so use a test organization and review the setup script before running it. The generated attachment is a separate file referenced from JSONL; the primary example streams it through a mapping that detects MIME type, while the sequential example passes it through unchanged.

For sign-in and connection management, see the upstream [authentication guide](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/getting-started/authentication.md), [connections concept](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/core-concepts/connections.md), and [`Get-DataverseConnection` cmdlet reference](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Get-DataverseConnection.md). The [Dataverse source](./docs/connectors/sources/dataverse.md) and [Dataverse target](./docs/connectors/targets/dataverse.md) guides describe the migration adapter's additional rules.

Pass `--run-id` with the same ID to resume either example.

> [!IMPORTANT]
> The sample uses a custom table for synthetic Salesforce users; Dataverse assigns imported records to the signed-in user. Source owner IDs are retained as data, not used to set Dataverse ownership. Monetary values have no source currency and use the active price list's currency.

## Dashboard

`run` starts the dashboard automatically using the effective database path and the resolved migration configuration, including connections initialized by `PreRun`. It prints the URL, runs the migration, and keeps the dashboard available until Ctrl+C; after closing it, the command returns the migration's exit code. Use `--no-dashboard` for automated or headless runs.

```sh
./Migration4Dataverse.Cli-linux-x64 run ./examples/dataverse/example-sequential.m4d
```

To start a standalone dashboard, run `start-dashboard` with the same `--database-path`; it remains in the foreground until Ctrl+C. See the [dashboard guide](./docs/cmdlets/migration-dashboard.md) for configuration previews, row inspection, data retention, and local-access limitations.

![Migration4Dataverse dashboard showing the dependency graph, captured rows, and selected-row lineage](./docs/assets/migration-dashboard-running.png)

## Documentation

- [Documentation index](./docs/README.md) — CLI-first user guidance, advanced PowerShell references, and contributor documentation.
- [Standalone CLI](./docs/cli.md) — the recommended zero-prerequisite install and command reference.
- [PowerShell cmdlets](./docs/cmdlets/README.md) — advanced API command syntax and behavior.
- [Step configuration](./docs/configuration.md) — graph shape, connections, callbacks, and step behavior.
- [Connectors](./docs/connectors/sources/README.md) — source and target connector guides.
- [Migration basics](./docs/concepts/migration-basics.md) — sources, transforms, filters, targets, and connections in plain language.
- [Binary values and sidecar files](./docs/concepts/binary-values-and-sidecars.md) — streaming file values, connector types, and attachment round-tripping.
- [Concepts](./docs/concepts/README.md) — dependencies, batching, recovery, binary values, and connections.

## For contributors

The C# implementation is an internal detail, not a supported public C# API. Build instructions, scheduler internals, and architecture material are kept in the [developer documentation](./docs/dev/README.md). Release automation and package publishing are documented in the [releasing guide](./docs/dev/releasing.md).

## License

MIT. See [LICENSE](./LICENSE).
