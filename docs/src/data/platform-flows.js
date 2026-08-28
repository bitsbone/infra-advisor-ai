export const requestServingFlows = [{
  id: 'request-serving',
  label: 'Request-serving map',
  description: 'One public client contract branches into authentication and two deliberately different agent implementations.',
  nodes: [
    { id: 'client', label: 'Web or mobile client', kind: 'client', summary: 'Starts authentication, Python queries, or .NET queries through the UI entrypoint.', why: 'The public edge should not expose every internal service.', evidence: 'RUM/mobile action and resource plus the selected route.', position: { x: 0, y: 145 } },
    { id: 'auth', label: 'Auth API', kind: 'service', summary: 'Owns login, JWT identity, and user administration.', why: 'Authentication remains independent from either agent framework.', evidence: 'An `/auth/*` request and PostgreSQL work.', position: { x: 245, y: 0 } },
    { id: 'python', label: 'Python Agent API', kind: 'intelligence', summary: 'Runs router and specialist orchestration with Datadog-native instrumentation.', why: 'This path is the native SDK implementation under comparison.', evidence: 'Agent Observability plus APM under `infra-advisor-agent-api`.', position: { x: 245, y: 145 } },
    { id: 'dotnet', label: '.NET Agent API', kind: 'intelligence', summary: 'Runs deterministic classification and one agent with OpenTelemetry.', why: 'This path exposes the same product contract through a different framework and telemetry boundary.', evidence: 'OTel/Agent Observability data under `infra-advisor-agent-api-dotnet`.', position: { x: 245, y: 290 } },
    { id: 'postgres', label: 'PostgreSQL', kind: 'state', summary: 'Persists identity and durable conversation data.', why: 'Durable state survives cache eviction and service restarts.', evidence: 'Database spans and DBM context where configured.', position: { x: 490, y: 0 } },
    { id: 'memory', label: 'Redis memory', kind: 'state', summary: 'Stores replaceable tenant-scoped hot memory and selections.', why: 'Fast context does not need the same durability promise as conversation history.', evidence: 'Redis operations and TTL behavior.', position: { x: 490, y: 145 } },
    { id: 'mcp-python', label: 'Python MCP server', kind: 'boundary', summary: 'Executes Python-side typed tools against external sources.', why: 'Agent reasoning and provider integration have separate ownership.', evidence: 'MCP protocol, server tool, and provider spans.', position: { x: 490, y: 290 } },
    { id: 'mcp-dotnet', label: '.NET MCP server', kind: 'boundary', summary: 'Executes the .NET tool catalog against the same source families.', why: 'Cross-backend parity is measured at behavior and evidence, not identical code.', evidence: 'MCP activities, server request, and provider operations.', position: { x: 735, y: 290 } },
    { id: 'sources', label: 'External data APIs', kind: 'source', summary: 'Supply governed infrastructure and procurement evidence.', why: 'Current external facts should come from tools rather than model memory.', evidence: 'HTTP, search, or database work with credentials and raw bodies excluded.', position: { x: 735, y: 145 } },
  ],
  edges: [
    { source: 'client', target: 'auth', label: '/auth' }, { source: 'client', target: 'python', label: '/api' }, { source: 'client', target: 'dotnet', label: '/api-dotnet' },
    { source: 'auth', target: 'postgres' }, { source: 'python', target: 'memory' }, { source: 'python', target: 'postgres' }, { source: 'python', target: 'mcp-python' },
    { source: 'dotnet', target: 'memory' }, { source: 'dotnet', target: 'postgres' }, { source: 'dotnet', target: 'mcp-dotnet' }, { source: 'mcp-python', target: 'sources' }, { source: 'mcp-dotnet', target: 'sources' },
  ],
}];

export const observabilityFlows = [
  {
    id: 'interactive-signals', label: 'Interactive request', description: 'Start with the user experience, then move inward through causal trace context and outward to supporting signals.',
    nodes: [
      { id: 'rum', label: 'RUM action', kind: 'client', summary: 'Captures what the browser or mobile user experienced.', evidence: 'View, action, resource, error, and session replay where enabled.' },
      { id: 'http', label: 'Traced HTTP request', kind: 'service', summary: 'Connects client latency to the selected backend service.', evidence: 'W3C trace headers, service, environment, version, status, and duration.' },
      { id: 'agent', label: 'Agent-specific spans', kind: 'intelligence', summary: 'Explain routing, model, retrieval, evaluation, and tool decisions.', evidence: 'Workflow/agent/LLM/tool kinds and bounded semantic fields.' },
      { id: 'mcp', label: 'MCP and provider work', kind: 'boundary', summary: 'Continues causality into the tool server and source operation.', evidence: 'Client/server protocol spans and downstream HTTP, search, or database work.' },
      { id: 'support', label: 'Supporting signals', kind: 'telemetry', summary: 'Adds logs, DBM, pod, and infrastructure context around the same request.', evidence: 'Trace IDs and consistent Unified Service Tagging—not timestamp proximity.' },
    ],
    edges: [{ source: 'rum', target: 'http' }, { source: 'http', target: 'agent' }, { source: 'agent', target: 'mcp' }, { source: 'mcp', target: 'support' }],
  },
  {
    id: 'kafka-signals', label: 'Kafka pathway', description: 'Data Streams Monitoring answers backlog and pathway questions while traces and logs explain individual executions.',
    nodes: [
      { id: 'producer', label: 'Kafka producer', kind: 'service', summary: 'Creates a synthetic query event.', evidence: 'Producer identity, topic, partition, and injected context.' },
      { id: 'topic', label: 'Query topic', kind: 'stream', summary: 'Buffers work and exposes pathway latency and lag.', evidence: 'Data Streams pathway, partition, offsets, and consumer lag.' },
      { id: 'consumer', label: 'Agent consumer', kind: 'intelligence', summary: 'Consumes the event and runs ordinary agent work.', evidence: 'Consumer span joined to model and tool operations.' },
      { id: 'result', label: 'Result topic', kind: 'telemetry', summary: 'Carries bounded execution output for stream analysis.', evidence: 'Produced result, end-to-end pathway latency, and safe result metadata.' },
    ],
    edges: [{ source: 'producer', target: 'topic' }, { source: 'topic', target: 'consumer' }, { source: 'consumer', target: 'result' }],
  },
  {
    id: 'airflow-signals', label: 'Airflow run', description: 'Data Jobs Monitoring provides lifecycle context; task telemetry explains provider, Blob, and Search work within the run.',
    nodes: [
      { id: 'run', label: 'DAG run', kind: 'service', summary: 'Defines the scheduled data refresh lifecycle.', evidence: 'OpenLineage run identity, state, duration, and task graph.' },
      { id: 'task', label: 'Task process', kind: 'service', summary: 'Executes one source, transform, or serving responsibility.', evidence: 'Task logs and trace correlation.' },
      { id: 'provider', label: 'Provider request', kind: 'source', summary: 'Fetches source data under provider-specific controls.', evidence: 'Safe request status, latency, and bounded counts.' },
      { id: 'storage', label: 'Blob and Search work', kind: 'state', summary: 'Persists snapshots and updates the derived serving index.', evidence: 'Blob upload span, manifest identity, Search operations, and document counts.' },
    ],
    edges: [{ source: 'run', target: 'task' }, { source: 'task', target: 'provider' }, { source: 'provider', target: 'storage' }],
  },
];

export const pipelineFlows = [{
  id: 'shared-ingestion',
  label: 'Shared ingestion pattern',
  description: 'Bulk records move through private Blob objects. Airflow XCom carries only the small manifest needed to verify each boundary.',
  nodes: [
    { id: 'source', label: 'Provider API or reviewed file', kind: 'source', summary: 'Supplies one governed source dataset.', why: 'Each source has independent cadence, schema, and failure modes.', evidence: 'Source identity, response status, and bounded record count.', position: { x: 0, y: 0 } },
    { id: 'normalize', label: 'Validate and normalize', kind: 'service', summary: 'Rejects invalid input and creates deterministic records.', why: 'Bad records should fail before storage or retrieval serving.', evidence: 'Validation counts, schema errors, and task status.', position: { x: 245, y: 0 } },
    { id: 'stage', label: 'JSONL staging object', kind: 'state', summary: 'Writes bulk data to a deterministic private Blob path.', why: 'Record arrays do not belong in Airflow metadata or XCom.', evidence: 'Object path, byte count, checksum, and upload span.', position: { x: 490, y: 0 } },
    { id: 'manifest', label: 'Versioned XCom manifest', kind: 'boundary', summary: 'Carries container, path, source, run, count, checksum, type, and schema version.', why: 'A small verifiable contract makes task handoff reproducible without moving the dataset.', evidence: 'Known schema version with no URL, query string, secret, or provider body.', position: { x: 735, y: 0 } },
    { id: 'verify', label: 'Verify and read records', kind: 'boundary', summary: 'Rejects mismatched source, encoding, count, checksum, or unknown fields.', why: 'A successful upstream task does not make its object trustworthy by implication.', evidence: 'Manifest validation and deterministic retry behavior.', position: { x: 735, y: 170 } },
    { id: 'snapshot', label: 'Parquet snapshot', kind: 'state', summary: 'Preserves a compact recoverable analytical snapshot.', why: 'Serving indexes are derived and replaceable.', evidence: 'Snapshot object associated with the same run manifest.', position: { x: 490, y: 170 } },
    { id: 'index', label: 'Narrate, chunk, embed, index', kind: 'intelligence', summary: 'Builds retrieval documents and upserts Azure AI Search.', why: 'Normalized records need explicit content shaping before hybrid retrieval.', evidence: 'Document counts, embedding calls, Search operations, and bounded errors.', position: { x: 245, y: 170 } },
  ],
  edges: [
    { source: 'source', target: 'normalize' }, { source: 'normalize', target: 'stage' }, { source: 'stage', target: 'manifest' }, { source: 'manifest', target: 'verify' }, { source: 'verify', target: 'snapshot' }, { source: 'verify', target: 'index' },
  ],
}];
