import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Spinner as FluentSpinner } from "@fluentui/react-components";
import { useState } from "react";
import { api } from "../dashboardApi";
import { number } from "../dashboardUtils";
import type { DashboardBatchError } from "../types";
import type { DashboardController } from "../hooks/useDashboardController";

export function TaskErrorDialog({ controller }: { controller: DashboardController }) {
  const { taskErrorDialog, loadMoreTaskErrors, closeTaskErrorDialog, run } = controller;
  const [exporting, setExporting] = useState(false);
  const [exportError, setExportError] = useState("");
  const [editedPayloads, setEditedPayloads] = useState<Record<string, string>>({});
  const [rowRetrying, setRowRetrying] = useState<Record<string, boolean>>({});
  const [queuedRows, setQueuedRows] = useState<Record<string, boolean>>({});
  const [rowActionErrors, setRowActionErrors] = useState<Record<string, string>>({});

  const exportErrors = async () => {
    if (!taskErrorDialog || !run) return;
    setExporting(true);
    setExportError("");
    try {
      const allErrors: DashboardBatchError[] = [];
      for (let offset = 0; offset < taskErrorDialog.totalErrors; offset += 500) {
        const page = await api<DashboardBatchError[]>(
          `/api/runs/${encodeURIComponent(run.runId)}/batch-errors/${encodeURIComponent(taskErrorDialog.stepId)}?limit=500&offset=${offset}`,
        );
        if (page.length === 0) break;
        allErrors.push(...page);
      }
      const blob = new Blob(
        [JSON.stringify({ runId: run.runId, stepId: taskErrorDialog.stepId, batches: allErrors }, null, 2)],
        { type: "application/json" },
      );
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement("a");
      anchor.href = url;
      anchor.download = `${taskErrorDialog.stepId.replace(/[^a-zA-Z0-9._-]/g, "_")}-errors.json`;
      anchor.click();
      URL.revokeObjectURL(url);
    } catch (reason) {
      setExportError(String(reason));
    } finally {
      setExporting(false);
    }
  };

  const retryRow = async (batch: DashboardBatchError, row: DashboardBatchError["rowErrors"][number]) => {
    if (!taskErrorDialog || !run || !row.canRetry) return;
    const key = `${batch.batchId}:${row.recordIndex}`;
    const payloadText = editedPayloads[key] ?? row.payloadJson;
    let payload: unknown;
    try {
      payload = JSON.parse(payloadText);
    } catch (reason) {
      setRowActionErrors((current) => ({ ...current, [key]: `Invalid JSON: ${String(reason)}` }));
      return;
    }
    if (payload === null || Array.isArray(payload) || typeof payload !== "object") {
      setRowActionErrors((current) => ({ ...current, [key]: "The row payload must be a JSON object." }));
      return;
    }
    if (!window.confirm(
      `Queue row ${row.recordIndex + 1} for retry in step "${taskErrorDialog.stepId}"?\n\n` +
      "Only this row is rerun. Step side effects may repeat; successful sibling rows are not replayed.",
    )) return;

    setRowActionErrors((current) => ({ ...current, [key]: "" }));
    setRowRetrying((current) => ({ ...current, [key]: true }));
    try {
      await api<{ queuedBatchId: string }>(
        `/api/runs/${encodeURIComponent(run.runId)}/retry-row`,
        undefined,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            stepId: taskErrorDialog.stepId,
            batchId: batch.batchId,
            recordIndex: row.recordIndex,
            payloadJson: JSON.stringify(payload),
          }),
        },
      );
      setQueuedRows((current) => ({ ...current, [key]: true }));
      await controller.refresh();
    } catch (reason) {
      setRowActionErrors((current) => ({ ...current, [key]: String(reason) }));
    } finally {
      setRowRetrying((current) => ({ ...current, [key]: false }));
    }
  };

  return <Dialog
    open={taskErrorDialog !== null}
    onOpenChange={(_, data) => {
      if (!data.open) closeTaskErrorDialog();
    }}
  >
    <DialogSurface className="task-error-dialog">
      <DialogBody>
        <DialogTitle>{taskErrorDialog ? `Failed task details · ${taskErrorDialog.stepId}` : "Failed task details"}</DialogTitle>
        <DialogContent>
          {taskErrorDialog && (
            <>
              <p className="task-error-summary">
                {number.format(taskErrorDialog.totalErrors)} failed batches recorded
                {taskErrorDialog.batches.length > 0
                  ? ` · showing ${number.format(taskErrorDialog.batches.length)}`
                  : ""}
              </p>
              <p className="task-error-summary">
                Export includes batch outcomes and may contain migration data.
              </p>
              <Button appearance="secondary" disabled={exporting || taskErrorDialog.totalErrors === 0} onClick={() => void exportErrors()}>
                {exporting ? "Exporting…" : "Export all error details"}
              </Button>
              {exportError && <div className="notice notice-error" role="alert">{exportError}</div>}
              {taskErrorDialog.loadError && (
                <div className="notice notice-error" role="alert">{taskErrorDialog.loadError}</div>
              )}
              {taskErrorDialog.isLoading && <FluentSpinner label="Loading failed batch details…" />}
              {!taskErrorDialog.isLoading && !taskErrorDialog.loadError &&
                taskErrorDialog.batches.length === 0 && (
                  <p className="inline-empty">No failed batch details are available for this step.</p>
                )}
              <div className="task-error-list">
                {taskErrorDialog.batches.map((batch) => (
                  <article className="task-error-entry" key={batch.batchId}>
                    <h3>
                      <span>Batch {batch.sequence} · {batch.batchId}</span>
                      <small>{number.format(batch.failedRows)} failed rows · {batch.attempts} attempts</small>
                    </h3>
                    <pre>{batch.lastError || "No error message was recorded for this failed batch."}</pre>
                    {batch.rowErrors.length > 0 && (
                      <div className="task-row-error-list">
                        {batch.rowErrors.map((row) => {
                          const key = `${batch.batchId}:${row.recordIndex}`;
                          const queued = queuedRows[key] || row.retryState === "Pending" || row.retryState === "InFlight";
                          return (
                            <section className="task-row-error" key={key}>
                              <h4>Input row {row.recordIndex + 1}</h4>
                              <pre>{row.error}</pre>
                              <label>
                                Edit the input row JSON before retrying
                                <textarea
                                  className="row-payload-editor"
                                  spellCheck={false}
                                  value={editedPayloads[key] ?? row.payloadJson}
                                  onChange={(event) => setEditedPayloads((current) => ({
                                    ...current,
                                    [key]: event.target.value,
                                  }))}
                                />
                              </label>
                              <Button
                                appearance="secondary"
                                disabled={!row.canRetry || queued || rowRetrying[key]}
                                onClick={() => void retryRow(batch, row)}
                              >
                                {queued
                                  ? "Row retry queued"
                                  : rowRetrying[key]
                                    ? "Queueing row retry…"
                                    : "Retry this row"}
                              </Button>
                              {row.retryCount > 0 && (
                                <small>Row retry attempts queued: {number.format(row.retryCount)}</small>
                              )}
                              {rowActionErrors[key] && (
                                <div className="notice notice-error" role="alert">{rowActionErrors[key]}</div>
                              )}
                            </section>
                          );
                        })}
                      </div>
                    )}
                  </article>
                ))}
              </div>
              {!taskErrorDialog.isLoading && !taskErrorDialog.loadError &&
                taskErrorDialog.batches.length < taskErrorDialog.totalErrors && (
                  <Button appearance="secondary" onClick={() => void loadMoreTaskErrors()}>
                    Load more failed batches
                  </Button>
                )}
            </>
          )}
        </DialogContent>
        <DialogActions>
          <Button appearance="primary" onClick={closeTaskErrorDialog}>Close</Button>
        </DialogActions>
      </DialogBody>
    </DialogSurface>
  </Dialog>;
}
