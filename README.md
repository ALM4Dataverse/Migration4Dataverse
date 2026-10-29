<p align="center">
  <img src="assets/logo.png" alt="Migration4Dataverse logo" width="180">
</p>

# Migration4Dataverse

**PowerShell migrations for Microsoft Dataverse and local data pipelines.**

> [!WARNING]
> This repository is an early proof of concept, not a production-ready migration product. Review the examples and test them against a non-production environment before using them with real data.

Migration4Dataverse lets you describe a migration as PowerShell steps and run it with `Invoke-Migration`. Source and target connections are supplied separately from the step definitions. The engine handles batching, dependencies, local run history, and recovery.

Supported connectors include CSV, JSON Lines, Parquet, Excel, SQLite, DuckDB, SQL Server, PostgreSQL, MySQL, Oracle, Azure Blob, Amazon S3, Azure Data Lake Storage Gen2, Google Cloud Storage, HTTP/OData, Salesforce Sales Cloud, SharePoint lists, HubSpot, Zendesk, ServiceNow, MongoDB, Dataverse, and custom PowerShell callbacks. A local dashboard can show run progress, outcomes, and optional captured-row lineage.

## Why Migration4Dataverse?

Dataverse migrations are often specific to an organization's data model: each job may need its own mappings, table dependencies, and custom handling. The goal is to make that logic transparent and adaptable without asking every project to build its own orchestration and recovery machinery.

Migration definitions, row mappings, and custom callbacks are ordinary PowerShell scripts. They can be read, reviewed, versioned, and changed without compiling an extension for each transformation. PowerShell remains the flexible orchestration surface; the runtime supplies bounded batching, dependency coordination, persisted run state, checkpoints, and recovery so those mechanics do not have to be reimplemented in every migration.

The project is also intended to run locally without SSIS, a separate SQL Server, or an Azure-hosted execution service. Embedded SQLite and DuckDB can provide local staging and SQL-based processing. A local SQLite owner can coordinate store access for multiple PowerShell processes, while migration work stays in those callers; see [`Start-MigrationDatabaseServer`](./docs/cmdlets/migration-database-server.md).

> [!NOTE]
> This is a single-machine migration tool, not a distributed scale-out platform.

## Quick start

You need PowerShell 7.6 or later, the .NET 10 SDK, and Node.js/npm to build the dashboard assets. From the repository root, build the module's engine and run the CSV example:

```powershell
dotnet build Migration4Dataverse.slnx --configuration Release
pwsh ./examples/csv/Setup-Example.ps1
pwsh ./examples/csv/example.ps1
```

> [!NOTE]
> The examples import the PowerShell module from this checkout. Build the solution in Release configuration before running them.

The setup script prepares the sample input; the example reads it, transforms each row, and publishes one complete CSV output file after the run succeeds. For a different connector, use the examples below as a template and see the connector guides for its connection objects and options.

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
            Type = 'csv'
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

Invoke-Migration -DatabasePath './migration.sqlite3' `
    -RunName 'Account import' -Steps $steps -Connections $connections
```

Create `$dataverseConnection` with `Rnwood.Dataverse.Data.PowerShell`; the Dataverse example below shows how to load a saved connection profile.

The `ConnectionId` values in the steps are names, not credentials or open connections. The actual values are passed separately in `-Connections`: here, `files` points to a folder and `crm` is an authenticated Dataverse connection. This keeps runtime connection details separate from the migration definition.

Start with the [migration basics](./docs/concepts/migration-basics.md) for a plain-language overview. The [step configuration guide](./docs/configuration.md) explains the supported graph shape and exact behavior; the [source](./docs/connectors/sources/README.md) and [target](./docs/connectors/targets/README.md) connector guides describe where rows can come from and go. For Dataverse-specific query, write, authentication, and connection options, see the [Dataverse connector guides](./docs/connectors/sources/dataverse.md) and [upstream module documentation](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell).

## Example migrations

Each target folder contains its own `Setup-Example.ps1` and `example.ps1`. The Dataverse example uses the full [Sample Sales CRM Data](https://www.kaggle.com/datasets/sushicatsan/sample-sales-crm-data) dataset; the other examples use small generated or handcrafted datasets, and the dashboard demo uses synthetic customer records.

| Target | Example | Setup | Demonstrates |
|---|---|---|---|
| Dataverse | [example.ps1](./examples/dataverse/example.ps1) | [Setup-Example.ps1](./examples/dataverse/Setup-Example.ps1) | Imports a multi-table dataset with keyed joins to parent records. |
| Dataverse (sequential) | [example-sequential.ps1](./examples/dataverse/example-sequential.ps1) | [Setup-Example.ps1](./examples/dataverse/Setup-Example.ps1) | Uses completion dependencies and alternate keys for lookups. |
| JSON Lines | [example.ps1](./examples/jsonl/example.ps1) | [Setup-Example.ps1](./examples/jsonl/Setup-Example.ps1) | Uses a `FileInfo` output connection to publish one complete JSON Lines file. |
| CSV | [example.ps1](./examples/csv/example.ps1) | [Setup-Example.ps1](./examples/csv/Setup-Example.ps1) | Reads and writes CSV with explicit column and type settings; outputs one complete file. |
| Parquet | [example.ps1](./examples/parquet/example.ps1) | [Setup-Example.ps1](./examples/parquet/Setup-Example.ps1) | Writes one complete Parquet file using an explicit output schema. |
| SQLite | [example.ps1](./examples/sqlite/example.ps1) | [Setup-Example.ps1](./examples/sqlite/Setup-Example.ps1) | File-path connections, parameterized queries, upfront SQL, per-batch and whole-dataset transforms with managed staging, schema/index validation, and keyed upserts. |
| DuckDB | [example.ps1](./examples/duckdb/example.ps1) | [Setup-Example.ps1](./examples/duckdb/Setup-Example.ps1) | File-path connections and upfront SQL with per-row, per-batch, and whole-dataset transforms. |
| Custom PowerShell | [example.ps1](./examples/custom/example.ps1) | [Setup-Example.ps1](./examples/custom/Setup-Example.ps1) | Writes a batch with a custom PowerShell callback. |

Run setup and migration from the repository root. For example:

```powershell
pwsh ./examples/jsonl/Setup-Example.ps1
pwsh ./examples/jsonl/example.ps1
```

## Dataverse example

The Dataverse setup downloads the pinned sample dataset and stores a device-code-authenticated connection profile named `Migration4DataverseExample`. The migration imports nine CSV files and writes records to the configured organization. It also creates or customizes Dataverse tables and alternate keys, so use a test organization and review the setup script before running it.

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

## Dashboard demo

The dashboard is a local view of run history and progress. Try the Dataverse-free synthetic demo:

```powershell
pwsh ./examples/dashboard/Setup-Example.ps1
pwsh ./examples/dashboard/example.ps1
```

Open the printed `http://127.0.0.1:5180` URL while the sample runs. See the [dashboard guide](./docs/cmdlets/migration-dashboard.md) for configuration previews, row inspection, data retention, and local-access limitations.

![Migration4Dataverse dashboard showing the dependency graph, captured rows, and selected-row lineage](./docs/assets/migration-dashboard-running.png)

## Documentation

- [Documentation index](./docs/README.md) — guides for PowerShell users and a separate section for contributors.
- [PowerShell cmdlets](./docs/cmdlets/README.md) — command syntax and behavior.
- [Step configuration](./docs/configuration.md) — graph shape, connections, callbacks, and step behavior.
- [Connectors](./docs/connectors/sources/README.md) — source and target connector guides.
- [Migration basics](./docs/concepts/migration-basics.md) — sources, transforms, filters, targets, and connections in plain language.
- [Concepts](./docs/concepts/README.md) — dependencies, batching, recovery, and connections.

## For contributors

The C# implementation is an internal detail, not a supported public C# API. Build instructions, scheduler internals, and architecture material are kept in the [developer documentation](./docs/dev/README.md).
