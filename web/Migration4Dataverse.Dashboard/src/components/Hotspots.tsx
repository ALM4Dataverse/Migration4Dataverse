import { BlockingHotspot } from "../types";
import { number, rateNumber } from "../dashboardUtils";

export function Hotspots({ hotspots }: { hotspots: BlockingHotspot[] }) {
  if (!hotspots.length) {
    return <div className="inline-empty">No upstream steps are currently blocked behind active downstream work.</div>;
  }
  const visible = hotspots.slice(0, 7);
  const maximum = Math.max(1, ...visible.map((item) => item.ancestorSeconds));
  return (
    <div className="hotspot-list">
      {visible.map(({ job, blockedAncestors, ancestorSeconds }) => {
        const impact = `${blockedAncestors > 0
          ? `${number.format(blockedAncestors)} upstream steps blocked now`
          : "No upstream steps blocked now"} · ${rateNumber.format(ancestorSeconds)} blocked-step-seconds over 60s`;
        return (
          <div className="hotspot-row" key={job.name} title={`${job.name}: ${impact}`}>
            <div><strong>{job.name}</strong><small>{impact}</small></div>
            <i aria-label={`${impact}; downstream blocker ${job.name}`}>
              <b style={{ width: `${Math.max(4, (ancestorSeconds / maximum) * 100)}%` }} />
            </i>
          </div>
        );
      })}
    </div>
  );
}
