import { WorkflowToolbar } from "./WorkflowToolbar";
import { WorkflowTable } from "./WorkflowTable";
import { WorkflowLevelOverview } from "./WorkflowLevelOverview";
import { WorkflowGraphCanvas } from "./WorkflowGraphCanvas";
import type { DashboardController } from "../hooks/useDashboardController";

export function WorkflowPanel({ controller }: { controller: DashboardController }) {
  const { jobViewMode, hideCompletedJobs, setSelectedSnapshot, trace, setTrace, traceId, setTraceId, graphLayoutError, configurationOnly, configuration, jobs, visibleJobs, largeOverview, traceJobNames } = controller;

  return <section className="graph-panel">
    <WorkflowToolbar controller={controller} />
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
      <WorkflowTable controller={controller} />
    ) : largeOverview ? (
      <WorkflowLevelOverview controller={controller} />
    ) : (
      <WorkflowGraphCanvas controller={controller} />
    )}
  </section>;
}
