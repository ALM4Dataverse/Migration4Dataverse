import { Background, Controls, MiniMap, ReactFlow } from "@xyflow/react";
import { JobNodeData } from "../types";
import { statusInfo } from "../dashboardUtils";
import { nodeTypes } from "./JobNode";
import type { DashboardController } from "../hooks/useDashboardController";

export function WorkflowGraphCanvas({ controller }: { controller: DashboardController }) {
  const { setFlowInstance, flowCanvasRef, configurationOnly, selectJob, edges, nodes } = controller;

  return <div className="flow-canvas" ref={flowCanvasRef}>
    {!configurationOnly && (
      <div className="flow-legend">
        <span className="legend-note"><i className="legend-line" />Longest observed chain</span>
      </div>
    )}
    <ReactFlow
      nodes={nodes}
      edges={edges}
      nodeTypes={nodeTypes}
      onInit={setFlowInstance}
      minZoom={0.15}
      maxZoom={1.4}
      onNodeClick={(_, node) => selectJob(node.id)}
      proOptions={{ hideAttribution: true }}
    >
      <Background color="#d6d6d6" gap={20} size={1} />
      <MiniMap pannable zoomable nodeColor={(node) => {
        const data = node.data as JobNodeData;
        return `var(--${statusInfo[data.status].color === "muted" ? "gray" : statusInfo[data.status].color})`;
      }} />
      <Controls showInteractive={false} />
    </ReactFlow>
  </div>;
}
