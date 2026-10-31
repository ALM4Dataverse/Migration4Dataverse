import { useEffect, useState } from "react";
import { isRunStalled, number } from "../dashboardUtils";
import { api } from "../dashboardApi";
import type { DashboardBatchRetryHistory } from "../types";
import type { DashboardController } from "../hooks/useDashboardController";

export function RunNotices({ controller }: { controller: DashboardController }) {
  const { error, viewRun } = controller;
  const [recoveryMessage, setRecoveryMessage] = useState("");
  const [recoveryError, setRecoveryError] = useState("");
  const [retryHistory, setRetryHistory] = useState<DashboardBatchRetryHistory[] | null>(null);
  const [loadingRetryHistory, setLoadingRetryHistory] = useState(false);
  const [retryHistoryOffset, setRetryHistoryOffset] = useState(0);
  const [hasMoreRetryHistory, setHasMoreRetryHistory] = useState(false);
  useEffect(() => {
    setRetryHistory(null);
    setRecoveryMessage("");
    setRecoveryError("");
    setRetryHistoryOffset(0);
    setHasMoreRetryHistory(false);
  }, [viewRun?.runId]);

  const queueFailedBatches = async () => {
    if (!viewRun || !window.confirm(
      `Queue all ${viewRun.batchesWithErrors} failed batches for replay?\n\n` +
      "Fix the cause first and reconcile external writes. Replaying a batch can repeat side effects.",
    )) return;
    setRecoveryError("");
    setRecoveryMessage("");
    try {
      const result = await api<{ queuedBatches: number }>(
        `/api/runs/${encodeURIComponent(viewRun.runId)}/retry-failed-batches`,
        undefined,
        { method: "POST" },
      );
      setRecoveryMessage(
        result.queuedBatches > 0
          ? `${number.format(result.queuedBatches)} batches are queued. Rerun Invoke-Migration with this run ID and the unchanged step definition.`
          : "There are no failed batches to queue.",
      );
      await controller.refresh();
    } catch (reason) {
      setRecoveryError(String(reason));
    }
  };

  const continuePastStep = async (stepId: string) => {
    if (!viewRun || !window.confirm(
      `Continue downstream work past failed step "${stepId}"?\n\n` +
      "Only outputs already emitted by this step will be used. Failed records are not retried, " +
      "source rows not yet read will be skipped, and completed external writes are not rolled back.",
    )) return;
    setRecoveryError("");
    setRecoveryMessage("");
    try {
      await api<{ allowed: boolean }>(
        `/api/runs/${encodeURIComponent(viewRun.runId)}/steps/${encodeURIComponent(stepId)}/continue-after-failure`,
        undefined,
        { method: "POST" },
      );
      setRecoveryMessage(
        `Continuation past "${stepId}" is recorded. Rerun Invoke-Migration with this run ID and the unchanged step definition.`,
      );
      await controller.refresh();
    } catch (reason) {
      setRecoveryError(String(reason));
    }
  };

  const continuationSteps = viewRun
    ? viewRun.jobs.filter((job) =>
        (job.unacceptedErrorBatches ?? 0) > 0 && !job.failureContinuationOverride)
    : [];
  const sourceReaderFailureStep = viewRun?.failure?.phase === "Source reader"
    ? viewRun.failure.stepId
    : null;
  const sourceReaderFailureAlreadyListed = sourceReaderFailureStep !== null &&
    viewRun?.jobs.some((job) =>
      job.name === sourceReaderFailureStep && job.failureContinuationOverride);

  const loadRetryHistory = async (offset = 0) => {
    if (!viewRun) return;
    setLoadingRetryHistory(true);
    setRecoveryError("");
    try {
      const page = await api<DashboardBatchRetryHistory[]>(
        `/api/runs/${encodeURIComponent(viewRun.runId)}/retry-history?limit=100&offset=${offset}`,
      );
      setRetryHistory((current) => offset === 0 ? page : [...(current ?? []), ...page]);
      setRetryHistoryOffset(offset + page.length);
      setHasMoreRetryHistory(page.length === 100);
    } catch (reason) {
      setRecoveryError(String(reason));
    } finally {
      setLoadingRetryHistory(false);
    }
  };

  return <>
    {error && <div className="notice notice-error" role="alert">{error}</div>}
    {viewRun && (viewRun.status === "Failed" || viewRun.status === "Cancelled") && (
      <div className="notice notice-error run-failure-notice" role="alert">
        <div>
          <strong>{viewRun.status === "Failed" ? "Migration failed." : "Migration was cancelled."}</strong>
          {viewRun.failure ? (
            <>
              <span>
                {" "}{viewRun.failure.phase}
                {viewRun.failure.stepId ? ` · ${viewRun.failure.stepId}` : ""}:
                {" "}{viewRun.failure.message}
                {viewRun.failure.attempts > 1 ? ` (${viewRun.failure.attempts} attempts)` : ""}
              </span>
              <details>
                <summary>Full failure details</summary>
                <pre>{viewRun.failure.details}</pre>
              </details>
            </>
          ) : (
            <span> No run-level failure details were persisted; check the Invoke-Migration output.</span>
          )}
          <p>
            The run stopped globally. Completed writes are not rolled back; inspect and reconcile them before replay.
            {" "}{number.format(viewRun.pendingBatches)} pending and {number.format(viewRun.inFlightBatches)} in-flight batches remain.
          </p>
          {viewRun.batchesWithErrors > 0 && (
            <button type="button" onClick={() => void queueFailedBatches()}>
              Queue all {number.format(viewRun.batchesWithErrors)} failed batches for replay
            </button>
          )}
          {continuationSteps.length > 0 || sourceReaderFailureStep ? (
            <section className="run-step-continuations">
              <p>
                If errors cannot be resolved, you may explicitly continue downstream from a failed step.
                This skips its failed or unread work and uses only outputs already emitted by that step.
              </p>
              {continuationSteps.map((job) => (
                <button
                  type="button"
                  key={job.name}
                  onClick={() => void continuePastStep(job.name)}
                >
                  Continue downstream from {job.name}
                </button>
              ))}
              {sourceReaderFailureStep && !sourceReaderFailureAlreadyListed && (
                <button
                  type="button"
                  onClick={() => void continuePastStep(sourceReaderFailureStep)}
                >
                  Continue downstream from {sourceReaderFailureStep}
                </button>
              )}
            </section>
          ) : null}
          {recoveryMessage && <p role="status">{recoveryMessage}</p>}
        </div>
      </div>
    )}
    {viewRun?.status === "CompletedWithErrors" && (
      <div className="notice notice-warning" role="status">
        <strong>Migration completed with row errors.</strong>
        {viewRun.failedRows > 0
          ? ` ${number.format(viewRun.failedRows)} failed rows were within the configured threshold or explicitly accepted.`
          : " The run completed after an operator-approved continuation; unread source work may have been skipped."}
        {" "}Downstream steps received only outputs emitted by the continued steps; inspect the affected steps and reconcile external writes.
        {viewRun.failure && (
          <details>
            <summary>Failure details before continuation</summary>
            <pre>{viewRun.failure.details}</pre>
          </details>
        )}
      </div>
    )}
    {viewRun && recoveryError && <div className="notice notice-error" role="alert">{recoveryError}</div>}
    {viewRun && retryHistory === null && (
      <button type="button" onClick={() => void loadRetryHistory()} disabled={loadingRetryHistory}>
        {loadingRetryHistory ? "Loading replay history…" : "View replay history"}
      </button>
    )}
    {viewRun && retryHistory !== null && (
      <details className="run-retry-history">
        <summary>Replay history ({number.format(retryHistory.length)} loaded)</summary>
        {retryHistory.length === 0
          ? <p>No failed batches have been queued for replay.</p>
          : <ul>
            {retryHistory.map((entry) => (
              <li key={entry.retryId}>
                {entry.stepId} · batch {entry.batchId} · {number.format(entry.previousAttempts)} attempts ·{" "}
                {new Date(entry.queuedUtc).toLocaleString()}
                {entry.previousLastError && <pre>{entry.previousLastError}</pre>}
                {entry.previousOutcomesJson && (
                  <details>
                    <summary>Previous outcomes</summary>
                    <pre>{entry.previousOutcomesJson}</pre>
                  </details>
                )}
              </li>
            ))}
          </ul>}
        {hasMoreRetryHistory && (
          <button type="button" onClick={() => void loadRetryHistory(retryHistoryOffset)} disabled={loadingRetryHistory}>
            {loadingRetryHistory ? "Loading replay history…" : "Load more replay history"}
          </button>
        )}
      </details>
    )}
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
