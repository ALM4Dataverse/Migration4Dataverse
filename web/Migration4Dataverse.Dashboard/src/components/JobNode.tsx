import { Handle, Position, type Node, type NodeProps } from "@xyflow/react";
import { Clock3, Database } from "lucide-react";
import { JobNodeData } from "../types";
import { friendlyStageName, jobTypeIcon, jobTypeInfo, lineageStages, number, prettyJson, snapshotKey, statusInfo } from "../dashboardUtils";
import { JobThroughputChart } from "./JobThroughputChart";

export function JobNode({ data }: NodeProps<Node<JobNodeData>>) {
  const {
    job,
    jobType,
    status,
    activityHint,
    specialistInfo,
    selected,
    selectionActive,
    relationshipHighlighted,
    highlighted,
    lineageActive,
    lineageSnapshots,
    sourceConnection,
    targetConnection,
    onShowErrors,
  } = data;
  const meta = statusInfo[status];
  const TypeIcon = jobTypeIcon(jobType);
  const StatusIcon = meta.icon;
  const typeLabel = jobTypeInfo[jobType.toLowerCase()]?.label ?? "Step";
  const rowProgress = data.progress;
  const stages = lineageStages(lineageSnapshots);

  return (
    <div className={`job-node status-${meta.color}${selected ? " selected" : ""}${selectionActive ? relationshipHighlighted ? " relationship-highlighted" : " relationship-muted" : ""}${highlighted ? " lineage-highlighted" : ""}${lineageActive ? " lineage-mode" : ""}`}>
      <Handle type="target" position={Position.Left} />
      <div className="job-node-head">
        <span className="job-icon" title={`${typeLabel} step`} role="img" aria-label={`${typeLabel} step`}>
          <TypeIcon size={14} aria-hidden="true" />
        </span>
        <div className="job-node-identity">
          <span className="job-node-name" title={job.name}>{job.name}</span>
        </div>
        {lineageActive
          ? <span className="lineage-node-label">{highlighted ? `${lineageSnapshots.length} row snapshots` : "Outside lineage"}</span>
          : job.batchesWithErrors > 0
            ? (
              <button
                type="button"
                className="job-status-label job-error-trigger nodrag"
                title="Show full batch error details"
                aria-label={`Show full error details for ${job.name}`}
                onClick={(event) => {
                  event.stopPropagation();
                  onShowErrors(job);
                }}
              ><StatusIcon size={12} aria-hidden="true" />{meta.label}</button>
            )
            : <span className="job-status-label"><StatusIcon size={12} aria-hidden="true" />{meta.label}</span>}
      </div>
      {!lineageActive && (sourceConnection || targetConnection) && (
        <div className="job-node-connections">
          {sourceConnection && (
            <div title={`Source connection: ${sourceConnection.typeName} · ${sourceConnection.id}`}>
              <Database size={12} aria-hidden="true" />
              <span>{sourceConnection.typeName} · {sourceConnection.id}</span>
            </div>
          )}
          {targetConnection && (
            <div title={`Target connection: ${targetConnection.typeName} · ${targetConnection.id}`}>
              <Database size={12} aria-hidden="true" />
              <span>{targetConnection.typeName} · {targetConnection.id}</span>
            </div>
          )}
        </div>
      )}
      {lineageActive ? (
        lineageSnapshots.length > 0 ? (
          <div className="lineage-node-snapshots">
            {stages.map(([stageName, rows]) => (
              <section className="lineage-node-stage" key={stageName}>
                <strong>{friendlyStageName(stageName)} · {number.format(rows.length)} rows</strong>
                {rows.slice(0, 4).map((snapshot) => (
                  <div className="lineage-node-snapshot" key={snapshotKey(snapshot)}>
                    <span>Row {snapshot.recordIndex + 1} · {snapshot.lineageId.slice(-8)}</span>
                    <pre>{prettyJson(snapshot.payloadJson)}</pre>
                  </div>
                ))}
                {rows.length > 4 && (
                  <span className="lineage-node-more">
                    {number.format(rows.length - 4)} more rows in Step Inspection
                  </span>
                )}
              </section>
            ))}
          </div>
        ) : <div className="lineage-node-empty">No captured row on this step.</div>
      ) : status === "Configured" ? (
        <div className="job-node-metrics"><span>Configuration preview</span><span>{job.dependsOn.length} dependencies</span></div>
      ) : (
        <>
          <div className="job-node-metrics">
            <span>{number.format(job.succeededRows)} rows</span>
            <span
              className={rowProgress ? "job-node-progress-cell" : undefined}
              title={rowProgress?.title ?? `Input rows in completed batches${job.inputFinalized ? "" : "; expected total may grow"}`}
            >
              <span>
                {rowProgress?.kind === "batches"
                  ? `${data.inputRowsDisplay} input rows · ${number.format(job.succeededBatches + job.batchesWithErrors)} / ${number.format(rowProgress.max)} batches`
                  : `${data.inputRowsDisplay} input rows`}
              </span>
              {rowProgress && (
                <progress
                  className="job-row-progress"
                  max={rowProgress.max}
                  value={rowProgress.value}
                  aria-label={rowProgress.ariaLabel}
                />
              )}
            </span>
          </div>
          {activityHint && (
            <div className="job-node-activity" title={activityHint}>
              <Clock3 size={12} aria-hidden="true" />
              <span>{activityHint}</span>
            </div>
          )}
          <JobThroughputChart job={job} />
        </>
      )}
      {!lineageActive && specialistInfo.length > 0 && (
        <div className="job-node-specialist-info" aria-label={`${typeLabel} details`}>
          {specialistInfo.map((line) => (
            <div key={line.label} title={line.title ?? `${line.label}: ${line.value}`}>
              <strong>{line.label}</strong>
              <span>{line.value}</span>
            </div>
          ))}
        </div>
      )}
      {(job.failedRows > 0 || job.batchesWithErrors > 0) && (
        <button
          type="button"
          className="job-node-error job-error-trigger nodrag"
          title="Show full batch error details"
          onClick={(event) => {
            event.stopPropagation();
            onShowErrors(job);
          }}
        >
          {job.batchesWithErrors > 0
            ? `${number.format(job.batchesWithErrors)} failed batches`
            : `${number.format(job.failedRows)} failed rows`}
          {job.batchesWithErrors > 0 && job.failedRows > 0 ? ` · ${number.format(job.failedRows)} rows` : ""}
        </button>
      )}
      <Handle type="source" position={Position.Right} />
    </div>
  );
}

export const nodeTypes = { job: JobNode };
