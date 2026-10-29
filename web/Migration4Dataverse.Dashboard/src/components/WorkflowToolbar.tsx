import { Combobox, Option } from "@fluentui/react-components";
import { Columns3, Layers3, Search, X } from "lucide-react";
import type { DashboardController } from "../hooks/useDashboardController";

export function WorkflowToolbar({ controller }: { controller: DashboardController }) {
  const { selectedJob, jobViewMode, setJobViewMode, hideCompletedJobs, setHideCompletedJobs, stepSearchOpen, setStepSearchOpen, stepSearchValue, setStepSearchValue, focusedLevel, setFocusedLevel, jobLevels, visibleJobs, selectJob } = controller;

  return <div className="graph-search-control">
    <div className="job-view-toggle" role="group" aria-label="Job view">
      <button
        type="button"
        className={jobViewMode === "graph" ? "active" : ""}
        aria-label="Graph view"
        aria-pressed={jobViewMode === "graph"}
        onClick={() => setJobViewMode("graph")}
      ><Layers3 size={15} /><span>Graph</span></button>
      <button
        type="button"
        className={jobViewMode === "table" ? "active" : ""}
        aria-label="Table view"
        aria-pressed={jobViewMode === "table"}
        onClick={() => setJobViewMode("table")}
      ><Columns3 size={15} /><span>Table</span></button>
    </div>
    <label className="hide-completed-toggle" title="Hide completed jobs in both views">
      <input
        type="checkbox"
        aria-label="Hide completed jobs"
        checked={hideCompletedJobs}
        onChange={(event) => {
          setHideCompletedJobs(event.currentTarget.checked);
          setFocusedLevel(null);
        }}
      />
      <span>Hide completed</span>
    </label>
    {stepSearchOpen && (
      <Combobox
        id="step-search-combobox"
        className="step-search-combobox"
        aria-label="Find a pipeline step"
        placeholder="Find a step by name"
        freeform={false}
        open={stepSearchOpen}
        value={stepSearchValue}
        onOpenChange={(_, data) => { if (!data.open) setStepSearchOpen(false); }}
        onChange={(event) => setStepSearchValue(event.currentTarget.value)}
        onOptionSelect={(_, data) => {
          const stepName = data.optionValue;
          if (typeof stepName !== "string" || !stepName) return;
          setFocusedLevel(visibleJobs.length > 40 ? jobLevels.get(stepName) ?? null : null);
          setStepSearchValue("");
          selectJob(stepName);
          setStepSearchOpen(false);
        }}
      >
        {visibleJobs
          .filter((job) => job.name.toLocaleLowerCase().includes(stepSearchValue.trim().toLocaleLowerCase()))
          .toSorted((a, b) => a.name.localeCompare(b.name))
          .map((job) => <Option key={job.name} value={job.name}>{job.name}</Option>)}
      </Combobox>
    )}
    {focusedLevel !== null && (
      <button className="action-button graph-all-levels" type="button" onClick={() => setFocusedLevel(null)}>All levels</button>
    )}
    {!stepSearchOpen && selectedJob && <span className="active-step-selection" title={selectedJob}>{selectedJob}</span>}
    {selectedJob && (
      <button
        className="icon-button graph-search-clear"
        type="button"
        title="Clear selected step"
        aria-label="Clear selected step"
        onClick={() => { selectJob(""); setStepSearchValue(""); setFocusedLevel(null); }}
      ><X size={16} /></button>
    )}
    <button
      className="icon-button graph-search-button"
      type="button"
      title={stepSearchOpen ? "Close step search" : "Find a step"}
      aria-label={stepSearchOpen ? "Close step search" : "Find a step"}
      aria-expanded={stepSearchOpen}
      aria-controls="step-search-combobox"
      onClick={() => {
        if (stepSearchOpen) {
          setStepSearchOpen(false);
        } else {
          setStepSearchValue("");
          setStepSearchOpen(true);
        }
      }}
    >{stepSearchOpen ? <X size={18} /> : <Search size={18} />}</button>
  </div>;
}
