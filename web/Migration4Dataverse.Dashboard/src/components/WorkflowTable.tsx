import { Database } from "lucide-react";
import { activityHintFor, blockingTaskFor, endpointConnection, inputRowsLabel, jobTypeIcon, jobTypeInfo, number, statusFor, statusInfo, stepProgress } from "../dashboardUtils";
import { JobThroughputChart } from "./JobThroughputChart";
import type { DashboardController } from "../hooks/useDashboardController";

export function WorkflowTable({ controller }: { controller: DashboardController }) {
  const { selectedJob, showTaskErrors, configurationOnly, jobs, jobsByName, configurationJobsByName, connectionsById, jobLevels, jobOutputs, visibleJobs, selectJob, jumpToJob } = controller;

  return <div className="job-table-scroll" role="region" aria-label="Migration jobs" tabIndex={0}>
    <table className="job-table">
      <thead>
        <tr>
          <th scope="col">Job</th>
          <th scope="col">Status / activity</th>
          <th scope="col">Output rows</th>
          <th scope="col">Input rows</th>
          <th scope="col">Throughput</th>
          <th scope="col">Inputs</th>
          <th scope="col">Outputs</th>
        </tr>
      </thead>
      <tbody>
        {visibleJobs.toSorted((left, right) =>
          (jobLevels.get(left.name) ?? Number.MAX_SAFE_INTEGER) -
          (jobLevels.get(right.name) ?? Number.MAX_SAFE_INTEGER) ||
          left.name.localeCompare(right.name),
        ).map((job) => {
          const status = statusFor(job, jobs, configurationOnly);
          const meta = statusInfo[status];
          const StatusIcon = meta.icon;
          const jobConfiguration = configurationJobsByName.get(job.name);
          const jobTypeName = jobConfiguration?.type ?? job.type;
          const jobType = jobTypeInfo[jobTypeName.toLowerCase()]?.label ?? jobTypeName;
          const JobIcon = jobTypeIcon(jobTypeName);
          const sourceConnection = endpointConnection(jobConfiguration, connectionsById, "Source");
          const targetConnection = endpointConnection(jobConfiguration, connectionsById, "Target");
          const activity = activityHintFor(job, jobs, configurationOnly);
          const blockingTask = status === "Blocked" ? blockingTaskFor(job, jobs) : null;
          const outputs = (jobOutputs.get(job.name) ?? []).toSorted((left, right) => left.localeCompare(right));
          const renderJobLinks = (relatedJobs: string[]) => relatedJobs.length === 0
            ? "—"
            : (
              <div className="job-table-links">
                {relatedJobs.map((relatedJobName) => {
                  const relatedJob = jobsByName.get(relatedJobName);
                  if (!relatedJob) {
                    return <span key={relatedJobName} className="job-table-link-missing">{relatedJobName}</span>;
                  }
                  const relatedStatus = statusInfo[statusFor(relatedJob, jobs, configurationOnly)];
                  const RelatedStatusIcon = relatedStatus.icon;
                  return (
                    <a
                      key={relatedJobName}
                      className={`job-table-link job-table-status ${relatedStatus.color}`}
                      href={`#job-row-${encodeURIComponent(relatedJobName)}`}
                      onClick={(event) => { event.preventDefault(); event.stopPropagation(); jumpToJob(relatedJobName); }}
                    ><RelatedStatusIcon size={12} aria-hidden="true" />{relatedJobName}</a>
                  );
                })}
              </div>
            );
          const inputRows = inputRowsLabel(job, jobs);
          const inputWork = [
            job.inputRowsInFlight > 0 ? `${number.format(job.inputRowsInFlight)} in progress` : "",
            job.inputRowsPending > 0 ? `${number.format(job.inputRowsPending)} queued` : "",
          ].filter(Boolean).join(" · ");
          const rowProgress = stepProgress(job, jobs);
          return (
            <tr
              key={job.name}
              id={`job-row-${encodeURIComponent(job.name)}`}
              className={selectedJob === job.name ? "selected" : ""}
              aria-selected={selectedJob === job.name}
              onClick={() => selectJob(job.name)}
            >
              <td>
                <button
                  type="button"
                  className="job-table-name"
                  onClick={(event) => { event.stopPropagation(); selectJob(job.name); }}
                >
                  <span className="job-table-identity">
                    <span className="job-icon" aria-hidden="true"><JobIcon size={14} /></span>
                    <span className="job-table-identity-text">
                      <strong className="job-table-job-name">{job.name}</strong>
                      <span>{jobType}</span>
                    </span>
                  </span>
                  {(sourceConnection || targetConnection) && (
                    <span className="job-table-connections">
                      {sourceConnection && (
                        <span title={`Source connection: ${sourceConnection.typeName} · ${sourceConnection.id}`}>
                          <Database size={12} aria-hidden="true" />
                          <span>{sourceConnection.typeName} · {sourceConnection.id}</span>
                        </span>
                      )}
                      {targetConnection && (
                        <span title={`Target connection: ${targetConnection.typeName} · ${targetConnection.id}`}>
                          <Database size={12} aria-hidden="true" />
                          <span>{targetConnection.typeName} · {targetConnection.id}</span>
                        </span>
                      )}
                    </span>
                  )}
                </button>
              </td>
              <td className="job-table-status-cell">
                {job.batchesWithErrors > 0
                  ? (
                    <button
                      type="button"
                      className={`job-table-status ${meta.color} job-error-trigger`}
                      title="Show full batch error details"
                      onClick={() => void showTaskErrors(job)}
                    ><StatusIcon size={12} />{meta.label}</button>
                  )
                  : <span className={`job-table-status ${meta.color}`}><StatusIcon size={12} />{meta.label}</span>}
                {blockingTask
                  ? <small className="job-table-activity" title={`Furthest blocking task: ${blockingTask.name}`}>Blocked by: {blockingTask.name}</small>
                  : activity && <small className="job-table-activity" title={activity}>{activity}</small>}
              </td>
              <td>
                <span>{number.format(job.succeededRows)}</span>
                {job.failedRows > 0 && <small className="job-table-secondary">{number.format(job.failedRows)} failed</small>}
              </td>
              <td title={`${inputRows} input rows${rowProgress?.title ? ` · ${rowProgress.title}` : ""}${inputWork ? ` · ${inputWork}` : ""}`}>
                <span>{inputRows}</span>
                {rowProgress?.kind === "batches" && (
                  <small className="job-table-secondary">
                    {number.format(job.succeededBatches + job.batchesWithErrors)} / {number.format(rowProgress.max)} batches materialized
                  </small>
                )}
                {rowProgress && (
                  <progress
                    className="job-row-progress"
                    max={rowProgress.max}
                    value={rowProgress.value}
                    aria-label={rowProgress.ariaLabel}
                  />
                )}
                {inputWork && <small className="job-table-secondary">{inputWork}</small>}
              </td>
              <td className="job-table-throughput-cell">
                <JobThroughputChart job={job} />
              </td>
              <td>{renderJobLinks(job.dependsOn.toSorted((left, right) => left.localeCompare(right)))}</td>
              <td>{renderJobLinks(outputs)}</td>
            </tr>
          );
        })}
      </tbody>
    </table>
  </div>;
}
