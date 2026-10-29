import { isRunStalled, number } from "../dashboardUtils";
import type { DashboardController } from "../hooks/useDashboardController";

export function RunNotices({ controller }: { controller: DashboardController }) {
  const { error, viewRun } = controller;

  return <>
    {error && <div className="notice notice-error" role="alert">{error}</div>}
    {viewRun && isRunStalled(viewRun) && (
      <div className="notice notice-warning run-stalled-notice" role="alert">
        <span>
          <strong>Run stalled — scheduler lease expired.</strong>
          {" "}It is still marked Running, but no scheduler lease is active. Last persisted update:{" "}
          {new Date(viewRun.updatedUtc).toLocaleString()}. Persisted work is resumable with the same run ID
          {" "}({viewRun.runId}) and unchanged step definition; {number.format(viewRun.inFlightBatches)} in flight,{" "}
          {number.format(viewRun.pendingBatches)} pending.
        </span>
      </div>
    )}
    {viewRun?.configuration?.isWhatIf && (
      <div className="notice notice-warning" role="status">
        <strong>WhatIf preview — no target writes or batch callbacks were executed.</strong>
        {" "}Source readers and transforms ran; inspect captured target rows in the step snapshots.
      </div>
    )}
  </>;
}
