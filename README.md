<p align="center">
  <img src="assets/logo.png" alt="Migration4Dataverse logo" width="180">
</p>

# Migration4Dataverse

**PowerShell migrations for Microsoft Dataverse and local data pipelines.**

> [!WARNING]
> This repository is an early proof of concept, not a production-ready migration product. Review the examples and test them against a non-production environment before using them with real data.

Migration4Dataverse lets you describe a migration as PowerShell steps and run it with `Invoke-M4dMigration`. Source and target connections are supplied separately from the step definitions. The engine handles batching, dependencies, local run history, and recovery.

Supported connectors include local files (CSV, JSON Lines, Parquet, Excel, binary, and custom formats), Azure Blob, Amazon S3, Azure Data Lake Storage Gen2, Google Cloud Storage, SQLite, DuckDB, SQL Server, PostgreSQL, MySQL, Oracle, HTTP/OData, Salesforce Sales Cloud, SharePoint, HubSpot, Zendesk, ServiceNow, MongoDB, Dataverse, and custom PowerShell callbacks. A local dashboard can show run progress, outcomes, and optional captured-row lineage.

## Why Migration4Dataverse?

Dataverse migrations are often specific to an organization's data model: each job may need its own mappings, table dependencies, and custom handling. The goal is to make that logic transparent and adaptable without asking every project to build its own orchestration and recovery machinery.

Migration definitions, row mappings, and custom callbacks are ordinary PowerShell scripts. They can be read, reviewed, versioned, and changed without compiling an extension for each transformation. PowerShell remains the flexible orchestration surface; the runtime supplies bounded batching, dependency coordination, persisted run state, checkpoints, and recovery so those mechanics do not have to be reimplemented in every migration.

The project is also intended to run locally without SSIS, a separate SQL Server, or an Azure-hosted execution service. Embedded SQLite and DuckDB can provide local staging and SQL-based processing. A local SQLite owner can coordinate store access for multiple PowerShell processes, while migration work stays in those callers; see [`Start-M4dMigrationDatabaseServer`](./docs/cmdlets/migration-database-server.md).

> [!NOTE]
> This is a single-machine migration tool, not a distributed scale-out platform.

## Features

- Process records incrementally with bounded queues and parallel workers; stream large files through disk-backed staging instead of buffering whole values in memory.
- Choose from file, object-storage, database, API, SaaS, SharePoint, Dataverse, and custom connectors, with file formats selected independently of local or object storage.
- Define mappings, filters, keyed joins, and callbacks as PowerShell steps that can be reviewed and adapted to each migration.
- Extend the engine with custom PowerShell connectors and streaming file-format readers and writers.
- Move binary attachments through file-backed values, sidecars, and Dataverse File/Image or document connectors; mappings can pass values through or apply Base64, JSON-stream, and MIME-type transformations.

## Install

Install the module from the PowerShell Gallery:

```powershell
Install-Module Migration4Dataverse -Scope CurrentUser
```

The Gallery package requires PowerShell 7.6 or later and installs the required `Rnwood.Dataverse.Data.PowerShell` connector (3.3.0 or later) as a dependency.

## Standalone CLI

Releases also provide a single-file CLI for Windows x64/ARM64, Linux x64/ARM64, and macOS ARM64. It bundles pinned .NET 10, PowerShell 7.6.7, and Dataverse PowerShell 3.3.0; no runtime or module installation is needed, and the executable can run offline after download.
Release assets have platform-specific filenames; rename the downloaded executable to `m4d` or use its full path in the examples below.

```sh
m4d --help
m4d run --script ./migration-job.ps1 --database-path ./migration.sqlite3
m4d get-run --database-path ./migration.sqlite3 --run-id <run-id> --json
```

See the [standalone CLI guide](./docs/cli.md) for script modes, commands, JSON events, and exit codes.

## Quick start

The repository examples run from a source checkout. Building from source requires PowerShell 7.6 or later, the .NET 10 SDK, and Node.js 24 or later with npm 11 or later to build the dashboard assets. Build the module, install the Dataverse connector module, and run the Dataverse example from the repository root:

```powershell
dotnet build Migration4Dataverse.slnx --configuration Release
Install-Module Rnwood.Dataverse.Data.PowerShell -MinimumVersion 3.3.0 -Scope CurrentUser
pwsh ./examples/dataverse/Setup-Example.ps1
pwsh ./examples/dataverse/example.ps1
```

> [!NOTE]
> The examples import the PowerShell module from this checkout. Build the solution in Release configuration before running them.

To exercise error inspection and recovery locally without an external service:

```powershell
pwsh ./examples/error-recovery/Setup-Example.ps1
pwsh ./examples/error-recovery/example.ps1
```

> [!NOTE]
> The module manifest requires `Rnwood.Dataverse.Data.PowerShell` 3.3.0 or later even for offline examples. Install it with `Install-Module Rnwood.Dataverse.Data.PowerShell -MinimumVersion 3.3.0 -Scope CurrentUser` if you have not already.

The recovery example pauses after each expected failure so you can inspect it in the dashboard before applying the demonstrated cmdlet action.

The setup script downloads the sample dataset, creates a JSONL sidecar reference and a generated text attachment, and prepares the Dataverse connection and File column. The migration maps the attachment to that account and streams it to Dataverse.

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

Invoke-M4dMigration -DatabasePath './migration.sqlite3' `
    -RunName 'Account import' -Steps $steps -Connections $connections
```

Create `$dataverseConnection` with `Rnwood.Dataverse.Data.PowerShell`; the Dataverse example below shows how to load a saved connection profile.

The `ConnectionId` values in the steps are names, not credentials or open connections. The actual values are passed separately in `-Connections`: here, `files` points to a folder and `crm` is an authenticated Dataverse connection. This keeps runtime connection details separate from the migration definition.

Start with the [migration basics](./docs/concepts/migration-basics.md) for a plain-language overview. The [step configuration guide](./docs/configuration.md) explains the supported graph shape and exact behavior; the [source](./docs/connectors/sources/README.md) and [target](./docs/connectors/targets/README.md) connector guides describe where rows can come from and go. For Dataverse-specific query, write, authentication, and connection options, see the [Dataverse connector guides](./docs/connectors/sources/dataverse.md) and [upstream module documentation](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell).

## Example migrations

The Dataverse examples use the full [Sample Sales CRM Data](https://www.kaggle.com/datasets/sushicatsan/sample-sales-crm-data) dataset. Setup also generates a roughly 1 MB text attachment beside a JSONL reference, keeping the file bytes out of the structured record.

| Target | Example | Setup | Demonstrates |
|---|---|---|---|
| Dataverse | [example.ps1](./examples/dataverse/example.ps1) | [Setup-Example.ps1](./examples/dataverse/Setup-Example.ps1) | Imports a multi-table dataset with keyed joins; resolves a JSONL sidecar into a file-backed value, detects its MIME type, and uploads a streamed replacement to a Dataverse File column. |
| Dataverse (sequential) | [example-sequential.ps1](./examples/dataverse/example-sequential.ps1) | [Setup-Example.ps1](./examples/dataverse/Setup-Example.ps1) | Uses completion dependencies and alternate keys, then passes the same sidecar-backed value unchanged to a Dataverse File column. |
| Local files (offline) | [example.ps1](./examples/error-recovery/example.ps1) | [Setup-Example.ps1](./examples/error-recovery/Setup-Example.ps1) | Inspects and recovers from source-reader, Transform, Filter, Target, and callback errors with the dashboard and PowerShell cmdlets. |

Run setup and migration from the repository root:

```powershell
pwsh ./examples/dataverse/Setup-Example.ps1
pwsh ./examples/dataverse/example.ps1
```

## Dataverse example

The Dataverse setup downloads the pinned sample dataset and stores a device-code-authenticated connection profile named `Migration4DataverseExample`. The migration imports nine CSV files and writes records to the configured organization. It also creates or customizes Dataverse tables, alternate keys, and an account File column, so use a test organization and review the setup script before running it. The generated attachment is a separate file referenced from JSONL; the primary example streams it through a mapping that detects MIME type, while the sequential example passes it through unchanged.

Install the required PowerShell module (version 3.3.0 or later), then set up and run the sample:

```powershell
Install-Module Rnwood.Dataverse.Data.PowerShell -MinimumVersion 3.3.0 -Scope CurrentUser
pwsh ./examples/dataverse/Setup-Example.ps1
pwsh ./examples/dataverse/example.ps1
```

For sign-in and connection management, see the upstream [authentication guide](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/getting-started/authentication.md), [connections concept](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/core-concepts/connections.md), and [`Get-DataverseConnection` cmdlet reference](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Get-DataverseConnection.md). The [Dataverse source](./docs/connectors/sources/dataverse.md) and [Dataverse target](./docs/connectors/targets/dataverse.md) guides describe the migration adapter's additional rules.

Set `$env:M4D_RUN_ID` to resume the primary example with its existing run ID. The sequential example uses a separate database and can be resumed with `$env:M4D_SEQUENTIAL_RUN_ID`.

> [!IMPORTANT]
> The sample uses a custom table for synthetic Salesforce users; Dataverse assigns imported records to the signed-in user. Source owner IDs are retained as data, not used to set Dataverse ownership. Monetary values have no source currency and use the active price list's currency.

## Dashboard

The Dataverse examples start a local dashboard so you can inspect run history, migration progress, and captured row lineage.

Open the printed `http://127.0.0.1:5180` URL while a migration runs. See the [dashboard guide](./docs/cmdlets/migration-dashboard.md) for configuration previews, row inspection, data retention, and local-access limitations.

![Migration4Dataverse dashboard showing the dependency graph, captured rows, and selected-row lineage](./docs/assets/migration-dashboard-running.png)

## Documentation

- [Documentation index](./docs/README.md) — guides for PowerShell users and a separate section for contributors.
- [PowerShell cmdlets](./docs/cmdlets/README.md) — command syntax and behavior.
- [Step configuration](./docs/configuration.md) — graph shape, connections, callbacks, and step behavior.
- [Connectors](./docs/connectors/sources/README.md) — source and target connector guides.
- [Migration basics](./docs/concepts/migration-basics.md) — sources, transforms, filters, targets, and connections in plain language.
- [Binary values and sidecar files](./docs/concepts/binary-values-and-sidecars.md) — streaming file values, connector types, and attachment round-tripping.
- [Concepts](./docs/concepts/README.md) — dependencies, batching, recovery, binary values, and connections.

## For contributors

The C# implementation is an internal detail, not a supported public C# API. Build instructions, scheduler internals, and architecture material are kept in the [developer documentation](./docs/dev/README.md). Release automation and package publishing are documented in the [releasing guide](./docs/dev/releasing.md).

## License

MIT. See [LICENSE](./LICENSE).
