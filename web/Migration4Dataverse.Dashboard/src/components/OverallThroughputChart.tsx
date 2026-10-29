import { OverallThroughput, ThroughputSeries } from "../types";
import { formatRowsPerMinute } from "../dashboardUtils";

export function OverallThroughputChart({ throughput }: { throughput: OverallThroughput | null }) {
  const width = 480;
  const height = 44;
  const outputPoints = throughput?.output.points ?? Array<number>(24).fill(0);
  const maximum = Math.max(1, ...outputPoints);
  const pointCoordinates = (rate: number, index: number, pointCount: number) => ({
    x: index * width / Math.max(1, pointCount - 1),
    y: height - 2 - (rate / maximum) * (height - 5),
  });
  const toPolyline = (points: number[]) => points
    .map((rate, index) => {
      const { x, y } = pointCoordinates(rate, index, points.length);
      return `${x},${y}`;
    })
    .join(" ");
  const pointTitle = (seriesName: string, rate: number, index: number) => {
    if (!throughput) return `${seriesName}: no throughput data`;
    const bucketDuration = (Date.parse(throughput.endedUtc) - Date.parse(throughput.startedUtc)) /
      Math.max(1, outputPoints.length);
    const start = Date.parse(throughput.startedUtc) + index * bucketDuration;
    const end = start + bucketDuration;
    return `${seriesName}: ${new Date(start).toLocaleString()} – ${new Date(end).toLocaleString()}: ${formatRowsPerMinute(rate)} rows/min`;
  };
  const pointMarkers = (seriesName: string, seriesClass: string, points: number[]) => (
    points.map((rate, index) => {
      const { x, y } = pointCoordinates(rate, index, points.length);
      return (
        <circle
          key={`${seriesClass}-${index}`}
          className={`overall-throughput-point ${seriesClass}`}
          cx={x}
          cy={y}
          r="3"
        >
          <title>{pointTitle(seriesName, rate, index)}</title>
        </circle>
      );
    })
  );
  const rateSummary = (series: ThroughputSeries | undefined) => series
    ? `Current ${formatRowsPerMinute(series.current)} · avg ${formatRowsPerMinute(series.average)} rows/min`
    : "Current — · avg — rows/min";

  return (
    <div className="overall-throughput" aria-label="Overall migration throughput">
      <svg viewBox={`0 0 ${width} ${height}`} role="img" aria-label="Target output rows per minute over the run">
        <line className="overall-throughput-grid" x1="0" y1="2" x2={width} y2="2" />
        <line className="overall-throughput-grid" x1="0" y1={height / 2} x2={width} y2={height / 2} />
        <line className="overall-throughput-grid" x1="0" y1={height - 1} x2={width} y2={height - 1} />
        <polyline className="overall-throughput-output" points={toPolyline(outputPoints)} />
        {throughput && pointMarkers("Output (target)", "output", outputPoints)}
      </svg>
      <div className="overall-throughput-legend">
        <span className="overall-throughput-label output"><i />Output (target)<small>{rateSummary(throughput?.output)}</small></span>
      </div>
    </div>
  );
}
