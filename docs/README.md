# Documentation

Choose the section that matches what you are doing. The PowerShell guides describe the supported migration workflow; the developer guides describe repository internals.

## PowerShell users

Start with the [repository README](../README.md) to install the module or build it from a source checkout, then run an example.

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

- [Standalone CLI](./cli.md) — bundled runtimes, commands, options, output, and exit codes.
- [Cmdlets and functions](./cmdlets/README.md) — command index.
- [Invoke-M4dMigration](./cmdlets/invoke-migration.md) — run, resume, preview, and debug capture.
- [Migration dashboard](./cmdlets/migration-dashboard.md) — local monitoring and row inspection.
- [Run inspection and recovery](./cmdlets/migration-inspection-and-recovery.md) — inspect persisted status, errors, and rows or queue recovery from PowerShell.
- [Batching and backpressure](./concepts/batching.md) — queue limits and concurrency behavior.
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
