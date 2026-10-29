import { RefreshCw } from "lucide-react";
import { runStatusLabel } from "../dashboardUtils";
import { Text } from "./Text";
import type { DashboardController } from "../hooks/useDashboardController";

export function DashboardHeader({ controller }: { controller: DashboardController }) {
  const { runs, runId, setRunId, previewConfiguration, setShowConfiguration, loading, setError, refresh, configurationOnly, selectJob } = controller;

  return <header className="topbar">
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
            {item.jobName}{item.configuration?.isWhatIf ? " · WhatIf preview" : ""} · {runStatusLabel(item)} · {new Date(item.createdUtc).toLocaleString()}
          </option>
        ))}
        {runs.length === 0 && !loading && !previewConfiguration && <option value="__empty__" disabled>No runs found</option>}
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
  </header>;
}
