import { DashboardJob } from "../types";
import { formatDuration, formatMilliseconds } from "../dashboardUtils";

export function Timeline({ jobs, onSelect }: { jobs: DashboardJob[]; onSelect: (name: string) => void }) {
  const now = Date.now();
  const active = jobs
    .map((job) => {
      const periods = job.activityPeriods?.length
        ? job.activityPeriods.map((period) => ({
          state: period.state.toLowerCase(),
          start: Date.parse(period.startedUtc),
          end: period.endedUtc ? Date.parse(period.endedUtc) : now,
        }))
        : job.firstBatchUtc && job.lastBatchUtc
          ? [{
            state: "observed",
            start: Date.parse(job.firstBatchUtc),
            end: Date.parse(job.lastBatchUtc),
          }]
          : [];
      return { job, periods };
    })
    .filter((item) => item.periods.length > 0);
  if (!active.length) return <div className="inline-empty">Batch activity will appear here once work is materialized.</div>;
  const first = Math.min(...active.flatMap(({ periods }) => periods.map((period) => period.start)));
  const last = Math.max(...active.flatMap(({ periods }) => periods.map((period) => period.end)));
  const duration = Math.max(1, last - first);
  return (
    <>
      <div className="timeline-legend">
        <span><i className="working" />Working</span>
        <span><i className="waiting" />Waiting / blocked</span>
        {active.some(({ periods }) => periods.some((period) => period.state === "observed")) && (
          <span><i className="observed" />Batch window only (older run)</span>
        )}
      </div>
      <div className="timeline-list">
        {active.toSorted((a, b) => b.job.activeMilliseconds - a.job.activeMilliseconds).slice(0, 14).map(({ job, periods }) => {
          const work = periods
            .filter((period) => period.state === "working")
            .reduce((total, period) => total + Math.max(0, period.end - period.start), 0);
          const waiting = periods
            .filter((period) => period.state === "waiting")
            .reduce((total, period) => total + Math.max(0, period.end - period.start), 0);
          const detail = periods.some((period) => period.state === "observed")
            ? `Observed window ${formatDuration(Math.floor((periods[0].end - periods[0].start) / 1000))}`
            : `Work ${formatMilliseconds(work)} · wait ${formatMilliseconds(waiting)}`;
          return (
            <button className="timeline-row" key={job.name} type="button" onClick={() => onSelect(job.name)}>
              <span className="timeline-name">{job.name}</span>
              <span className="timeline-track" aria-label={detail}>
                {periods.map((period, index) => {
                  const left = Math.max(0, Math.min(100, ((period.start - first) / duration) * 100));
                  const width = Math.max(0.4, Math.min(100 - left, ((period.end - period.start) / duration) * 100));
                  const title = period.state === "observed"
                    ? "Observed batch window; detailed activity history is unavailable"
                    : `${period.state === "working" ? "Working" : "Waiting / blocked"}: ${new Date(period.start).toLocaleTimeString()} – ${new Date(period.end).toLocaleTimeString()}`;
                  return <i key={index} className={`timeline-segment ${period.state}`} title={title} style={{ left: `${left}%`, width: `${width}%` }} />;
                })}
              </span>
              <span className="timeline-duration">{detail}</span>
            </button>
          );
        })}
      </div>
    </>
  );
}
