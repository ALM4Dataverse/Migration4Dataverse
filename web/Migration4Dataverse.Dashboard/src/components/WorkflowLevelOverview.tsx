import { ChevronDown } from "lucide-react";
import { statusFor } from "../dashboardUtils";
import type { DashboardController } from "../hooks/useDashboardController";

export function WorkflowLevelOverview({ controller }: { controller: DashboardController }) {
  const { setFocusedLevel, configurationOnly, jobs, jobLevels, visibleJobs } = controller;

  return <div className="level-grid">
    {[...new Set(visibleJobs.map((job) => jobLevels.get(job.name) ?? 0))].sort((a, b) => a - b).map((level) => {
      const items = visibleJobs.filter((job) => jobLevels.get(job.name) === level);
      const failed = items.filter((job) => statusFor(job, jobs, configurationOnly) === "Failed").length;
      const active = items.filter((job) => statusFor(job, jobs, configurationOnly) === "Running").length;
      return (
        <button className="level-card" key={level} onClick={() => setFocusedLevel(level)}>
          <span className="level-index">LEVEL {level + 1}</span>
          <strong>{items.length} steps</strong>
          <span>{configurationOnly ? "Configuration only" : `${active} running · ${failed} failed`}</span>
          <span className="level-drill">Open focused graph <ChevronDown size={13} /></span>
        </button>
      );
    })}
  </div>;
}
