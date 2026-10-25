# Dataverse Integration Replacement: Initial Plan

**Status:** The C# engine, embedded PowerShell SDK, and SQLite are implemented. `Invoke-Migration` runs a top-level DAG of Source, Transform, Filter, Append, Target, and callback steps through the C# dependency scheduler. Connector definitions and script blocks are inline on their respective steps; `Inputs` declares graph edges. The dashboard, opt-in row capture, and recursive lineage tracing are implemented. Dataverse auto tuning now includes pipeline-weighted fair dispatch, globally measured per-target batch experiments, a shared adaptive concurrency budget per connection, and optional retry/throttle telemetry. Stream-wide Merge/Sort are currently available only through the separate data-flow API; production Dataverse hardening remains.
**Public repository:** [rnwood/Migration4Dataverse](https://github.com/rnwood/Migration4Dataverse)

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

## Rough outline

1. **Freeze the execution contract**
   - Define connection-pool limits, SQLite checkpoint persistence, and restart semantics.
   - Inventory the KingswaySoft version/components and workflows to confirm detailed feature parity.
2. **Map capabilities to the PowerShell module**
   - Map each required KingswaySoft workflow to module cmdlets and identify gaps.
   - Prove connection, query, write, lookup, batch, retry, and error behavior against a Dataverse test environment.
   - Separately prove multi-identity scheduling; the module's parallel workers clone a supplied connection and do not by themselves provide a pool of different identities.
3. **Build the bulk execution core**
   - Host bounded PowerShell runspaces from the C# engine for source-page and transformation scripts.
   - Accept streamed/chunked caller data without materializing a multi-million-row run in memory.
   - Model transformations as a named DAG; stream row-preserving transforms where possible and use explicit memory/disk bounds for blocking transforms.
   - Batch writes and lookups, schedule work across configured identities with per-identity concurrency limits, and correlate each result/error to its input.
   - Follow caller-defined table order and resolve relationships without a remote lookup for every row.
   - Persist batches, checkpoints, and dependency events in SQLite so interrupted runs can resume safely.
4. **Add the requested Dataverse capabilities**
   - Implement read/extract, create/update/upsert/delete, mapping and choices, relationships, audit/change tracking, custom operations, and file/attachment workflows.
   - Add idempotency, checkpoints/restart, and operational reporting where required for long-running runs.
5. **Add migration monitoring and row lineage**
   - Build an engine-hosted web GUI for active and completed runs, with near-real-time status/progress, job/table pipeline views, batch and row outcomes/errors, and run history.
   - Use a hybrid dashboard and interactive DAG: show dependency state, execution state, progress, counts, throughput, and failure impact together rather than relying on a graph alone. Keep each job as its own node and show configured source/target details on applicable steps.
   - Provide a run overview banner and health summary for elapsed time, completed/total objects and records, data volume where measurable, node state counts, success/failure counts, current throughput, and estimated completion. Mark unknown totals and estimates clearly; do not imply precise percentages or ETAs when source totals or reliable rates are unavailable.
   - Scale navigation to large pipelines without merging steps into entity summaries. Do not make a graph of 200+ nodes the default view; provide a dependency-level overview, search/filtering, and a focused graph to avoid an unreadable "hairball."
   - Add node inspection for status, start time/duration, record counts, throughput, retries, dependency state, logs, metrics, errors, batches, and row outcomes. When debug capture is enabled, allow inspection of row values and lineage through each stage.
   - Include bottleneck/runtime summaries and an execution timeline; add critical-path highlighting to show which unfinished dependencies constrain completion.
   - Represent pending, queued, running, completed, failed, and blocked states with consistent visual indicators. Do not rely on color alone; expose labels or symbols for status and failure.
   - Add opt-in per-run debug capture of row inputs and outputs at source, target, and intermediate transformation stages. Keep credentials and access tokens out of captured data; define access controls and configurable retention because payloads may be sensitive and large.
   - Preserve stable row-lineage IDs and source/job/batch references through transformations and dependencies, including split, merge, and generated-child relationships, so the GUI can show a row's values and stage history.
   - Target status/progress updates within a few seconds and make debug events available promptly, while measuring and limiting capture overhead. Test dashboard responsiveness and information density with both typical and synthetic 50+ and 200+ node pipelines.
6. **Harden and release**
   - Test mapping and failure cases with mocks, then validate against a non-production Dataverse environment.
   - Load-test multi-million-row runs, memory bounds, batch sizes, identity distribution, and throttling/retry behavior.
   - Document setup, security, supported PowerShell 7+ platforms, GUI access, debug capture, retention, and deployment.

## Initial feature inventory to validate

The component names below are the initial KingswaySoft Dataverse surface to investigate. Exact parity should be checked against the installed product version, license, and packages in use.

| Area | Candidate replacement capability | Requested scope / design note |
|---|---|---|
| Connection management | Configure and reuse Dataverse connections, including multiple configured identities and secure non-interactive authentication. | Required; scheduler-managed pool with per-identity concurrency limits. |
| Caller-owned sources | Accept records from any source a caller can query with PowerShell; allow caller-authored PowerShell transformations. | Typed jobs support JSONL, CSV, Parquet, paged custom PowerShell, and Dataverse sources. General connector registration remains future work; prefer streaming/chunked input and document source-side bulk-query patterns. |
| Typed connectors and connections | Select a source/target type per job and bind it to an invoker-provided named connection. | The binary cmdlet supports JSONL/CSV/Parquet/custom/Dataverse sources and Dataverse/file/custom targets per job. There is no general connector plug-in API or fan-out yet. |
| Data-flow transformations | Apply reusable row and rowset transformations between sources and targets. | Initial named DAG supports Append/Union All, sorted Merge, and external-memory Sort. See the transformation inventory below; more SSIS transformations are not yet implemented. |
| Dataverse source/extract | Query Dataverse tables when Dataverse itself is a source; support useful filters, selected fields, paging, and specialized reads. | Baseline implemented through Simple, FetchXML, and read-only Sql4Cds queries. Prioritize change tracking/audit reads and validate paging, throttling, and query edge cases against real organizations. |
| Dataverse destination | Create, update, upsert, and delete records; match by primary/alternate keys; map fields and report outcomes. | Required. |
| Lookup and relationships | Resolve references in bulk, associate/disassociate relationships, and load dependent tables in a defined order. | Required; caller defines table order; avoid a Dataverse request per input row. |
| Field mapping and metadata | Map source-to-target fields, convert common types, handle choices/labels, nulls, dates, lookups, and metadata-driven values. | Required; transformations remain PowerShell-authored. |
| Bulk throughput | Stream/chunk records, batch target writes, and distribute bounded work across connections with different identities. | Critical for millions of records; maximize sustained throughput within service protection, allow manual throttling, and avoid unbounded fan-out. |
| Reliability and diagnostics | Correlate failures to input rows, capture server details, safely retry transient failures, and report progress/results. | Required; SQLite-backed durable checkpoint/resume is required for the first production release. |
| Specialized extraction | Incremental/change-tracking and audit-history reads. | Requested; confirm exact modes and semantics against current KWS use. |
| Special destination behavior | File/attachment handling, duplicate detection, owner/state changes, and bypassing business logic where supported. | Requested; confirm exact behaviors and safety requirements. |
| Commands and control flow | Invoke custom APIs/actions, workflows, bulk operations, or other Dataverse requests. | Requested; confirm which operations need first-class job support. |
| Host and integration model | PowerShell 7+ pipelines/jobs on Windows, Linux, and macOS. | Requested; SSIS compatibility and generic source adapters are not in the current scope. |

## Comparative feature scan: ETLBox and EtlKit

These products suggest useful capabilities to evaluate, not a requirement to match their full scope. Prioritize only features that address representative Dataverse migration workflows; several overlap with the current engine or connector roadmap.

| Capability to evaluate | Examples in other products | Relevance and current gap |
|---|---|---|
| Row validation with reject/error outputs | ETLBox validates rows and can route invalid rows separately; database type checks validate values against destination schema. | Add explicit validation steps and a durable reject path with per-row reasons, rather than relying only on target failures or caller-authored scripts. Decide whether validation schemas come from declared mappings, Dataverse metadata, or both. |
| Conditional routing and fan-out | ETLBox documents conditional split and multicast; EtlKit supports linked data-flow components. | The current graph has Filter and Append but no explicit multi-output split or multicast/fan-out contract. Evaluate routing records to different targets while preserving lineage and dependency behavior. |
| Common row and set transformations | ETLBox includes distinct, lookup, column shaping, row expansion, merge join, aggregation, and pivot; EtlKit documents scripted transforms, filtration, and table merge/sync. | The engine has scripted transforms and Append, with Merge/Sort only in the separate data-flow API. Consider which reusable, typed transformations would remove repeated user scripts without breaking streaming or bounded-memory guarantees. |
| Backpressure and blocking-operation controls | ETLBox distinguishes row-streaming from blocking transforms and exposes configurable component buffers; both products document streaming flows. | Keep explicit buffer budgets and classify every built-in transform as streaming or blocking. Require memory/disk bounds and cancellation behavior for blocking transforms before adding aggregation or joins. |
| Connector breadth | ETLBox documents CSV, JSON, XML, Excel, Parquet, relational and NoSQL databases, and messaging connectors; EtlKit documents database/file integrations plus REST, Kafka, and RabbitMQ. | The current engine has CSV/JSONL/Parquet, custom, and Dataverse connectors; relational connectors and HTTP/OData are already in the roadmap. Expand only against identified migration demand, with pagination, retry, schema, and checkpoint contracts. |
| Schema/type checks and operational diagnostics | ETLBox documents destination type validation and structured logging; EtlKit documents control-flow tasks and logging. | Consider preflight validation for mappings/types and structured, queryable step diagnostics. Keep scheduler/job-store internals separate from user-authored migration control flow. |
| Declarative data-flow definitions | EtlKit documents XML-defined data flows in addition to code-authored flows. | The current public contract is PowerShell-authored and typed. Only consider another definition format if users need versionable, tool-independent job definitions; avoid maintaining parallel formats without a concrete use case. |

Sources: [ETLBox transformation overview](https://www.etlbox.net/docs/transformations/overview/), [ETLBox blocking transformations](https://www.etlbox.net/docs/blocking-transformations/overview/), [ETLBox connector documentation](https://www.etlbox.net/docs/), [ETLBox structured logging](https://www.etlbox.net/docs/logging/structured-logging/), [EtlKit documentation index](https://github.com/etlkit/etlkit/blob/master/docs/README.md), and [EtlKit transformation guide](https://github.com/etlkit/etlkit/blob/master/docs/dataflow/transformations.md).

Initial direction from the user: broaden selectively for migration workloads. Evaluate SQL/relational, REST/OData, object-storage, and NoSQL connectors; lookup, distinct, aggregation, conditional split, and validation transformations; and structured logs, durable rejected-row handling, and per-step metrics. These priorities are not yet ranked or delivery commitments; choose specific implementations from representative workflows before changing delivery order.

## Prioritized connector and feature roadmap

This is a proposed order for product value and implementation learning, not a commitment to deliver every item. Validate it against representative migration workloads before expanding the connector surface.

| Priority | Connector or feature | Direction | Initial scope and design points |
|---|---|---|---|
| 1 | SQLite staging and SQL transforms | Source and target | Add a relational staging connector so users can load intermediate datasets and transform/join them with SQL. Keep staging data in a user-selected database separate from the scheduler's internal job-store database. Read with parameterized, read-only queries and deterministic ordering on a stable key; write batches transactionally with replay-safe keys. |
|  | SQLite schema and index lifecycle | Source and target | Require declared column types and key semantics rather than inferring a durable schema from the first row. Default to validating existing tables and indexes; allow creation of missing objects only through an explicit policy, and never silently drop or alter user schema. Support declared unique keys and indexes for lookup/join workloads, report missing or incompatible definitions, and benchmark whether secondary indexes should be built before or after bulk loads. |
| 2 | SQL Server | Source and target | Add a focused SQL Server connector for parameterized/paged reads and high-throughput batch writes. Validate transaction boundaries, bulk-load behavior, upsert strategy, type mapping, and checkpoint ordering before considering a generic relational-provider abstraction. |
| 3 | Dataverse source and target enhancements | Source and target | Extend the current source with change-tracking/incremental and audit reads where supported. Prioritize service-protection-aware paging and retries, then target-side relationship operations and file/attachment handling based on confirmed workflows. Preserve replay safety and avoid per-row network lookups. |
| 4 | PostgreSQL | Source and target | Add after the relational connector contract is proven with SQLite and SQL Server. Reuse tested batching, parameterization, schema validation, key/index, and checkpoint semantics without assuming provider-specific upsert behavior is identical. |
| 5 | Object storage | Source and target | Consider Azure Blob/ADLS and S3 for partitioned CSV/Parquet/JSONL datasets, with resumable reads, bounded uploads, and cloud-native identity. Prefer one storage connector design that can support multiple providers without hiding their authentication and consistency differences. |
| 6 | Existing file connector enhancements | Source and target | Prioritize compression, explicit schema/type handling, CSV dialect and encoding options, Parquet partitioning/compression, and atomic/replay-safe file outputs according to user demand. Retain streaming and bounded-memory behavior. |
| 7 | HTTP/REST and OData connectors | Primarily source | Consider only for common APIs that cannot be served well by the paged custom PowerShell connector. Define authentication, rate limiting, pagination, retries, and resumable cursors before adding a generic HTTP surface. |

For the SQLite staging connector, the schema/index policy is a prerequisite to implementation, not a convenience detail: indexes improve query and upsert performance but add write cost, inferred types can become unstable across batches, and changing a user-owned database implicitly would be unsafe. The first design should distinguish connector-managed tables from user-managed tables, define how schema versions are validated or migrated, and measure index-build versus per-row maintenance costs on representative loads.

## Module guidance already reviewed

- `Get-DataverseConnection` returns a connection object to pass to other cmdlets. Its documented authentication options include interactive and device-code flows, username/password, client secret/certificate, `DefaultAzureCredential`, managed identity, SDK connection strings, PAC CLI profiles, and custom access-token script blocks. Named connections are available; persistent credentials should not be used casually in production.
- `Get-DataverseRecord` supports multiple retrieval strategies, including table-based queries and FetchXML, and the module documents automatic paging.
- `Set-DataverseRecord` supports create/update/upsert behavior, primary or alternate-key matching, lookup resolution, and related state/status/assignment operations. Multiple records are batched with `ExecuteMultipleRequest` by default (batch size 100); `-BatchSize` configures the batch and `-BatchSize 1` disables batching. Batch errors are reported with the corresponding input record.
- For writes, `-MaxDegreeOfParallelism` enables bounded workers that use cloned copies of the supplied connection; batching and parallelism can be combined. `Invoke-DataverseParallel` is the documented option for multi-step PowerShell work. The typed cmdlet serializes writes that share one Dataverse target connection and sets cmdlet-level parallelism to one. Callers can provide distinct connections under distinct IDs, but identity-aware scheduling remains future work.
- Retries use exponential backoff when configured. They are not automatically safe for every operation (for example, retrying create-only operations can create duplicates), so retry policy must be operation-aware.
- The module can optimize Dataverse operations, but it cannot make an arbitrary source module's query efficient. The caller owns source-side query shape; the replacement should avoid per-row remote calls in its own lookup/write path and provide examples for bulk source reads.
- The desired throughput policy is to maximize sustained performance within Dataverse service-protection limits and permit a manual throttle. A prototype must establish which throttling signals are available and whether concurrency should be reduced dynamically rather than assuming that parallelism alone improves throughput.

The replacement should build on these module behaviors rather than reimplementing Dataverse authentication or transport without a demonstrated gap.

## Open design questions

- What is the default SQLite file location and backup/retention policy for production runs?
- Cyclic job dependencies are rejected. Which Dataverse relationship cycles need a separate second pass after record creation?
- Which precise KWS version/components and edge-case semantics are required for audit/change tracking, custom operations, and files/attachments?
- What per-identity or global throttle controls are needed, and what is the throughput acceptance target once tested under Dataverse service protection?
- What storage budgets or payload-size limits should cap opt-in debug capture for very large runs?
- How should lineage snapshot volume and capture overhead be measured and bounded under production throughput?
- Which run metrics can be reported as exact values versus estimates when a streaming source does not expose a total count or byte size?
- Should remote/multi-user dashboard access be added later? The initial dashboard is read-only and loopback-only.
- What duration and dependency semantics are required before upgrading the observed longest-chain highlight into a true critical-path calculation or adding completion ETAs?

## Implementation progress

- **Done:** Initialized Git, captured the PowerShell-first scope, reviewed the module's connection/batch/parallelization guidance, and agreed on the pipeline, identity-pool, dependency, and crash-recovery direction.
- **Done:** Selected a C# engine with embedded PowerShell SDK 7.6 and SQLite as the initial durable store.
- **Done:** Implemented the .NET 10 solution foundation and SQLite migration runner/store, including an ordered migration ledger, durable batches, lease recovery, failed-row outcomes, contiguous checkpoint advancement, and a transactional dependency outbox.
- **Done:** Implemented the general C# `DependencyScheduler` batch-DAG engine API. Root batch readers use persisted checkpoints; dependent batches are released from durable dependency events, with optional multi-parent prerequisite gating. Worker concurrency and queued/in-flight batches are bounded; SQLite run and batch leases are renewed during work; retries use bounded exponential backoff; invalid results and exhausted attempts are recorded as terminal batch failures. Dependency outbox paging uses a stable keyset cursor so blocked earlier events do not starve later ready events.
- **Done:** Added SQLite schema migration from version 1 to version 2 for scheduler leases and dependency prerequisites.
- **Done:** Added run/step monitoring metadata, retained batch outcomes/timings, opt-in row snapshots, lineage links, and configurable debug snapshot retention.
- **Done:** Added sanitized step/connection configuration snapshots, start/completion connection instance details, configuration-only dashboard preview, searchable/paged step row snapshots with bidirectional lineage tracing, and `Get-MigrationRun` history queries. The dashboard uses the project logo, Fluent UI 9 theme tokens, native controls, and a graph-first collapsible panel layout.
- **Validated (initial scheduler implementation):** All 31 solution tests passed at that stage. The 21 focused scheduler/store tests covered graph-cycle rejection, early parent-to-child streaming, multi-parent gating, bounded concurrency, retries and terminal failures, cancellation/resume, checkpoint resume, expired leases, run-lease exclusivity, atomic dependency materialization, paged event release, and schema migration.
- **Done:** Added embedded PowerShell runspace-pool execution with module imports, script arguments, surfaced nonterminating errors, asynchronous cancellation, and a typed batch-handler adapter.
- **Done:** Added a PowerShell module wrapper that loads the Release engine and exposes SQLite job-store helpers.
- **Done:** Made Release builds copy runtime dependencies and added platform-aware resolution of the bundled SQLite native library so the module can initialize SQLite from a standalone PowerShell process.
- **Done:** Split the initial example into independent target folders, each with a direct `Setup-Example.ps1` and `example.ps1`. Scripts contain no shared example helpers, configuration files, wrapper scripts, or script parameters; source/target connection definitions and connector options are inline.
- **Done:** Removed the interim JSON/PowerShell run-settings files and shared configuration reader. To change example settings, edit that target folder's script directly.
- **Done:** Added a POC README with the job schema/invocation, usage, safety limitations, and a resized logo at `assets/logo.png`.
- **Done:** Added explicit run-ID recovery to the fixed example. Re-running with the same GUID validates the persisted job definition and source-file metadata, skips already-succeeded upsert batches after matching their durable payload hashes, and replays pending root batches and dependency events. Resume by setting `$runId` in the target script; abrupt process termination uses the existing 30-minute batch lease expiry.
- **Done:** Coalesced dependent contact writes across each completed account batch so child batches use the configured size without buffering beyond one batch. Per-account child counts and parent-batch dependency release are preserved; per-batch messages are available with `-Verbose`.
- **Done:** Added engine-controlled batch parallelism (default four concurrent scheduler handlers, configurable from one to 32), with one OAuth `ServiceClient.Clone()` connection leased to each active worker and `Set-DataverseRecord` internal parallelism disabled. Also exposed `Get-DataverseConnection -DisableAffinityCookie` for setup and execution. The setting defaults on for throughput and can be disabled to retain affinity; the README documents the consistency tradeoff.
- **Done:** Created and pushed the initial commit to the public `rnwood/Migration4Dataverse` GitHub repository.
- **Validated (earlier example stage):** The 15-test suite passed at that stage, including PowerShell parsing, configuration validation, and a mocked interrupted-run/resume flow. That test was subsequently replaced with scheduler-backed completion and replay coverage.
- **Done:** Added GitHub Actions CI to run `dotnet test Migration4Dataverse.slnx --configuration Release` on pushes and pull requests, with manual workflow dispatch available.
- **Validated against a live organization:** Interactive authentication succeeded; setup ensured the fixed tables, columns, and relationships; the smoke migration upserted 10 accounts, 20 contacts, and 20 projects; read-back confirmed all 50 records and all 20 contact/account and 20 project/contact lookups. The 50-row local dataset and temporary config were removed after verification.
- **Paused partial-scale attempt:** A 1.6-million-row live run at batch size 100 was stopped after 8:12.06 at the user's request. SQLite recorded completed batches for 1,000 accounts, 4,600 contacts, and 9,200 projects; a contact batch was still in flight. This is not a completed benchmark or an independent Dataverse read-back. The unchanged inputs and run configuration remain available for later resumption after the 30-minute lease expires.
- **Done:** Added `JsonLinesHierarchyBatchSource` to stream and validate the nested JSONL hierarchy, retain child rows in durable root payloads, and resume from a root cursor.
- **Done:** Routed the fixed example through `DependencyScheduler` and `PowerShellScriptExecutor`. Successful parent handlers emit bounded child payloads; SQLite dependency events release those children, and ready child work is prioritized over queued root batches.
- **Done:** Kept parallelization in the engine rather than the Dataverse write cmdlet. The typed cmdlet configures scheduler worker concurrency and serializes writes that share a target connection, while setting cmdlet-level parallelism to one. Scheduler integration tests cover bounded handlers, early dependent writes, and replay behavior.
- **Done:** Added named `ConnectionId` references to each Source and Target step. The invoker supplies a connection map keyed by those IDs; the migration command resolves references while leaving supplied connection ownership with the caller.
- **Done:** Added JSONL, CSV, and Parquet file sources and targets, plus paged custom PowerShell source and batch target callbacks with per-runspace module imports. File target batches are written atomically and identical replays are accepted while conflicting content is rejected. Connector types and connection IDs can be selected per step.
- **Done:** Added a Dataverse source connector with Simple, FetchXML, and read-only Sql4Cds query modes, bounded PowerShell streaming, stable-key checkpoint replay, and automatic import of the Dataverse PowerShell module.
- **Done:** Added a named data-flow transformation DAG with streaming Append/Union All, two-input sorted Merge with input-order validation, and stable external-memory Sort. Sort uses configurable in-memory row/byte budgets, maximum record size, spill limits, and a temporary directory; the transformation API tests exercise all three nodes.
- **Done:** Exposed the C# batch-DAG scheduler through the binary `Invoke-Migration` cmdlet. It accepts typed connector jobs with mappings and dependencies, plus callback-based source/batch handlers and explicit batch prerequisites for multi-parent readiness.
- **Done:** Replaced the interim job-shaped PowerShell contract with top-level Source, Transform, Filter, Append, Target, and callback steps connected through `Inputs`. All standalone target examples invoke the generic `Invoke-Migration` cmdlet using the step graph; connector definitions and scripts remain inline.
- **Done:** Updated examples to demonstrate standalone source, transformation, and target steps, plus setup-time data-flow API runs.
- **Done:** Added an engine-hosted React/Vite SPA using React Flow, with near-real-time run selection, dependency graph, large-graph level overview, status/progress, health/failure views, runtime timeline/hotspots, batch drill-down, and row-lineage inspection. The HTTP host is restricted to loopback; the app is embedded into the engine assembly.
- **Done:** Added `Invoke-Migration -EnableDebugCapture` with per-run retention configuration, input/mapped-output/intermediate/outcome snapshots, common secret/token property redaction, and recursive lineage traces. Callback stages can map merged rows to records in the current batch or across distinct parent batches; compact lineage indexes remain available for active runs and the configured post-run retention period. Basic run/job/batch metadata remains available without debug capture.
- **Done:** Added the compact per-batch input-lineage index and explicit `(BatchId, RecordIndex)` callback references for merges across distinct dependency batches.
- **Done:** Added the synthetic, paced PowerShell dashboard demo and public docs for startup, API, capture, retention, and security.
- **Validated:** The dashboard SPA production build passed, and focused SQLite/dashboard regression tests pass, including cross-batch merge lineage, debug-retention lookup, schema upgrades, secret redaction, dashboard HTTP serving, and loopback host enforcement.
- **Remaining:** Integrate stream-wide Sort/Merge into the migration step graph or document their continued separation; benchmark parallel-write and affinity settings, complete the full-scale run, and verify final outcomes; assess service-protection behavior; implement identity-aware concurrency and broader KingswaySoft/SSIS parity.

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
