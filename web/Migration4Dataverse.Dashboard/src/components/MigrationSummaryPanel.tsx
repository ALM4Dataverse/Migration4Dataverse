import { Activity, ChevronDown, ChevronUp, Gauge } from "lucide-react";
import { DashboardJob, DataverseConnectionSample, ProcessSample, RunSummary, SchedulerSample } from "../types";
import { buildOverallThroughput, formatElapsed, getTerminalTargetNames, isRunStalled, number, projectedInputRows, rankBlockingHotspots } from "../dashboardUtils";
import { Text } from "./Text";
import { OverallThroughputChart } from "./OverallThroughputChart";
import { ProcessResourceCharts } from "./ProcessResourceCharts";
import { Timeline } from "./Timeline";
import { Hotspots } from "./Hotspots";
import { SchedulerCapacityChart } from "./SchedulerCapacityChart";
import { ConnectionThroughputChart } from "./ConnectionThroughputChart";

export function MigrationSummaryPanel({
  run,
  jobs,
  samples,
  connectionSamples,
  processSamples,
  expanded,
  onToggle,
  onSelectJob,
}: {
  run: RunSummary;
  jobs: DashboardJob[];
  samples: SchedulerSample[];
  connectionSamples: DataverseConnectionSample[];
  processSamples: ProcessSample[];
  expanded: boolean;
  onToggle: () => void;
  onSelectJob: (jobName: string) => void;
}) {
  const terminalTargets = getTerminalTargetNames(run);
  const targets = jobs.filter((job) =>
    terminalTargets.has(job.name) || job.type.toLowerCase() === "target",
  );
  const targetRowsProcessed = targets.reduce((total, job) => total + job.inputRowsProcessed, 0);
  const targetRowsSucceeded = targets.reduce((total, job) => total + job.succeededRows, 0);
  const targetRowsFailed = targets.reduce((total, job) => total + job.failedRows, 0);
  const targetRowProjections = targets.map((job) => projectedInputRows(job, jobs));
  const targetRowsExpected = targets.length > 0 && targetRowProjections.every((projection) => projection !== null)
    ? targetRowProjections.reduce((total, projection) => total + (projection?.expectedRows ?? 0), 0)
    : null;
  const targetRowsExpectedIsEstimate = targetRowProjections.some((projection) => projection?.isEstimate);
  const targetProgress = targetRowsExpected === null
    ? null
    : targetRowsExpected === 0
      ? 100
      : Math.min(100, (targetRowsProcessed / targetRowsExpected) * 100);
  const runStalled = isRunStalled(run);
  const overallThroughput = buildOverallThroughput(jobs, run);
  const estimatedCompletion = runStalled
    ? "Run stalled — resume required"
    : run.status === "Completed"
      ? `Completed ${new Date(run.updatedUtc).toLocaleString()}`
      : run.status !== "Running"
        ? "Unavailable"
        : targetRowsExpected === null || !overallThroughput || overallThroughput.output.average <= 0
          ? "Estimating…"
          : targetRowsExpected <= targetRowsProcessed
            ? "Finishing now"
            : new Date(
              Date.now() +
              ((targetRowsExpected - targetRowsProcessed) / overallThroughput.output.average) * 1000,
            ).toLocaleString();
  const blockingHotspots = rankBlockingHotspots(jobs, samples, run.runId);

  return (
    <section className={`summary-panel${expanded ? " expanded" : ""}`} aria-label="Migration overview">
      <div className="summary-header">
        <OverallThroughputChart throughput={overallThroughput} />
        <div className="summary-header-metrics">
          <div className="summary-header-metric">
            <span>Elapsed</span>
            <strong>{formatElapsed(run.createdUtc, run.status === "Running" && !runStalled ? undefined : run.updatedUtc)}</strong>
          </div>
          <div className="summary-target-metric">
            <div className="summary-target-count">
              <span>
                {targetRowsExpected === null
                  ? "Target rows so far"
                  : targetRowsExpectedIsEstimate
                    ? "Projected target rows"
                    : "Target rows"}
              </span>
              <strong>
                {targetRowsExpectedIsEstimate ? "~" : ""}
                {number.format(targetRowsExpected ?? targetRowsProcessed)}
              </strong>
            </div>
            <div className="summary-target-outcomes">
              <span>Succeeded <strong>{number.format(targetRowsSucceeded)}</strong></span>
              <span>Errors <strong>{number.format(targetRowsFailed)}</strong></span>
            </div>
            {targetRowsExpected !== null && (
              <progress
                className="summary-target-progress"
                max={100}
                value={targetProgress ?? undefined}
                aria-label={`${Math.round(targetProgress ?? 0)}% of ${targetRowsExpectedIsEstimate ? "projected " : ""}target rows processed`}
                title={targetRowsExpectedIsEstimate
                  ? "Projected from expected source rows and observed input ratios."
                  : "Target rows processed."}
              />
            )}
          </div>
          <div className="summary-header-metric summary-eta">
            <span>Estimated completion</span>
            <strong>{estimatedCompletion}</strong>
          </div>
        </div>
        <button
          className="icon-button summary-toggle"
          type="button"
          title={expanded ? "Collapse migration overview" : "Expand migration overview"}
          aria-label={expanded ? "Collapse migration overview" : "Expand migration overview"}
          aria-expanded={expanded}
          onClick={onToggle}
        >{expanded ? <ChevronUp size={16} /> : <ChevronDown size={16} />}</button>
      </div>
      {expanded && (
        <div className="summary-content">
          <div className="summary-detail-grid">
            <section className="summary-block">
              <Text variant="mediumPlus" className="section-title"><Activity size={16} /> Execution timeline</Text>
              <Timeline jobs={jobs} onSelect={onSelectJob} />
            </section>
            <section className="summary-block">
              <Text variant="mediumPlus" className="section-title"><Gauge size={16} /> Upstream blocking hotspots</Text>
              <Hotspots hotspots={blockingHotspots} />
            </section>
          </div>
          <section className="summary-block scheduler-capacity-block">
            <Text variant="mediumPlus" className="section-title"><Gauge size={16} /> Scheduler limits and current values</Text>
            <SchedulerCapacityChart
              run={run}
              samples={samples}
              blockingStepName={blockingHotspots[0]?.job.name ?? null}
            />
          </section>
          <section className="summary-block">
            <Text variant="mediumPlus" className="section-title"><Activity size={16} /> Dataverse connection throughput</Text>
            <ConnectionThroughputChart run={run} samples={connectionSamples} />
          </section>
          <section className="summary-block">
            <Text variant="mediumPlus" className="section-title"><Activity size={16} /> Process CPU and memory</Text>
            <ProcessResourceCharts samples={processSamples} />
          </section>
        </div>
      )}
    </section>
  );
}
