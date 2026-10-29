import { ProcessSample } from "../types";
import { rateNumber } from "../dashboardUtils";

export function ProcessResourceCharts({ samples }: { samples: ProcessSample[] }) {
  const liveSamples = samples.toSorted((left, right) => left.sampledAt - right.sampledAt);
  if (liveSamples.length === 0) {
    return <div className="inline-empty">Waiting for the first process-resource sample.</div>;
  }

  const cpuValues = liveSamples.map((sample, index) => {
    const previous = liveSamples[index - 1];
    if (!previous) return 0;
    const elapsedMilliseconds = Math.max(1, sample.sampledAt - previous.sampledAt);
    const cpuMilliseconds = Math.max(0, sample.cpuTimeMilliseconds - previous.cpuTimeMilliseconds);
    return Math.min(100, cpuMilliseconds / elapsedMilliseconds /
      Math.max(1, sample.logicalProcessorCount) * 100);
  });
  const memoryLimit = liveSamples.some((sample) => sample.memoryLimitBytes !== null)
    ? liveSamples.map((sample) => sample.memoryLimitBytes ?? 0)
    : null;
  const memoryMaximum = Math.max(
    1,
    ...liveSamples.map((sample) => sample.workingSetBytes),
    ...liveSamples.map((sample) => sample.managedHeapBytes),
    ...(memoryLimit ?? []),
  ) * 1.05;
  const chartWidth = 640;
  const chartHeight = 136;
  const left = 34;
  const right = 8;
  const top = 8;
  const bottom = 20;
  const x = (index: number) => liveSamples.length === 1
    ? chartWidth - right
    : left + index * (chartWidth - left - right) / (liveSamples.length - 1);
  const y = (value: number, maximum: number) => chartHeight - bottom -
    Math.max(0, value) / maximum * (chartHeight - top - bottom);
  const polyline = (values: number[], maximum: number) => values
    .map((value, index) => `${x(index)},${y(value, maximum)}`)
    .join(" ");
  const formatMemory = (bytes: number) => {
    const gibibytes = bytes / (1024 ** 3);
    return gibibytes >= 1
      ? `${rateNumber.format(gibibytes)} GiB`
      : `${rateNumber.format(bytes / (1024 ** 2))} MiB`;
  };
  const latest = liveSamples.at(-1)!;
  const cpuPoints = cpuValues.map((value, index) => (
    <circle
      key={index}
      className="resource-chart-point cpu"
      cx={x(index)}
      cy={y(value, 100)}
      r="2.5"
    >
      <title>{`${new Date(liveSamples[index].sampledAt).toLocaleString()}: ${rateNumber.format(value)}% CPU`}</title>
    </circle>
  ));
  const memorySeries = [
    {
      key: "working-set",
      label: "Working set",
      values: liveSamples.map((sample) => sample.workingSetBytes),
      current: formatMemory(latest.workingSetBytes),
    },
    {
      key: "managed-heap",
      label: "Managed heap",
      values: liveSamples.map((sample) => sample.managedHeapBytes),
      current: formatMemory(latest.managedHeapBytes),
    },
    ...(memoryLimit
      ? [{
        key: "memory-limit",
        label: "Runtime-reported memory budget",
        values: memoryLimit,
        current: formatMemory(latest.memoryLimitBytes ?? 0),
      }]
      : []),
  ];

  return (
    <div className="resource-charts">
      <div className="resource-chart">
        <div className="resource-chart-heading"><strong>CPU utilization</strong><span>{rateNumber.format(cpuValues.at(-1) ?? 0)}%</span></div>
        <svg viewBox={`0 0 ${chartWidth} ${chartHeight}`} role="img" aria-label="Process CPU utilization over time">
          {[0, 50, 100].map((value) => (
            <g key={value}>
              <line className="resource-chart-grid" x1={left} y1={y(value, 100)} x2={chartWidth - right} y2={y(value, 100)} />
              <text className="resource-chart-axis" x={left - 5} y={y(value, 100) + 3} textAnchor="end">{value}%</text>
            </g>
          ))}
          <polyline className="resource-chart-line cpu" points={polyline(cpuValues, 100)} />
          {cpuPoints}
        </svg>
      </div>
      <div className="resource-chart">
        <div className="resource-chart-heading">
          <strong>Memory usage</strong>
          <span>{formatMemory(latest.workingSetBytes)} working set</span>
        </div>
        <svg viewBox={`0 0 ${chartWidth} ${chartHeight}`} role="img" aria-label="Process working set and managed heap over time">
          {[0, 0.5, 1].map((fraction) => {
            const value = memoryMaximum * fraction;
            return (
              <g key={fraction}>
                <line className="resource-chart-grid" x1={left} y1={y(value, memoryMaximum)} x2={chartWidth - right} y2={y(value, memoryMaximum)} />
                <text className="resource-chart-axis" x={left - 5} y={y(value, memoryMaximum) + 3} textAnchor="end">{formatMemory(value)}</text>
              </g>
            );
          })}
          {memorySeries.map((series) => (
            <g key={series.key}>
              <polyline className={`resource-chart-line ${series.key}`} points={polyline(series.values, memoryMaximum)} />
              {series.values.map((value, index) => (
                <circle
                  key={index}
                  className={`resource-chart-point ${series.key}`}
                  cx={x(index)}
                  cy={y(value, memoryMaximum)}
                  r="2.5"
                >
                  <title>{`${series.label} · ${new Date(liveSamples[index].sampledAt).toLocaleString()}: ${formatMemory(value)}`}</title>
                </circle>
              ))}
            </g>
          ))}
        </svg>
        <div className="resource-chart-legend">
          {memorySeries.map((series) => (
            <span className={series.key} key={series.key}>
              <i />{series.label}<small>{series.current}</small>
            </span>
          ))}
        </div>
        {memoryLimit && (
          <p className="resource-chart-note">
            The runtime-reported memory budget may reflect host or container limits; it is not a measure of free memory.
          </p>
        )}
      </div>
    </div>
  );
}
