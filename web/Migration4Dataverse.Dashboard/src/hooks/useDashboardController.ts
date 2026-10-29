import { FileSearch } from "lucide-react";
import { useCallback, useEffect, useRef, useState } from "react";
import type {
  ApiConfiguration,
  ApiDebugSnapshot,
  ApiDebugSnapshotPage,
  ApiProcessMetrics,
  ApiRunSummary,
  DashboardBatchError,
  DashboardJob,
  DataverseConnectionPoolSnapshot,
  DataverseConnectionSample,
  DebugSnapshot,
  DebugSnapshotPage,
  MigrationConfiguration,
  ProcessSample,
  RunSummary,
  SchedulerSample,
  SnapshotGridSortState,
  TaskErrorDialogState,
} from "../types";
import {
  api,
  createSnapshotQuery,
  normalizeConfiguration,
  normalizeRun,
  normalizeSnapshot,
  normalizeSnapshotPage,
} from "../dashboardApi";
import {
  createSchedulerSample,
  defaultSnapshotSort,
  emptySnapshotFilters,
  jobTypeIcon,
  snapshotKey,
} from "../dashboardUtils";
import { useWorkflowGraph } from "./useWorkflowGraph";

export function useDashboardController() {
  const [runs, setRuns] = useState<RunSummary[]>([]);

  const [runId, setRunId] = useState("");

  const [run, setRun] = useState<RunSummary | null>(null);

  const [schedulerSamples, setSchedulerSamples] = useState<SchedulerSample[]>([]);

  const [connectionSamples, setConnectionSamples] = useState<DataverseConnectionSample[]>([]);

  const [processSamples, setProcessSamples] = useState<ProcessSample[]>([]);

  const [taskErrorDialog, setTaskErrorDialog] = useState<TaskErrorDialogState | null>(null);

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
  const refreshInFlight = useRef(false);
  const taskErrorRequestVersion = useRef(0);
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

  const showTaskErrors = useCallback(async (job: DashboardJob) => {
    const requestVersion = ++taskErrorRequestVersion.current;
    const activeRunId = run?.runId;
    setTaskErrorDialog({
      stepId: job.name,
      totalErrors: job.batchesWithErrors,
      batches: [],
      isLoading: true,
      loadError: null,
    });
    if (!activeRunId) {
      setTaskErrorDialog({
        stepId: job.name,
        totalErrors: job.batchesWithErrors,
        batches: [],
        isLoading: false,
        loadError: "No migration run is selected.",
      });
      return;
    }
    try {
      const batches = await api<DashboardBatchError[]>(
        `/api/runs/${encodeURIComponent(activeRunId)}/batch-errors/${encodeURIComponent(job.name)}?limit=100&offset=0`,
      );
      if (requestVersion !== taskErrorRequestVersion.current) return;
      setTaskErrorDialog({
        stepId: job.name,
        totalErrors: job.batchesWithErrors,
        batches,
        isLoading: false,
        loadError: null,
      });
    } catch (reason) {
      if (requestVersion !== taskErrorRequestVersion.current) return;
      setTaskErrorDialog({
        stepId: job.name,
        totalErrors: job.batchesWithErrors,
        batches: [],
        isLoading: false,
        loadError: String(reason),
      });
    }
  }, [run?.runId]);

  const loadMoreTaskErrors = useCallback(async () => {
    const dialog = taskErrorDialog;
    const activeRunId = run?.runId;
    if (!dialog || dialog.isLoading || !activeRunId || dialog.batches.length >= dialog.totalErrors) return;
    const requestVersion = taskErrorRequestVersion.current;
    setTaskErrorDialog({ ...dialog, isLoading: true, loadError: null });
    try {
      const batches = await api<DashboardBatchError[]>(
        `/api/runs/${encodeURIComponent(activeRunId)}/batch-errors/${encodeURIComponent(dialog.stepId)}?limit=100&offset=${dialog.batches.length}`,
      );
      if (requestVersion !== taskErrorRequestVersion.current) return;
      setTaskErrorDialog((current) => current?.stepId === dialog.stepId
        ? { ...current, batches: [...current.batches, ...batches], isLoading: false, loadError: null }
        : current);
    } catch (reason) {
      if (requestVersion !== taskErrorRequestVersion.current) return;
      setTaskErrorDialog((current) => current?.stepId === dialog.stepId
        ? { ...current, isLoading: false, loadError: String(reason) }
        : current);
    }
  }, [run?.runId, taskErrorDialog]);

  const closeTaskErrorDialog = useCallback(() => {
    taskErrorRequestVersion.current++;
    setTaskErrorDialog(null);
  }, []);

  const refresh = useCallback(async (signal?: AbortSignal) => {
    if (refreshInFlight.current) return;
    refreshInFlight.current = true;
    try {
      const listedRuns = (await api<ApiRunSummary[]>("/api/runs", signal)).map(normalizeRun);
      setRuns(listedRuns);
      const processMetrics = await api<ApiProcessMetrics>("/api/process-metrics", signal);
      const sampledAt = Date.parse(processMetrics.capturedUtc);
      if (!Number.isFinite(sampledAt)) {
        throw new Error("The dashboard returned an invalid process-metrics timestamp.");
      }
      setProcessSamples((previous) => {
        const lastSample = previous.at(-1);
        if (lastSample && sampledAt - lastSample.sampledAt < 2500) return previous;
        return [...previous, {
          sampledAt,
          cpuTimeMilliseconds: processMetrics.cpuTimeMilliseconds,
          logicalProcessorCount: processMetrics.logicalProcessorCount,
          workingSetBytes: processMetrics.workingSetBytes,
          managedHeapBytes: processMetrics.managedHeapBytes,
          memoryLimitBytes: processMetrics.memoryLimitBytes,
        }]
          .filter((sample) => sample.sampledAt >= sampledAt - 6 * 60_000)
          .slice(-120);
      });
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
      const sample = createSchedulerSample(currentRun);
      setSchedulerSamples((previous) => {
        const currentRunSamples = previous.filter((item) => item.runId === currentRun.runId);
        const lastSample = currentRunSamples.at(-1);
        if (lastSample && sample.sampledAt - lastSample.sampledAt < 2500) {
          return currentRunSamples;
        }
        return [...currentRunSamples, sample]
          .filter((item) => item.sampledAt >= sample.sampledAt - 6 * 60_000)
          .slice(-120);
      });
      const connectionPools = await api<DataverseConnectionPoolSnapshot[]>(
        `/api/runs/${encodeURIComponent(currentRun.runId)}/dataverse-connection-throughput`,
        signal,
      );
      if (connectionPools.length > 0) {
        const sampledAt = Date.now();
        setConnectionSamples((previous) => {
          const currentRunSamples = previous.filter((item) => item.runId === currentRun.runId);
          const lastSample = currentRunSamples.at(-1);
          if (lastSample && sampledAt - lastSample.sampledAt < 2500) {
            return currentRunSamples;
          }
          return [...currentRunSamples, {
            runId: currentRun.runId,
            sampledAt,
            pools: connectionPools,
          }]
            .filter((item) => item.sampledAt >= sampledAt - 6 * 60_000)
            .slice(-120);
        });
      }
      setError("");
    } finally {
      refreshInFlight.current = false;
    }
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
    taskErrorRequestVersion.current++;
    setTaskErrorDialog(null);
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

  const workflowGraph = useWorkflowGraph({
    jobs, configuration, configurationOnly, hideCompletedJobs, setHideCompletedJobs, focusedLevel,
    setFocusedLevel, selectedJob, trace, runId, onSelectJob: selectJob, onShowTaskErrors: showTaskErrors,
  });

  const SelectedJobIcon = selectedJob
    ? jobTypeIcon(workflowGraph.configurationJobsByName.get(selectedJob)?.type ?? "step")
    : FileSearch;

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

  return {
    ...workflowGraph,
    runs,
    setRuns,
    runId,
    setRunId,
    run,
    setRun,
    schedulerSamples,
    setSchedulerSamples,
    connectionSamples,
    setConnectionSamples,
    processSamples,
    setProcessSamples,
    taskErrorDialog,
    setTaskErrorDialog,
    previewConfiguration,
    setPreviewConfiguration,
    showConfiguration,
    setShowConfiguration,
    summaryExpanded,
    setSummaryExpanded,
    inspectorPanelOpen,
    setInspectorPanelOpen,
    inspectorTab,
    setInspectorTab,
    selectedJob,
    setSelectedJob,
    jobViewMode,
    setJobViewMode,
    hideCompletedJobs,
    setHideCompletedJobs,
    jobRows,
    setJobRows,
    jobRowCount,
    setJobRowCount,
    rowStageCounts,
    setRowStageCounts,
    rowPayloadColumns,
    setRowPayloadColumns,
    selectedStage,
    setSelectedStage,
    hasMoreJobRows,
    setHasMoreJobRows,
    loadingJobRows,
    setLoadingJobRows,
    rowSearchByStage,
    setRowSearchByStage,
    rowFiltersByStage,
    setRowFiltersByStage,
    rowSortByStage,
    setRowSortByStage,
    selectedSnapshot,
    setSelectedSnapshot,
    trace,
    setTrace,
    traceId,
    setTraceId,
    traceSearch,
    setTraceSearch,
    stepSearchOpen,
    setStepSearchOpen,
    stepSearchValue,
    setStepSearchValue,
    focusedLevel,
    setFocusedLevel,
    loading,
    setLoading,
    error,
    setError,
    refreshInFlight,
    taskErrorRequestVersion,
    rowRequestVersion,
    loadMoreController,
    rowSearch,
    rowColumnFilters,
    rowSortState,
    clearJobRows,
    restartSnapshotQuery,
    showTaskErrors,
    loadMoreTaskErrors,
    closeTaskErrorDialog,
    refresh,
    configurationOnly,
    configuration,
    jobs,
    SelectedJobIcon,
    selectJob,
    viewRun,
    activeInspectorTab,
    loadTrace,
    selectSnapshot,
    loadMoreJobRows,
  };
}

export type DashboardController = ReturnType<typeof useDashboardController>;
