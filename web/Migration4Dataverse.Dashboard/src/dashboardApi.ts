import { ApiConfiguration, ApiDebugSnapshot, ApiDebugSnapshotPage, ApiRunSummary, DebugSnapshot, DebugSnapshotPage, MigrationConfiguration, RunSummary, SnapshotGridSortState } from "./types";
import { getStringProperty, readSourceDependencies } from "./dashboardUtils";

export const api = async <T,>(path: string, signal?: AbortSignal): Promise<T> => {
  const response = await fetch(path, { cache: "no-store", signal });
  if (!response.ok) {
    const body = await response.json().catch(() => null);
    throw new Error(body?.message ?? `${response.status} ${response.statusText}`);
  }
  return response.json() as Promise<T>;
};

export function normalizeConfiguration(configuration: ApiConfiguration): MigrationConfiguration {
  return {
    jobs: configuration.steps.map((step) => {
      const sourceDependencies = readSourceDependencies(step.properties);
      const sourceConnectionId = step.sourceConnectionId ??
        getStringProperty(step.properties, "SourceConnectionId");
      const targetConnectionId = step.targetConnectionId ??
        getStringProperty(step.properties, "TargetConnectionId");
      return {
        name: step.id,
        type: step.type,
        dependsOn: [...new Set([...step.inputs, ...sourceDependencies.all])],
        completionDependencies: sourceDependencies.completion,
        hasSource: step.hasSource || Boolean(sourceConnectionId),
        sourceConnectionId,
        sourceConnectionType: step.sourceConnectionType,
        targetConnectionId,
        targetConnectionType: step.targetConnectionType,
        properties: step.properties ?? {},
      };
    }),
    connections: configuration.connections,
    schedulerLimits: configuration.schedulerLimits,
    isWhatIf: configuration.isWhatIf,
  };
}

export function normalizeRun(run: ApiRunSummary): RunSummary {
  return {
    ...run,
    jobName: run.runName,
    jobs: run.steps.map((step) => ({
      ...step,
      name: step.id,
      hasSource: step.isSource,
      type: step.type,
      dependsOn: [...step.inputs, ...step.completionDependencies],
    })),
    configuration: run.configuration ? normalizeConfiguration(run.configuration) : null,
  };
}

export function normalizeSnapshot(snapshot: ApiDebugSnapshot): DebugSnapshot {
  const { stepId, ...details } = snapshot;
  return { ...details, jobName: stepId };
}

export function normalizeSnapshotPage(page: ApiDebugSnapshotPage): DebugSnapshotPage {
  return { ...page, items: page.items.map(normalizeSnapshot) };
}

export function createSnapshotQuery({
  stepId,
  search,
  limit,
  offset,
  stageName,
  sortState,
  filters,
}: {
  stepId: string;
  search: string;
  limit: number;
  offset: number;
  stageName: string;
  sortState: SnapshotGridSortState;
  filters: Record<string, string>;
}): URLSearchParams {
  const query = new URLSearchParams({
    stepId,
    search,
    limit: String(limit),
    offset: String(offset),
    sortColumn: typeof sortState.sortColumn === "string" ? sortState.sortColumn : "record",
    sortDirection: sortState.sortDirection,
    filters: JSON.stringify(Object.fromEntries(
      Object.entries(filters).filter(([, value]) => value.trim().length > 0),
    )),
  });
  if (stageName) query.set("stageName", stageName);
  return query;
}
