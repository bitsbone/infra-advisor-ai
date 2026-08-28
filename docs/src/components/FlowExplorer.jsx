import React, { useMemo, useState } from 'react';
import { Background, Controls, Handle, MarkerType, Position, ReactFlow } from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import './flow-explorer.css';

const KINDS = {
  client: { accent: '#38bdf8', tint: 'rgba(56,189,248,.13)', label: 'Entry point' },
  service: { accent: '#818cf8', tint: 'rgba(129,140,248,.13)', label: 'Service' },
  intelligence: { accent: '#c084fc', tint: 'rgba(192,132,252,.13)', label: 'AI operation' },
  boundary: { accent: '#f59e0b', tint: 'rgba(245,158,11,.13)', label: 'Boundary' },
  state: { accent: '#34d399', tint: 'rgba(52,211,153,.13)', label: 'State' },
  stream: { accent: '#fb7185', tint: 'rgba(251,113,133,.13)', label: 'Stream' },
  source: { accent: '#2dd4bf', tint: 'rgba(45,212,191,.13)', label: 'External source' },
  telemetry: { accent: '#a3e635', tint: 'rgba(163,230,53,.13)', label: 'Observable output' },
};

function ExplorerNode({ data }) {
  const style = KINDS[data.kind] ?? KINDS.service;
  return (
    <>
      {!data.isFirst && <Handle type="target" position={Position.Left} style={{ opacity: 0 }} />}
      <div
        className="flow-node nodrag nopan"
        role="button"
        tabIndex={0}
        aria-pressed={data.selected}
        aria-label={`Step ${data.step}: ${data.label}. ${data.summary}`}
        data-selected={data.selected}
        data-muted={data.muted}
        style={{ '--node-accent': style.accent, '--node-tint': style.tint }}
        onClick={() => data.onSelect(data.id)}
        onKeyDown={(event) => {
          if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            data.onSelect(data.id);
          }
        }}
      >
        <div className="flow-node__topline">
          <span className="flow-node__step">{data.step}</span>
          <span className="flow-node__kind">{data.eyebrow || style.label}</span>
        </div>
        <strong>{data.label}</strong>
        <p>{data.summary}</p>
      </div>
      {!data.isLast && <Handle type="source" position={Position.Right} style={{ opacity: 0 }} />}
    </>
  );
}

const NODE_TYPES = { explorer: ExplorerNode };

function prepareFlow(flow, selectedId, onSelect) {
  const connected = new Set([selectedId]);
  for (const edge of flow.edges) {
    if (edge.source === selectedId) connected.add(edge.target);
    if (edge.target === selectedId) connected.add(edge.source);
  }

  const nodes = flow.nodes.map((node, index) => ({
    id: node.id,
    type: 'explorer',
    position: node.position ?? { x: (node.column ?? index) * 245, y: (node.row ?? 0) * 145 },
    draggable: false,
    deletable: false,
    selectable: false,
    focusable: false,
    data: {
      ...node,
      step: index + 1,
      isFirst: !flow.edges.some((edge) => edge.target === node.id),
      isLast: !flow.edges.some((edge) => edge.source === node.id),
      selected: node.id === selectedId,
      muted: selectedId && !connected.has(node.id),
      onSelect,
    },
  }));

  const edges = flow.edges.map((edge, index) => {
    const active = edge.source === selectedId || edge.target === selectedId;
    const source = flow.nodes.find((node) => node.id === edge.source)?.label;
    const target = flow.nodes.find((node) => node.id === edge.target)?.label;
    return {
      id: `${flow.id}-edge-${index}`,
      ...edge,
      type: edge.type || 'smoothstep',
      deletable: false,
      focusable: true,
      animated: active,
      markerEnd: { type: MarkerType.ArrowClosed, width: 16, height: 16 },
      style: { opacity: selectedId && !active ? 0.28 : 1, stroke: active ? '#a78bfa' : undefined },
      ariaLabel: `${source} to ${target}${edge.label ? `: ${edge.label}` : ''}`,
    };
  });

  return { nodes, edges };
}

export default function FlowExplorer({
  title = 'Explore the system',
  instruction = 'Select a stage to inspect its responsibility and observable evidence.',
  flows = [],
  height,
}) {
  const [flowId, setFlowId] = useState(flows[0]?.id);
  const activeFlow = flows.find((flow) => flow.id === flowId) ?? flows[0];
  const [selectedByFlow, setSelectedByFlow] = useState(() => Object.fromEntries(flows.map((flow) => [flow.id, flow.nodes[0]?.id])));
  const selectedId = selectedByFlow[activeFlow?.id];
  const selectedIndex = Math.max(0, activeFlow?.nodes.findIndex((node) => node.id === selectedId) ?? 0);
  const selected = activeFlow?.nodes[selectedIndex];
  const selectNode = (id) => setSelectedByFlow((current) => ({ ...current, [activeFlow.id]: id }));
  const prepared = useMemo(() => activeFlow ? prepareFlow(activeFlow, selectedId, selectNode) : { nodes: [], edges: [] }, [activeFlow, selectedId]);
  const detailStyle = KINDS[selected?.kind] ?? KINDS.service;

  if (!activeFlow || !selected) return null;

  const move = (offset) => {
    const next = activeFlow.nodes[Math.min(activeFlow.nodes.length - 1, Math.max(0, selectedIndex + offset))];
    if (next) selectNode(next.id);
  };

  return (
    <section className="flow-explorer not-content" aria-label={title}>
      <header className="flow-explorer__header">
        <div><p className="flow-explorer__eyebrow">Interactive system explorer</p><h2>{title}</h2></div>
        <p className="flow-explorer__instruction">{instruction}</p>
      </header>

      {flows.length > 1 && (
        <div className="flow-explorer__switcher" role="tablist" aria-label="Choose a system flow">
          {flows.map((flow) => (
            <button key={flow.id} type="button" role="tab" aria-selected={flow.id === activeFlow.id} className="flow-explorer__switch" onClick={() => setFlowId(flow.id)}>{flow.label}</button>
          ))}
        </div>
      )}

      <div className="flow-explorer__context"><p>{activeFlow.description}</p><span>{activeFlow.nodes.length} stages</span></div>
      <div className="flow-explorer__workspace">
        <div className="flow-explorer__canvas" style={height ? { minHeight: height } : undefined}>
          <ReactFlow
            key={activeFlow.id}
            nodes={prepared.nodes}
            edges={prepared.edges}
            nodeTypes={NODE_TYPES}
            fitView
            fitViewOptions={{ padding: 0.2, minZoom: 0.5, maxZoom: 1.15 }}
            minZoom={0.4}
            maxZoom={1.4}
            nodesDraggable={false}
            nodesConnectable={false}
            nodesFocusable={false}
            edgesFocusable={false}
            deleteKeyCode={null}
            selectionKeyCode={null}
            multiSelectionKeyCode={null}
            panOnScroll={false}
            zoomOnDoubleClick={false}
            proOptions={{ hideAttribution: true }}
            aria-label={`${activeFlow.label} system flow. Use Tab to move between stages and Enter to select one.`}
          >
            <Background gap={22} size={1} color="var(--sl-color-gray-5)" />
            <Controls showInteractive={false} aria-label="Diagram zoom controls" />
          </ReactFlow>
        </div>

        <aside className="flow-explorer__detail" style={{ '--detail-accent': detailStyle.accent }} aria-live="polite">
          <div className="flow-explorer__detail-intro">
            <p className="flow-explorer__detail-label">Step {selectedIndex + 1} · {selected.eyebrow || detailStyle.label}</p>
            <h3>{selected.label}</h3>
            <p className="flow-explorer__detail-summary">{selected.detail || selected.summary}</p>
          </div>
          {selected.why && <div className="flow-explorer__detail-block"><span>Why this stage exists</span><p>{selected.why}</p></div>}
          {selected.evidence && <div className="flow-explorer__detail-block"><span>Evidence to inspect</span><p>{selected.evidence}</p></div>}
          <div className="flow-explorer__navigator" aria-label="Move through stages">
            <button type="button" onClick={() => move(-1)} disabled={selectedIndex === 0} aria-label="Previous stage">←</button>
            <select value={selected.id} onChange={(event) => selectNode(event.target.value)} aria-label="Select a stage">
              {activeFlow.nodes.map((node, index) => <option key={node.id} value={node.id}>{index + 1}. {node.label}</option>)}
            </select>
            <button type="button" onClick={() => move(1)} disabled={selectedIndex === activeFlow.nodes.length - 1} aria-label="Next stage">→</button>
          </div>
        </aside>
      </div>

      <details className="flow-explorer__fallback">
        <summary>Read this flow as a text sequence</summary>
        <ol>{activeFlow.nodes.map((node) => <li key={node.id}><strong>{node.label}:</strong> {node.summary}</li>)}</ol>
      </details>
    </section>
  );
}
