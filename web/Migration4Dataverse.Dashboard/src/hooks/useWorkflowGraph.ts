import { getViewportForBounds, type Edge, type Node, type ReactFlowInstance } from "@xyflow/react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { DashboardJob, DebugSnapshot, JobNodeData, MigrationConfiguration } from "../types";
import { activityHintFor, asRecord, endpointConnection, getPropertyValue, getStringProperty, inputRowsLabel, specialistInfoFor, statusFor, stepProgress } from "../dashboardUtils";

type WorkflowGraphOptions = {
  jobs: DashboardJob[];
  configuration: MigrationConfiguration | null;
  configurationOnly: boolean;
  hideCompletedJobs: boolean;
  setHideCompletedJobs: (hide: boolean) => void;
  focusedLevel: number | null;
  setFocusedLevel: (level: number | null) => void;
  selectedJob: string;
  trace: DebugSnapshot[];
  runId: string;
  onSelectJob: (jobName: string) => void;
  onShowTaskErrors: (job: DashboardJob) => Promise<void>;
};

export function useWorkflowGraph({
  jobs, configuration, configurationOnly, hideCompletedJobs, setHideCompletedJobs, focusedLevel,
  setFocusedLevel, selectedJob, trace, runId, onSelectJob, onShowTaskErrors,
}: WorkflowGraphOptions) {
  const selectJob = onSelectJob;
  const showTaskErrors = onShowTaskErrors;
  const [flowInstance, setFlowInstance] = useState<ReactFlowInstance | null>(null);

  const [graphLayout, setGraphLayout] = useState<{
    key: string;
    positions: Map<string, { x: number; y: number }>;
  } | null>(null);

  const [graphLayoutError, setGraphLayoutError] = useState("");

  const flowCanvasRef = useRef<HTMLDivElement | null>(null);

  const lastAutoFit = useRef<{ instance: ReactFlowInstance; key: string } | null>(null);

  const lastCenteredSelection = useRef<{ instance: ReactFlowInstance; key: string } | null>(null);

  const jobsByName = useMemo(() => new Map(jobs.map((job) => [job.name, job])), [jobs]);

  const configurationJobsByName = useMemo(
    () => new Map((configuration?.jobs ?? []).map((job) => [job.name, job])),
    [configuration],
  );

  const connectionsById = useMemo(
    () => new Map((configuration?.connections ?? []).map((connection) => [connection.id, connection])),
    [configuration],
  );

  const jobLevels = useMemo(() => {
    const levels = new Map<string, number>();
    const getLevel = (job: DashboardJob, visiting = new Set<string>()): number => {
      const known = levels.get(job.name);
      if (known !== undefined) return known;
      if (visiting.has(job.name)) return 0;
      const nextVisiting = new Set(visiting).add(job.name);
      const parentLevels = job.dependsOn
        .map((name) => jobs.find((candidate) => candidate.name === name))
        .filter((parent): parent is DashboardJob => parent !== undefined)
        .map((parent) => getLevel(parent, nextVisiting));
      const level = parentLevels.length ? Math.max(...parentLevels) + 1 : 0;
      levels.set(job.name, level);
      return level;
    };
    jobs.forEach((job) => getLevel(job));
    return levels;
  }, [jobs]);

  const jobOutputs = useMemo(() => {
    const outputs = new Map<string, string[]>();
    jobs.forEach((job) => outputs.set(job.name, []));
    jobs.forEach((job) => {
      job.dependsOn.forEach((parentName) => outputs.get(parentName)?.push(job.name));
    });
    return outputs;
  }, [jobs]);

  const visibleJobs = useMemo(
    () => hideCompletedJobs
      ? jobs.filter((job) => statusFor(job, jobs, configurationOnly) !== "Completed")
      : jobs,
    [configurationOnly, hideCompletedJobs, jobs],
  );

  const largeOverview = visibleJobs.length > 40 && focusedLevel === null;

  const graphJobs = useMemo(() => {
    if (!largeOverview && focusedLevel === null) return visibleJobs;
    if (focusedLevel === null) return [];
    const included = new Set(
      visibleJobs.filter((job) => jobLevels.get(job.name) === focusedLevel).map((job) => job.name),
    );
    visibleJobs.filter((job) => included.has(job.name)).forEach((job) => job.dependsOn.forEach((parent) => included.add(parent)));
    return visibleJobs.filter((job) => included.has(job.name));
  }, [focusedLevel, jobLevels, visibleJobs, largeOverview]);

  const criticalEdges = useMemo(() => {
    const duration = (job: DashboardJob) => {
      if (!job.firstBatchUtc || !job.lastBatchUtc) return 0;
      return Math.max(0, Date.parse(job.lastBatchUtc) - Date.parse(job.firstBatchUtc));
    };
    const memo = new Map<string, { score: number; path: string[] }>();
    const scoreTo = (job: DashboardJob, visiting = new Set<string>()): { score: number; path: string[] } => {
      const stored = memo.get(job.name);
      if (stored) return stored;
      if (visiting.has(job.name)) return { score: 0, path: [job.name] };
      const next = new Set(visiting).add(job.name);
      const children = jobs.filter((candidate) => candidate.dependsOn.includes(job.name));
      const bestChild = children.map((child) => scoreTo(child, next)).sort((a, b) => b.score - a.score)[0];
      const result = bestChild
        ? { score: duration(job) + bestChild.score, path: [job.name, ...bestChild.path] }
        : { score: duration(job), path: [job.name] };
      memo.set(job.name, result);
      return result;
    };
    const best = jobs.map((job) => scoreTo(job)).sort((a, b) => b.score - a.score)[0];
    return new Set((best?.path ?? []).slice(0, -1).map((name, index) => `${name}->${best?.path[index + 1]}`));
  }, [jobs]);

  const traceJobNames = useMemo(() => new Set(trace.map((snapshot) => snapshot.jobName)), [trace]);

  const traceEdges = useMemo(() => {
    const jobsByLineage = new Map<string, Set<string>>();
    trace.forEach((snapshot) => {
      const names = jobsByLineage.get(snapshot.lineageId) ?? new Set<string>();
      names.add(snapshot.jobName);
      jobsByLineage.set(snapshot.lineageId, names);
    });
    const edges = new Set<string>();
    trace.forEach((snapshot) => {
      snapshot.parentLineageIds.forEach((parentLineageId) => {
        jobsByLineage.get(parentLineageId)?.forEach((parentJob) => {
          if (parentJob !== snapshot.jobName) edges.add(`${parentJob}->${snapshot.jobName}`);
        });
      });
    });
    return edges;
  }, [trace]);

  const selectedRelationshipJobs = useMemo(() => {
    const related = new Set<string>();
    if (!selectedJob) return related;
    related.add(selectedJob);
    graphJobs.forEach((job) => {
      if (job.name === selectedJob) job.dependsOn.forEach((parent) => related.add(parent));
      if (job.dependsOn.includes(selectedJob)) related.add(job.name);
    });
    return related;
  }, [graphJobs, selectedJob]);

  const jumpToJob = useCallback((jobName: string) => {
    const target = jobsByName.get(jobName);
    const revealCompleted = hideCompletedJobs &&
      target !== undefined &&
      statusFor(target, jobs, configurationOnly) === "Completed";
    selectJob(jobName);
    if (revealCompleted) {
      setHideCompletedJobs(false);
      setFocusedLevel(null);
    }
    const scrollToRow = () => {
      const row = document.getElementById(`job-row-${encodeURIComponent(jobName)}`);
      row?.scrollIntoView({ behavior: "smooth", block: "center", inline: "nearest" });
      row?.querySelector<HTMLButtonElement>(".job-table-name")?.focus({ preventScroll: true });
    };
    window.requestAnimationFrame(() => {
      if (revealCompleted) window.requestAnimationFrame(scrollToRow);
      else scrollToRow();
    });
  }, [configurationOnly, hideCompletedJobs, jobs, jobsByName, selectJob]);

  const baseNodes = useMemo<Node<JobNodeData>[]>(() => {
    const lineageActive = trace.length > 0;
    const columns = new Map<number, DashboardJob[]>();
    graphJobs.forEach((job) => {
      const level = jobLevels.get(job.name) ?? 0;
      const column = columns.get(level) ?? [];
      column.push(job);
      columns.set(level, column);
    });
    return [...columns.entries()].flatMap(([level, columnJobs]) => {
      let top = 0;
      return columnJobs
        .sort((a, b) => a.name.localeCompare(b.name))
        .map((job) => {
          const jobConfiguration = configurationJobsByName.get(job.name);
          const sourceConnection = endpointConnection(jobConfiguration, connectionsById, "Source");
          const targetConnection = endpointConnection(jobConfiguration, connectionsById, "Target");
          const width = lineageActive ? 360 : 320;
          const activityHint = activityHintFor(job, jobs, configurationOnly);
          const specialistInfo = specialistInfoFor(job, jobConfiguration);
          const targetType = getStringProperty(
            asRecord(getPropertyValue(jobConfiguration?.properties, "Target")),
            "Type",
          );
          const stableSpecialistInfoCount = targetType?.toLocaleLowerCase() === "dataverse"
            ? 9
            : specialistInfo.length;
          const connectionHeight = (sourceConnection || targetConnection)
            ? 26 + (sourceConnection && targetConnection ? 13 : 0)
            : 0;
          const height = lineageActive
            ? 300
            : configurationOnly
              ? 168 + connectionHeight
              : 275 + connectionHeight +
              (stableSpecialistInfoCount > 0 ? 8 + stableSpecialistInfoCount * 18 : 0);
          const position = {
            x: level * (lineageActive ? 410 : 350),
            y: top,
          };
          top += height + (lineageActive ? 34 : 28);
          return {
            id: job.name,
            type: "job",
            width,
            height,
            position,
            data: {
              job,
              progress: stepProgress(job, jobs),
              inputRowsDisplay: inputRowsLabel(job, jobs),
              jobType: jobConfiguration?.type ?? "step",
              status: statusFor(job, jobs, configurationOnly),
              activityHint,
              specialistInfo,
              selected: selectedJob === job.name,
              selectionActive: Boolean(selectedJob),
              relationshipHighlighted: selectedRelationshipJobs.has(job.name),
              highlighted: traceJobNames.has(job.name),
              lineageActive,
              lineageSnapshots: trace.filter((snapshot) =>
                snapshot.jobName === job.name && snapshot.stageName !== "BatchOutcome"),
              sourceConnection,
              targetConnection,
              onSelect: selectJob,
              onShowErrors: showTaskErrors,
            },
          };
        });
    });
  }, [
    configurationOnly,
    configurationJobsByName,
    connectionsById,
    graphJobs,
    jobs,
    jobLevels,
    selectedJob,
    selectedRelationshipJobs,
    selectJob,
    showTaskErrors,
    trace,
    traceJobNames,
  ]);

  const edges = useMemo<Edge[]>(() => graphJobs.flatMap((job) =>
    job.dependsOn
      .filter((parent) => graphJobs.some((candidate) => candidate.name === parent))
      .map((parent) => {
        const edgeId = `${parent}->${job.name}`;
        const isCompletionDependency = job.completionDependencies.includes(parent);
        const selectedRelationship = selectedJob === parent || selectedJob === job.name;
        const tracedRelationship = traceEdges.has(edgeId);
        const color = selectedRelationship || trace.length > 0 && tracedRelationship
          ? "#0078d4"
          : selectedJob || trace.length > 0
            ? "#c8c6c4"
            : criticalEdges.has(edgeId)
              ? "#ca5010"
              : "#8a8886";
        const strokeWidth = selectedRelationship || trace.length > 0 && tracedRelationship
          ? 4
          : !selectedJob && trace.length === 0 && criticalEdges.has(edgeId) ? 3 : 1;
        return {
          id: edgeId,
          source: parent,
          target: job.name,
          type: "smoothstep",
          className: selectedJob
            ? selectedRelationship ? "relationship-edge-highlighted" : "relationship-edge-muted"
            : undefined,
          animated: !configurationOnly && statusFor(job, jobs) === "Running",
          style: {
            stroke: color,
            strokeWidth,
            opacity: selectedJob && !selectedRelationship ? 0.28 : 1,
            strokeDasharray: isCompletionDependency ? "6 4" : undefined,
          },
        };
      }),
  ), [configurationOnly, criticalEdges, graphJobs, jobs, selectedJob, trace.length, traceEdges]);

  const graphLayoutKey = [
    ...baseNodes.map((node) => `${node.id}:${node.width}:${node.height}`),
    ...edges.map((edge) => `${edge.source}->${edge.target}`),
  ].join("|");

  const graphAutoFitKey = [
    runId,
    configurationOnly ? "configuration" : "run",
    graphJobs.map((job) => job.name).join(","),
    edges.map((edge) => edge.id).join(","),
    trace.length > 0 ? "lineage" : "workflow",
  ].join("|");

  useEffect(() => {
    if (baseNodes.length === 0) {
      setGraphLayout({ key: graphLayoutKey, positions: new Map() });
      setGraphLayoutError("");
      return;
    }

    let cancelled = false;
    setGraphLayoutError("");
    const layoutGraph = async () => {
      try {
        const { default: ELK } = await import("elkjs/lib/elk.bundled.js");
        if (cancelled) return;
        const result = await new ELK().layout({
          id: "migration",
          layoutOptions: {
            "elk.algorithm": "layered",
            "elk.direction": "RIGHT",
            "elk.edgeRouting": "ORTHOGONAL",
            "elk.layered.crossingMinimization.strategy": "LAYER_SWEEP",
            "elk.layered.nodePlacement.strategy": "NETWORK_SIMPLEX",
            "elk.layered.spacing.nodeNodeBetweenLayers": "72",
            "elk.spacing.nodeNode": "40",
          },
          children: baseNodes.map((node) => ({
            id: node.id,
            width: node.width ?? 320,
            height: node.height ?? 120,
          })),
          edges: edges.map((edge) => ({
            id: edge.id,
            sources: [edge.source],
            targets: [edge.target],
          })),
        });
        if (cancelled) return;
        const positions = new Map<string, { x: number; y: number }>();
        result.children?.forEach((node) => {
          if (node.id) positions.set(node.id, { x: node.x ?? 0, y: node.y ?? 0 });
        });
        setGraphLayout({ key: graphLayoutKey, positions });
      } catch (reason) {
        if (cancelled) return;
        setGraphLayoutError(
          `Unable to arrange the workflow graph: ${reason instanceof Error ? reason.message : String(reason)}`,
        );
        setGraphLayout((current) => ({
          key: graphLayoutKey,
          positions: current?.positions ?? new Map(),
        }));
      }
    };
    void layoutGraph();

    return () => { cancelled = true; };
  }, [graphLayoutKey]);

  const nodes = useMemo<Node<JobNodeData>[]>(() => baseNodes.map((node) => {
    const position = graphLayout?.positions.get(node.id);
    return position ? { ...node, position } : node;
  }), [baseNodes, graphLayout, graphLayoutKey]);

  useEffect(() => {
    const canvas = flowCanvasRef.current;
    if (!flowInstance || !canvas || nodes.length === 0 || graphLayout?.key !== graphLayoutKey) return;
    if (lastAutoFit.current?.instance === flowInstance &&
      lastAutoFit.current.key === graphAutoFitKey) return;
    const timer = window.setTimeout(() => {
      const bounds = flowInstance.getNodesBounds(flowInstance.getNodes());
      const viewport = getViewportForBounds(
        bounds,
        canvas.clientWidth,
        canvas.clientHeight,
        0.15,
        1,
        0.18,
      );
      void flowInstance.setViewport(viewport);
      lastAutoFit.current = { instance: flowInstance, key: graphAutoFitKey };
    }, 100);
    return () => window.clearTimeout(timer);
  }, [flowInstance, graphAutoFitKey, graphLayout?.key, graphLayoutKey, nodes.length]);

  useEffect(() => {
    if (!flowInstance || !selectedJob || graphLayout?.key !== graphLayoutKey) return;
    const selectionKey = `${graphAutoFitKey}|${selectedJob}`;
    if (lastCenteredSelection.current?.instance === flowInstance &&
      lastCenteredSelection.current.key === selectionKey) return;
    const node = flowInstance.getNode(selectedJob);
    if (!node) return;
    const timer = window.setTimeout(() => {
      void flowInstance.setCenter(
        node.position.x + (node.width ?? 320) / 2,
        node.position.y + (node.height ?? 120) / 2,
        { zoom: Math.max(flowInstance.getZoom(), 0.7), duration: 350 },
      );
      lastCenteredSelection.current = { instance: flowInstance, key: selectionKey };
    }, 180);
    return () => window.clearTimeout(timer);
  }, [flowInstance, graphAutoFitKey, graphLayout?.key, graphLayoutKey, selectedJob]);

  return {
    configurationJobsByName, connectionsById, jobsByName, jobLevels, jobOutputs, visibleJobs, largeOverview,
    graphJobs, criticalEdges, traceJobNames, traceEdges, selectedRelationshipJobs, jumpToJob,
    graphLayoutError, flowCanvasRef, setFlowInstance, edges, nodes,
  };
}
