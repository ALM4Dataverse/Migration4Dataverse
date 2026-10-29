import { DashboardJob } from "../types";
import { formatElapsed, formatRowsPerMinute } from "../dashboardUtils";

export function JobThroughputChart({ job }: { job: DashboardJob }) {
  const samples = job.rowsPerSecondSamples ?? [];
  const width = 200;
  const height = 24;
  const maximumRate = Math.max(0, ...samples.map((sample) => sample.rowsPerSecond));
  const coordinates = samples.map((sample, index) => ({
    sample,
    x: samples.length === 1 ? width / 2 : index * width / (samples.length - 1),
    y: maximumRate === 0 ? height / 2 : height - (sample.rowsPerSecond / maximumRate) * (height - 2) - 1,
  }));
  const lineCoordinates = samples.length === 1
    ? `0,${coordinates[0].y} ${width},${coordinates[0].y}`
    : coordinates.map(({ x, y }) => `${x},${y}`).join(" ");
  const timeRange = samples.length > 0
    ? `${new Date(samples[0].periodStartedUtc).toLocaleString()} – ${new Date(samples[samples.length - 1].periodEndedUtc).toLocaleString()}`
    : null;
  const label = timeRange
    ? `${job.name} rows per minute from ${timeRange}`
    : `${job.name} average throughput in rows per minute`;

  return (
    <div className="job-node-throughput" title={label}>
      <div className="job-node-throughput-header">
        <span>
          {job.rowsPerSecond === null ? "Avg — rows/min" : `Avg ${formatRowsPerMinute(job.rowsPerSecond)} rows/min`}
        </span>
        <span>
          {timeRange
            ? `${formatElapsed(samples[0].periodStartedUtc, samples[samples.length - 1].periodEndedUtc)} · max ${formatRowsPerMinute(maximumRate)}`
            : "No batch samples"}
        </span>
      </div>
      {samples.length > 0 && (
        <svg viewBox={`0 0 ${width} ${height}`} role="img" aria-label={label} preserveAspectRatio="none">
          <title>{label}</title>
          <line className="job-node-throughput-baseline" x1="0" y1={height - 1} x2={width} y2={height - 1} />
          <polyline className="job-node-throughput-line" points={lineCoordinates} />
          {coordinates.map(({ sample, x, y }) => (
            <circle
              key={`${sample.periodStartedUtc}-${sample.periodEndedUtc}`}
              className="job-node-throughput-point"
              cx={x}
              cy={y}
              r="3"
            >
              <title>
                {`${new Date(sample.periodStartedUtc).toLocaleString()} – ${new Date(sample.periodEndedUtc).toLocaleString()}: ${formatRowsPerMinute(sample.rowsPerSecond)} rows/min`}
              </title>
            </circle>
          ))}
        </svg>
      )}
    </div>
  );
}
