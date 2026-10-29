import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Spinner as FluentSpinner } from "@fluentui/react-components";
import { number } from "../dashboardUtils";
import type { DashboardController } from "../hooks/useDashboardController";

export function TaskErrorDialog({ controller }: { controller: DashboardController }) {
  const { taskErrorDialog, loadMoreTaskErrors, closeTaskErrorDialog } = controller;

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
