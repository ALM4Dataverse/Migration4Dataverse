import { RunSummary, SchedulerSample } from "../types";
import { number } from "../dashboardUtils";

export function SchedulerCapacityChart({
  run,
  samples,
  blockingStepName,
}: {
  run: RunSummary;
  samples: SchedulerSample[];
  blockingStepName: string | null;
}) {
  const limits = run.configuration?.schedulerLimits;
  if (!limits) {
    return <div className="inline-empty">Scheduler limits were not captured for this run.</div>;
  }
  const now = Date.now();
  const liveSamples = samples
    .filter((sample) => sample.runId === run.runId && sample.sampledAt >= now - 3 * 60_000)
    .toSorted((left, right) => left.sampledAt - right.sampledAt);
  const latest = liveSamples.at(-1);
  if (!latest) {
    return <div className="inline-empty">Waiting for the first live scheduler sample.</div>;
  }
  const busiestStep = Object.entries(latest.outstandingByStep)
    .toSorted((left, right) => right[1] - left[1])[0];
  const stepName = blockingStepName ?? (busiestStep?.[1] > 0 ? busiestStep[0] : null);
  const chartWidth = 640;
  const chartHeight = 144;
  const left = 30;
  const right = 8;
  const top = 8;
  const bottom = 22;
  const maximum = Math.max(
    1,
    limits.maxQueuedBatches,
    limits.maxDegreeOfParallelism,
    limits.maxQueuedBatchesPerStep,
    ...liveSamples.map((sample) => sample.outstandingBatches),
    ...liveSamples.map((sample) => sample.inFlightBatches),
    ...(stepName ? liveSamples.map((sample) => sample.outstandingByStep[stepName] ?? 0) : []),
  );
  const x = (index: number) => liveSamples.length === 1
    ? chartWidth - right
    : left + index * (chartWidth - left - right) / (liveSamples.length - 1);
  const y = (value: number) => chartHeight - bottom -
    Math.max(0, value) / maximum * (chartHeight - top - bottom);
  const formatPolyline = (values: number[]) => values
    .map((value, index) => `${x(index)},${y(value)}`)
    .join(" ");
  const series = [
    {
      key: "outstanding",
      label: "Outstanding batches",
      values: liveSamples.map((sample) => sample.outstandingBatches),
      limit: limits.maxQueuedBatches,
    },
    {
      key: "workers",
      label: "In-flight batches",
      values: liveSamples.map((sample) => sample.inFlightBatches),
      limit: limits.maxDegreeOfParallelism,
    },
    ...(stepName ? [{
      key: "step",
      label: `${stepName} outstanding`,
      values: liveSamples.map((sample) => sample.outstandingByStep[stepName] ?? 0),
      limit: limits.maxQueuedBatchesPerStep,
    }] : []),
  ];

  return (
    <div className="scheduler-capacity-chart">
      <svg
        viewBox={`0 0 ${chartWidth} ${chartHeight}`}
        role="img"
        aria-label="Persisted queue and in-flight batch counts compared with scheduler limits"
      >
        {[0, 0.5, 1].map((fraction) => {
          const value = Math.round(maximum * fraction);
          const gridY = y(value);
          return (
            <g key={fraction}>
              <line className="scheduler-capacity-grid" x1={left} y1={gridY} x2={chartWidth - right} y2={gridY} />
              <text className="scheduler-capacity-axis" x={left - 5} y={gridY + 3} textAnchor="end">{value}</text>
            </g>
          );
        })}
        {series.map((item) => (
          <g key={item.key}>
            <line
              className={`scheduler-capacity-limit ${item.key}`}
              x1={left}
              y1={y(item.limit)}
              x2={chartWidth - right}
              y2={y(item.limit)}
            >
              <title>{`${item.label} limit: ${number.format(item.limit)} batches`}</title>
            </line>
            <polyline className={`scheduler-capacity-line ${item.key}`} points={formatPolyline(item.values)} />
            {item.values.map((value, index) => (
              <circle
                className={`scheduler-capacity-point ${item.key}`}
                key={`${item.key}-${index}`}
                cx={x(index)}
                cy={y(value)}
                r="2.7"
              >
                <title>{`${item.label}: ${number.format(value)} batches at ${new Date(liveSamples[index].sampledAt).toLocaleString()}`}</title>
              </circle>
            ))}
          </g>
        ))}
        <text className="scheduler-capacity-axis" x={left} y={chartHeight - 4}>
          {new Date(liveSamples[0].sampledAt).toLocaleTimeString()}
        </text>
        <text className="scheduler-capacity-axis" x={chartWidth - right} y={chartHeight - 4} textAnchor="end">
          {new Date(latest.sampledAt).toLocaleTimeString()}
        </text>
      </svg>
      <div className="scheduler-capacity-legend">
        {series.map((item) => (
          <span className={`scheduler-capacity-label ${item.key}`} key={item.key}>
            <i />
            {item.label}
            <small>Current {number.format(item.values.at(-1) ?? 0)} / limit {number.format(item.limit)}</small>
          </span>
        ))}
      </div>
      <small className="scheduler-capacity-note">
        Samples are collected every three seconds while this dashboard is open. Counts include persisted pending and in-flight batches, not producer reservations.
      </small>
    </div>
  );
}
