# Documentation

The self-contained CLI is the recommended way to install and run migrations. The PowerShell module and cmdlet references are for advanced interactive use and custom integrations. The developer guides describe repository internals.

## Start here: CLI users

- [Standalone CLI](./cli.md) — download and run the zero-prerequisite executable, define a `.m4d` file, and use CLI commands.
- [Repository README](../README.md) — quick start, motivations, features, and runnable examples.

### Define a migration

- [Migration basics](./concepts/migration-basics.md) — what sources, transforms, filters, targets, and connections mean.
- [Step configuration](./configuration.md) — supported step types, storage connectors and independent file formats, common properties, connections, and callback shapes.
- [Dependencies](./concepts/dependencies.md) — row flow, keyed source relationships, joins, and completion barriers.
- [Recovery and replay](./concepts/recovery-and-replay.md) — run IDs, checkpoints, retries, and side-effect safety.
- [Connections and callbacks](./concepts/connections-and-callbacks.md) — connection ownership and PowerShell callback rules.
- [Binary values and sidecar files](./concepts/binary-values-and-sidecars.md) — streaming binary values, connector type conventions, and structured-file attachment round-tripping.

### Choose connectors

- [File formats](./connectors/file-formats.md) — select serialization independently from local file or object-storage access, including custom streaming formats.
- [Source connectors](./connectors/sources/README.md) — file and binary content, SQL database, object storage, HTTP/OData, Salesforce, SharePoint, HubSpot, Zendesk, ServiceNow, MongoDB, Dataverse, and custom readers.
- [Target connectors](./connectors/targets/README.md) — file and binary content, SQL database, object storage, SharePoint, MongoDB, Dataverse, and custom writers.
- [Dataverse source](./connectors/sources/dataverse.md) and [Dataverse target](./connectors/targets/dataverse.md) — Migration4Dataverse-specific behavior and links to the upstream command references.

### Run and monitor

- [Migration dashboard](./cmdlets/migration-dashboard.md) — local monitoring and row inspection.
- [Run inspection and recovery](./cmdlets/migration-inspection-and-recovery.md) — inspect persisted status, errors, and rows or queue recovery from the CLI or PowerShell.
- [Batching and backpressure](./concepts/batching.md) — queue limits and concurrency behavior.

## Advanced PowerShell interface

Use the module when you want an interactive PowerShell workflow, custom orchestration, or direct cmdlet access:

- [Cmdlets and functions](./cmdlets/README.md) — command index.
- [Invoke-M4dMigration](./cmdlets/invoke-migration.md) — run, resume, preview, and debug capture.
- [New-M4dMigrationFile](./cmdlets/new-migration-file.md) — create a commented `.m4d` definition template.
- [PowerShell reference](./reference.md) — supported migration contract, organized by topic.

> [!NOTE]
> Dataverse query, write, authentication, and connection options belong to `Rnwood.Dataverse.Data.PowerShell`. The connector guides link to its cmdlet references and concepts instead of duplicating their full option lists.

## Developers and contributors

The C# engine is an internal implementation, not a supported public C# API. These guides are for people changing or testing this repository:

- [Developer documentation](./dev/README.md) — entry point for implementation material.
- [Engine overview](./dev/engine-overview.md) — internal scheduler, development, and test workflow.
- [Releasing](./dev/releasing.md) — module packaging, PowerShell Gallery publishing, and semantic-release workflow.
- [Architecture](./dev/architecture.md) — persistence, scheduling, recovery, and component boundaries.
- [Internal data-flow transformations](./dev/transformations/README.md) — the separate engine transformation pipeline, not `Invoke-M4dMigration` step types.
- [Feature plan](../PLAN.md) — implementation status and remaining work.
