export type RunStatus = "Running" | "Completed" | "Failed" | "Cancelled";

export type JobStatus = "Running" | "Completed" | "Pending" | "Queued" | "Failed" | "Blocked" | "Configured";

export interface DashboardActivityPeriod {
  state: "Working" | "Waiting";
  startedUtc: string;
  endedUtc: string | null;
}

export interface DashboardRateSample {
  periodStartedUtc: string;
  periodEndedUtc: string;
  rowsPerSecond: number;
}

export interface ThroughputSeries {
  points: number[];
  current: number;
  average: number;
}

export interface OverallThroughput {
  input: ThroughputSeries;
  output: ThroughputSeries;
  startedUtc: string;
  endedUtc: string;
}

export interface SchedulerLimits {
  maxDegreeOfParallelism: number;
  maxQueuedBatches: number;
  maxQueuedBatchesPerStep: number;
}

export interface SchedulerSample {
  runId: string;
  sampledAt: number;
  outstandingBatches: number;
  inFlightBatches: number;
  outstandingByStep: Record<string, number>;
  blockedAncestorsByStep: Record<string, number>;
}

export interface DataverseConnectionPoolMemberSnapshot {
  memberNumber: number;
  totalRequests: number;
  totalRequestUnits: number;
  activeRequests: number;
}

export interface DataverseConnectionPoolSnapshot {
  connectionId: string;
  members: DataverseConnectionPoolMemberSnapshot[];
}

export interface DataverseConnectionSample {
  runId: string;
  sampledAt: number;
  pools: DataverseConnectionPoolSnapshot[];
}

export interface ApiProcessMetrics {
  cpuTimeMilliseconds: number;
  logicalProcessorCount: number;
  workingSetBytes: number;
  managedHeapBytes: number;
  memoryLimitBytes: number | null;
  capturedUtc: string;
}

export interface ProcessSample extends Omit<ApiProcessMetrics, "capturedUtc"> {
  sampledAt: number;
}

export interface DashboardBatchError {
  batchId: string;
  sequence: number;
  state: string;
  attempts: number;
  lastError: string | null;
  outcomesJson: string | null;
  failedRows: number;
}

export interface TaskErrorDialogState {
  stepId: string;
  totalErrors: number;
  batches: DashboardBatchError[];
  isLoading: boolean;
  loadError: string | null;
}

export interface BlockingHotspot {
  job: DashboardJob;
  blockedAncestors: number;
  ancestorSeconds: number;
}

export interface DashboardJob {
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

export interface ConfigurationJob {
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

export interface ConfigurationConnection {
  id: string;
  typeName: string | null;
  properties: Record<string, unknown> | unknown[];
}

export interface ConnectionReference {
  id: string;
  typeName: string;
}

export interface MigrationConfiguration {
  jobs: ConfigurationJob[];
  connections: ConfigurationConnection[];
  schedulerLimits?: SchedulerLimits;
  isWhatIf?: boolean;
}

export interface ConnectionInstance {
  id: string;
  typeName: string | null;
  properties: Record<string, unknown> | unknown[];
}

export interface ConnectionInstanceSnapshot {
  phase: string;
  capturedUtc: string;
  connections: ConnectionInstance[];
}

export interface RunSummary {
  runId: string;
  jobName: string;
  status: RunStatus;
  createdUtc: string;
  updatedUtc: string;
  schedulerLeaseExpiresUtc: string | null;
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

export interface DebugSnapshot {
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

export interface DebugSnapshotPage {
  items: DebugSnapshot[];
  totalCount: number;
  hasMore: boolean;
  stageCounts: { stageName: string; count: number }[];
  payloadColumns: string[];
}

export interface ApiStep {
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

export interface ApiConfiguration {
  steps: ApiStep[];
  connections: ConfigurationConnection[];
  schedulerLimits?: SchedulerLimits;
  isWhatIf?: boolean;
}

export interface ApiDashboardStep extends Omit<DashboardJob, "name" | "dependsOn" | "hasSource"> {
  id: string;
  inputs: string[];
  isSource: boolean;
}

export interface ApiRunSummary extends Omit<RunSummary, "jobName" | "jobs" | "configuration"> {
  runName: string;
  steps: ApiDashboardStep[];
  configuration: ApiConfiguration | null;
}

export interface ApiDebugSnapshot extends Omit<DebugSnapshot, "jobName"> {
  stepId: string;
}

export interface ApiDebugSnapshotPage extends Omit<DebugSnapshotPage, "items"> {
  items: ApiDebugSnapshot[];
}

export type SnapshotGridSortState = {
  sortColumn: string | number | undefined;
  sortDirection: "ascending" | "descending";
};

export interface JobNodeData extends Record<string, unknown> {
  job: DashboardJob;
  progress: StepProgress | null;
  inputRowsDisplay: string;
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
  onShowErrors: (job: DashboardJob) => void;
}

export interface SpecialistInfoLine {
  label: string;
  value: string;
  title?: string;
}

export interface ProjectedInputRows {
  expectedRows: number;
  isEstimate: boolean;
}

export interface ProjectedStepRows {
  inputRows: ProjectedInputRows | null;
  outputRows: ProjectedInputRows | null;
}

export interface StepProgress {
  value: number;
  max: number;
  kind: "rows" | "batches";
  ariaLabel: string;
  title: string;
}
