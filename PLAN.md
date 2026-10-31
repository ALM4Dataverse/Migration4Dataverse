# Dataverse Integration Replacement: Initial Plan

**Public repository:** [rnwood/Migration4Dataverse](https://github.com/rnwood/Migration4Dataverse)

## Remaining short-term work

- Complete the one-time `v0.1.0` PowerShell Gallery bootstrap release and verify installation in a clean PowerShell 7.6 environment.

## Goal

Build a C# engine for the KingswaySoft capabilities used when targeting Microsoft Dataverse. Embed the PowerShell SDK to execute caller-authored source readers and transformations, and use `Rnwood.Dataverse.Data.PowerShell` as the Dataverse connection and data-access layer.

The production replacement focuses on high-throughput Dataverse writes and related target-side operations. The examples demonstrate JSONL, CSV, Parquet, custom PowerShell, and Dataverse connectors as standalone Source and Target steps connected by Transform/Filter/Append or callback steps. The invoking script supplies live connection objects separately. The engine should not require SSIS package compatibility, but its transformation APIs will implement the subset of SSIS behavior needed by migration workflows. The initial engine target is .NET 10 with `Microsoft.PowerShell.SDK` 7.6, cross-platform; this currently implies PowerShell 7.6 as the embedded runtime baseline.

## Clarified requirements so far

- Support core migration and advanced Dataverse-targeting operations, including extract, create/update/upsert/delete, mapping, lookups/relationships, batching/parallelism, per-row errors/retries/logging/progress, audit or change-tracking reads, custom operations, and files/attachments.
- Expect millions of records per run and many dependent tables.
- Avoid network round trips per row on both sides. Source queries and transformations are authored by the caller; the replacement should accept streamed/chunked input and provide patterns that keep source reads and Dataverse lookups/writes set-based or batched.
- Support multiple Dataverse connections using different identities to distribute work, with bounded concurrency and service-protection-aware handling.
- Use a streaming PowerShell pipeline as the primary input contract, with bounded internal buffering.
- Use a scheduler-managed pool of distinct connections/identities; callers explicitly define table load order.
- Require checkpoint/resume for the first production release. Target the maximum sustained throughput allowed by Dataverse service protection, with a manual throttle override.
- Use SQLite via `Microsoft.Data.Sqlite` for the initial durable job store. Persist pending work and progress before advancing source cursors; use a durable file outside the repository.
- Use `Microsoft.PowerShell.SDK` to host caller-authored PowerShell source and transformation scripts inside the C# engine. Load configured source modules in the runspace; do not evaluate mapping expressions as C#.
- Keep runtime connections outside JSON configuration. Source and Target steps refer to named connection IDs, and the invoker supplies the corresponding objects. Define a reusable connector extension contract before adding more connector implementations.
- Provide an engine-hosted web GUI for near-real-time monitoring of active and completed migrations, including run status/progress, the step pipeline, batch and row outcomes/errors, and run history.
- Offer explicit per-run debug capture of source and target row values, intermediate transformation inputs/outputs, and errors for end-to-end row tracing. Never capture credentials or access tokens; document and configure retention, protect captured data, and avoid materially slowing the migration.
- Assign stable row-lineage IDs and retain source-step and batch references so operators can inspect a row's values and transformation history across stages, including split, merged, and generated child rows.

## Storage and recovery contract

- Bound in-memory pages and queues; apply backpressure when Dataverse writers are busy. Do not retain a whole table or run in memory.
- Persist mapped batches before dispatch. Record per-row outcomes, source cursors, and dependency-release events durably; remove successful payloads only after outcomes/checkpoints are committed.
- Use SQLite transactions for batch state, contiguous checkpoint advancement, and dependency outbox updates. Enable foreign keys, WAL, and full synchronous durability.
- On restart, re-lease expired/in-flight batches and replay uncertain writes. Prefer alternate-key upserts for idempotent replay; non-idempotent operations require explicit reconciliation/idempotency behavior.
- Source readers must support a stable resumable cursor/watermark, or their output must be durably spooled before it is relied on. SQLite protects process crashes when its file remains on durable storage; host/disk loss requires a backup or a future shared-store backend.
- The general scheduler passes each root reader its persisted table checkpoint and resumes sequence numbering after the last contiguous completed batch. Readers must use that checkpoint to continue deterministically and reproduce any already-persisted pending batches.
- Do not persist credentials or access tokens. Keep failed rows available for retry or review and apply an explicit retention policy before cleanup.
- Persist only normalized, replayable step payloads; keep script definitions and module references versioned with the step graph, and never serialize arbitrary live PowerShell/.NET objects as the durable batch format.

## Initial feature inventory

The component names below are the initial KingswaySoft Dataverse surface to investigate. Exact parity should be checked against the installed product version, license, and packages in use.

| Area | Candidate replacement capability | Requested scope / design note |
|---|---|---|
| Connection management | Configure and reuse Dataverse connections, including multiple configured identities and secure non-interactive authentication. | Required; scheduler-managed pool with per-identity concurrency limits. |
| Caller-owned sources | Accept records from sources callers can query with PowerShell; allow caller-authored PowerShell transformations. | Prefer streamed/chunked input and document source-side bulk-query patterns. |
| Typed connectors and connections | Select a source/target type per step and bind it to an invoker-provided named connection. | Define a reusable connector extension contract before adding connector implementations. |
| Data-flow transformations | Apply reusable row and rowset transformations between sources and targets. | Consider transformations that address migration workflows while preserving streaming and bounded-memory behavior. |
| Dataverse source/extract | Query Dataverse tables when Dataverse itself is a source; support useful filters, selected fields, paging, and specialized reads. | Consider change-tracking and audit reads, with service-protection-aware paging and retry behavior. |
| Dataverse destination | Create, update, upsert, and delete records; match by primary/alternate keys; map fields and report outcomes. | Required. |
| Lookup and relationships | Resolve references in bulk, associate/disassociate relationships, and load dependent tables in a defined order. | Required; caller defines table order; avoid a Dataverse request per input row. |
| Field mapping and metadata | Map source-to-target fields, convert common types, handle choices/labels, nulls, dates, lookups, and metadata-driven values. | Required; transformations remain PowerShell-authored. |
| Bulk throughput | Stream/chunk records, batch target writes, and distribute bounded work across connections with different identities. | Critical for millions of records; maximize sustained throughput within service protection, allow manual throttling, and avoid unbounded fan-out. |
| Reliability and diagnostics | Correlate failures to input rows, capture server details, safely retry transient failures, and report progress/results. | Preserve replay safety for long-running runs and avoid persisting credentials or access tokens. |
| Specialized extraction | Incremental/change-tracking and audit-history reads. | Requested; confirm exact modes and semantics against current KWS use. |
| Special destination behavior | File/attachment handling, duplicate detection, owner/state changes, and bypassing business logic where supported. | Requested; confirm exact behaviors and safety requirements. |
| Commands and control flow | Invoke custom APIs/actions, workflows, bulk operations, or other Dataverse requests. | Requested; confirm which operations need first-class job support. |
| Host and integration model | PowerShell 7+ pipelines/jobs on Windows, Linux, and macOS. | Requested; SSIS compatibility and generic source adapters are not in the current scope. |

## Additional future directions

Potential areas include SQL/relational, REST/OData, object-storage, and NoSQL connectors; lookup, distinct, aggregation, conditional split, and validation transformations; and structured logs, durable rejected-row handling, and per-step metrics. Prioritize specific additions according to migration needs.

## Prioritized connector and feature roadmap

This is a proposed order for product value, not a commitment to deliver every item. Priorities 1, 3, 4, and 6–17 are implemented; priorities 2 and 5 remain proposals.

| Priority | Connector or feature | Direction | Initial scope and design points |
|---|---|---|---|
| 1 | SQL Server | Source and target | Add a focused SQL Server connector for parameterized/paged reads and high-throughput batch writes. Plan transaction boundaries, bulk-load behavior, upsert strategy, type mapping, and checkpoint ordering before considering a generic relational-provider abstraction. |
| 2 | Dataverse source and target enhancements | Source and target | Consider change-tracking/incremental and audit reads, service-protection-aware paging and retries, and target-side relationship operations and file/attachment handling. Preserve replay safety and avoid per-row network lookups. |
| 3 | PostgreSQL | Source and target | Add after specifying the relational connector contract for SQL Server. Reuse common batching, parameterization, schema, key/index, and checkpoint semantics without assuming provider-specific upsert behavior is identical. |
| 4 | Object storage | Source and target | Consider Azure Blob/ADLS and S3 for partitioned CSV/Parquet/JSONL datasets, with resumable reads, bounded uploads, and cloud-native identity. Prefer one storage connector design that can support multiple providers without hiding their authentication and consistency differences. |
| 5 | Existing file connector enhancements | Source and target | Prioritize compression, explicit schema/type handling, CSV dialect and encoding options, Parquet partitioning/compression, and atomic/replay-safe file outputs according to user demand. Retain streaming and bounded-memory behavior. |
| 6 | HTTP/REST and OData connectors | Primarily source | Consider only for common APIs that cannot be served well by the paged custom PowerShell connector. Define authentication, rate limiting, pagination, retries, and resumable cursors before adding a generic HTTP surface. |
| 7 | MySQL | Source and target | Add after SQL Server/PostgreSQL using the shared relational contract, with `MySqlConnector`, backtick quoting, `ON DUPLICATE KEY UPDATE`, and current-database schema handling. |
| 8 | Oracle | Source and target | Add with caller-supplied `OracleConnection`, `:name` parameters, current-schema handling, uppercase quoted identifiers, and update-then-insert upsert behavior. |
| 9 | Azure Data Lake Storage Gen2 | Source and target | Reuse the object-storage staging and replay contract with `DataLakeFileSystemClient`/`DataLakeServiceClient` connections and `Options.FileSystem`. |
| 10 | Google Cloud Storage | Source and target | Reuse the object-storage staging and replay contract with `StorageClient`, bucket listings, conditional uploads, and SHA-256 metadata. |
| 11 | MongoDB | Source and target | Add caller-supplied `IMongoClient`/`IMongoDatabase`, BSON-to-JSON conversion, sorted source reads, keyed upserts, and a batch replay ledger. |
| 12 | Salesforce Sales Cloud | Source | Add authenticated REST and Bulk API 2.0 reads with SOQL, count, paging, and caller-supplied `HttpClient` connections. |
| 13 | Excel | Source | Add `.xlsx`/`.xls` workbook reads with sheet/header selection, typed columns, and file connections. |
| 14 | SharePoint lists | Source | Read list items through Microsoft Graph with field selection, filtering, flattening, and next-link paging. |
| 15 | HubSpot | Source | Read CRM objects through list and search endpoints with properties, filters, sorts, and cursor paging. |
| 16 | Zendesk | Source | Read resources and search results with next-link and cursor paging and optional search counts. |
| 17 | ServiceNow | Source | Read Table API records with encoded queries, display values, field selection, and offset paging. |

## Open design questions

- What is the default SQLite file location and backup/retention policy for production runs?
- Cyclic job dependencies are rejected. Which Dataverse relationship cycles need a separate second pass after record creation?
- Which precise KWS version/components and edge-case semantics are required for audit/change tracking, custom operations, and files/attachments?
- What per-identity or global throttle controls are needed?
- What storage budgets or payload-size limits should cap opt-in debug capture for very large runs?
- Which run metrics can be reported as exact values versus estimates when a streaming source does not expose a total count or byte size?
- Should remote/multi-user dashboard access be added later? The initial dashboard is read-only and loopback-only.
- What duration and dependency semantics are required before upgrading the observed longest-chain highlight into a true critical-path calculation or adding completion ETAs?

## References

- [Step, connector, and transformation reference](./docs/reference.md)
- [Module README](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/README.md)
- [Get-DataverseConnection documentation](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Get-DataverseConnection.md)
- [Get-DataverseRecord documentation](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Get-DataverseRecord.md)
- [Set-DataverseRecord documentation](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Rnwood.Dataverse.Data.PowerShell/docs/Set-DataverseRecord.md)
- [Parallelization guidance](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/advanced/parallelization.md)
- [Batch error handling and retry guidance](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/docs/core-concepts/error-handling.md)
- [Module examples comparing batch operations](https://github.com/ALM4Dataverse/Rnwood.Dataverse.Data.PowerShell/blob/main/Examples-Comparison.md)
- [KingswaySoft Dataverse integration components](https://www.kingswaysoft.com/products/ssis-integration-toolkit/help-manual/major-enterprise-applications/dynamics-365/dataverse)
- [KingswaySoft Dataverse/CRM connection manager](https://www.kingswaysoft.com/products/ssis-integration-toolkit/help-manual/major-enterprise-applications/dynamics-365/crm/connection-manager)
