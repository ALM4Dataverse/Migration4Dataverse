import {
  Button,
  Combobox,
  DataGrid,
  DataGridBody,
  DataGridCell,
  DataGridHeader,
  DataGridHeaderCell,
  DataGridRow,
  FluentProvider,
  Input,
  Menu,
  MenuItemCheckbox,
  MenuList,
  MenuPopover,
  MenuTrigger,
  Option,
  Popover,
  PopoverSurface,
  PopoverTrigger,
  Spinner as FluentSpinner,
  Text as FluentText,
  createTableColumn,
  type TableColumnDefinition,
  type TableColumnSizingOptions,
  webLightTheme,
} from "@fluentui/react-components";
import {
  Background,
  Controls,
  Handle,
  MiniMap,
  Position,
  ReactFlow,
  getViewportForBounds,
  type Edge,
  type Node,
  type NodeProps,
  type ReactFlowInstance,
} from "@xyflow/react";
import {
  Activity,
  AlertTriangle,
  CheckCircle2,
  Check,
  ChevronDown,
  ChevronUp,
  CircleDashed,
  Clock3,
  Columns3,
  Copy,
  Database,
  Download,
  FileSearch,
  Gauge,
  GripVertical,
  Layers3,
  ListFilter,
  Play,
  RefreshCw,
  Search,
  Settings2,
  Shuffle,
  Upload,
  X,
  XCircle,
} from "lucide-react";
import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type DragEvent as ReactDragEvent,
  type KeyboardEvent as ReactKeyboardEvent,
} from "react";

type RunStatus = "Running" | "Completed" | "Failed" | "Cancelled";
type JobStatus = "Running" | "Completed" | "Pending" | "Queued" | "Failed" | "Blocked" | "Configured";

interface DashboardActivityPeriod {
  state: "Working" | "Waiting";
  startedUtc: string;
  endedUtc: string | null;
}

interface DashboardRateSample {
  periodStartedUtc: string;
  periodEndedUtc: string;
  rowsPerSecond: number;
}

interface ThroughputSeries {
  points: number[];
  current: number;
  average: number;
}

interface OverallThroughput {
  input: ThroughputSeries;
  output: ThroughputSeries;
  startedUtc: string;
  endedUtc: string;
}

interface DashboardJob {
  name: string;
  type: string;
  dependsOn: string[];
  completionDependencies: string[];
  hasSource: boolean;
  isComplete: boolean;
  pendingBatches: number;
  inFlightBatches: number;
  pendingDependencyEvents: number;
  succeededBatches: number;
  batchesWithErrors: number;
  succeededRows: number;
  failedRows: number;
  inputRowsProcessed: number;
  inputRowsPending: number;
  inputRowsInFlight: number;
  filteredRows: number;
  expectedRows: number | null;
  inputFinalized: boolean;
  batchesWithUnknownInputRows: number;
  rowsPerSecond: number | null;
  rowsPerSecondSamples?: DashboardRateSample[] | null;
  activeMilliseconds: number;
  idleMilliseconds: number;
  specialistInfo?: Record<string, unknown> | null;
  activityPeriods: DashboardActivityPeriod[];
  firstBatchUtc: string | null;
  lastBatchUtc: string | null;
}

interface ConfigurationJob {
  name: string;
  type: string;
  dependsOn: string[];
  completionDependencies: string[];
  hasSource: boolean;
  sourceConnectionId?: string | null;
  sourceConnectionType?: string | null;
  targetConnectionId?: string | null;
  targetConnectionType?: string | null;
  properties: Record<string, unknown>;
}

interface ConfigurationConnection {
  id: string;
  typeName: string | null;
  properties: Record<string, unknown> | unknown[];
}

interface ConnectionReference {
  id: string;
  typeName: string;
}

interface MigrationConfiguration {
  jobs: ConfigurationJob[];
  connections: ConfigurationConnection[];
}

interface ConnectionInstance {
  id: string;
  typeName: string | null;
  properties: Record<string, unknown> | unknown[];
}

interface ConnectionInstanceSnapshot {
  phase: string;
  capturedUtc: string;
  connections: ConnectionInstance[];
}

interface RunSummary {
  runId: string;
  jobName: string;
  status: RunStatus;
  createdUtc: string;
  updatedUtc: string;
  debugCaptureEnabled: boolean;
  pendingBatches: number;
  inFlightBatches: number;
  succeededBatches: number;
  batchesWithErrors: number;
  succeededRows: number;
  failedRows: number;
  jobs: DashboardJob[];
  configuration: MigrationConfiguration | null;
  connectionInstances: ConnectionInstanceSnapshot[];
}

interface DebugSnapshot {
  runId: string;
  batchId: string;
  jobName: string;
  stageName: string;
  recordIndex: number;
  attempt: number;
  lineageId: string;
  parentLineageIds: string[];
  payloadJson: string;
  capturedUtc: string;
}

interface DebugSnapshotPage {
  items: DebugSnapshot[];
  totalCount: number;
  hasMore: boolean;
  stageCounts: { stageName: string; count: number }[];
  payloadColumns: string[];
}

interface ApiStep {
  id: string;
  type: string;
  inputs: string[];
  hasSource: boolean;
  sourceConnectionId?: string | null;
  sourceConnectionType?: string | null;
  targetConnectionId?: string | null;
  targetConnectionType?: string | null;
  properties?: Record<string, unknown>;
}

interface ApiConfiguration {
  steps: ApiStep[];
  connections: ConfigurationConnection[];
}

interface ApiDashboardStep extends Omit<DashboardJob, "name" | "dependsOn"> {
  id: string;
  inputs: string[];
}

interface ApiRunSummary extends Omit<RunSummary, "jobName" | "jobs" | "configuration"> {
  runName: string;
  steps: ApiDashboardStep[];
  configuration: ApiConfiguration | null;
}

interface ApiDebugSnapshot extends Omit<DebugSnapshot, "jobName"> {
  stepId: string;
}

interface ApiDebugSnapshotPage extends Omit<DebugSnapshotPage, "items"> {
  items: ApiDebugSnapshot[];
}

type SnapshotGridSortState = {
  sortColumn: string | number | undefined;
  sortDirection: "ascending" | "descending";
};

const defaultSnapshotSort: SnapshotGridSortState = {
  sortColumn: "record",
  sortDirection: "descending",
};
const emptySnapshotFilters: Record<string, string> = {};

interface JobNodeData extends Record<string, unknown> {
  job: DashboardJob;
  jobType: string;
  status: JobStatus;
  activityHint: string | null;
  specialistInfo: SpecialistInfoLine[];
  selected: boolean;
  selectionActive: boolean;
  relationshipHighlighted: boolean;
  highlighted: boolean;
  lineageActive: boolean;
  lineageSnapshots: DebugSnapshot[];
  sourceConnection?: ConnectionReference;
  targetConnection?: ConnectionReference;
  onSelect: (jobName: string) => void;
}

interface SpecialistInfoLine {
  label: string;
  value: string;
  title?: string;
}

const statusInfo: Record<JobStatus | RunStatus, { label: string; color: string; icon: typeof Activity }> = {
  Running: { label: "Running", color: "blue", icon: Play },
  Completed: { label: "Completed", color: "green", icon: CheckCircle2 },
  Pending: { label: "Pending", color: "gray", icon: CircleDashed },
  Queued: { label: "Queued", color: "yellow", icon: Clock3 },
  Failed: { label: "Failed", color: "red", icon: XCircle },
  Blocked: { label: "Blocked", color: "muted", icon: AlertTriangle },
  Cancelled: { label: "Cancelled", color: "muted", icon: XCircle },
  Configured: { label: "Configured", color: "muted", icon: CircleDashed },
};

const jobTypeInfo: Record<string, { label: string; icon: typeof Activity }> = {
  source: { label: "Source", icon: Upload },
  transform: { label: "Transform", icon: Shuffle },
  target: { label: "Target", icon: Download },
};
const jobTypeIcon = (type: string) => jobTypeInfo[type.toLowerCase()]?.icon ?? Layers3;

const number = new Intl.NumberFormat();
const rateNumber = new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 });
const api = async <T,>(path: string, signal?: AbortSignal): Promise<T> => {
  const response = await fetch(path, { cache: "no-store", signal });
  if (!response.ok) {
    const body = await response.json().catch(() => null);
    throw new Error(body?.message ?? `${response.status} ${response.statusText}`);
  }
  return response.json() as Promise<T>;
};

function normalizeConfiguration(configuration: ApiConfiguration): MigrationConfiguration {
  return {
    jobs: configuration.steps.map((step) => {
      const sourceDependencies = readSourceDependencies(step.properties);
      const sourceConnectionId = step.sourceConnectionId ??
        getStringProperty(step.properties, "SourceConnectionId");
      const targetConnectionId = step.targetConnectionId ??
        getStringProperty(step.properties, "TargetConnectionId");
      return {
        name: step.id,
        type: step.type,
        dependsOn: [...new Set([...step.inputs, ...sourceDependencies.all])],
        completionDependencies: sourceDependencies.completion,
        hasSource: step.hasSource || Boolean(sourceConnectionId),
        sourceConnectionId,
        sourceConnectionType: step.sourceConnectionType,
        targetConnectionId,
        targetConnectionType: step.targetConnectionType,
        properties: step.properties ?? {},
      };
    }),
    connections: configuration.connections,
  };
}

function normalizeRun(run: ApiRunSummary): RunSummary {
  return {
    ...run,
    jobName: run.runName,
    jobs: run.steps.map((step) => ({
      ...step,
      name: step.id,
      type: step.type,
      dependsOn: [...step.inputs, ...step.completionDependencies],
    })),
    configuration: run.configuration ? normalizeConfiguration(run.configuration) : null,
  };
}

function normalizeSnapshot(snapshot: ApiDebugSnapshot): DebugSnapshot {
  const { stepId, ...details } = snapshot;
  return { ...details, jobName: stepId };
}

function normalizeSnapshotPage(page: ApiDebugSnapshotPage): DebugSnapshotPage {
  return { ...page, items: page.items.map(normalizeSnapshot) };
}

function createSnapshotQuery({
  stepId,
  search,
  limit,
  offset,
  stageName,
  sortState,
  filters,
}: {
  stepId: string;
  search: string;
  limit: number;
  offset: number;
  stageName: string;
  sortState: SnapshotGridSortState;
  filters: Record<string, string>;
}): URLSearchParams {
  const query = new URLSearchParams({
    stepId,
    search,
    limit: String(limit),
    offset: String(offset),
    sortColumn: typeof sortState.sortColumn === "string" ? sortState.sortColumn : "record",
    sortDirection: sortState.sortDirection,
    filters: JSON.stringify(Object.fromEntries(
      Object.entries(filters).filter(([, value]) => value.trim().length > 0),
    )),
  });
  if (stageName) query.set("stageName", stageName);
  return query;
}

function formatElapsed(from: string, to = new Date().toISOString()): string {
  const totalSeconds = Math.max(0, Math.floor((Date.parse(to) - Date.parse(from)) / 1000));
  return formatDuration(totalSeconds);
}

function formatMilliseconds(milliseconds: number): string {
  return formatDuration(Math.floor(Math.max(0, milliseconds) / 1000));
}

function formatDuration(totalSeconds: number): string {
  if (totalSeconds < 60) return `${totalSeconds}s`;
  if (totalSeconds < 3600) return `${Math.floor(totalSeconds / 60)}m ${totalSeconds % 60}s`;
  return `${Math.floor(totalSeconds / 3600)}h ${Math.floor((totalSeconds % 3600) / 60)}m`;
}

function getTerminalTargetNames(run: RunSummary): Set<string> {
  const configuredJobs = run.configuration?.jobs ?? [];
  const downstreamNames = new Set(configuredJobs.flatMap((job) => job.dependsOn));
  return new Set(
    configuredJobs
      .filter((job) => Boolean(job.targetConnectionId) && !downstreamNames.has(job.name))
      .map((job) => job.name),
  );
}

function buildOverallThroughput(jobs: DashboardJob[], run: RunSummary): OverallThroughput | null {
  const start = Date.parse(run.createdUtc);
  const end = run.status === "Running" ? Date.now() : Date.parse(run.updatedUtc);
  const elapsedMilliseconds = end - start;
  if (!Number.isFinite(start) || !Number.isFinite(end) || elapsedMilliseconds <= 0) return null;

  const terminalTargets = getTerminalTargetNames(run);
  const configuredJobs = new Map((run.configuration?.jobs ?? []).map((job) => [job.name, job]));
  const sampleJobs = jobs
    .flatMap((job) => [
      ...(job.hasSource || configuredJobs.get(job.name)?.hasSource || job.type.toLowerCase() === "source"
        ? [{ group: "input" as const, samples: job.rowsPerSecondSamples ?? [] }]
        : []),
      ...(terminalTargets.has(job.name) || job.type.toLowerCase() === "target"
        ? [{ group: "output" as const, samples: job.rowsPerSecondSamples ?? [] }]
        : []),
    ])
    .filter((job) => job.samples.length > 0);
  if (sampleJobs.length === 0) return null;

  const bucketCount = 24;
  const bucketMilliseconds = elapsedMilliseconds / bucketCount;
  const inputRows = Array<number>(bucketCount).fill(0);
  const outputRows = Array<number>(bucketCount).fill(0);
  let inputCurrentRows = 0;
  let outputCurrentRows = 0;
  const currentWindowStart = Math.max(start, end - 10_000);

  for (const job of sampleJobs) {
    for (const sample of job.samples) {
      const sampleStart = Math.max(start, Date.parse(sample.periodStartedUtc));
      const sampleEnd = Math.min(end, Date.parse(sample.periodEndedUtc));
      if (!Number.isFinite(sampleStart) || !Number.isFinite(sampleEnd) ||
          sampleEnd <= sampleStart || !Number.isFinite(sample.rowsPerSecond)) {
        continue;
      }

      const rowsByBucket = job.group === "input" ? inputRows : outputRows;
      for (let bucket = 0; bucket < bucketCount; bucket++) {
        const bucketStart = start + bucket * bucketMilliseconds;
        const bucketEnd = bucketStart + bucketMilliseconds;
        const overlapMilliseconds = Math.max(
          0,
          Math.min(sampleEnd, bucketEnd) - Math.max(sampleStart, bucketStart),
        );
        rowsByBucket[bucket] += Math.max(0, sample.rowsPerSecond) * overlapMilliseconds / 1000;
      }

      const currentOverlapMilliseconds = Math.max(
        0,
        Math.min(sampleEnd, end) - Math.max(sampleStart, currentWindowStart),
      );
      if (job.group === "input") {
        inputCurrentRows += Math.max(0, sample.rowsPerSecond) * currentOverlapMilliseconds / 1000;
      } else {
        outputCurrentRows += Math.max(0, sample.rowsPerSecond) * currentOverlapMilliseconds / 1000;
      }
    }
  }

  const elapsedSeconds = elapsedMilliseconds / 1000;
  const currentWindowSeconds = (end - currentWindowStart) / 1000;
  return {
    input: {
      points: inputRows.map((rows) => rows / (bucketMilliseconds / 1000)),
      current: inputCurrentRows / currentWindowSeconds,
      average: inputRows.reduce((total, rows) => total + rows, 0) / elapsedSeconds,
    },
    output: {
      points: outputRows.map((rows) => rows / (bucketMilliseconds / 1000)),
      current: outputCurrentRows / currentWindowSeconds,
      average: outputRows.reduce((total, rows) => total + rows, 0) / elapsedSeconds,
    },
    startedUtc: new Date(start).toISOString(),
    endedUtc: new Date(end).toISOString(),
  };
}

function readSourceDependencies(properties: Record<string, unknown> | undefined): {
  all: string[];
  completion: string[];
} {
  const value = properties?.DependsOn ?? properties?.dependsOn;
  const entries = typeof value === "string" ? [value] : Array.isArray(value) ? value : value ? [value] : [];
  const all: string[] = [];
  const completion: string[] = [];
  entries.forEach((entry) => {
    if (typeof entry === "string") {
      all.push(entry);
      completion.push(entry);
      return;
    }
    const record = asRecord(entry);
    if (!record) return;
    const parent = getStringProperty(record, "Job");
    if (!parent) return;
    all.push(parent);
    if (!getStringProperty(record, "ParentKey")) completion.push(parent);
  });
  return { all: [...new Set(all)], completion: [...new Set(completion)] };
}

function statusFor(job: DashboardJob, jobs: DashboardJob[], configurationOnly = false): JobStatus {
  if (configurationOnly) return "Configured";
  if (job.batchesWithErrors > 0) return "Failed";
  if (job.inFlightBatches > 0) return "Running";
  if (job.pendingBatches > 0) return job.succeededBatches > 0 ? "Running" : "Queued";
  if (job.pendingDependencyEvents > 0) return job.succeededBatches > 0 ? "Running" : "Queued";
  if (job.isComplete) return "Completed";
  const parents = job.dependsOn
    .map((parentName) => jobs.find((candidate) => candidate.name === parentName))
    .filter((parent): parent is DashboardJob => parent !== undefined);
  if (parents.some((parent) => parent.batchesWithErrors > 0)) return "Blocked";
  if (job.hasSource &&
      job.completionDependencies.some((dependency) =>
        parents.some((parent) => parent.name === dependency && !parent.isComplete))) {
    return "Queued";
  }
  if (isBlockedByDownstream(job, jobs)) return "Blocked";
  if (job.succeededBatches > 0 || job.hasSource) return "Running";
  return parents.some((parent) => !parent.isComplete) ? "Queued" : "Pending";
}

function activeDownstreamSteps(job: DashboardJob, jobs: DashboardJob[]): DashboardJob[] {
  const visited = new Set([job.name]);
  const queue = [job.name];
  const descendants: DashboardJob[] = [];
  while (queue.length > 0) {
    const parentName = queue.shift()!;
    for (const candidate of jobs) {
      if (!visited.has(candidate.name) && candidate.dependsOn.includes(parentName)) {
        visited.add(candidate.name);
        queue.push(candidate.name);
        if (candidate.pendingBatches > 0 ||
            candidate.inFlightBatches > 0 ||
            candidate.pendingDependencyEvents > 0) {
          descendants.push(candidate);
        }
      }
    }
  }
  return descendants;
}

function isBlockedByDownstream(job: DashboardJob, jobs: DashboardJob[]): boolean {
  if (job.isComplete ||
      job.batchesWithErrors > 0 ||
      job.inFlightBatches > 0 ||
      job.pendingBatches > 0 ||
      job.pendingDependencyEvents > 0 ||
      job.succeededBatches === 0) {
    return false;
  }
  return activeDownstreamSteps(job, jobs).length > 0;
}

function furthestFailedDependency(job: DashboardJob, jobs: DashboardJob[]): DashboardJob | null {
  const distances = new Map([[job.name, 0]]);
  for (let pass = 0; pass < jobs.length; pass++) {
    let changed = false;
    for (const candidate of jobs) {
      const distance = distances.get(candidate.name);
      if (distance === undefined) continue;
      for (const dependencyName of candidate.dependsOn) {
        const nextDistance = distance + 1;
        if (nextDistance > (distances.get(dependencyName) ?? -1)) {
          distances.set(dependencyName, nextDistance);
          changed = true;
        }
      }
    }
    if (!changed) break;
  }

  return jobs
    .filter((candidate) => candidate.batchesWithErrors > 0 && distances.has(candidate.name))
    .toSorted((left, right) =>
      (distances.get(right.name) ?? 0) - (distances.get(left.name) ?? 0) ||
      left.name.localeCompare(right.name),
    )[0] ?? null;
}

function furthestActiveDownstream(job: DashboardJob, jobs: DashboardJob[]): DashboardJob | null {
  const downstream = new Map<string, DashboardJob[]>();
  for (const candidate of jobs) {
    for (const dependencyName of candidate.dependsOn) {
      const dependents = downstream.get(dependencyName) ?? [];
      dependents.push(candidate);
      downstream.set(dependencyName, dependents);
    }
  }

  const distances = new Map([[job.name, 0]]);
  for (let pass = 0; pass < jobs.length; pass++) {
    let changed = false;
    for (const [parentName, dependents] of downstream) {
      const distance = distances.get(parentName);
      if (distance === undefined) continue;
      for (const dependent of dependents) {
        const nextDistance = distance + 1;
        if (nextDistance > (distances.get(dependent.name) ?? -1)) {
          distances.set(dependent.name, nextDistance);
          changed = true;
        }
      }
    }
    if (!changed) break;
  }

  return jobs
    .filter((candidate) =>
      distances.has(candidate.name) &&
      (candidate.pendingBatches > 0 ||
       candidate.inFlightBatches > 0 ||
       candidate.pendingDependencyEvents > 0),
    )
    .toSorted((left, right) =>
      (distances.get(right.name) ?? 0) - (distances.get(left.name) ?? 0) ||
      left.name.localeCompare(right.name),
    )[0] ?? null;
}

function blockingTaskFor(job: DashboardJob, jobs: DashboardJob[]): DashboardJob | null {
  return furthestFailedDependency(job, jobs) ?? furthestActiveDownstream(job, jobs);
}

function activityHintFor(job: DashboardJob, jobs: DashboardJob[], configurationOnly = false): string | null {
  if (configurationOnly || job.isComplete || job.batchesWithErrors > 0) return null;
  if (statusFor(job, jobs) === "Blocked") {
    const blockingTask = blockingTaskFor(job, jobs);
    if (blockingTask) return `Blocked by: ${blockingTask.name}`;
  }

  const currentWork = [
    job.inFlightBatches > 0 ? `${number.format(job.inFlightBatches)} in progress` : "",
    job.pendingBatches > 0 ? `${number.format(job.pendingBatches)} queued` : "",
  ].filter(Boolean);
  if (currentWork.length > 0) return `This step: ${currentWork.join(" · ")} batches`;
  if (job.pendingDependencyEvents > 0) {
    return `Waiting on ${number.format(job.pendingDependencyEvents)} dependency events`;
  }
  if (isBlockedByDownstream(job, jobs)) return null;

  const downstream = activeDownstreamSteps(job, jobs);
  if (downstream.length > 0) {
    return `Downstream: ${downstream.map((step) => {
      const activity = [
        step.inFlightBatches > 0 ? `${number.format(step.inFlightBatches)} in progress` : "",
        step.pendingBatches > 0 ? `${number.format(step.pendingBatches)} queued` : "",
        step.pendingDependencyEvents > 0
          ? `${number.format(step.pendingDependencyEvents)} dependency events`
          : "",
      ].filter(Boolean);
      return `${step.name} (${activity.join(", ")})`;
    }).join("; ")}`;
  }

  const waitingFor = job.dependsOn
    .map((dependency) => jobs.find((candidate) => candidate.name === dependency))
    .filter((parent): parent is DashboardJob => parent !== undefined && !parent.isComplete)
    .map((parent) => parent.name);
  if (waitingFor.length > 0) return `Waiting for inputs: ${waitingFor.join(", ")}`;
  if (job.hasSource) return "Waiting for the next source batch";
  return "Waiting for work";
}

function inputRowProgress(job: DashboardJob): { value: number; max: number } | null {
  if (job.expectedRows === null || job.expectedRows <= 0) return null;
  return { value: Math.min(job.inputRowsProcessed, job.expectedRows), max: job.expectedRows };
}

function asRecord(value: unknown): Record<string, unknown> | undefined {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return undefined;
  return value as Record<string, unknown>;
}

function getStringProperty(value: unknown, ...propertyNames: string[]): string | undefined {
  const property = getPropertyValue(value, ...propertyNames);
  return typeof property === "string" && property.trim() ? property.trim() : undefined;
}

function getPropertyValue(value: unknown, ...propertyNames: string[]): unknown {
  const record = asRecord(value);
  if (!record) return undefined;
  const propertyNameSet = new Set(propertyNames.map((name) => name.toLocaleLowerCase()));
  const entry = Object.entries(record).find(([name]) => propertyNameSet.has(name.toLocaleLowerCase()));
  return entry?.[1];
}

function getNumberProperty(value: unknown, ...propertyNames: string[]): number | undefined {
  const property = getPropertyValue(value, ...propertyNames);
  if (typeof property === "number" && Number.isFinite(property)) return property;
  if (typeof property === "string" && property.trim()) {
    const parsed = Number(property);
    if (Number.isFinite(parsed)) return parsed;
  }
  return undefined;
}

function specialistInfoFor(
  job: DashboardJob,
  configurationJob: ConfigurationJob | undefined,
): SpecialistInfoLine[] {
  const type = (configurationJob?.type ?? job.type).toLocaleLowerCase();
  const properties = configurationJob?.properties;
  const lines: SpecialistInfoLine[] = [];
  const add = (label: string, value: string | undefined, title?: string) => {
    if (value) lines.push({ label, value, title });
  };

  if (type === "source") {
    const source = asRecord(getPropertyValue(properties, "Source"));
    add("Connector", getStringProperty(source, "Type"));
    add("Key", getStringProperty(source, "Key", "Id"));
    add("Path", getStringProperty(source, "Path"));
  } else if (type === "target") {
    const target = asRecord(getPropertyValue(properties, "Target"));
    const targetType = getStringProperty(target, "Type");
    add("Connector", targetType);
    add("Table", getStringProperty(target, "Table"));
    add("Operation", getStringProperty(target, "Operation"));
    if (targetType?.toLocaleLowerCase() === "dataverse") {
      const runtime = job.specialistInfo;
      const batchSetting = getPropertyValue(target, "BatchSize");
      const concurrencySetting = getPropertyValue(target, "MaxConcurrency");
      const batchAuto = batchSetting === undefined ||
        typeof batchSetting === "string" && batchSetting.toLocaleLowerCase() === "auto";
      const concurrencyAuto = concurrencySetting === undefined ||
        typeof concurrencySetting === "string" && concurrencySetting.toLocaleLowerCase() === "auto";
      const currentBatchSize = getNumberProperty(runtime, "BatchSize");
      const batchLimit = getNumberProperty(runtime, "BatchSizeLimit");
      const batchState = getStringProperty(runtime, "BatchSizeState");
      const currentDop = getNumberProperty(runtime, "MaxConcurrency");
      const dopLimit = getNumberProperty(runtime, "ConcurrencyLimit");
      const dopState = getStringProperty(runtime, "ConcurrencyState");
      const runtimeUnavailable = job.succeededBatches > 0
        ? "runtime state unavailable"
        : "awaiting first batch";
      const settingDescription = (
        label: string,
        setting: unknown,
        automatic: boolean,
        current: number | undefined,
        limit: number | undefined,
        state: string | undefined,
      ) => {
        if (!automatic) {
          const configured = setting === undefined ? undefined : String(setting);
          const effective = current === undefined ? configured : number.format(current);
          const cap = limit !== undefined && effective !== undefined &&
            effective !== configured
            ? ` · invocation cap ${number.format(limit)}`
            : "";
          return {
            value: `${effective ?? "Fixed"} fixed${cap}`,
            title: `${label}: configured ${configured ?? "fixed"}${cap}; effective ${effective ?? "unknown"}`,
          };
        }

        const candidate = current === undefined ? runtimeUnavailable : `current ${number.format(current)}`;
        const ceiling = limit === undefined ? "" : ` / max ${number.format(limit)}`;
        const tuningState = state ? ` · ${state}` : "";
        return {
          value: `Auto · ${candidate}${ceiling}${tuningState}`,
          title: `${label}: automatic${current === undefined ? `; ${runtimeUnavailable}` : `; current ${number.format(current)}${ceiling}`}${tuningState}`,
        };
      };
      const batchInfo = settingDescription(
        "Batch size",
        batchSetting,
        batchAuto,
        currentBatchSize,
        batchLimit,
        batchState,
      );
      const dopInfo = settingDescription(
        "DOP",
        concurrencySetting,
        concurrencyAuto,
        currentDop,
        dopLimit,
        dopState,
      );
      add("Batch size", batchInfo.value, batchInfo.title);
      add("DOP", dopInfo.value, dopInfo.title);
    } else {
      const batchSize = getPropertyValue(target, "BatchSize");
      if (batchSize !== undefined) add("Batch size", String(batchSize));
    }
  } else if (type === "transform") {
    const transform = asRecord(getPropertyValue(properties, "Transform"));
    add("Mode", getStringProperty(transform, "Type") ?? (getPropertyValue(properties, "Script") ? "PowerShell" : undefined));
    add("Connection", getStringProperty(transform, "ConnectionId"));
    add("Inputs", `${job.dependsOn.length}`);
  } else if (type === "join") {
    add("Join type", getStringProperty(properties, "JoinType"));
    add("Order", getStringProperty(properties, "JoinOrder") ?? "ready");
    add("Inputs", `${job.dependsOn.length}`);
  } else if (type === "filter") {
    add("Predicate", getPropertyValue(properties, "Script") ? "PowerShell" : undefined);
    add("Inputs", `${job.dependsOn.length}`);
  } else if (type === "append") {
    add("Streams", `${job.dependsOn.length}`);
  } else if (type === "callback") {
    add("Handler", "Batch callback");
    add("Inputs", `${job.dependsOn.length}`);
  } else {
    add("Inputs", `${job.dependsOn.length}`);
  }

  return lines;
}

function endpointConnection(
  job: ConfigurationJob | undefined,
  connections: Map<string, ConfigurationConnection>,
  endpointName: "Source" | "Target",
): ConnectionReference | undefined {
  if (!job) return undefined;
  const endpoint = asRecord(job.properties[endpointName]);
  const endpointConnectionId = endpointName === "Source"
    ? job.sourceConnectionId
    : job.targetConnectionId;
  const endpointType = endpointName === "Source"
    ? job.sourceConnectionType
    : job.targetConnectionType;
  const connectionId = endpointConnectionId ??
    getStringProperty(job.properties, `${endpointName}ConnectionId`) ??
    getStringProperty(endpoint, "ConnectionId");
  if (!connectionId) return undefined;

  const connection = connections.get(connectionId);
  const typeName = endpointType ??
    getStringProperty(endpoint, "Type", "Provider", "Kind") ??
    connection?.typeName?.split(".").at(-1) ??
    getStringProperty(connection?.properties, "Type", "Provider", "Kind") ??
    "Connection";
  return { id: connectionId, typeName };
}

function lineageStages(snapshots: DebugSnapshot[]): [string, DebugSnapshot[]][] {
  const stages = new Map<string, DebugSnapshot[]>();
  snapshots.forEach((snapshot) => {
    const rows = stages.get(snapshot.stageName) ?? [];
    rows.push(snapshot);
    stages.set(snapshot.stageName, rows);
  });
  return [...stages.entries()];
}

function JobThroughputChart({ job }: { job: DashboardJob }) {
  const samples = job.rowsPerSecondSamples ?? [];
  if (samples.length === 0) return null;

  const width = 200;
  const height = 24;
  const maximumRate = Math.max(0, ...samples.map((sample) => sample.rowsPerSecond));
  const coordinates = samples.map((sample, index) => ({
    sample,
    x: samples.length === 1 ? width / 2 : index * width / (samples.length - 1),
    y: maximumRate === 0 ? height / 2 : height - (sample.rowsPerSecond / maximumRate) * (height - 2) - 1,
  }));
  const lineCoordinates = samples.length === 1
    ? `0,${coordinates[0].y} ${width},${coordinates[0].y}`
    : coordinates.map(({ x, y }) => `${x},${y}`).join(" ");
  const startedUtc = samples[0].periodStartedUtc;
  const endedUtc = samples[samples.length - 1].periodEndedUtc;
  const timeRange = `${new Date(startedUtc).toLocaleString()} – ${new Date(endedUtc).toLocaleString()}`;
  const label = `${job.name} rows per second from ${timeRange}`;

  return (
    <div className="job-node-throughput" title={label}>
      <div className="job-node-throughput-header">
        <span>Rows/s</span>
        <span>{formatElapsed(startedUtc, endedUtc)} · max {rateNumber.format(maximumRate)}</span>
      </div>
      <svg viewBox={`0 0 ${width} ${height}`} role="img" aria-label={label} preserveAspectRatio="none">
        <title>{label}</title>
        <line className="job-node-throughput-baseline" x1="0" y1={height - 1} x2={width} y2={height - 1} />
        <polyline className="job-node-throughput-line" points={lineCoordinates} />
        {coordinates.map(({ sample, x, y }) => (
          <circle
            key={`${sample.periodStartedUtc}-${sample.periodEndedUtc}`}
            className="job-node-throughput-point"
            cx={x}
            cy={y}
            r="3"
          >
            <title>
              {`${new Date(sample.periodStartedUtc).toLocaleString()} – ${new Date(sample.periodEndedUtc).toLocaleString()}: ${rateNumber.format(sample.rowsPerSecond)} rows/s`}
            </title>
          </circle>
        ))}
      </svg>
    </div>
  );
}

function JobNode({ data }: NodeProps<Node<JobNodeData>>) {
  const {
    job,
    jobType,
    status,
    activityHint,
    specialistInfo,
    selected,
    selectionActive,
    relationshipHighlighted,
    highlighted,
    lineageActive,
    lineageSnapshots,
    sourceConnection,
    targetConnection,
  } = data;
  const meta = statusInfo[status];
  const TypeIcon = jobTypeIcon(jobType);
  const StatusIcon = meta.icon;
  const typeLabel = jobTypeInfo[jobType.toLowerCase()]?.label ?? "Step";
  const done = job.succeededBatches + job.batchesWithErrors;
  const rowProgress = inputRowProgress(job);
  const stages = lineageStages(lineageSnapshots);

  return (
    <div className={`job-node status-${meta.color}${selected ? " selected" : ""}${selectionActive ? relationshipHighlighted ? " relationship-highlighted" : " relationship-muted" : ""}${highlighted ? " lineage-highlighted" : ""}${lineageActive ? " lineage-mode" : ""}`}>
      <Handle type="target" position={Position.Left} />
      <div className="job-node-head">
        <span className="job-icon" title={`${typeLabel} step`} role="img" aria-label={`${typeLabel} step`}>
          <TypeIcon size={14} aria-hidden="true" />
        </span>
        <div className="job-node-identity">
          <span className="job-node-name" title={job.name}>{job.name}</span>
        </div>
        {lineageActive
          ? <span className="lineage-node-label">{highlighted ? `${lineageSnapshots.length} row snapshots` : "Outside lineage"}</span>
          : <span className="job-status-label"><StatusIcon size={12} aria-hidden="true" />{meta.label}</span>}
      </div>
      {!lineageActive && (sourceConnection || targetConnection) && (
        <div className="job-node-connections">
          {sourceConnection && (
            <div title={`Source connection: ${sourceConnection.typeName} · ${sourceConnection.id}`}>
              <Database size={12} aria-hidden="true" />
              <span>{sourceConnection.typeName} · {sourceConnection.id}</span>
            </div>
          )}
          {targetConnection && (
            <div title={`Target connection: ${targetConnection.typeName} · ${targetConnection.id}`}>
              <Database size={12} aria-hidden="true" />
              <span>{targetConnection.typeName} · {targetConnection.id}</span>
            </div>
          )}
        </div>
      )}
      {lineageActive ? (
        lineageSnapshots.length > 0 ? (
          <div className="lineage-node-snapshots">
            {stages.map(([stageName, rows]) => (
              <section className="lineage-node-stage" key={stageName}>
                <strong>{friendlyStageName(stageName)} · {number.format(rows.length)} rows</strong>
                {rows.slice(0, 4).map((snapshot) => (
                  <div className="lineage-node-snapshot" key={snapshotKey(snapshot)}>
                    <span>Row {snapshot.recordIndex + 1} · {snapshot.lineageId.slice(-8)}</span>
                    <pre>{prettyJson(snapshot.payloadJson)}</pre>
                  </div>
                ))}
                {rows.length > 4 && (
                  <span className="lineage-node-more">
                    {number.format(rows.length - 4)} more rows in Step Inspection
                  </span>
                )}
              </section>
            ))}
          </div>
        ) : <div className="lineage-node-empty">No captured row on this step.</div>
      ) : status === "Configured" ? (
        <div className="job-node-metrics"><span>Configuration preview</span><span>{job.dependsOn.length} dependencies</span></div>
      ) : (
        <>
          <div className="job-node-metrics">
            <span>{number.format(job.succeededRows)} rows</span>
            <span>{number.format(done)} batches done</span>
          </div>
          <div className="job-node-metrics">
            <span
              className={rowProgress ? "job-node-progress-cell" : undefined}
              title={`Input rows in completed batches${job.inputFinalized ? "" : "; expected total may grow"}`}
            >
              <span>
                {number.format(job.inputRowsProcessed)} input rows
                {job.expectedRows === null ? "" : ` / ${number.format(job.expectedRows)}`}
              </span>
              {rowProgress && (
                <progress
                  className="job-row-progress"
                  max={rowProgress.max}
                  value={rowProgress.value}
                  aria-label={`${job.name} input row progress${job.inputFinalized ? "" : "; expected total may grow"}`}
                />
              )}
            </span>
            <span title="Rows excluded at this step; counts are across pipeline steps">{number.format(job.filteredRows)} filtered</span>
          </div>
          <div className="job-node-metrics">
            <span title="Average throughput across observed batches">
              {job.rowsPerSecond === null ? "— avg rows/s" : `Avg ${rateNumber.format(job.rowsPerSecond)} rows/s`}
            </span>
            <span title={job.lastBatchUtc ? `Most recent batch update: ${new Date(job.lastBatchUtc).toLocaleString()}` : undefined}>
              {job.isComplete
                ? "Finished"
                : job.lastBatchUtc
                  ? `Last batch update ${formatElapsed(job.lastBatchUtc)} ago`
                  : "No output yet"}
            </span>
          </div>
          {activityHint && (
            <div className="job-node-activity" title={activityHint}>
              <Clock3 size={12} aria-hidden="true" />
              <span>{activityHint}</span>
            </div>
          )}
          <JobThroughputChart job={job} />
        </>
      )}
      {!lineageActive && specialistInfo.length > 0 && (
        <div className="job-node-specialist-info" aria-label={`${typeLabel} details`}>
          {specialistInfo.map((line) => (
            <div key={line.label} title={line.title ?? `${line.label}: ${line.value}`}>
              <strong>{line.label}</strong>
              <span>{line.value}</span>
            </div>
          ))}
        </div>
      )}
      {job.failedRows > 0 && <div className="job-node-error">{number.format(job.failedRows)} failed rows</div>}
      <Handle type="source" position={Position.Right} />
    </div>
  );
}

const nodeTypes = { job: JobNode };

type TextVariant = "small" | "smallPlus" | "mediumPlus" | "large" | "xxLarge";

const textSizes: Record<TextVariant, 200 | 300 | 400 | 500 | 900> = {
  small: 200,
  smallPlus: 300,
  mediumPlus: 400,
  large: 500,
  xxLarge: 900,
};

function Text({
  variant = "mediumPlus",
  className,
  title,
  children,
}: {
  variant?: TextVariant;
  className?: string;
  title?: string;
  children: React.ReactNode;
}) {
  return (
    <FluentText
      className={["app-text", `text-${variant}`, className].filter(Boolean).join(" ")}
      size={textSizes[variant]}
      weight={variant === "large" || variant === "xxLarge" ? "semibold" : undefined}
      title={title}
    >
      {children}
    </FluentText>
  );
}

export default function App() {
  const [runs, setRuns] = useState<RunSummary[]>([]);
  const [runId, setRunId] = useState("");
  const [run, setRun] = useState<RunSummary | null>(null);
  const [previewConfiguration, setPreviewConfiguration] = useState<MigrationConfiguration | null>(null);
  const [showConfiguration, setShowConfiguration] = useState(false);
  const [summaryExpanded, setSummaryExpanded] = useState(false);
  const [inspectorPanelOpen, setInspectorPanelOpen] = useState(() =>
    window.localStorage.getItem("migration4dataverse.configuration-panel-open") === "true");
  const [inspectorTab, setInspectorTab] = useState<"rows" | "configuration">("rows");
  const [selectedJob, setSelectedJob] = useState("");
  const [jobViewMode, setJobViewMode] = useState<"graph" | "table">("graph");
  const [hideCompletedJobs, setHideCompletedJobs] = useState(false);
  const [jobRows, setJobRows] = useState<DebugSnapshot[]>([]);
  const [jobRowCount, setJobRowCount] = useState(0);
  const [rowStageCounts, setRowStageCounts] = useState<DebugSnapshotPage["stageCounts"]>([]);
  const [rowPayloadColumns, setRowPayloadColumns] = useState<string[]>([]);
  const [selectedStage, setSelectedStage] = useState("");
  const [hasMoreJobRows, setHasMoreJobRows] = useState(false);
  const [loadingJobRows, setLoadingJobRows] = useState(false);
  const [rowSearchByStage, setRowSearchByStage] = useState<Record<string, string>>({});
  const [rowFiltersByStage, setRowFiltersByStage] = useState<Record<string, Record<string, string>>>({});
  const [rowSortByStage, setRowSortByStage] = useState<Record<string, SnapshotGridSortState>>({});
  const [selectedSnapshot, setSelectedSnapshot] = useState<DebugSnapshot | null>(null);
  const [trace, setTrace] = useState<DebugSnapshot[]>([]);
  const [traceId, setTraceId] = useState("");
  const [traceSearch, setTraceSearch] = useState("");
  const [stepSearchOpen, setStepSearchOpen] = useState(false);
  const [stepSearchValue, setStepSearchValue] = useState("");
  const [focusedLevel, setFocusedLevel] = useState<number | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [flowInstance, setFlowInstance] = useState<ReactFlowInstance | null>(null);
  const [graphLayout, setGraphLayout] = useState<{
    key: string;
    positions: Map<string, { x: number; y: number }>;
  } | null>(null);
  const [graphLayoutError, setGraphLayoutError] = useState("");
  const flowCanvasRef = useRef<HTMLDivElement | null>(null);
  const rowRequestVersion = useRef(0);
  const loadMoreController = useRef<AbortController | null>(null);
  const rowSearch = rowSearchByStage[selectedStage] ?? "";
  const rowColumnFilters = rowFiltersByStage[selectedStage] ?? emptySnapshotFilters;
  const rowSortState = rowSortByStage[selectedStage] ?? defaultSnapshotSort;

  const clearJobRows = useCallback(() => {
    rowRequestVersion.current++;
    loadMoreController.current?.abort();
    setJobRows([]);
    setJobRowCount(0);
    setHasMoreJobRows(false);
  }, []);
  const restartSnapshotQuery = useCallback(() => {
    rowRequestVersion.current++;
    loadMoreController.current?.abort();
    setHasMoreJobRows(false);
  }, []);

  useEffect(() => {
    window.localStorage.setItem(
      "migration4dataverse.configuration-panel-open",
      String(inspectorPanelOpen),
    );
  }, [inspectorPanelOpen]);

  const refresh = useCallback(async (signal?: AbortSignal) => {
    const listedRuns = (await api<ApiRunSummary[]>("/api/runs", signal)).map(normalizeRun);
    setRuns(listedRuns);
    const configurationResponse = await fetch("/api/configuration", { cache: "no-store", signal });
    if (configurationResponse.ok) {
      setPreviewConfiguration(normalizeConfiguration(
        await configurationResponse.json() as ApiConfiguration,
      ));
    } else if (configurationResponse.status === 404) {
      setPreviewConfiguration(null);
    } else {
      throw new Error(`${configurationResponse.status} ${configurationResponse.statusText}`);
    }
    const currentId = runId || listedRuns[0]?.runId || "";
    if (!currentId) {
      setRun(null);
      setRunId("");
      setShowConfiguration(true);
      return;
    }
    if (currentId !== runId) setRunId(currentId);
    const currentRun = normalizeRun(
      await api<ApiRunSummary>(`/api/runs/${encodeURIComponent(currentId)}`, signal),
    );
    setRun(currentRun);
    setError("");
  }, [runId]);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    refresh(controller.signal)
      .catch((reason: unknown) => {
        if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(String(reason));
      })
      .finally(() => setLoading(false));
    const timer = window.setInterval(() => {
      refresh(controller.signal).catch((reason: unknown) => {
        if (!(reason instanceof DOMException && reason.name === "AbortError")) setError(String(reason));
      });
    }, 3000);
    return () => {
      controller.abort();
      window.clearInterval(timer);
    };
  }, [refresh]);

  useEffect(() => {
    rowRequestVersion.current++;
    loadMoreController.current?.abort();
    setSelectedJob("");
    setJobRows([]);
    setJobRowCount(0);
    setRowStageCounts([]);
    setRowPayloadColumns([]);
    setSelectedStage("");
    setHasMoreJobRows(false);
    setSelectedSnapshot(null);
    setTrace([]);
    setTraceId("");
    setRowSearchByStage({});
    setRowFiltersByStage({});
    setRowSortByStage({});
    setTraceSearch("");
    setFocusedLevel(null);
    setInspectorTab("rows");
  }, [runId]);

  useEffect(() => {
    if (showConfiguration || !run || !selectedJob || !run.debugCaptureEnabled) {
      setJobRows([]);
      setJobRowCount(0);
      setRowStageCounts([]);
      setHasMoreJobRows(false);
      setLoadingJobRows(false);
      return;
    }
    const controller = new AbortController();
    const requestVersion = ++rowRequestVersion.current;
    loadMoreController.current?.abort();
    setLoadingJobRows(true);
    const query = createSnapshotQuery({
      stepId: selectedJob,
      search: rowSearch,
      limit: 100,
      offset: 0,
      stageName: selectedStage,
      sortState: rowSortState,
      filters: rowColumnFilters,
    });
    const timer = window.setTimeout(() => {
      api<ApiDebugSnapshotPage>(
        `/api/runs/${encodeURIComponent(run.runId)}/rows?${query.toString()}`,
        controller.signal,
      )
      .then((apiPage) => {
        if (requestVersion !== rowRequestVersion.current) return;
        const page = normalizeSnapshotPage(apiPage);
        setRowStageCounts(page.stageCounts);
        setRowPayloadColumns(page.payloadColumns);
        if (!selectedStage && page.stageCounts.length > 0) {
          setSelectedStage(page.stageCounts[0].stageName);
          return;
        }
        setJobRows(page.items);
        setJobRowCount(page.totalCount);
        setHasMoreJobRows(page.hasMore);
      })
      .catch((reason: unknown) => {
        if (requestVersion === rowRequestVersion.current &&
            !(reason instanceof DOMException && reason.name === "AbortError")) {
          setError(String(reason));
        }
      })
      .finally(() => {
        if (requestVersion === rowRequestVersion.current) setLoadingJobRows(false);
      });
    }, 250);
    return () => {
      window.clearTimeout(timer);
      controller.abort();
      if (requestVersion === rowRequestVersion.current) rowRequestVersion.current++;
    };
  }, [
    run?.runId,
    run?.debugCaptureEnabled,
    selectedJob,
    rowSearch,
    rowColumnFilters,
    rowSortState,
    selectedStage,
    showConfiguration,
  ]);

  const configurationOnly = showConfiguration || (!run && previewConfiguration !== null);
  const configuration = configurationOnly ? previewConfiguration : run?.configuration ?? null;
  const jobs = configurationOnly && configuration
    ? configuration.jobs.map((job) => ({
        name: job.name,
        type: job.type,
        dependsOn: job.dependsOn,
        completionDependencies: job.completionDependencies,
        hasSource: job.hasSource,
        isComplete: false,
        pendingBatches: 0,
        inFlightBatches: 0,
        pendingDependencyEvents: 0,
        succeededBatches: 0,
        batchesWithErrors: 0,
        succeededRows: 0,
        failedRows: 0,
        inputRowsProcessed: 0,
        inputRowsPending: 0,
        inputRowsInFlight: 0,
        filteredRows: 0,
        expectedRows: null,
        inputFinalized: false,
        batchesWithUnknownInputRows: 0,
        rowsPerSecond: null,
        activeMilliseconds: 0,
        idleMilliseconds: 0,
        activityPeriods: [],
        firstBatchUtc: null,
        lastBatchUtc: null,
      }))
    : run?.jobs ?? [];
  const jobsByName = useMemo(() => new Map(jobs.map((job) => [job.name, job])), [jobs]);
  const configurationJobsByName = useMemo(
    () => new Map((configuration?.jobs ?? []).map((job) => [job.name, job])),
    [configuration],
  );
  const SelectedJobIcon = selectedJob
    ? jobTypeIcon(configurationJobsByName.get(selectedJob)?.type ?? "step")
    : FileSearch;
  const connectionsById = useMemo(
    () => new Map((configuration?.connections ?? []).map((connection) => [connection.id, connection])),
    [configuration],
  );
  const jobLevels = useMemo(() => {
    const levels = new Map<string, number>();
    const getLevel = (job: DashboardJob, visiting = new Set<string>()): number => {
      const known = levels.get(job.name);
      if (known !== undefined) return known;
      if (visiting.has(job.name)) return 0;
      const nextVisiting = new Set(visiting).add(job.name);
      const parentLevels = job.dependsOn
        .map((name) => jobs.find((candidate) => candidate.name === name))
        .filter((parent): parent is DashboardJob => parent !== undefined)
        .map((parent) => getLevel(parent, nextVisiting));
      const level = parentLevels.length ? Math.max(...parentLevels) + 1 : 0;
      levels.set(job.name, level);
      return level;
    };
    jobs.forEach((job) => getLevel(job));
    return levels;
  }, [jobs]);
  const jobOutputs = useMemo(() => {
    const outputs = new Map<string, string[]>();
    jobs.forEach((job) => outputs.set(job.name, []));
    jobs.forEach((job) => {
      job.dependsOn.forEach((parentName) => outputs.get(parentName)?.push(job.name));
    });
    return outputs;
  }, [jobs]);

  const visibleJobs = useMemo(
    () => hideCompletedJobs
      ? jobs.filter((job) => statusFor(job, jobs, configurationOnly) !== "Completed")
      : jobs,
    [configurationOnly, hideCompletedJobs, jobs],
  );
  const largeOverview = visibleJobs.length > 40 && focusedLevel === null;
  const graphJobs = useMemo(() => {
    if (!largeOverview && focusedLevel === null) return visibleJobs;
    if (focusedLevel === null) return [];
    const included = new Set(
      visibleJobs.filter((job) => jobLevels.get(job.name) === focusedLevel).map((job) => job.name),
    );
    visibleJobs.filter((job) => included.has(job.name)).forEach((job) => job.dependsOn.forEach((parent) => included.add(parent)));
    return visibleJobs.filter((job) => included.has(job.name));
  }, [focusedLevel, jobLevels, visibleJobs, largeOverview]);
  const criticalEdges = useMemo(() => {
    const duration = (job: DashboardJob) => {
      if (!job.firstBatchUtc || !job.lastBatchUtc) return 0;
      return Math.max(0, Date.parse(job.lastBatchUtc) - Date.parse(job.firstBatchUtc));
    };
    const memo = new Map<string, { score: number; path: string[] }>();
    const scoreTo = (job: DashboardJob, visiting = new Set<string>()): { score: number; path: string[] } => {
      const stored = memo.get(job.name);
      if (stored) return stored;
      if (visiting.has(job.name)) return { score: 0, path: [job.name] };
      const next = new Set(visiting).add(job.name);
      const children = jobs.filter((candidate) => candidate.dependsOn.includes(job.name));
      const bestChild = children.map((child) => scoreTo(child, next)).sort((a, b) => b.score - a.score)[0];
      const result = bestChild
        ? { score: duration(job) + bestChild.score, path: [job.name, ...bestChild.path] }
        : { score: duration(job), path: [job.name] };
      memo.set(job.name, result);
      return result;
    };
    const best = jobs.map((job) => scoreTo(job)).sort((a, b) => b.score - a.score)[0];
    return new Set((best?.path ?? []).slice(0, -1).map((name, index) => `${name}->${best?.path[index + 1]}`));
  }, [jobs]);

  const traceJobNames = useMemo(() => new Set(trace.map((snapshot) => snapshot.jobName)), [trace]);
  const traceEdges = useMemo(() => {
    const jobsByLineage = new Map<string, Set<string>>();
    trace.forEach((snapshot) => {
      const names = jobsByLineage.get(snapshot.lineageId) ?? new Set<string>();
      names.add(snapshot.jobName);
      jobsByLineage.set(snapshot.lineageId, names);
    });
    const edges = new Set<string>();
    trace.forEach((snapshot) => {
      snapshot.parentLineageIds.forEach((parentLineageId) => {
        jobsByLineage.get(parentLineageId)?.forEach((parentJob) => {
          if (parentJob !== snapshot.jobName) edges.add(`${parentJob}->${snapshot.jobName}`);
        });
      });
    });
    return edges;
  }, [trace]);
  const selectedRelationshipJobs = useMemo(() => {
    const related = new Set<string>();
    if (!selectedJob) return related;
    related.add(selectedJob);
    graphJobs.forEach((job) => {
      if (job.name === selectedJob) job.dependsOn.forEach((parent) => related.add(parent));
      if (job.dependsOn.includes(selectedJob)) related.add(job.name);
    });
    return related;
  }, [graphJobs, selectedJob]);

  const selectJob = useCallback((jobName: string) => {
    setSelectedJob(jobName);
    clearJobRows();
    setRowStageCounts([]);
    setRowPayloadColumns([]);
    setSelectedStage("");
    setRowSearchByStage({});
    setRowFiltersByStage({});
    setRowSortByStage({});
    setSelectedSnapshot(null);
    setTrace([]);
    setTraceId("");
    setTraceSearch("");
  }, [clearJobRows]);
  const jumpToJob = useCallback((jobName: string) => {
    const target = jobsByName.get(jobName);
    const revealCompleted = hideCompletedJobs &&
      target !== undefined &&
      statusFor(target, jobs, configurationOnly) === "Completed";
    selectJob(jobName);
    if (revealCompleted) {
      setHideCompletedJobs(false);
      setFocusedLevel(null);
    }
    const scrollToRow = () => {
      const row = document.getElementById(`job-row-${encodeURIComponent(jobName)}`);
      row?.scrollIntoView({ behavior: "smooth", block: "center", inline: "nearest" });
      row?.querySelector<HTMLButtonElement>(".job-table-name")?.focus({ preventScroll: true });
    };
    window.requestAnimationFrame(() => {
      if (revealCompleted) window.requestAnimationFrame(scrollToRow);
      else scrollToRow();
    });
  }, [configurationOnly, hideCompletedJobs, jobs, jobsByName, selectJob]);

  const baseNodes = useMemo<Node<JobNodeData>[]>(() => {
    const lineageActive = trace.length > 0;
    const columns = new Map<number, DashboardJob[]>();
    graphJobs.forEach((job) => {
      const level = jobLevels.get(job.name) ?? 0;
      const column = columns.get(level) ?? [];
      column.push(job);
      columns.set(level, column);
    });
    return [...columns.entries()].flatMap(([level, columnJobs]) => {
      let top = 0;
      return columnJobs
        .sort((a, b) => a.name.localeCompare(b.name))
        .map((job) => {
          const jobConfiguration = configurationJobsByName.get(job.name);
          const sourceConnection = endpointConnection(jobConfiguration, connectionsById, "Source");
          const targetConnection = endpointConnection(jobConfiguration, connectionsById, "Target");
          const width = lineageActive ? 360 : 320;
          const activityHint = activityHintFor(job, jobs, configurationOnly);
          const specialistInfo = specialistInfoFor(job, jobConfiguration);
          const height = lineageActive
            ? 300
            : 168 +
              ((sourceConnection || targetConnection)
                ? 26 + (sourceConnection && targetConnection ? 13 : 0)
                : 0) +
              (inputRowProgress(job) ? 8 : 0) +
              (activityHint ? 28 : 0) +
              ((job.rowsPerSecondSamples?.length ?? 0) > 0 ? 42 : 0) +
              (specialistInfo.length > 0 ? 8 + specialistInfo.length * 18 : 0) +
              (job.failedRows > 0 ? 29 : 0);
          const position = {
            x: level * (lineageActive ? 410 : 350),
            y: top,
          };
          top += height + (lineageActive ? 34 : 28);
          return {
            id: job.name,
            type: "job",
            width,
            height,
            position,
            data: {
              job,
              jobType: jobConfiguration?.type ?? "step",
              status: statusFor(job, jobs, configurationOnly),
              activityHint,
              specialistInfo,
              selected: selectedJob === job.name,
              selectionActive: Boolean(selectedJob),
              relationshipHighlighted: selectedRelationshipJobs.has(job.name),
              highlighted: traceJobNames.has(job.name),
              lineageActive,
              lineageSnapshots: trace.filter((snapshot) =>
                snapshot.jobName === job.name && snapshot.stageName !== "BatchOutcome"),
              sourceConnection,
              targetConnection,
              onSelect: selectJob,
            },
          };
        });
    });
  }, [
    configurationOnly,
    configurationJobsByName,
    connectionsById,
    graphJobs,
    jobs,
    jobLevels,
    selectedJob,
    selectedRelationshipJobs,
    selectJob,
    trace,
    traceJobNames,
  ]);
  const edges = useMemo<Edge[]>(() => graphJobs.flatMap((job) =>
    job.dependsOn
      .filter((parent) => graphJobs.some((candidate) => candidate.name === parent))
      .map((parent) => {
        const edgeId = `${parent}->${job.name}`;
        const isCompletionDependency = job.completionDependencies.includes(parent);
        const selectedRelationship = selectedJob === parent || selectedJob === job.name;
        const tracedRelationship = traceEdges.has(edgeId);
        const color = selectedRelationship || trace.length > 0 && tracedRelationship
          ? "#0078d4"
          : selectedJob || trace.length > 0
            ? "#c8c6c4"
            : criticalEdges.has(edgeId)
              ? "#ca5010"
              : "#8a8886";
        const strokeWidth = selectedRelationship || trace.length > 0 && tracedRelationship
          ? 4
          : !selectedJob && trace.length === 0 && criticalEdges.has(edgeId) ? 3 : 1;
        return {
          id: edgeId,
          source: parent,
          target: job.name,
          type: "smoothstep",
          className: selectedJob
            ? selectedRelationship ? "relationship-edge-highlighted" : "relationship-edge-muted"
            : undefined,
          animated: !configurationOnly && statusFor(job, jobs) === "Running",
          style: {
            stroke: color,
            strokeWidth,
            opacity: selectedJob && !selectedRelationship ? 0.28 : 1,
            strokeDasharray: isCompletionDependency ? "6 4" : undefined,
          },
        };
      }),
  ), [configurationOnly, criticalEdges, graphJobs, jobs, selectedJob, trace.length, traceEdges]);

  const graphLayoutKey = [
    ...baseNodes.map((node) => `${node.id}:${node.width}:${node.height}`),
    ...edges.map((edge) => `${edge.source}->${edge.target}`),
  ].join("|");

  // Status-only refreshes should not rerun the layered graph layout.
  useEffect(() => {
    if (baseNodes.length === 0) {
      setGraphLayout({ key: graphLayoutKey, positions: new Map() });
      setGraphLayoutError("");
      return;
    }

    let cancelled = false;
    setGraphLayoutError("");
    const layoutGraph = async () => {
      try {
        const { default: ELK } = await import("elkjs/lib/elk.bundled.js");
        if (cancelled) return;
        const result = await new ELK().layout({
          id: "migration",
          layoutOptions: {
            "elk.algorithm": "layered",
            "elk.direction": "RIGHT",
            "elk.edgeRouting": "ORTHOGONAL",
            "elk.layered.crossingMinimization.strategy": "LAYER_SWEEP",
            "elk.layered.nodePlacement.strategy": "NETWORK_SIMPLEX",
            "elk.layered.spacing.nodeNodeBetweenLayers": "72",
            "elk.spacing.nodeNode": "40",
          },
          children: baseNodes.map((node) => ({
            id: node.id,
            width: node.width ?? 320,
            height: node.height ?? 120,
          })),
          edges: edges.map((edge) => ({
            id: edge.id,
            sources: [edge.source],
            targets: [edge.target],
          })),
        });
        if (cancelled) return;
        const positions = new Map<string, { x: number; y: number }>();
        result.children?.forEach((node) => {
          if (node.id) positions.set(node.id, { x: node.x ?? 0, y: node.y ?? 0 });
        });
        setGraphLayout({ key: graphLayoutKey, positions });
      } catch (reason) {
        if (cancelled) return;
        setGraphLayoutError(
          `Unable to arrange the workflow graph: ${reason instanceof Error ? reason.message : String(reason)}`,
        );
        setGraphLayout({ key: graphLayoutKey, positions: new Map() });
      }
    };
    void layoutGraph();

    return () => { cancelled = true; };
  }, [graphLayoutKey]);

  const nodes = useMemo<Node<JobNodeData>[]>(() => baseNodes.map((node) => {
    const position = graphLayout?.key === graphLayoutKey
      ? graphLayout.positions.get(node.id)
      : undefined;
    return position ? { ...node, position } : node;
  }), [baseNodes, graphLayout, graphLayoutKey]);

  useEffect(() => {
    const canvas = flowCanvasRef.current;
    if (!flowInstance || !canvas || nodes.length === 0 || graphLayout?.key !== graphLayoutKey) return;
    const timer = window.setTimeout(() => {
      const bounds = flowInstance.getNodesBounds(flowInstance.getNodes());
      const viewport = getViewportForBounds(
        bounds,
        canvas.clientWidth,
        canvas.clientHeight,
        0.15,
        1,
        0.18,
      );
      void flowInstance.setViewport(viewport);
    }, 100);
    return () => window.clearTimeout(timer);
  }, [flowInstance, graphLayout?.key, graphLayoutKey, inspectorPanelOpen, nodes.length, summaryExpanded]);

  useEffect(() => {
    if (!flowInstance || !selectedJob || graphLayout?.key !== graphLayoutKey) return;
    const node = flowInstance.getNode(selectedJob);
    if (!node) return;
    const timer = window.setTimeout(() => {
      void flowInstance.setCenter(
        node.position.x + (node.width ?? 320) / 2,
        node.position.y + (node.height ?? 120) / 2,
        { zoom: Math.max(flowInstance.getZoom(), 0.7), duration: 350 },
      );
    }, 180);
    return () => window.clearTimeout(timer);
  }, [flowInstance, graphLayout?.key, graphLayoutKey, selectedJob]);

  const viewRun = configurationOnly ? null : run;
  const activeInspectorTab = configuration && (!viewRun || inspectorTab === "configuration")
    ? "configuration"
    : "rows";

  const loadTrace = useCallback(async (lineageId: string) => {
    if (!run) return;
    setTraceId(lineageId);
    setTraceSearch("");
    try {
      const snapshots = await api<ApiDebugSnapshot[]>(
        `/api/runs/${encodeURIComponent(run.runId)}/trace/${encodeURIComponent(lineageId)}`,
      );
      setTrace(snapshots.map(normalizeSnapshot));
    } catch (reason) {
      setError(String(reason));
    }
  }, [run]);

  const selectSnapshot = useCallback((snapshot: DebugSnapshot) => {
    setSelectedSnapshot(snapshot);
    void loadTrace(snapshot.lineageId);
  }, [loadTrace]);

  const loadMoreJobRows = useCallback(async () => {
    if (!run || !selectedJob || loadingJobRows || !hasMoreJobRows) return;
    const requestVersion = rowRequestVersion.current;
    const controller = new AbortController();
    loadMoreController.current = controller;
    setLoadingJobRows(true);
    const query = createSnapshotQuery({
      stepId: selectedJob,
      search: rowSearch,
      limit: 100,
      offset: jobRows.length,
      stageName: selectedStage,
      sortState: rowSortState,
      filters: rowColumnFilters,
    });
    try {
      const apiPage = await api<ApiDebugSnapshotPage>(
        `/api/runs/${encodeURIComponent(run.runId)}/rows?${query.toString()}`,
        controller.signal,
      );
      if (requestVersion !== rowRequestVersion.current) return;
      const page = normalizeSnapshotPage(apiPage);
      setJobRows((current) => {
        const currentKeys = new Set(current.map(snapshotKey));
        return [...current, ...page.items.filter((snapshot) => !currentKeys.has(snapshotKey(snapshot)))];
      });
      setJobRowCount(page.totalCount);
      setRowPayloadColumns(page.payloadColumns);
      setHasMoreJobRows(page.hasMore);
    } catch (reason) {
      if (requestVersion === rowRequestVersion.current &&
          !(reason instanceof DOMException && reason.name === "AbortError")) {
        setError(String(reason));
      }
    } finally {
      if (requestVersion === rowRequestVersion.current) setLoadingJobRows(false);
      if (loadMoreController.current === controller) loadMoreController.current = null;
    }
  }, [
    hasMoreJobRows,
    jobRows.length,
    loadingJobRows,
    rowSearch,
    rowColumnFilters,
    rowSortState,
    run,
    selectedJob,
    selectedStage,
  ]);

  if (loading && !run && !error) {
    return <FluentProvider className="fluent-root" theme={webLightTheme}><div className="loading-state"><FluentSpinner size="large" label="Starting migration monitor…" /></div></FluentProvider>;
  }

  return (
    <FluentProvider className="fluent-root" theme={webLightTheme}>
      <div className="app-shell">
        <header className="topbar">
          <div className="brand">
            <img src="/logo.png" alt="" className="brand-logo" />
            <span><Text variant="large">Migration4Dataverse</Text><Text variant="small">Migration operations</Text></span>
          </div>
          <div className="topbar-actions">
            <select
              className="run-select"
              aria-label="Select migration run"
              value={configurationOnly ? "__configuration__" : runId || "__empty__"}
              onChange={(event) => {
                const selectedKey = event.currentTarget.value;
                if (selectedKey === "__configuration__") {
                  setShowConfiguration(true);
                  selectJob("");
                } else if (selectedKey !== "__empty__") {
                setShowConfiguration(false);
                setRunId(selectedKey);
                }
              }}
            >
              {previewConfiguration && <option value="__configuration__">Configuration preview · not executing</option>}
              {runs.map((item) => (
                <option key={item.runId} value={item.runId}>
                  {item.jobName} · {item.status} · {new Date(item.createdUtc).toLocaleString()}
                </option>
              ))}
              {runs.length === 0 && !previewConfiguration && <option value="__empty__" disabled>No runs found</option>}
            </select>
            <div className="live-state"><span className="live-dot" /> Live · 3s refresh</div>
            <button
              className="icon-button"
              type="button"
              title="Refresh now"
              aria-label="Refresh now"
              onClick={() => refresh().catch((reason: unknown) => setError(String(reason)))}
            ><RefreshCw size={18} /></button>
          </div>
        </header>

      {error && <div className="notice notice-error" role="alert">{error}</div>}

      {!viewRun && !configurationOnly ? (
        <section className="empty-state">
          <Layers3 size={44} />
          <Text variant="xxLarge">No migration runs found</Text>
          <Text>Start an Invoke-Migration run using this database, or run the monitoring demo.</Text>
        </section>
      ) : (
        <main className="dashboard-main">
          <section className="graph-panel">
            <div className="graph-search-control">
              <div className="job-view-toggle" role="group" aria-label="Job view">
                <button
                  type="button"
                  className={jobViewMode === "graph" ? "active" : ""}
                  aria-label="Graph view"
                  aria-pressed={jobViewMode === "graph"}
                  onClick={() => setJobViewMode("graph")}
                ><Layers3 size={15} /><span>Graph</span></button>
                <button
                  type="button"
                  className={jobViewMode === "table" ? "active" : ""}
                  aria-label="Table view"
                  aria-pressed={jobViewMode === "table"}
                  onClick={() => setJobViewMode("table")}
                ><Columns3 size={15} /><span>Table</span></button>
              </div>
              <label className="hide-completed-toggle" title="Hide completed jobs in both views">
                <input
                  type="checkbox"
                  aria-label="Hide completed jobs"
                  checked={hideCompletedJobs}
                  onChange={(event) => {
                    setHideCompletedJobs(event.currentTarget.checked);
                    setFocusedLevel(null);
                  }}
                />
                <span>Hide completed</span>
              </label>
              {stepSearchOpen && (
                <Combobox
                  id="step-search-combobox"
                  className="step-search-combobox"
                  aria-label="Find a pipeline step"
                  placeholder="Find a step by name"
                  freeform={false}
                  open={stepSearchOpen}
                  value={stepSearchValue}
                  onOpenChange={(_, data) => { if (!data.open) setStepSearchOpen(false); }}
                  onChange={(event) => setStepSearchValue(event.currentTarget.value)}
                  onOptionSelect={(_, data) => {
                    const stepName = data.optionValue;
                    if (typeof stepName !== "string" || !stepName) return;
                    setFocusedLevel(visibleJobs.length > 40 ? jobLevels.get(stepName) ?? null : null);
                    setStepSearchValue("");
                    selectJob(stepName);
                    setStepSearchOpen(false);
                  }}
                >
                  {visibleJobs
                    .filter((job) => job.name.toLocaleLowerCase().includes(stepSearchValue.trim().toLocaleLowerCase()))
                    .toSorted((a, b) => a.name.localeCompare(b.name))
                    .map((job) => <Option key={job.name} value={job.name}>{job.name}</Option>)}
                </Combobox>
              )}
              {focusedLevel !== null && (
                <button className="action-button graph-all-levels" type="button" onClick={() => setFocusedLevel(null)}>All levels</button>
              )}
              {!stepSearchOpen && selectedJob && <span className="active-step-selection" title={selectedJob}>{selectedJob}</span>}
              {selectedJob && (
                <button
                  className="icon-button graph-search-clear"
                  type="button"
                  title="Clear selected step"
                  aria-label="Clear selected step"
                  onClick={() => { selectJob(""); setStepSearchValue(""); setFocusedLevel(null); }}
                ><X size={16} /></button>
              )}
              <button
                className="icon-button graph-search-button"
                type="button"
                title={stepSearchOpen ? "Close step search" : "Find a step"}
                aria-label={stepSearchOpen ? "Close step search" : "Find a step"}
                aria-expanded={stepSearchOpen}
                aria-controls="step-search-combobox"
                onClick={() => {
                  if (stepSearchOpen) {
                    setStepSearchOpen(false);
                  } else {
                    setStepSearchValue("");
                    setStepSearchOpen(true);
                  }
                }}
              >{stepSearchOpen ? <X size={18} /> : <Search size={18} />}</button>
            </div>
            {trace.length > 0 && (
              <div className="lineage-selection-overlay">
                <span>Row {traceId} · {traceJobNames.size} steps highlighted</span>
                <button
                  className="text-button"
                  type="button"
                  onClick={() => { setTrace([]); setTraceId(""); setSelectedSnapshot(null); }}
                >Clear lineage</button>
              </div>
            )}
            {configurationOnly && (
              <div className="notice notice-info configuration-preview-notice" role="status">
                Configuration preview · not executing · {jobs.length} steps · {configuration?.connections.length ?? 0} connections
              </div>
            )}
            {graphLayoutError && (
              <div className="notice notice-error graph-layout-error" role="alert">{graphLayoutError}</div>
            )}
            {hideCompletedJobs && jobs.length > 0 && visibleJobs.length === 0 ? (
              <div className="job-filter-empty" role="status">
                All jobs are completed. Turn off “Hide completed” to show the full pipeline.
              </div>
            ) : jobViewMode === "table" ? (
              <div className="job-table-scroll" role="region" aria-label="Migration jobs" tabIndex={0}>
                <table className="job-table">
                  <thead>
                    <tr>
                      <th scope="col">Job</th>
                      <th scope="col">Status / activity</th>
                      <th scope="col">Output rows</th>
                      <th scope="col">Batches</th>
                      <th scope="col">Input rows</th>
                      <th scope="col">Filtered</th>
                      <th scope="col">Avg rows/s</th>
                      <th scope="col">Inputs</th>
                      <th scope="col">Outputs</th>
                    </tr>
                  </thead>
                  <tbody>
                    {visibleJobs.toSorted((left, right) =>
                      (jobLevels.get(left.name) ?? Number.MAX_SAFE_INTEGER) -
                        (jobLevels.get(right.name) ?? Number.MAX_SAFE_INTEGER) ||
                      left.name.localeCompare(right.name),
                    ).map((job) => {
                      const status = statusFor(job, jobs, configurationOnly);
                      const meta = statusInfo[status];
                      const StatusIcon = meta.icon;
                      const jobConfiguration = configurationJobsByName.get(job.name);
                      const jobTypeName = jobConfiguration?.type ?? job.type;
                      const jobType = jobTypeInfo[jobTypeName.toLowerCase()]?.label ?? jobTypeName;
                      const JobIcon = jobTypeIcon(jobTypeName);
                      const sourceConnection = endpointConnection(jobConfiguration, connectionsById, "Source");
                      const targetConnection = endpointConnection(jobConfiguration, connectionsById, "Target");
                      const batchesDone = job.succeededBatches + job.batchesWithErrors;
                      const batchesObserved = batchesDone + job.pendingBatches + job.inFlightBatches;
                      const activity = activityHintFor(job, jobs, configurationOnly);
                      const blockingTask = status === "Blocked" ? blockingTaskFor(job, jobs) : null;
                      const outputs = (jobOutputs.get(job.name) ?? []).toSorted((left, right) => left.localeCompare(right));
                      const renderJobLinks = (relatedJobs: string[]) => relatedJobs.length === 0
                        ? "—"
                        : (
                          <div className="job-table-links">
                            {relatedJobs.map((relatedJobName) => {
                              const relatedJob = jobsByName.get(relatedJobName);
                              if (!relatedJob) {
                                return <span key={relatedJobName} className="job-table-link-missing">{relatedJobName}</span>;
                              }
                              const relatedStatus = statusInfo[statusFor(relatedJob, jobs, configurationOnly)];
                              const RelatedStatusIcon = relatedStatus.icon;
                              return (
                                <a
                                  key={relatedJobName}
                                  className={`job-table-link job-table-status ${relatedStatus.color}`}
                                  href={`#job-row-${encodeURIComponent(relatedJobName)}`}
                                  onClick={(event) => { event.preventDefault(); event.stopPropagation(); jumpToJob(relatedJobName); }}
                                ><RelatedStatusIcon size={12} aria-hidden="true" />{relatedJobName}</a>
                              );
                            })}
                          </div>
                        );
                      const inputRows = job.expectedRows === null
                        ? number.format(job.inputRowsProcessed)
                        : `${number.format(job.inputRowsProcessed)} / ${number.format(job.expectedRows)}`;
                      const inputWork = [
                        job.inputRowsInFlight > 0 ? `${number.format(job.inputRowsInFlight)} in progress` : "",
                        job.inputRowsPending > 0 ? `${number.format(job.inputRowsPending)} queued` : "",
                      ].filter(Boolean).join(" · ");
                      const rowProgress = inputRowProgress(job);
                      return (
                        <tr
                          key={job.name}
                          id={`job-row-${encodeURIComponent(job.name)}`}
                          className={selectedJob === job.name ? "selected" : ""}
                          aria-selected={selectedJob === job.name}
                          onClick={() => selectJob(job.name)}
                        >
                          <td>
                            <button
                              type="button"
                              className="job-table-name"
                              onClick={(event) => { event.stopPropagation(); selectJob(job.name); }}
                            >
                              <span className="job-table-identity">
                                <span className="job-icon" aria-hidden="true"><JobIcon size={14} /></span>
                                <span className="job-table-identity-text">
                                  <strong className="job-table-job-name">{job.name}</strong>
                                  <span>{jobType}</span>
                                </span>
                              </span>
                              {(sourceConnection || targetConnection) && (
                                <span className="job-table-connections">
                                  {sourceConnection && (
                                    <span title={`Source connection: ${sourceConnection.typeName} · ${sourceConnection.id}`}>
                                      <Database size={12} aria-hidden="true" />
                                      <span>{sourceConnection.typeName} · {sourceConnection.id}</span>
                                    </span>
                                  )}
                                  {targetConnection && (
                                    <span title={`Target connection: ${targetConnection.typeName} · ${targetConnection.id}`}>
                                      <Database size={12} aria-hidden="true" />
                                      <span>{targetConnection.typeName} · {targetConnection.id}</span>
                                    </span>
                                  )}
                                </span>
                              )}
                            </button>
                          </td>
                          <td className="job-table-status-cell">
                            <span className={`job-table-status ${meta.color}`}><StatusIcon size={12} />{meta.label}</span>
                            {blockingTask
                              ? <small className="job-table-activity" title={`Furthest blocking task: ${blockingTask.name}`}>Blocked by: {blockingTask.name}</small>
                              : activity && <small className="job-table-activity" title={activity}>{activity}</small>}
                          </td>
                          <td>
                            <span>{number.format(job.succeededRows)}</span>
                            {job.failedRows > 0 && <small className="job-table-secondary">{number.format(job.failedRows)} failed</small>}
                          </td>
                          <td title={`${batchesDone} completed or failed batches; ${job.inFlightBatches} in progress; ${job.pendingBatches} queued`}>
                            {number.format(batchesDone)} / {number.format(batchesObserved)}
                          </td>
                          <td title={`${inputRows} input rows${job.inputFinalized ? "" : " · expected total may grow"}${inputWork ? ` · ${inputWork}` : ""}`}>
                            <span>{inputRows}</span>
                            {rowProgress && (
                              <progress
                                className="job-row-progress"
                                max={rowProgress.max}
                                value={rowProgress.value}
                                aria-label={`${job.name} input row progress${job.inputFinalized ? "" : "; expected total may grow"}`}
                              />
                            )}
                            {inputWork && <small className="job-table-secondary">{inputWork}</small>}
                          </td>
                          <td>{number.format(job.filteredRows)}</td>
                          <td className="job-table-throughput-cell">
                            <span>{job.rowsPerSecond === null ? "—" : rateNumber.format(job.rowsPerSecond)}</span>
                            <JobThroughputChart job={job} />
                          </td>
                          <td>{renderJobLinks(job.dependsOn.toSorted((left, right) => left.localeCompare(right)))}</td>
                          <td>{renderJobLinks(outputs)}</td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>
            ) : largeOverview ? (
              <div className="level-grid">
                {[...new Set(visibleJobs.map((job) => jobLevels.get(job.name) ?? 0))].sort((a, b) => a - b).map((level) => {
                  const items = visibleJobs.filter((job) => jobLevels.get(job.name) === level);
                  const failed = items.filter((job) => statusFor(job, jobs, configurationOnly) === "Failed").length;
                  const active = items.filter((job) => statusFor(job, jobs, configurationOnly) === "Running").length;
                  return (
                    <button className="level-card" key={level} onClick={() => setFocusedLevel(level)}>
                      <span className="level-index">LEVEL {level + 1}</span>
                      <strong>{items.length} steps</strong>
                      <span>{configurationOnly ? "Configuration only" : `${active} running · ${failed} failed`}</span>
                      <span className="level-drill">Open focused graph <ChevronDown size={13} /></span>
                    </button>
                  );
                })}
              </div>
            ) : (
              <div className="flow-canvas" ref={flowCanvasRef}>
                {!configurationOnly && (
                  <div className="flow-legend">
                    <span className="legend-note"><i className="legend-line" />Longest observed chain</span>
                  </div>
                )}
                <ReactFlow
                  nodes={nodes}
                  edges={edges}
                  nodeTypes={nodeTypes}
                  onInit={setFlowInstance}
                  minZoom={0.15}
                  maxZoom={1.4}
                  onNodeClick={(_, node) => selectJob(node.id)}
                  proOptions={{ hideAttribution: true }}
                >
                  <Background color="#d6d6d6" gap={20} size={1} />
                  <MiniMap pannable zoomable nodeColor={(node) => {
                    const data = node.data as JobNodeData;
                    return `var(--${statusInfo[data.status].color === "muted" ? "gray" : statusInfo[data.status].color})`;
                  }} />
                  <Controls showInteractive={false} />
                </ReactFlow>
              </div>
            )}
            </section>

          {viewRun && (
            <MigrationSummaryPanel
              run={viewRun}
              jobs={jobs}
              expanded={summaryExpanded}
              onToggle={() => setSummaryExpanded((expanded) => !expanded)}
              onSelectJob={selectJob}
            />
          )}

          {(viewRun || configuration) && (
            <section className={`row-drawer${inspectorPanelOpen ? " expanded" : ""}`}>
              <div className="row-drawer-header">
                <button
                  type="button"
                  className="row-panel-toggle"
                  aria-expanded={inspectorPanelOpen}
                  aria-controls="row-panel-content"
                  onClick={() => setInspectorPanelOpen((open) => !open)}
                >
                  <SelectedJobIcon size={17} />
                  <span>{selectedJob || "Step inspection"}</span>
                  <ChevronDown className={inspectorPanelOpen ? "expanded" : ""} size={16} />
                </button>
                <div className="inspector-tabs" role="tablist" aria-label="Step inspector">
                  {viewRun && (
                    <button
                      className={`inspector-tab${activeInspectorTab === "rows" ? " active" : ""}`}
                      type="button"
                      role="tab"
                      aria-selected={activeInspectorTab === "rows"}
                      aria-controls="step-rows-panel"
                      onClick={() => {
                        if (activeInspectorTab === "rows" && inspectorPanelOpen) {
                          setInspectorPanelOpen(false);
                        } else {
                          setInspectorTab("rows");
                          setInspectorPanelOpen(true);
                        }
                      }}
                    ><FileSearch size={15} />Captured rows</button>
                  )}
                  {configuration && (
                    <button
                      className={`inspector-tab${activeInspectorTab === "configuration" ? " active" : ""}`}
                      type="button"
                      role="tab"
                      aria-selected={activeInspectorTab === "configuration"}
                      aria-controls="step-configuration-panel"
                      onClick={() => {
                        if (activeInspectorTab === "configuration" && inspectorPanelOpen) {
                          setInspectorPanelOpen(false);
                        } else {
                          setInspectorTab("configuration");
                          setInspectorPanelOpen(true);
                        }
                      }}
                    ><Settings2 size={15} />Configuration</button>
                  )}
                </div>
              </div>
              {inspectorPanelOpen && (
                <div className="row-drawer-content" id="row-panel-content">
                  {activeInspectorTab === "rows" && viewRun && (
                    <div className="inspector-tab-panel" id="step-rows-panel" role="tabpanel">
                      {selectedJob ? (
                        <JobInspector
                          rows={jobRows}
                          rowCount={jobRowCount}
                          stageCounts={rowStageCounts}
                          payloadColumns={rowPayloadColumns}
                          selectedStage={selectedStage}
                          hasMoreRows={hasMoreJobRows}
                          loadingRows={loadingJobRows}
                          onLoadMore={loadMoreJobRows}
                          onSelectRow={selectSnapshot}
                          debugEnabled={viewRun.debugCaptureEnabled}
                          trace={trace}
                          selectedSnapshot={selectedSnapshot}
                          rowSearch={rowSearch}
                          columnFilters={rowColumnFilters}
                          sortState={rowSortState}
                          onSortChange={(sortState) => {
                            setRowSortByStage((current) => ({ ...current, [selectedStage]: sortState }));
                            restartSnapshotQuery();
                          }}
                          onColumnFiltersChange={(filters) => {
                            setRowFiltersByStage((current) => ({ ...current, [selectedStage]: filters }));
                            restartSnapshotQuery();
                          }}
                          onStageChange={(stageName) => {
                            setSelectedStage(stageName);
                            clearJobRows();
                            setSelectedSnapshot(null);
                            setTrace([]);
                            setTraceId("");
                          }}
                          onRowSearchChange={(value) => {
                            setRowSearchByStage((current) => ({ ...current, [selectedStage]: value }));
                            restartSnapshotQuery();
                            setSelectedSnapshot(null);
                            setTrace([]);
                            setTraceId("");
                          }}
                          traceSearch={traceSearch}
                          onTraceSearchChange={setTraceSearch}
                        />
                      ) : <div className="inline-empty">Select a graph step to see its captured rows.</div>}
                    </div>
                  )}
                  {activeInspectorTab === "configuration" && configuration && (
                    <div className="inspector-tab-panel" id="step-configuration-panel" role="tabpanel">
                      {selectedJob ? (
                        <ConfigurationInspector
                          configuration={configuration}
                          selectedJob={selectedJob}
                          connectionInstances={viewRun?.connectionInstances ?? []}
                        />
                      ) : (
                        <div className="inline-empty">Select a graph step to see its configuration properties.</div>
                      )}
                    </div>
                  )}
                </div>
              )}
            </section>
          )}

        </main>
      )}
      </div>
    </FluentProvider>
  );
}

function MigrationSummaryPanel({
  run,
  jobs,
  expanded,
  onToggle,
  onSelectJob,
}: {
  run: RunSummary;
  jobs: DashboardJob[];
  expanded: boolean;
  onToggle: () => void;
  onSelectJob: (jobName: string) => void;
}) {
  const terminalTargets = getTerminalTargetNames(run);
  const targets = jobs.filter((job) =>
    terminalTargets.has(job.name) || job.type.toLowerCase() === "target",
  );
  const targetRowsProcessed = targets.reduce((total, job) => total + job.inputRowsProcessed, 0);
  const targetRowsSucceeded = targets.reduce((total, job) => total + job.succeededRows, 0);
  const targetRowsFailed = targets.reduce((total, job) => total + job.failedRows, 0);
  const targetRowsExpected = targets.length > 0 &&
    targets.every((job) => job.inputFinalized && job.expectedRows !== null)
    ? targets.reduce((total, job) => total + (job.expectedRows ?? 0), 0)
    : null;
  const targetProgress = targetRowsExpected === null
    ? null
    : targetRowsExpected === 0
      ? 100
      : Math.min(100, (targetRowsProcessed / targetRowsExpected) * 100);
  const overallThroughput = buildOverallThroughput(jobs, run);
  const estimatedCompletion = run.status === "Completed"
    ? `Completed ${new Date(run.updatedUtc).toLocaleString()}`
    : run.status !== "Running"
      ? "Unavailable"
      : targetRowsExpected === null || !overallThroughput || overallThroughput.output.average <= 0
        ? "Estimating…"
        : targetRowsExpected <= targetRowsProcessed
          ? "Finishing now"
          : new Date(
              Date.now() +
              ((targetRowsExpected - targetRowsProcessed) / overallThroughput.output.average) * 1000,
            ).toLocaleString();

  return (
    <section className={`summary-panel${expanded ? " expanded" : ""}`} aria-label="Migration overview">
      <div className="summary-header">
        <OverallThroughputChart throughput={overallThroughput} />
        <div className="summary-header-metrics">
          <div className="summary-header-metric">
            <span>Elapsed</span>
            <strong>{formatElapsed(run.createdUtc, run.status === "Running" ? undefined : run.updatedUtc)}</strong>
          </div>
          <div className="summary-target-metric">
            <div className="summary-target-count">
              <span>{targetRowsExpected === null ? "Target rows so far" : "Target rows"}</span>
              <strong>{number.format(targetRowsExpected ?? targetRowsProcessed)}</strong>
            </div>
            <div className="summary-target-outcomes">
              <span>Succeeded <strong>{number.format(targetRowsSucceeded)}</strong></span>
              <span>Errors <strong>{number.format(targetRowsFailed)}</strong></span>
            </div>
            {targetRowsExpected !== null && (
              <progress
                className="summary-target-progress"
                max={100}
                value={targetProgress ?? undefined}
                aria-label={`${Math.round(targetProgress ?? 0)}% of target rows processed`}
              />
            )}
          </div>
          <div className="summary-header-metric summary-eta">
            <span>Estimated completion</span>
            <strong>{estimatedCompletion}</strong>
          </div>
        </div>
        <button
          className="icon-button summary-toggle"
          type="button"
          title={expanded ? "Collapse migration overview" : "Expand migration overview"}
          aria-label={expanded ? "Collapse migration overview" : "Expand migration overview"}
          aria-expanded={expanded}
          onClick={onToggle}
        >{expanded ? <ChevronUp size={16} /> : <ChevronDown size={16} />}</button>
      </div>
      {expanded && (
        <div className="summary-content">
          <div className="summary-detail-grid">
            <section className="summary-block">
              <Text variant="mediumPlus" className="section-title"><Activity size={16} /> Execution timeline</Text>
              <Timeline jobs={jobs} onSelect={onSelectJob} />
            </section>
            <section className="summary-block">
              <Text variant="mediumPlus" className="section-title"><Gauge size={16} /> Runtime hotspots</Text>
              <Hotspots jobs={jobs} />
            </section>
          </div>
        </div>
      )}
    </section>
  );
}

function OverallThroughputChart({ throughput }: { throughput: OverallThroughput | null }) {
  const width = 480;
  const height = 44;
  const inputPoints = throughput?.input.points ?? Array<number>(24).fill(0);
  const outputPoints = throughput?.output.points ?? Array<number>(24).fill(0);
  const maximum = Math.max(1, ...inputPoints, ...outputPoints);
  const pointCoordinates = (rate: number, index: number, pointCount: number) => ({
    x: index * width / Math.max(1, pointCount - 1),
    y: height - 2 - (rate / maximum) * (height - 5),
  });
  const toPolyline = (points: number[]) => points
    .map((rate, index) => {
      const { x, y } = pointCoordinates(rate, index, points.length);
      return `${x},${y}`;
    })
    .join(" ");
  const pointTitle = (seriesName: string, rate: number, index: number) => {
    if (!throughput) return `${seriesName}: no throughput data`;
    const bucketDuration = (Date.parse(throughput.endedUtc) - Date.parse(throughput.startedUtc)) /
      Math.max(1, inputPoints.length);
    const start = Date.parse(throughput.startedUtc) + index * bucketDuration;
    const end = start + bucketDuration;
    return `${seriesName}: ${new Date(start).toLocaleString()} – ${new Date(end).toLocaleString()}: ${rateNumber.format(rate)} rows/s`;
  };
  const pointMarkers = (seriesName: string, seriesClass: string, points: number[]) => (
    points.map((rate, index) => {
      const { x, y } = pointCoordinates(rate, index, points.length);
      return (
        <circle
          key={`${seriesClass}-${index}`}
          className={`overall-throughput-point ${seriesClass}`}
          cx={x}
          cy={y}
          r="3"
        >
          <title>{pointTitle(seriesName, rate, index)}</title>
        </circle>
      );
    })
  );
  const rateSummary = (series: ThroughputSeries | undefined) => series
    ? `Current ${rateNumber.format(series.current)} · avg ${rateNumber.format(series.average)} rows/s`
    : "Current — · avg — rows/s";

  return (
    <div className="overall-throughput" aria-label="Overall migration throughput">
      <svg viewBox={`0 0 ${width} ${height}`} role="img" aria-label="Input and output rows per second over the run">
        <line className="overall-throughput-grid" x1="0" y1="2" x2={width} y2="2" />
        <line className="overall-throughput-grid" x1="0" y1={height / 2} x2={width} y2={height / 2} />
        <line className="overall-throughput-grid" x1="0" y1={height - 1} x2={width} y2={height - 1} />
        <polyline className="overall-throughput-input" points={toPolyline(inputPoints)} />
        <polyline className="overall-throughput-output" points={toPolyline(outputPoints)} />
        {throughput && pointMarkers("Input (source)", "input", inputPoints)}
        {throughput && pointMarkers("Output (target)", "output", outputPoints)}
      </svg>
      <div className="overall-throughput-legend">
        <span className="overall-throughput-label input"><i />Input (source)<small>{rateSummary(throughput?.input)}</small></span>
        <span className="overall-throughput-label output"><i />Output (target)<small>{rateSummary(throughput?.output)}</small></span>
      </div>
    </div>
  );
}

function InfoRow({ label, value }: { label: string; value: string }) {
  return <div className="info-row"><span>{label}</span><strong>{value}</strong></div>;
}

function ConfigurationInspector({
  configuration,
  selectedJob,
  connectionInstances,
}: {
  configuration: MigrationConfiguration;
  selectedJob: string;
  connectionInstances: ConnectionInstanceSnapshot[];
}) {
  const job = configuration.jobs.find((candidate) => candidate.name === selectedJob);
  if (!job) {
    return <section className="panel configuration-inspector"><div className="inline-empty">Configuration for this step is unavailable.</div></section>;
  }

  const connectionsById = new Map(configuration.connections.map((connection) => [connection.id, connection]));
  const endpointReferences = [
    endpointConnection(job, connectionsById, "Source"),
    endpointConnection(job, connectionsById, "Target"),
  ].filter((connection): connection is ConnectionReference => Boolean(connection));
  const referencedConnectionIds = new Set(
    endpointReferences.map((connection) => connection.id),
  );
  const endpointTypesById = new Map(endpointReferences.map((connection) => [connection.id, connection.typeName]));
  const connections = configuration.connections.filter((connection) => referencedConnectionIds.has(connection.id));

  return (
    <section className="panel configuration-inspector">
      <div className="configuration-columns">
        <div>
          <Text variant="mediumPlus" className="section-title">Step configuration</Text>
          <div className="configuration-meta">
            <InfoRow label="Type" value={job.type} />
            <InfoRow label="Inputs" value={job.dependsOn.filter((id) => !job.completionDependencies.includes(id)).join(", ") || "None"} />
            <InfoRow label="Waits for completion" value={job.completionDependencies.join(", ") || "None"} />
            <InfoRow label="Reads connector source" value={job.hasSource ? "Yes" : "No"} />
          </div>
          <h4>Properties</h4>
          <pre className="configuration-json">{JSON.stringify(job.properties, null, 2)}</pre>
        </div>
        <div>
          <Text variant="mediumPlus" className="section-title">Connected endpoints</Text>
          {connections.length === 0 ? (
            <div className="inline-empty">This step has no named source or target connection.</div>
          ) : (
            <div className="configuration-list">
              {connections.map((connection) => {
                const snapshots = connectionInstances.flatMap((snapshot) =>
                  snapshot.connections
                    .filter((instance) => instance.id === connection.id)
                    .map((instance) => ({ phase: snapshot.phase, capturedUtc: snapshot.capturedUtc, instance })),
                );
                return (
                  <section className="configuration-connection" key={connection.id}>
                    <div className="configuration-connection-heading">
                      <strong>{endpointTypesById.get(connection.id) ?? connection.typeName?.split(".").at(-1) ?? "Connection"}</strong>
                      <span>{connection.id}</span>
                    </div>
                    <h4>Configured properties</h4>
                    <pre className="configuration-json">{JSON.stringify(connection.properties, null, 2)}</pre>
                    {snapshots.map((snapshot) => (
                      <div className="connection-snapshot" key={`${snapshot.phase}-${snapshot.capturedUtc}`}>
                        <h4>{snapshot.phase} instance · {new Date(snapshot.capturedUtc).toLocaleString()}</h4>
                        <div className="configuration-meta">
                          <InfoRow label="Runtime type" value={snapshot.instance.typeName ?? "Unknown"} />
                        </div>
                        <pre className="configuration-json">{JSON.stringify(snapshot.instance.properties, null, 2)}</pre>
                      </div>
                    ))}
                    {snapshots.length === 0 && <Text>No runtime instance snapshot is stored for this connection.</Text>}
                  </section>
                );
              })}
            </div>
          )}
        </div>
      </div>
      <Text variant="small" className="configuration-disclaimer">Configuration values are read-only; secret fields are redacted.</Text>
    </section>
  );
}

function Timeline({ jobs, onSelect }: { jobs: DashboardJob[]; onSelect: (name: string) => void }) {
  const now = Date.now();
  const active = jobs
    .map((job) => {
      const periods = job.activityPeriods?.length
        ? job.activityPeriods.map((period) => ({
            state: period.state.toLowerCase(),
            start: Date.parse(period.startedUtc),
            end: period.endedUtc ? Date.parse(period.endedUtc) : now,
          }))
        : job.firstBatchUtc && job.lastBatchUtc
          ? [{
              state: "observed",
              start: Date.parse(job.firstBatchUtc),
              end: Date.parse(job.lastBatchUtc),
            }]
          : [];
      return { job, periods };
    })
    .filter((item) => item.periods.length > 0);
  if (!active.length) return <div className="inline-empty">Batch activity will appear here once work is materialized.</div>;
  const first = Math.min(...active.flatMap(({ periods }) => periods.map((period) => period.start)));
  const last = Math.max(...active.flatMap(({ periods }) => periods.map((period) => period.end)));
  const duration = Math.max(1, last - first);
  return (
    <>
      <div className="timeline-legend">
        <span><i className="working" />Working</span>
        <span><i className="waiting" />Waiting / blocked</span>
        {active.some(({ periods }) => periods.some((period) => period.state === "observed")) && (
          <span><i className="observed" />Batch window only (older run)</span>
        )}
      </div>
      <div className="timeline-list">
        {active.toSorted((a, b) => b.job.activeMilliseconds - a.job.activeMilliseconds).slice(0, 14).map(({ job, periods }) => {
          const work = periods
            .filter((period) => period.state === "working")
            .reduce((total, period) => total + Math.max(0, period.end - period.start), 0);
          const waiting = periods
            .filter((period) => period.state === "waiting")
            .reduce((total, period) => total + Math.max(0, period.end - period.start), 0);
          const detail = periods.some((period) => period.state === "observed")
            ? `Observed window ${formatDuration(Math.floor((periods[0].end - periods[0].start) / 1000))}`
            : `Work ${formatMilliseconds(work)} · wait ${formatMilliseconds(waiting)}`;
          return (
            <button className="timeline-row" key={job.name} type="button" onClick={() => onSelect(job.name)}>
              <span className="timeline-name">{job.name}</span>
              <span className="timeline-track" aria-label={detail}>
                {periods.map((period, index) => {
                  const left = Math.max(0, Math.min(100, ((period.start - first) / duration) * 100));
                  const width = Math.max(0.4, Math.min(100 - left, ((period.end - period.start) / duration) * 100));
                  const title = period.state === "observed"
                    ? "Observed batch window; detailed activity history is unavailable"
                    : `${period.state === "working" ? "Working" : "Waiting / blocked"}: ${new Date(period.start).toLocaleTimeString()} – ${new Date(period.end).toLocaleTimeString()}`;
                  return <i key={index} className={`timeline-segment ${period.state}`} title={title} style={{ left: `${left}%`, width: `${width}%` }} />;
                })}
              </span>
              <span className="timeline-duration">{detail}</span>
            </button>
          );
        })}
      </div>
    </>
  );
}

function Hotspots({ jobs }: { jobs: DashboardJob[] }) {
  const jobsByName = new Map(jobs.map((job) => [job.name, job]));
  const waitingDescendants = (job: DashboardJob): number => {
    const visited = new Set([job.name]);
    const queue = [job.name];
    let waiting = 0;
    while (queue.length > 0) {
      const parentName = queue.shift()!;
      for (const candidate of jobs) {
        if (visited.has(candidate.name) || !candidate.dependsOn.includes(parentName)) continue;
        visited.add(candidate.name);
        queue.push(candidate.name);
        const hasIncompleteParent = candidate.dependsOn.some((dependency) =>
          !jobsByName.get(dependency)?.isComplete,
        );
        if (!candidate.isComplete &&
            candidate.batchesWithErrors === 0 &&
            candidate.pendingBatches === 0 &&
            candidate.inFlightBatches === 0 &&
            (candidate.pendingDependencyEvents > 0 || hasIncompleteParent)) {
          waiting++;
        }
      }
    }
    return waiting;
  };
  const active = jobs
    .filter((job) =>
      !job.isComplete &&
      job.batchesWithErrors === 0 &&
      !isBlockedByDownstream(job, jobs) &&
      (job.pendingBatches > 0 || job.inFlightBatches > 0),
    )
    .map((job) => {
      const rate = job.rowsPerSecondSamples?.at(-1)?.rowsPerSecond ?? job.rowsPerSecond;
      const waiting = waitingDescendants(job);
      const pressure = job.pendingBatches * 4 + waiting * 3 + (job.inFlightBatches > 0 ? 1 : 0);
      const score = pressure / Math.max(rate ?? 1, 1);
      return { job, rate, waiting, score };
    })
    .toSorted((left, right) =>
      right.score - left.score ||
      right.job.pendingBatches - left.job.pendingBatches ||
      right.waiting - left.waiting ||
      (left.rate ?? Number.POSITIVE_INFINITY) - (right.rate ?? Number.POSITIVE_INFINITY),
    )
    .slice(0, 7);
  if (!active.length) return <div className="inline-empty">No active throughput bottlenecks.</div>;
  const maximum = Math.max(1, ...active.map((item) => item.score));
  return <div className="hotspot-list">{active.map(({ job, rate, waiting, score }) => {
    const impact = [
      job.pendingBatches > 0 ? `${number.format(job.pendingBatches)} queued` : "",
      waiting > 0 ? `${number.format(waiting)} downstream waiting` : "",
      job.inFlightBatches > 0 ? `${number.format(job.inFlightBatches)} active` : "",
      rate === null ? "sampling throughput" : `${rateNumber.format(rate)} rows/s`,
    ].filter(Boolean).join(" · ");
    return (
      <div className="hotspot-row" key={job.name} title={`${job.name}: ${impact}`}>
        <div><strong>{job.name}</strong><small>{impact}</small></div>
        <i aria-label={`Relative blocking impact ${number.format(score)}`}><b style={{ width: `${Math.max(4, (score / maximum) * 100)}%` }} /></i>
      </div>
    );
  })}</div>;
}

function JobInspector({
  rows,
  rowCount,
  stageCounts,
  payloadColumns,
  selectedStage,
  hasMoreRows,
  loadingRows,
  onLoadMore,
  onSelectRow,
  debugEnabled,
  trace,
  selectedSnapshot,
  rowSearch,
  columnFilters,
  sortState,
  onSortChange,
  onColumnFiltersChange,
  onRowSearchChange,
  onStageChange,
  traceSearch,
  onTraceSearchChange,
}: {
  rows: DebugSnapshot[];
  rowCount: number;
  stageCounts: DebugSnapshotPage["stageCounts"];
  payloadColumns: string[];
  selectedStage: string;
  hasMoreRows: boolean;
  loadingRows: boolean;
  onLoadMore: () => void;
  onSelectRow: (snapshot: DebugSnapshot) => void;
  debugEnabled: boolean;
  trace: DebugSnapshot[];
  selectedSnapshot: DebugSnapshot | null;
  rowSearch: string;
  columnFilters: Record<string, string>;
  sortState: SnapshotGridSortState;
  onSortChange: (state: SnapshotGridSortState) => void;
  onColumnFiltersChange: (filters: Record<string, string>) => void;
  onRowSearchChange: (value: string) => void;
  onStageChange: (stageName: string) => void;
  traceSearch: string;
  onTraceSearchChange: (value: string) => void;
}) {
  const [visibleTraceCount, setVisibleTraceCount] = useState(100);
  useEffect(() => setVisibleTraceCount(100), [trace, traceSearch]);

  if (!debugEnabled) {
    return (
      <div className="notice notice-info" role="status">
        Row details aren&apos;t visible because debug capture was off for this run. Enable debug capture on a future run to retain its row snapshots.
      </div>
    );
  }

  const filteredTrace = trace.filter((snapshot) => {
    const query = traceSearch.trim().toLocaleLowerCase();
    return !query ||
      snapshot.jobName.toLocaleLowerCase().includes(query) ||
      snapshot.stageName.toLocaleLowerCase().includes(query) ||
      snapshot.lineageId.toLocaleLowerCase().includes(query) ||
      snapshot.payloadJson.toLocaleLowerCase().includes(query);
  });
  const visibleTrace = filteredTrace.slice(0, visibleTraceCount);

  return (
    <div className="row-browser">
      {stageCounts.length > 0 && (
        <div className="row-stage-tabs" role="tablist" aria-label="Captured stages">
          {stageCounts.map((stage) => (
            <button
              className={`row-stage-tab${selectedStage === stage.stageName ? " active" : ""}`}
              id={`stage-tab-${encodeURIComponent(stage.stageName)}`}
              key={stage.stageName}
              type="button"
              role="tab"
              aria-selected={selectedStage === stage.stageName}
              onClick={() => onStageChange(stage.stageName)}
            >
              <span>{friendlyStageName(stage.stageName)}</span>
              <small>{number.format(stage.count)}</small>
            </button>
          ))}
        </div>
      )}
      {rows.length === 0 && (
        <div className="row-count-toolbar">
          <Text variant="small" className="row-count">{number.format(rows.length)} of {number.format(rowCount)} matching row snapshots</Text>
          {selectedStage && (
            <input
              className="search-input snapshot-search-input"
              type="search"
              aria-label={`Search ${friendlyStageName(selectedStage)} rows`}
              placeholder={`Search ${friendlyStageName(selectedStage)} rows`}
              value={rowSearch}
              onChange={(event) => onRowSearchChange(event.currentTarget.value)}
            />
          )}
        </div>
      )}
      {loadingRows && rows.length === 0 ? (
        <FluentSpinner label="Searching captured rows…" />
      ) : rows.length === 0 ? (
        <div className="inline-empty">No captured rows match this search for the selected step.</div>
      ) : (
        <SnapshotTable
          items={rows}
          onSelectRow={onSelectRow}
          selectedSnapshot={selectedSnapshot}
          payloadColumns={payloadColumns}
          matchingRowCount={rowCount}
          remoteColumnFilters={columnFilters}
          remoteSortState={sortState}
          onRemoteColumnFiltersChange={onColumnFiltersChange}
          onRemoteSortChange={onSortChange}
          rowSearch={rowSearch}
          rowSearchStage={selectedStage}
          onRowSearchChange={onRowSearchChange}
          hasMoreRows={hasMoreRows}
          loadingRows={loadingRows}
          onLoadMore={onLoadMore}
        />
      )}
      {loadingRows && rows.length > 0 && <FluentSpinner size="small" label="Loading more captured rows…" />}
      {selectedSnapshot && (
        <section className="selected-row-panel">
          <Text variant="mediumPlus">Selected row · {friendlyStageName(selectedSnapshot.stageName)}</Text>
          <Text variant="small">Lineage {selectedSnapshot.lineageId} · captured {new Date(selectedSnapshot.capturedUtc).toLocaleString()}</Text>
          <pre className="configuration-json">{prettyJson(selectedSnapshot.payloadJson)}</pre>
        </section>
      )}
      {trace.length > 0 && (
        <section className="lineage-history">
          <div className="rows-toolbar">
            <div>
              <Text variant="mediumPlus">Row lineage across the pipeline</Text>
              <Text variant="small">{traceJobCount(trace)} steps · {number.format(filteredTrace.length)} matching row snapshots</Text>
            </div>
            <input
              className="search-input"
              type="search"
              aria-label="Search lineage stages or values"
              placeholder="Search lineage stages or values"
              value={traceSearch}
              onChange={(event) => onTraceSearchChange(event.currentTarget.value)}
            />
          </div>
          <SnapshotTable
            items={visibleTrace}
            onSelectRow={onSelectRow}
            selectedSnapshot={selectedSnapshot}
            matchingRowCount={filteredTrace.length}
            showStageColumn
          />
          {filteredTrace.length === 0 && <div className="inline-empty">No lineage rows match this search.</div>}
          {visibleTrace.length < filteredTrace.length && (
            <div className="load-more">
              <button
                className="action-button"
                type="button"
                onClick={() => setVisibleTraceCount((count) => count + 100)}
              >Show more lineage rows ({number.format(visibleTrace.length)} of {number.format(filteredTrace.length)})</button>
            </div>
          )}
        </section>
      )}
    </div>
  );
}

interface SnapshotGridColumn {
  id: string;
  label: string;
  value: (item: DebugSnapshot) => unknown;
  display: (item: DebugSnapshot) => string;
  copyValue?: (item: DebugSnapshot) => string;
  selectsLineage?: boolean;
}

const MIN_SNAPSHOT_COLUMN_WIDTH = 72;
const MAX_AUTO_SNAPSHOT_COLUMN_WIDTH = 360;

function SnapshotTable({
  items,
  onSelectRow,
  selectedSnapshot,
  showStageColumn = false,
  payloadColumns: serverPayloadColumns = [],
  matchingRowCount,
  remoteColumnFilters,
  remoteSortState,
  onRemoteColumnFiltersChange,
  onRemoteSortChange,
  rowSearch,
  rowSearchStage,
  onRowSearchChange,
  hasMoreRows = false,
  loadingRows = false,
  onLoadMore,
}: {
  items: DebugSnapshot[];
  onSelectRow: (snapshot: DebugSnapshot) => void;
  selectedSnapshot: DebugSnapshot | null;
  showStageColumn?: boolean;
  payloadColumns?: string[];
  matchingRowCount?: number;
  remoteColumnFilters?: Record<string, string>;
  remoteSortState?: SnapshotGridSortState;
  onRemoteColumnFiltersChange?: (filters: Record<string, string>) => void;
  onRemoteSortChange?: (state: SnapshotGridSortState) => void;
  rowSearch?: string;
  rowSearchStage?: string;
  onRowSearchChange?: (value: string) => void;
  hasMoreRows?: boolean;
  loadingRows?: boolean;
  onLoadMore?: () => void;
}) {
  const [jsonMode, setJsonMode] = useState(false);
  const [hiddenColumnIds, setHiddenColumnIds] = useState<Set<string>>(() => new Set());
  const [columnOrder, setColumnOrder] = useState<string[]>([]);
  const [columnWidths, setColumnWidths] = useState<Record<string, number>>({});
  const [localColumnFilters, setLocalColumnFilters] = useState<Record<string, string>>({});
  const [columnMenuSearch, setColumnMenuSearch] = useState("");
  const [filterColumnSearch, setFilterColumnSearch] = useState("");
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [localSortState, setLocalSortState] = useState<SnapshotGridSortState>(defaultSnapshotSort);
  const columnFilters = remoteColumnFilters ?? localColumnFilters;
  const sortState = remoteSortState ?? localSortState;
  const remoteMode = remoteColumnFilters !== undefined && remoteSortState !== undefined;
  const scrollContainerRef = useRef<HTMLDivElement | null>(null);
  const loadMoreSentinelRef = useRef<HTMLDivElement | null>(null);
  const draggedColumnId = useRef<string | null>(null);
  const changeColumnFilters = useCallback((
    update: Record<string, string> | ((current: Record<string, string>) => Record<string, string>),
  ) => {
    const next = typeof update === "function" ? update(columnFilters) : update;
    if (remoteColumnFilters === undefined) setLocalColumnFilters(next);
    else onRemoteColumnFiltersChange?.(next);
  }, [columnFilters, onRemoteColumnFiltersChange, remoteColumnFilters]);
  const changeSortState = useCallback((next: SnapshotGridSortState) => {
    if (remoteSortState === undefined) setLocalSortState(next);
    onRemoteSortChange?.(next);
  }, [onRemoteSortChange, remoteSortState]);
  useEffect(() => {
    const root = scrollContainerRef.current;
    const sentinel = loadMoreSentinelRef.current;
    if (!root || !sentinel || !onLoadMore || !hasMoreRows || loadingRows) return;
    const observer = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting)) onLoadMore();
    }, { root, rootMargin: "120px" });
    observer.observe(sentinel);
    return () => observer.disconnect();
  }, [hasMoreRows, items.length, loadingRows, onLoadMore]);

  const payloads = useMemo(() => items.map((item) => parsePayload(item.payloadJson)), [items]);
  const payloadByItem = useMemo(
    () => new Map(items.map((item, index) => [item, payloads[index]] as const)),
    [items, payloads],
  );
  const payloadColumns = useMemo(
    () => [...new Set([...serverPayloadColumns, ...payloads.flatMap((payload) => Object.keys(payload))])],
    [payloads, serverPayloadColumns],
  );
  const columnConfigs = useMemo<SnapshotGridColumn[]>(() => {
    const columns: SnapshotGridColumn[] = [
      { id: "record", label: "Record", value: (item) => item.recordIndex, display: (item) => String(item.recordIndex) },
      { id: "attempt", label: "Attempt", value: (item) => item.attempt, display: (item) => String(item.attempt) },
      {
        id: "captured",
        label: "Captured",
        value: (item) => Date.parse(item.capturedUtc),
        display: (item) => new Date(item.capturedUtc).toLocaleString(),
      },
      { id: "batch", label: "Batch", value: (item) => item.batchId, display: (item) => item.batchId },
      {
        id: "lineage",
        label: "Row lineage",
        value: (item) => item.lineageId,
        display: (item) => item.lineageId,
        selectsLineage: true,
      },
    ];
    if (showStageColumn) {
      columns.push({
        id: "stage",
        label: "Stage",
        value: (item) => friendlyStageName(item.stageName),
        display: (item) => friendlyStageName(item.stageName),
      });
    }
    if (jsonMode) {
      columns.push({
        id: "payload-json",
        label: "Payload JSON",
        value: (item) => item.payloadJson,
        display: (item) => item.payloadJson.replace(/\s+/g, " "),
        copyValue: (item) => item.payloadJson,
      });
    } else {
      for (const property of payloadColumns) {
        columns.push({
          id: `payload:${property}`,
          label: property,
          value: (item) => payloadByItem.get(item)?.[property],
          display: (item) => formatPayloadValue(payloadByItem.get(item)?.[property]),
          copyValue: (item) => formatPayloadValue(payloadByItem.get(item)?.[property]),
        });
      }
    }
    return columns;
  }, [jsonMode, payloadByItem, payloadColumns, showStageColumn]);
  const orderedColumnConfigs = useMemo(() => {
    const orderIndices = new Map(columnOrder.map((columnId, index) => [columnId, index]));
    return [...columnConfigs].sort(
      (left, right) =>
        (orderIndices.get(left.id) ?? Number.MAX_SAFE_INTEGER) -
        (orderIndices.get(right.id) ?? Number.MAX_SAFE_INTEGER),
    );
  }, [columnConfigs, columnOrder]);
  const visibleColumnConfigs = useMemo(
    () => orderedColumnConfigs.filter((column) => !hiddenColumnIds.has(column.id)),
    [hiddenColumnIds, orderedColumnConfigs],
  );
  const visibleColumnIds = useMemo(
    () => visibleColumnConfigs.map((column) => column.id),
    [visibleColumnConfigs],
  );
  const searchableColumns = useMemo(() => {
    const query = columnMenuSearch.trim().toLocaleLowerCase();
    return orderedColumnConfigs.filter((column) =>
      !query ||
      column.label.toLocaleLowerCase().includes(query) ||
      column.id.toLocaleLowerCase().includes(query),
    );
  }, [columnMenuSearch, orderedColumnConfigs]);
  const filterColumnConfigs = useMemo(() => [
    ...columnConfigs.map(({ id, label }) => ({ id, label })),
    ...(jsonMode
      ? payloadColumns.map((property) => ({ id: `payload:${property}`, label: property }))
      : []),
  ], [columnConfigs, jsonMode, payloadColumns]);
  const filterableColumns = useMemo(() => {
    const query = filterColumnSearch.trim().toLocaleLowerCase();
    return filterColumnConfigs.filter((column) =>
      !query ||
      column.label.toLocaleLowerCase().includes(query) ||
      column.id.toLocaleLowerCase().includes(query),
    );
  }, [filterColumnConfigs, filterColumnSearch]);
  const activeFilterCount = Object.values(columnFilters).filter((value) => value.trim()).length;
  const filteredItems = useMemo(() => {
    if (remoteMode) return items;
    const activeFilters = columnConfigs.filter((column) => columnFilters[column.id]?.trim());
    if (!activeFilters.length) return items;
    return items.filter((item) =>
      activeFilters.every((column) =>
        column.display(item).toLocaleLowerCase().includes(columnFilters[column.id].trim().toLocaleLowerCase()),
      ),
    );
  }, [columnConfigs, columnFilters, items, remoteMode]);
  const measuredWidths = useMemo(() => {
    const canvas = document.createElement("canvas");
    const context = canvas.getContext("2d");
    if (context) context.font = "12px Segoe UI, sans-serif";
    const widths: Record<string, number> = {};
    for (const column of visibleColumnConfigs) {
      let contentWidth = context?.measureText(column.label).width ?? column.label.length * 7;
      for (const item of items) {
        const valueWidth = context?.measureText(column.display(item)).width ?? column.display(item).length * 7;
        contentWidth = Math.max(contentWidth, valueWidth);
      }
      widths[column.id] = Math.min(
        MAX_AUTO_SNAPSHOT_COLUMN_WIDTH,
        Math.max(MIN_SNAPSHOT_COLUMN_WIDTH, Math.ceil(contentWidth + 40)),
      );
    }
    return widths;
  }, [items, visibleColumnConfigs]);
  const columnSizingOptions = useMemo<TableColumnSizingOptions>(() => {
    const options: TableColumnSizingOptions = {};
    for (const column of visibleColumnConfigs) {
      const width = columnWidths[column.id] ?? measuredWidths[column.id] ?? MIN_SNAPSHOT_COLUMN_WIDTH;
      options[column.id] = {
        defaultWidth: width,
        idealWidth: width,
        minWidth: MIN_SNAPSHOT_COLUMN_WIDTH,
        padding: 24,
      };
    }
    return options;
  }, [columnWidths, measuredWidths, visibleColumnConfigs]);
  const columns = useMemo<TableColumnDefinition<DebugSnapshot>[]>(() =>
    visibleColumnConfigs.map((column) =>
      createTableColumn<DebugSnapshot>({
        columnId: column.id,
        compare: (left, right) => compareGridValues(column.value(left), column.value(right)),
        renderHeaderCell: () => (
          <span className="grid-header-label" title="Drag to reorder; press Alt+Left or Alt+Right to move">
            <GripVertical size={13} aria-hidden="true" />
            <span>{column.label}</span>
          </span>
        ),
        renderCell: (item) => {
          const displayValue = column.display(item);
          if (column.selectsLineage) {
            return (
              <button
                className="text-button lineage-link"
                type="button"
                aria-label={`Show lineage for ${item.lineageId}`}
                onClick={() => onSelectRow(item)}
              >{displayValue}</button>
            );
          }
          if (column.copyValue) {
            return (
              <CopyableValue
                label={column.label}
                displayValue={displayValue}
                copyValue={column.copyValue(item)}
              />
            );
          }
          return <span className="grid-cell-text" title={displayValue}>{displayValue}</span>;
        },
      }),
    ),
    [onSelectRow, visibleColumnConfigs],
  );
  useEffect(() => {
    if (visibleColumnConfigs.length > 0 &&
        !visibleColumnConfigs.some((column) => column.id === sortState.sortColumn)) {
      changeSortState({ sortColumn: visibleColumnConfigs[0].id, sortDirection: "ascending" });
    }
  }, [changeSortState, sortState.sortColumn, visibleColumnConfigs]);

  const moveColumn = useCallback((sourceId: string, targetId: string) => {
    if (sourceId === targetId) return;
    const nextOrder = orderedColumnConfigs.map((column) => column.id);
    const sourceIndex = nextOrder.indexOf(sourceId);
    const targetIndex = nextOrder.indexOf(targetId);
    if (sourceIndex < 0 || targetIndex < 0) return;
    nextOrder.splice(sourceIndex, 1);
    nextOrder.splice(targetIndex, 0, sourceId);
    setColumnOrder(nextOrder);
  }, [orderedColumnConfigs]);
  const moveColumnByOffset = useCallback((columnId: string, offset: number) => {
    const visibleIds = visibleColumnConfigs.map((column) => column.id);
    const currentIndex = visibleIds.indexOf(columnId);
    const targetIndex = Math.max(0, Math.min(visibleIds.length - 1, currentIndex + offset));
    if (currentIndex >= 0 && targetIndex !== currentIndex) {
      moveColumn(columnId, visibleIds[targetIndex]);
    }
  }, [moveColumn, visibleColumnConfigs]);

  return (
    <div className="snapshot-table-container">
      <div className="snapshot-table-toolbar">
        <span>{number.format(items.length)} of {number.format(matchingRowCount ?? items.length)} matching row snapshots</span>
        <div className="snapshot-grid-actions">
          {rowSearch !== undefined && onRowSearchChange && (
            <input
              className="search-input snapshot-search-input"
              type="search"
              aria-label={`Search ${friendlyStageName(rowSearchStage ?? items[0]?.stageName ?? "")} rows`}
              placeholder={`Search ${friendlyStageName(rowSearchStage ?? items[0]?.stageName ?? "")} rows`}
              value={rowSearch}
              onChange={(event) => onRowSearchChange(event.currentTarget.value)}
            />
          )}
          <Popover open={filtersOpen} onOpenChange={(_, data) => setFiltersOpen(data.open)}>
            <PopoverTrigger disableButtonEnhancement>
              <Button
                appearance="secondary"
                size="small"
                icon={<ListFilter size={14} />}
                aria-label={remoteMode ? "Filter all captured rows by column" : "Filter loaded rows by column"}
              >Filters{activeFilterCount > 0 ? ` (${activeFilterCount})` : ""}</Button>
            </PopoverTrigger>
            <PopoverSurface className="grid-filter-popover">
              <div className="grid-filter-heading">
                <Text variant="mediumPlus">{remoteMode ? "Filter all captured rows" : "Filter loaded rows"}</Text>
                <Text variant="small">
                  {remoteMode
                    ? "Each column filter is applied across all matching snapshots on the server."
                    : "Column filters apply to the loaded lineage rows."}
                </Text>
              </div>
              <Input
                size="small"
                type="search"
                contentBefore={<Search size={14} />}
                aria-label="Search filter columns"
                placeholder="Search columns"
                value={filterColumnSearch}
                onChange={(_, data) => setFilterColumnSearch(data.value)}
              />
              <div className="grid-filter-list">
                {filterableColumns.map((column) => (
                  <label className="grid-filter-field" key={column.id}>
                    <span>{column.label}</span>
                    <Input
                      size="small"
                      type="search"
                      aria-label={`Filter ${column.label}`}
                      placeholder={`Filter ${column.label}`}
                      value={columnFilters[column.id] ?? ""}
                      onChange={(_, data) => changeColumnFilters((current) => ({
                        ...current,
                        [column.id]: data.value,
                      }))}
                    />
                  </label>
                ))}
                {filterableColumns.length === 0 && <span className="inline-empty">No columns match that search.</span>}
              </div>
              <div className="grid-filter-footer">
                <Text variant="small">{activeFilterCount} active filters</Text>
                <Button
                  appearance="subtle"
                  size="small"
                  onClick={() => changeColumnFilters({})}
                  disabled={activeFilterCount === 0}
                >Clear filters</Button>
              </div>
            </PopoverSurface>
          </Popover>
          <Menu
            checkedValues={{ columns: visibleColumnIds }}
            onCheckedValueChange={(_, data) => {
              if (data.name !== "columns") return;
              const menuColumnIds = new Set(searchableColumns.map((column) => column.id));
              setHiddenColumnIds((current) => {
                const next = new Set(current);
                for (const columnId of menuColumnIds) {
                  if (data.checkedItems.includes(columnId)) next.delete(columnId);
                  else next.add(columnId);
                }
                const visibleCount = columnConfigs.filter((column) => !next.has(column.id)).length;
                return visibleCount === 0 ? current : next;
              });
            }}
          >
            <MenuTrigger disableButtonEnhancement>
              <Button
                appearance="secondary"
                size="small"
                icon={<Columns3 size={14} />}
                aria-label="Choose visible columns"
              >Columns</Button>
            </MenuTrigger>
            <MenuPopover>
              <div className="grid-column-menu">
                <Input
                  size="small"
                  type="search"
                  contentBefore={<Search size={14} />}
                  aria-label="Search columns"
                  placeholder="Search columns"
                  value={columnMenuSearch}
                  onChange={(_, data) => setColumnMenuSearch(data.value)}
                />
                <MenuList>
                  {searchableColumns.map((column) => (
                    <MenuItemCheckbox
                      key={column.id}
                      name="columns"
                      value={column.id}
                      persistOnClick
                      disabled={visibleColumnIds.length === 1 && visibleColumnIds[0] === column.id}
                    >{column.label}</MenuItemCheckbox>
                  ))}
                  {searchableColumns.length === 0 && <Text className="inline-empty">No columns match that search.</Text>}
                </MenuList>
                <Button
                  appearance="subtle"
                  size="small"
                  onClick={() => setHiddenColumnIds(new Set())}
                >Show all columns</Button>
              </div>
            </MenuPopover>
          </Menu>
          <Button
            appearance="subtle"
            size="small"
            aria-pressed={jsonMode}
            onClick={() => setJsonMode((current) => !current)}
          >{jsonMode ? "Show payload columns" : "Show payload as JSON"}</Button>
        </div>
      </div>
      <div className="snapshot-grid-scroll" ref={scrollContainerRef} role="region" aria-label="Captured row snapshots">
        <DataGrid
          className="snapshot-data-grid"
          items={filteredItems}
          columns={columns}
          getRowId={snapshotKey}
          sortable
          sortState={sortState}
          onSortChange={(_, nextSortState) => changeSortState(nextSortState)}
          resizableColumns
          resizableColumnsOptions={{ autoFitColumns: false }}
          columnSizingOptions={columnSizingOptions}
          onColumnResize={(_, data) => setColumnWidths((current) => ({
            ...current,
            [String(data.columnId)]: Math.min(data.width, MAX_AUTO_SNAPSHOT_COLUMN_WIDTH),
          }))}
        >
          <DataGridHeader>
            <DataGridRow>
              {(column) => (
                <DataGridHeaderCell
                  key={column.columnId}
                  className="snapshot-grid-header-cell"
                  draggable
                  onDragStart={(event: ReactDragEvent<HTMLTableCellElement>) => {
                    draggedColumnId.current = String(column.columnId);
                    event.dataTransfer.effectAllowed = "move";
                    event.dataTransfer.setData("text/plain", String(column.columnId));
                  }}
                  onDragOver={(event: ReactDragEvent<HTMLTableCellElement>) => event.preventDefault()}
                  onDrop={(event: ReactDragEvent<HTMLTableCellElement>) => {
                    event.preventDefault();
                    const sourceId = draggedColumnId.current ?? event.dataTransfer.getData("text/plain");
                    if (sourceId) moveColumn(sourceId, String(column.columnId));
                    draggedColumnId.current = null;
                  }}
                  onDragEnd={() => { draggedColumnId.current = null; }}
                  onKeyDown={(event: ReactKeyboardEvent<HTMLTableCellElement>) => {
                    if (!event.altKey || (event.key !== "ArrowLeft" && event.key !== "ArrowRight")) return;
                    event.preventDefault();
                    moveColumnByOffset(String(column.columnId), event.key === "ArrowLeft" ? -1 : 1);
                  }}
                  title="Drag to reorder; press Alt+Left or Alt+Right to move"
                >{column.renderHeaderCell()}</DataGridHeaderCell>
              )}
            </DataGridRow>
          </DataGridHeader>
          <DataGridBody<DebugSnapshot>>
            {({ item, rowId }) => (
              <DataGridRow
                key={rowId}
                className={`snapshot-grid-row${isSelectedSnapshot(item, selectedSnapshot) ? " selected" : ""}`}
              >
                {(column) => (
                  <DataGridCell
                    key={column.columnId}
                    className="snapshot-grid-cell"
                    focusMode={
                      column.columnId === "lineage" ||
                      column.columnId === "payload-json" ||
                      String(column.columnId).startsWith("payload:")
                        ? "group"
                        : "none"
                    }
                  >{column.renderCell(item)}</DataGridCell>
                )}
              </DataGridRow>
            )}
          </DataGridBody>
        </DataGrid>
        {filteredItems.length === 0 && <div className="inline-empty grid-empty">No rows match the active column filters.</div>}
        {onLoadMore && hasMoreRows && <div className="snapshot-grid-sentinel" ref={loadMoreSentinelRef} aria-hidden="true" />}
        {onLoadMore && loadingRows && <div className="snapshot-grid-loading" role="status">Loading more rows…</div>}
      </div>
    </div>
  );
}

function CopyableValue({
  label,
  displayValue,
  copyValue,
}: {
  label: string;
  displayValue: string;
  copyValue: string;
}) {
  const [copyStatus, setCopyStatus] = useState<{ message: string; isError: boolean } | null>(null);
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(copyValue);
      setCopyStatus({ message: "Copied", isError: false });
    } catch (reason) {
      const message = reason instanceof Error ? reason.message : String(reason);
      setCopyStatus({ message: `Copy failed: ${message}`, isError: true });
    }
  };

  return (
    <span className="grid-copy-cell">
      <span className="grid-cell-text" title={displayValue}>{displayValue}</span>
      <Button
        className="grid-copy-button"
        appearance="subtle"
        size="small"
        icon={copyStatus?.isError ? <Copy size={13} /> : copyStatus ? <Check size={13} /> : <Copy size={13} />}
        aria-label={`Copy ${label} value`}
        title={copyStatus?.message ?? `Copy ${label} value`}
        onClick={(event) => {
          event.stopPropagation();
          void copy();
        }}
      />
      {copyStatus && (
        <span
          className={`grid-copy-feedback${copyStatus.isError ? " error" : ""}`}
          role={copyStatus.isError ? "alert" : "status"}
        >{copyStatus.message}</span>
      )}
    </span>
  );
}

function compareGridValues(left: unknown, right: unknown): number {
  if (typeof left === "number" && typeof right === "number") {
    return left - right;
  }
  if (typeof left === "boolean" && typeof right === "boolean") {
    return Number(left) - Number(right);
  }
  return formatPayloadValue(left).localeCompare(formatPayloadValue(right), undefined, {
    numeric: true,
    sensitivity: "base",
  });
}

function parsePayload(payloadJson: string): Record<string, unknown> {
  try {
    const value: unknown = JSON.parse(payloadJson);
    if (value !== null && typeof value === "object" && !Array.isArray(value)) {
      return value as Record<string, unknown>;
    }
    return { Value: value };
  } catch {
    return { Value: payloadJson };
  }
}

function formatPayloadValue(value: unknown): string {
  if (value === null || value === undefined) return "";
  return typeof value === "object" ? JSON.stringify(value) : String(value);
}

function isSelectedSnapshot(candidate: DebugSnapshot, selected: DebugSnapshot | null): boolean {
  return selected?.batchId === candidate.batchId &&
    selected.stageName === candidate.stageName &&
    selected.recordIndex === candidate.recordIndex &&
    selected.attempt === candidate.attempt &&
    selected.capturedUtc === candidate.capturedUtc;
}

function snapshotKey(snapshot: DebugSnapshot): string {
  return `${snapshot.batchId}:${snapshot.stageName}:${snapshot.recordIndex}:${snapshot.attempt}:${snapshot.capturedUtc}`;
}

function friendlyStageName(stageName: string): string {
  if (stageName === "SourceInput") return "Source input";
  if (stageName === "BatchInput") return "Step input";
  if (stageName.startsWith("DependencyOutput:")) {
    return `Output to ${stageName.slice("DependencyOutput:".length)}`;
  }
  return stageName;
}

function traceJobCount(snapshots: DebugSnapshot[]): number {
  return new Set(snapshots.map((snapshot) => snapshot.jobName)).size;
}

function prettyJson(source: string): string {
  try {
    return JSON.stringify(JSON.parse(source), null, 2);
  } catch {
    return source;
  }
}
