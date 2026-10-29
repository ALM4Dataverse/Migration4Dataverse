import { ChevronDown, FileSearch, Settings2 } from "lucide-react";
import { ConfigurationInspector } from "./ConfigurationInspector";
import { JobInspector } from "./JobInspector";
import type { DashboardController } from "../hooks/useDashboardController";

export function StepInspectorDrawer({ controller }: { controller: DashboardController }) {
  const { inspectorPanelOpen, setInspectorPanelOpen, setInspectorTab, selectedJob, jobRows, jobRowCount, rowStageCounts, rowPayloadColumns, selectedStage, setSelectedStage, hasMoreJobRows, loadingJobRows, setRowSearchByStage, setRowFiltersByStage, setRowSortByStage, selectedSnapshot, setSelectedSnapshot, trace, setTrace, setTraceId, traceSearch, setTraceSearch, rowSearch, rowColumnFilters, rowSortState, clearJobRows, restartSnapshotQuery, configuration, SelectedJobIcon, viewRun, activeInspectorTab, selectSnapshot, loadMoreJobRows } = controller;

  return <section className={`row-drawer${inspectorPanelOpen ? " expanded" : ""}`}>
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
                configurationJobs={viewRun.configuration?.jobs ?? []}
                connectionInstances={viewRun.connectionInstances ?? []}
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
  </section>;
}
