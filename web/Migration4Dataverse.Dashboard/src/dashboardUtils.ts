import { Activity, AlertTriangle, CheckCircle2, CircleDashed, Clock3, Download, Layers3, Play, Shuffle, Upload, XCircle } from "lucide-react";
import { BlockingHotspot, ConfigurationConnection, ConfigurationJob, ConnectionInstanceSnapshot, ConnectionReference, DashboardJob, DebugSnapshot, JobStatus, OverallThroughput, ProjectedInputRows, ProjectedStepRows, RunStatus, RunSummary, SchedulerSample, SnapshotGridSortState, SpecialistInfoLine, StepProgress } from "./types";
export const defaultSnapshotSort: SnapshotGridSortState = {
  sortColumn: "record",
  sortDirection: "descending",
};

export const emptySnapshotFilters: Record<string, string> = {};

export const statusInfo: Record<JobStatus | RunStatus, { label: string; color: string; icon: typeof Activity }> = {
  Running: { label: "Running", color: "blue", icon: Play },
  Completed: { label: "Completed", color: "green", icon: CheckCircle2 },
  Pending: { label: "Pending", color: "gray", icon: CircleDashed },
  Queued: { label: "Queued", color: "yellow", icon: Clock3 },
  Failed: { label: "Failed", color: "red", icon: XCircle },
  Blocked: { label: "Blocked", color: "muted", icon: AlertTriangle },
  Cancelled: { label: "Cancelled", color: "muted", icon: XCircle },
  Configured: { label: "Configured", color: "muted", icon: CircleDashed },
};

export const jobTypeInfo: Record<string, { label: string; icon: typeof Activity }> = {
  source: { label: "Source", icon: Upload },
  transform: { label: "Transform", icon: Shuffle },
  target: { label: "Target", icon: Download },
};

export const jobTypeIcon = (type: string) => jobTypeInfo[type.toLowerCase()]?.icon ?? Layers3;

export const number = new Intl.NumberFormat();

export const rateNumber = new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 });

export const formatRowsPerMinute = (rowsPerSecond: number) => rateNumber.format(rowsPerSecond * 60);

export function isRunStalled(run: Pick<RunSummary, "status" | "schedulerLeaseExpiresUtc">): boolean {
  if (run.status !== "Running" || !run.schedulerLeaseExpiresUtc) return false;
  const leaseExpiresAt = Date.parse(run.schedulerLeaseExpiresUtc);
  return Number.isFinite(leaseExpiresAt) && leaseExpiresAt <= Date.now();
}

export function runStatusLabel(run: RunSummary): string {
  return isRunStalled(run) ? "Stalled" : run.status;
}

export function formatElapsed(from: string, to = new Date().toISOString()): string {
  const totalSeconds = Math.max(0, Math.floor((Date.parse(to) - Date.parse(from)) / 1000));
  return formatDuration(totalSeconds);
}

export function formatMilliseconds(milliseconds: number): string {
  return formatDuration(Math.floor(Math.max(0, milliseconds) / 1000));
}

export function formatDuration(totalSeconds: number): string {
  if (totalSeconds < 60) return `${totalSeconds}s`;
  if (totalSeconds < 3600) return `${Math.floor(totalSeconds / 60)}m ${totalSeconds % 60}s`;
  return `${Math.floor(totalSeconds / 3600)}h ${Math.floor((totalSeconds % 3600) / 60)}m`;
}

export function getTerminalTargetNames(run: RunSummary): Set<string> {
  const configuredJobs = run.configuration?.jobs ?? [];
  const downstreamNames = new Set(configuredJobs.flatMap((job) => job.dependsOn));
  return new Set(
    configuredJobs
      .filter((job) => Boolean(job.targetConnectionId) && !downstreamNames.has(job.name))
      .map((job) => job.name),
  );
}

export function buildOverallThroughput(jobs: DashboardJob[], run: RunSummary): OverallThroughput | null {
  const start = Date.parse(run.createdUtc);
  const end = run.status === "Running" && !isRunStalled(run) ? Date.now() : Date.parse(run.updatedUtc);
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

export function readSourceDependencies(properties: Record<string, unknown> | undefined): {
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

export function statusFor(job: DashboardJob, jobs: DashboardJob[], configurationOnly = false): JobStatus {
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

export function activeDownstreamSteps(job: DashboardJob, jobs: DashboardJob[]): DashboardJob[] {
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

export function isBlockedByDownstream(job: DashboardJob, jobs: DashboardJob[]): boolean {
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

export function furthestFailedDependency(job: DashboardJob, jobs: DashboardJob[]): DashboardJob | null {
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

export function furthestActiveDownstream(job: DashboardJob, jobs: DashboardJob[]): DashboardJob | null {
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

export function blockingTaskFor(job: DashboardJob, jobs: DashboardJob[]): DashboardJob | null {
  return furthestFailedDependency(job, jobs) ?? furthestActiveDownstream(job, jobs);
}

export function getUpstreamBlockingCounts(jobs: DashboardJob[]): Record<string, number> {
  const counts: Record<string, number> = {};
  for (const upstream of jobs) {
    if (!isBlockedByDownstream(upstream, jobs)) continue;
    const blocker = furthestActiveDownstream(upstream, jobs);
    if (blocker) counts[blocker.name] = (counts[blocker.name] ?? 0) + 1;
  }
  return counts;
}

export function createSchedulerSample(run: RunSummary, sampledAt = Date.now()): SchedulerSample {
  return {
    runId: run.runId,
    sampledAt,
    outstandingBatches: run.pendingBatches + run.inFlightBatches,
    inFlightBatches: run.inFlightBatches,
    outstandingByStep: Object.fromEntries(run.jobs.map((job) => [
      job.name,
      job.pendingBatches + job.inFlightBatches,
    ])),
    blockedAncestorsByStep: getUpstreamBlockingCounts(run.jobs),
  };
}

export function rankBlockingHotspots(
  jobs: DashboardJob[],
  samples: SchedulerSample[],
  runId: string,
): BlockingHotspot[] {
  const now = Date.now();
  const windowStart = now - 60_000;
  const recentSamples = samples
    .filter((sample) => sample.runId === runId && sample.sampledAt >= windowStart)
    .toSorted((left, right) => left.sampledAt - right.sampledAt);
  const ancestorSecondsByStep = new Map<string, number>();
  recentSamples.forEach((sample, index) => {
    const nextSampleAt = recentSamples[index + 1]?.sampledAt ?? now;
    const durationSeconds = Math.min(5, Math.max(0, nextSampleAt - sample.sampledAt) / 1000);
    for (const [stepName, blockedAncestors] of Object.entries(sample.blockedAncestorsByStep)) {
      ancestorSecondsByStep.set(
        stepName,
        (ancestorSecondsByStep.get(stepName) ?? 0) + blockedAncestors * durationSeconds,
      );
    }
  });

  const currentCounts = getUpstreamBlockingCounts(jobs);
  const stepNames = new Set([
    ...ancestorSecondsByStep.keys(),
    ...Object.keys(currentCounts),
  ]);
  const jobsByName = new Map(jobs.map((job) => [job.name, job]));
  return [...stepNames]
    .map((stepName) => ({
      job: jobsByName.get(stepName),
      blockedAncestors: currentCounts[stepName] ?? 0,
      ancestorSeconds: ancestorSecondsByStep.get(stepName) ?? 0,
    }))
    .filter((item): item is BlockingHotspot & { job: DashboardJob } =>
      item.job !== undefined &&
      (item.blockedAncestors > 0 || item.ancestorSeconds > 0),
    )
    .toSorted((left, right) =>
      right.ancestorSeconds - left.ancestorSeconds ||
      right.blockedAncestors - left.blockedAncestors ||
      left.job.name.localeCompare(right.job.name),
    );
}

export function activityHintFor(job: DashboardJob, jobs: DashboardJob[], configurationOnly = false): string | null {
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

export function projectedInputRows(job: DashboardJob, jobs: DashboardJob[]): ProjectedInputRows | null {
  const jobsByName = new Map(jobs.map((candidate) => [candidate.name, candidate]));
  return projectedStepRows(job, jobsByName, new Map(), new Set()).inputRows;
}

export function projectedStepRows(
  job: DashboardJob,
  jobsByName: Map<string, DashboardJob>,
  cache: Map<string, ProjectedStepRows>,
  visiting: Set<string>,
): ProjectedStepRows {
  const cached = cache.get(job.name);
  if (cached) return cached;
  if (visiting.has(job.name)) return { inputRows: null, outputRows: null };
  visiting.add(job.name);

  const knownInputRows = job.inputRowsProcessed + job.inputRowsPending + job.inputRowsInFlight;
  let inputRows: ProjectedInputRows | null = job.inputFinalized
    ? job.expectedRows === null
      ? null
      : { expectedRows: job.expectedRows, isEstimate: false }
    : job.hasSource && job.expectedRows !== null
      ? { expectedRows: job.expectedRows, isEstimate: false }
      : null;

  if (!inputRows && !job.inputFinalized && !job.hasSource) {
    const parentNames = job.dependsOn
      .filter((dependency) => !job.completionDependencies.includes(dependency));
    const parents = parentNames
      .map((name) => jobsByName.get(name))
      .filter((parent): parent is DashboardJob => parent !== undefined);
    if (parents.length > 0 && parents.length === parentNames.length) {
      const parentRows = parents.map((parent) =>
        projectedStepRows(parent, jobsByName, cache, visiting).outputRows);
      if (parentRows.every((rows) => rows !== null)) {
        const parentRowsExpected = parentRows.reduce(
          (total, rows) => total + (rows?.expectedRows ?? 0),
          0,
        );
        const parentRowsProduced = parents.reduce(
          (total, parent) => total + parent.succeededRows + parent.failedRows,
          0,
        );
        const observedInputRatio = parentRowsProduced > 0
          ? knownInputRows / parentRowsProduced
          : 1;
        const expectedRows = parents.length === 1
          ? parentRowsExpected
          : knownInputRows +
          Math.max(0, parentRowsExpected - parentRowsProduced) * observedInputRatio;
        inputRows = {
          expectedRows: Math.max(knownInputRows, expectedRows),
          isEstimate: true,
        };
      }
    }
  }

  const outputRowsProduced = job.succeededRows + job.failedRows;
  let outputRows: ProjectedInputRows | null = null;
  if (inputRows) {
    const observedOutputRatio = job.inputRowsProcessed > 0
      ? outputRowsProduced / job.inputRowsProcessed
      : 1;
    outputRows = {
      expectedRows: Math.max(
        outputRowsProduced,
        outputRowsProduced +
        Math.max(0, inputRows.expectedRows - job.inputRowsProcessed) * observedOutputRatio,
      ),
      isEstimate: !job.isComplete,
    };
  } else if (job.isComplete) {
    outputRows = { expectedRows: outputRowsProduced, isEstimate: false };
  }

  visiting.delete(job.name);
  const projection = { inputRows, outputRows };
  cache.set(job.name, projection);
  return projection;
}

export function stepProgress(job: DashboardJob, jobs: DashboardJob[]): StepProgress | null {
  const projectedRows = projectedInputRows(job, jobs);
  if (projectedRows && projectedRows.expectedRows > 0) {
    const estimateLabel = projectedRows.isEstimate ? "estimated " : "";
    return {
      value: Math.min(job.inputRowsProcessed, projectedRows.expectedRows),
      max: projectedRows.expectedRows,
      kind: "rows",
      ariaLabel: `${job.name} ${estimateLabel}input row progress`,
      title: projectedRows.isEstimate
        ? "Estimated from upstream row projections and observed input ratios."
        : "Input rows processed."
    };
  }

  const totalBatches = job.succeededBatches + job.batchesWithErrors +
    job.pendingBatches + job.inFlightBatches;
  if (totalBatches <= 0) return null;
  const completedBatches = job.succeededBatches + job.batchesWithErrors;
  return {
    value: Math.min(completedBatches, totalBatches),
    max: totalBatches,
    kind: "batches",
    ariaLabel: `${job.name} batch progress among currently materialized batches`,
    title: "Progress among currently materialized batches; more batches may be added."
  };
}

export function inputRowsLabel(job: DashboardJob, jobs: DashboardJob[]): string {
  const projectedRows = projectedInputRows(job, jobs);
  if (!projectedRows) return number.format(job.inputRowsProcessed);
  const estimateMarker = projectedRows.isEstimate ? "~" : "";
  return `${number.format(job.inputRowsProcessed)} / ${estimateMarker}${number.format(projectedRows.expectedRows)}`;
}

export function asRecord(value: unknown): Record<string, unknown> | undefined {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return undefined;
  return value as Record<string, unknown>;
}

export function getStringProperty(value: unknown, ...propertyNames: string[]): string | undefined {
  const property = getPropertyValue(value, ...propertyNames);
  return typeof property === "string" && property.trim() ? property.trim() : undefined;
}

export function modelDrivenRecordUrl(
  snapshot: DebugSnapshot,
  configurationJobs: ConfigurationJob[],
  connectionInstances: ConnectionInstanceSnapshot[],
): string | null {
  const job = configurationJobs.find((candidate) => candidate.name === snapshot.jobName);
  const target = asRecord(getPropertyValue(job?.properties, "Target"));
  const source = asRecord(getPropertyValue(job?.properties, "Source"));
  const targetIsDataverse = (job?.targetConnectionType ?? getStringProperty(target, "Type"))
    ?.toLocaleLowerCase().includes("dataverse") ?? false;
  const sourceIsDataverse = (job?.sourceConnectionType ?? getStringProperty(source, "Type"))
    ?.toLocaleLowerCase().includes("dataverse") ?? false;
  const endpoint = targetIsDataverse ? target : sourceIsDataverse ? source : null;
  const connectionId = targetIsDataverse ? job?.targetConnectionId : job?.sourceConnectionId;
  const tableName = getStringProperty(endpoint, "TableName", "Table", "EntityName", "LogicalName");
  if (!tableName || !/^[A-Za-z][A-Za-z0-9_]*$/.test(tableName) ||
    !connectionId) {
    return null;
  }

  const primaryId = getStringProperty(parsePayload(snapshot.payloadJson), `${tableName}id`);
  if (!primaryId || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(primaryId)) {
    return null;
  }

  const connection = [...connectionInstances]
    .reverse()
    .flatMap((capture) => capture.connections)
    .find((candidate) => candidate.id === connectionId);
  const organizationUrl = getStringProperty(
    connection?.properties,
    "ConnectedOrgUriActual",
    "ConnectedOrgUri",
    "OrganizationUri",
    "OrganizationUrl",
  );
  if (!organizationUrl) return null;

  let baseUrl: URL;
  try {
    baseUrl = new URL(organizationUrl);
  } catch {
    return null;
  }
  if (!["https:", "http:"].includes(baseUrl.protocol) || baseUrl.username || baseUrl.password) {
    return null;
  }

  const recordUrl = new URL("/main.aspx", baseUrl.origin);
  recordUrl.searchParams.set("pagetype", "entityrecord");
  recordUrl.searchParams.set("etn", tableName);
  recordUrl.searchParams.set("id", primaryId);
  return recordUrl.toString();
}

export function getPropertyValue(value: unknown, ...propertyNames: string[]): unknown {
  const record = asRecord(value);
  if (!record) return undefined;
  const propertyNameSet = new Set(propertyNames.map((name) => name.toLocaleLowerCase()));
  const entry = Object.entries(record).find(([name]) => propertyNameSet.has(name.toLocaleLowerCase()));
  return entry?.[1];
}

export function getNumberProperty(value: unknown, ...propertyNames: string[]): number | undefined {
  const property = getPropertyValue(value, ...propertyNames);
  if (typeof property === "number" && Number.isFinite(property)) return property;
  if (typeof property === "string" && property.trim()) {
    const parsed = Number(property);
    if (Number.isFinite(parsed)) return parsed;
  }
  return undefined;
}

export function specialistInfoFor(
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
      const batchHasLegacyTuning = typeof batchSetting === "string" &&
        batchSetting.toLocaleLowerCase() === "auto";
      const concurrencyHasLegacyTuning = typeof concurrencySetting === "string" &&
        concurrencySetting.toLocaleLowerCase() === "auto";
      const currentBatchSize = getNumberProperty(runtime, "BatchSize");
      const batchState = getStringProperty(runtime, "BatchSizeState");
      const currentDop = getNumberProperty(runtime, "MaxConcurrency");
      const dopState = getStringProperty(runtime, "ConcurrencyState");
      const connectionId = getStringProperty(runtime, "ConnectionId");
      const connectionBudget = getNumberProperty(runtime, "ConnectionConcurrencyBudget");
      const activeConnectionCalls = getNumberProperty(runtime, "ActiveConnectionCalls");
      const retryCount = getNumberProperty(runtime, "RetryCount");
      const throttleCount = getNumberProperty(runtime, "ThrottleCount");
      const retryTelemetryAvailable = getPropertyValue(runtime, "RetryTelemetryAvailable");
      const settingDescription = (
        label: string,
        setting: unknown,
        current: number | undefined,
        state: string | undefined,
        hasLegacyTuning: boolean,
      ) => {
        if (hasLegacyTuning) {
          const effective = current === undefined ? "legacy runtime state unavailable" : number.format(current);
          const status = state ? ` · ${state}` : "";
          return {
            value: `Legacy tuning · ${effective}${status}`,
            title: `${label}: this saved migration used automatic tuning in an older version; current ${effective}${status}`,
          };
        }

        const configured = setting === undefined ? undefined : String(setting);
        const effective = current === undefined ? configured : number.format(current);
        const status = state ? ` · ${state}` : "";
        return {
          value: `${effective ?? "Default"} fixed`,
          title: `${label}: configured ${configured ?? "default"}; effective ${effective ?? "unknown"}${status}`,
        };
      };
      const batchInfo = settingDescription(
        "Batch size",
        batchSetting,
        currentBatchSize,
        batchState,
        batchHasLegacyTuning,
      );
      const dopInfo = settingDescription(
        "DOP",
        concurrencySetting,
        currentDop,
        dopState,
        concurrencyHasLegacyTuning,
      );
      add("Batch size", batchInfo.value, batchInfo.title);
      add("DOP", dopInfo.value, dopInfo.title);
      add("Connection", connectionId);
      if (connectionBudget !== undefined || activeConnectionCalls !== undefined) {
        add(
          "Connection activity",
          `${number.format(activeConnectionCalls ?? 0)} active / ${number.format(connectionBudget ?? 0)} budget`,
        );
      }
      if (retryCount !== undefined || throttleCount !== undefined) {
        add(
          "Retries / throttles",
          `${number.format(retryCount ?? 0)} / ${number.format(throttleCount ?? 0)}`,
        );
      }
      if (typeof retryTelemetryAvailable === "boolean") {
        add("Retry telemetry", retryTelemetryAvailable ? "available" : "unavailable");
      }
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

export function endpointConnection(
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

export function lineageStages(snapshots: DebugSnapshot[]): [string, DebugSnapshot[]][] {
  const stages = new Map<string, DebugSnapshot[]>();
  snapshots.forEach((snapshot) => {
    const rows = stages.get(snapshot.stageName) ?? [];
    rows.push(snapshot);
    stages.set(snapshot.stageName, rows);
  });
  return [...stages.entries()];
}

export function compareGridValues(left: unknown, right: unknown): number {
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

export function parsePayload(payloadJson: string): Record<string, unknown> {
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

export interface BinaryValueMetadata {
  fileName: string | null;
  contentType: string | null;
  representation: string;
  length: number | null;
  sha256: string | null;
}

export function displayBinaryFileName(fileName: string | null): string {
  const safeName = fileName
    ?.replaceAll("\\", "/")
    .split("/")
    .at(-1)
    ?.replace(/[\u0000-\u001f\u007f]/g, "");
  return safeName || "Binary value";
}

export function getBinaryValueMetadata(value: unknown): BinaryValueMetadata | null {
  const wrapper = asRecord(value);
  if (!wrapper || Object.keys(wrapper).length !== 1) return null;
  const descriptor = asRecord(wrapper.__migration4dataverse_binary_v1);
  if (!descriptor || typeof descriptor.Representation !== "string") {
    return null;
  }
  const length = typeof descriptor.Length === "number" &&
    Number.isFinite(descriptor.Length) &&
    descriptor.Length >= 0
    ? descriptor.Length
    : null;
  return {
    fileName: typeof descriptor.FileName === "string" ? descriptor.FileName : null,
    contentType: typeof descriptor.ContentType === "string" ? descriptor.ContentType : null,
    representation: descriptor.Representation,
    length,
    sha256: typeof descriptor.Sha256 === "string" ? descriptor.Sha256 : null,
  };
}

function safePayloadValue(value: unknown): unknown {
  const binaryValue = getBinaryValueMetadata(value);
  if (binaryValue) {
    return {
      type: "binary",
      fileName: displayBinaryFileName(binaryValue.fileName),
      contentType: binaryValue.contentType,
      representation: binaryValue.representation,
      length: binaryValue.length,
      sha256: binaryValue.sha256,
    };
  }
  if (Array.isArray(value)) return value.map(safePayloadValue);
  const record = asRecord(value);
  if (record) {
    return Object.fromEntries(
      Object.entries(record).map(([key, entry]) => [
        key,
        key === "__migration4dataverse_binary_v1"
          ? "[binary metadata omitted]"
          : safePayloadValue(entry),
      ]),
    );
  }
  return value;
}

export function formatByteCount(byteCount: number | null): string {
  if (byteCount === null || !Number.isFinite(byteCount) || byteCount < 0) return "Size unknown";
  if (byteCount < 1024) return `${number.format(byteCount)} B`;
  const units = ["KB", "MB", "GB", "TB"];
  let value = byteCount / 1024;
  let unitIndex = 0;
  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024;
    unitIndex++;
  }
  return `${new Intl.NumberFormat(undefined, { maximumFractionDigits: 1 }).format(value)} ${units[unitIndex]}`;
}

export function formatPayloadValue(value: unknown): string {
  if (value === null || value === undefined) return "";
  const binaryValue = getBinaryValueMetadata(value);
  if (binaryValue) {
    const fileName = displayBinaryFileName(binaryValue.fileName);
    const contentType = binaryValue.contentType ?? "Unknown type";
    return `${fileName} · ${contentType} · ${formatByteCount(binaryValue.length)}`;
  }
  return typeof value === "object"
    ? JSON.stringify(safePayloadValue(value)) ?? ""
    : String(value);
}

export function isSelectedSnapshot(candidate: DebugSnapshot, selected: DebugSnapshot | null): boolean {
  return selected?.batchId === candidate.batchId &&
    selected.stageName === candidate.stageName &&
    selected.recordIndex === candidate.recordIndex &&
    selected.attempt === candidate.attempt &&
    selected.capturedUtc === candidate.capturedUtc;
}

export function snapshotKey(snapshot: DebugSnapshot): string {
  return `${snapshot.batchId}:${snapshot.stageName}:${snapshot.recordIndex}:${snapshot.attempt}:${snapshot.capturedUtc}`;
}

export function friendlyStageName(stageName: string): string {
  if (stageName === "SourceInput") return "Source input";
  if (stageName === "BatchInput") return "Step input";
  if (stageName.startsWith("DependencyOutput:")) {
    return `Output to ${stageName.slice("DependencyOutput:".length)}`;
  }
  return stageName;
}

export function traceJobCount(snapshots: DebugSnapshot[]): number {
  return new Set(snapshots.map((snapshot) => snapshot.jobName)).size;
}

export function prettyJson(source: string): string {
  try {
    return JSON.stringify(safePayloadValue(JSON.parse(source)), null, 2);
  } catch {
    return source;
  }
}
