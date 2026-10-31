import { Spinner as FluentSpinner } from "@fluentui/react-components";
import { Layers3 } from "lucide-react";
import { Text } from "./Text";
import { MigrationSummaryPanel } from "./MigrationSummaryPanel";
import { WorkflowPanel } from "./WorkflowPanel";
import { StepInspectorDrawer } from "./StepInspectorDrawer";
import type { DashboardController } from "../hooks/useDashboardController";

export function DashboardContent({ controller }: { controller: DashboardController }) {
  const { schedulerSamples, connectionSamples, processSamples, summaryExpanded, setSummaryExpanded, loading, configurationOnly, configuration, jobs, selectJob, viewRun } = controller;

  return (loading && !viewRun && !configurationOnly ? (
    <section className="empty-state">
      <FluentSpinner />
      <Text variant="large">Loading migration runs...</Text>
    </section>
  ) : !viewRun && !configurationOnly ? (
    <section className="empty-state">
      <Layers3 size={44} />
      <Text variant="xxLarge">No migration runs found</Text>
      <Text>Start an Invoke-M4dMigration run using this database, or run the monitoring demo.</Text>
    </section>
  ) : (
    <main className="dashboard-main">
      <WorkflowPanel controller={controller} />

      {viewRun && (
        <MigrationSummaryPanel
          run={viewRun}
          jobs={jobs}
          samples={schedulerSamples}
          connectionSamples={connectionSamples}
          processSamples={processSamples}
          expanded={summaryExpanded}
          onToggle={() => setSummaryExpanded((expanded) => !expanded)}
          onSelectJob={selectJob}
        />
      )}

      {(viewRun || configuration) && (
        <StepInspectorDrawer controller={controller} />
      )}

    </main>
  ));
}
