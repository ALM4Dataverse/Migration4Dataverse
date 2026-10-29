import { DataverseConnectionSample, RunSummary } from "../types";
import { number, rateNumber } from "../dashboardUtils";

const ROLLING_WINDOW_MILLISECONDS = 30_000;

export function ConnectionThroughputChart({
  run,
  samples,
}: {
  run: RunSummary;
  samples: DataverseConnectionSample[];
}) {
  const now = Date.now();
  const liveSamples = samples
    .filter((sample) => sample.runId === run.runId && sample.sampledAt >= now - 3 * 60_000)
    .toSorted((left, right) => left.sampledAt - right.sampledAt);
  const latest = liveSamples.at(-1);
  if (!latest) {
    return <div className="inline-empty">No pooled Dataverse connection activity has been captured for this run.</div>;
  }

  const memberByKey = (sample: DataverseConnectionSample, key: string) => {
    for (const pool of sample.pools) {
      for (const member of pool.members) {
        if (`${encodeURIComponent(pool.connectionId)}:${member.memberNumber}` === key) {
          return member;
        }
      }
    }
    return undefined;
  };
  const rollingAverageRate = (sampleIndex: number, key: string) => {
    const sample = liveSamples[sampleIndex];
    const currentMember = memberByKey(sample, key);
    if (!currentMember) return 0;

    const windowStart = sample.sampledAt - ROLLING_WINDOW_MILLISECONDS;
    let baselineSample: DataverseConnectionSample | undefined;
    let baselineMember: ReturnType<typeof memberByKey>;
    for (let index = sampleIndex - 1; index >= 0; index--) {
      const candidate = liveSamples[index];
      if (candidate.sampledAt > windowStart) continue;
      const candidateMember = memberByKey(candidate, key);
      if (candidateMember) {
        baselineSample = candidate;
        baselineMember = candidateMember;
        break;
      }
    }

    if (!baselineSample) {
      for (let index = 0; index < sampleIndex; index++) {
        const candidate = liveSamples[index];
        if (candidate.sampledAt < windowStart) continue;
        const candidateMember = memberByKey(candidate, key);
        if (candidateMember) {
          baselineSample = candidate;
          baselineMember = candidateMember;
          break;
        }
      }
    }

    if (!baselineSample || !baselineMember) return 0;
    const elapsedSeconds = (sample.sampledAt - baselineSample.sampledAt) / 1000;
    if (elapsedSeconds <= 0) return 0;

    return Math.max(
      0,
      (currentMember.totalRequestUnits - baselineMember.totalRequestUnits) / elapsedSeconds,
    );
  };
  const memberLabels = new Map<string, string>();
  for (const sample of liveSamples) {
    for (const pool of sample.pools) {
      for (const member of pool.members) {
        const key = `${encodeURIComponent(pool.connectionId)}:${member.memberNumber}`;
        memberLabels.set(key, `${pool.connectionId} · connection ${member.memberNumber}`);
      }
    }
  }

  const colors = ["#0078d4", "#ca5010", "#8764b8", "#498205", "#c239b3", "#038387", "#986f0b", "#4f6bed"];
  const chartWidth = 640;
  const chartHeight = 144;
  const left = 42;
  const right = 8;
  const top = 8;
  const bottom = 22;
  const series = [...memberLabels.entries()]
    .toSorted(([leftKey], [rightKey]) => leftKey.localeCompare(rightKey))
    .map(([key, label], index) => {
      const rates = liveSamples.map((_, sampleIndex) => rollingAverageRate(sampleIndex, key));
      const latestMember = memberByKey(latest, key);
      return {
        key,
        label,
        color: colors[index % colors.length],
        rates,
        totalRequests: latestMember?.totalRequests ?? 0,
        activeRequests: latestMember?.activeRequests ?? 0,
      };
    });
  const maximum = Math.max(1, ...series.flatMap((item) => item.rates));
  const x = (index: number) => liveSamples.length === 1
    ? chartWidth - right
    : left + index * (chartWidth - left - right) / (liveSamples.length - 1);
  const y = (value: number) => chartHeight - bottom -
    Math.max(0, value) / maximum * (chartHeight - top - bottom);
  const formatPolyline = (values: number[]) => values
    .map((value, index) => `${x(index)},${y(value)}`)
    .join(" ");

  return (
    <div className="connection-throughput-chart">
      <svg
        viewBox={`0 0 ${chartWidth} ${chartHeight}`}
        role="img"
        aria-label="Thirty-second average routed Dataverse request units per second by pooled connection"
      >
        {[0, 0.5, 1].map((fraction) => {
          const value = maximum * fraction;
          const gridY = y(value);
          return (
            <g key={fraction}>
              <line className="connection-throughput-grid" x1={left} y1={gridY} x2={chartWidth - right} y2={gridY} />
              <text className="connection-throughput-axis" x={left - 6} y={gridY + 3} textAnchor="end">
                {rateNumber.format(value)}
              </text>
            </g>
          );
        })}
        {series.map((item) => (
          <g key={item.key}>
            <polyline
              className="connection-throughput-line"
              points={formatPolyline(item.rates)}
              style={{ stroke: item.color }}
            />
            {item.rates.map((value, index) => (
              <circle
                className="connection-throughput-point"
                key={`${item.key}-${index}`}
                cx={x(index)}
                cy={y(value)}
                r="2.7"
                style={{ fill: item.color }}
              >
                <title>
                  {`${item.label}: ${rateNumber.format(value)} 30-second average request units/s at ${new Date(liveSamples[index].sampledAt).toLocaleString()}`}
                </title>
              </circle>
            ))}
          </g>
        ))}
        <text className="connection-throughput-axis" x={left} y={chartHeight - 4}>
          {new Date(liveSamples[0].sampledAt).toLocaleTimeString()}
        </text>
        <text className="connection-throughput-axis" x={chartWidth - right} y={chartHeight - 4} textAnchor="end">
          {new Date(latest.sampledAt).toLocaleTimeString()}
        </text>
      </svg>
      <div className="connection-throughput-legend">
        {series.map((item) => (
          <span className="connection-throughput-label" key={item.key}>
            <i style={{ backgroundColor: item.color }} />
            {item.label}
            <small>
              30s avg {rateNumber.format(item.rates.at(-1) ?? 0)} units/s ·
              {number.format(item.totalRequests)} routed requests ·
              {item.activeRequests} active
            </small>
          </span>
        ))}
      </div>
      <small className="connection-throughput-note">
        Request units count each routed operation once and bulk target messages by record. Throughput is averaged over the trailing 30 seconds (warming up as samples accumulate). Samples are collected every three seconds while this dashboard is open and are not persisted.
      </small>
    </div>
  );
}
