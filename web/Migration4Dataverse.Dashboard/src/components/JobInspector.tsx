import { Spinner as FluentSpinner } from "@fluentui/react-components";
import { useEffect, useState } from "react";
import { ConfigurationJob, ConnectionInstanceSnapshot, DebugSnapshot, DebugSnapshotPage, SnapshotGridSortState } from "../types";
import { friendlyStageName, modelDrivenRecordUrl, number, prettyJson, traceJobCount } from "../dashboardUtils";
import { Text } from "./Text";
import { SnapshotTable } from "./SnapshotTable";
import { BinaryLineageSummary } from "./BinaryLineageSummary";

export function JobInspector({
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
  configurationJobs,
  connectionInstances,
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
  configurationJobs: ConfigurationJob[];
  connectionInstances: ConnectionInstanceSnapshot[];
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
  const selectedRecordUrl = selectedSnapshot
    ? modelDrivenRecordUrl(selectedSnapshot, configurationJobs, connectionInstances)
    : null;

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
          {selectedRecordUrl && (
            <a className="action-button" href={selectedRecordUrl} target="_blank" rel="noopener noreferrer">
              Open in model-driven app
            </a>
          )}
          {trace.length > 0 && <BinaryLineageSummary snapshots={trace} />}
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
