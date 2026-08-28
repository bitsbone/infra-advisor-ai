export const instrumentationFlows = [
  {
    id: 'python-sdk',
    label: 'Python · Datadog SDK',
    description: 'Datadog integrations create supported framework spans; explicit LLMObs operations add the product-specific orchestration they cannot infer.',
    nodes: [
      { id: 'bootstrap', label: 'ddtrace.auto bootstrap', kind: 'service', summary: 'Loads instrumentation before supported libraries.', why: 'A patched library imported too early may never be instrumented.', evidence: 'Service startup logs, integration status, and an APM root span.' },
      { id: 'automatic', label: 'Framework integrations', kind: 'intelligence', summary: 'Capture supported LangChain, LangGraph, OpenAI, and MCP calls.', why: 'Automatic coverage provides useful library-level detail with little application code.', evidence: 'Framework, LLM, and MCP spans with Datadog-native names and fields.' },
      { id: 'explicit', label: 'Explicit LLMObs operations', kind: 'boundary', summary: 'Describe workflow, agent, task, and product-specific tool meaning.', why: 'The application must supply orchestration meaning that a library patch cannot know.', evidence: 'Recognizable root workflow and nested agent/task operations with bounded annotations.' },
      { id: 'agent', label: 'Datadog Agent', kind: 'telemetry', summary: 'Receives APM and Agent Observability telemetry through the native path.', why: 'Agentless mode is disabled so the in-cluster Agent owns forwarding and correlation.', evidence: 'Agent intake health and an Agent Observability trace linked to application APM.' },
      { id: 'datadog', label: 'Datadog trace experience', kind: 'telemetry', summary: 'Presents native span kinds, annotations, evaluations, and APM links.', why: 'The output—not setup syntax—is what determines whether the instrumentation answers learner questions.', evidence: 'Model/provider/tokens/errors, tool identity, evaluation fields, and trace relationships where emitted.' },
    ],
    edges: [
      { source: 'bootstrap', target: 'automatic', label: 'patch' },
      { source: 'automatic', target: 'explicit', label: 'add meaning' },
      { source: 'explicit', target: 'agent', label: 'native export' },
      { source: 'agent', target: 'datadog', label: 'forward' },
    ],
  },
  {
    id: 'dotnet-otel',
    label: '.NET · OpenTelemetry',
    description: 'The application emits activities and GenAI conventions, exports OTLP, and relies on collector and ingestion translation for the Datadog experience.',
    nodes: [
      { id: 'sources', label: 'Activity sources', kind: 'service', summary: 'Register ASP.NET, HTTP, Microsoft agent, MCP, and product activity sources.', why: 'An unregistered source can execute correctly while leaving a gap in the trace.', evidence: 'Started activities from every expected source and an application request root.' },
      { id: 'semantic', label: 'GenAI semantic attributes', kind: 'intelligence', summary: 'Model agent, LLM, tool, and evaluation meaning with supported conventions.', why: 'Vendor-neutral transport does not invent AI meaning that the application never emits.', evidence: 'Operation kind, model/provider, usage, tool identity, and correlation attributes.' },
      { id: 'otlp', label: 'OTLP exporter', kind: 'boundary', summary: 'Serializes activities and resources over HTTP/protobuf.', why: 'Exporter protocol, endpoint, resource fields, and batching all influence delivery.', evidence: 'Exporter health, OTEL service resource, and successful requests to the in-cluster collector.' },
      { id: 'collector', label: 'Datadog OTLP intake', kind: 'telemetry', summary: 'Receives and translates supported OpenTelemetry data.', why: 'Translation responsibility is shared between application conventions and Datadog ingestion.', evidence: 'Collector intake health plus spans arriving under the expected service, environment, and version.' },
      { id: 'experience', label: 'Datadog trace experience', kind: 'telemetry', summary: 'Presents the translated Agent Observability and APM relationships.', why: 'A span reaching APM does not guarantee it has the Agent Observability kind you intended.', evidence: 'Correct span classification, semantic fields, evaluations, and an APM continuation.' },
    ],
    edges: [
      { source: 'sources', target: 'semantic', label: 'create activities' },
      { source: 'semantic', target: 'otlp', label: 'process' },
      { source: 'otlp', target: 'collector', label: 'OTLP HTTP' },
      { source: 'collector', target: 'experience', label: 'translate' },
    ],
  },
];

export const artifactFlows = [{
  id: 'artifact',
  label: 'Artifact lifecycle',
  description: 'The application carries versioned evidence beside assistant prose without exposing raw provider responses as a client contract.',
  nodes: [
    { id: 'providers', label: 'SAM.gov + Grants.gov', kind: 'source', summary: 'Return provider-specific opportunity records.', why: 'Providers disagree on identifiers, dates, statuses, classifications, and funding fields.', evidence: 'Provider request status and bounded counts—never API-key-bearing URLs or raw response bodies.' },
    { id: 'normalize', label: 'MCP provider adapters', kind: 'boundary', summary: 'Normalize honest shared meaning into artifact version 1.', why: 'The client should depend on a stable application contract, not incidental provider JSON.', evidence: 'Schema version, status, counts, truncation, and bounded partial errors.' },
    { id: 'extract', label: 'Agent extraction', kind: 'intelligence', summary: 'Rebuilds the artifact from allowlisted protocol locations and fields.', why: 'MCP libraries expose structured results in several shapes, including nested lookalikes that must be rejected.', evidence: 'Accepted artifact kind/version, size validation, and sanitized citation URLs.' },
    { id: 'transport', label: 'Stream and persist', kind: 'state', summary: 'Emits an SSE artifact event and saves the same payload in conversation JSONB.', why: 'Evidence should arrive independently of answer prose and survive conversation restoration.', evidence: 'Artifact stream event, stored assistant message, and the same source set after reload.' },
    { id: 'render', label: 'MAUI evidence cards', kind: 'client', summary: 'Maps supported versions into typed, durable evidence cards.', why: 'Unknown kinds and versions must fail safely without breaking the transcript.', evidence: 'Card render, official-source link, restoration, and mobile-to-agent trace correlation.' },
  ],
  edges: [
    { source: 'providers', target: 'normalize', label: 'raw records' },
    { source: 'normalize', target: 'extract', label: 'artifact v1' },
    { source: 'extract', target: 'transport', label: 'validated payload' },
    { source: 'transport', target: 'render', label: 'SSE + JSONB' },
  ],
}];

export const multimodalFlows = [{
  id: 'multimodal',
  label: 'Attachment lifecycle',
  description: 'Upload and query are separate requests. A narrow reference joins them without moving media bytes through chat JSON or telemetry.',
  nodes: [
    { id: 'upload', label: 'Authenticated upload', kind: 'client', summary: 'Accepts one allowlisted image or audio file up to 10 MiB.', why: 'Media needs rate limiting, ownership, and validation before it can become model input.', evidence: 'Upload resource with modality, byte count, duration, and status only.', column: 0, row: 0 },
    { id: 'blob', label: 'Private Blob object', kind: 'state', summary: 'Stores media under a kind plus random UUID, without original filename or session ID.', why: 'Private storage keeps media out of query bodies and durable conversation text.', evidence: 'Blob operation and a generated object name with no user-supplied identifiers.', column: 1, row: 0 },
    { id: 'validate', label: 'Validate read-only reference', kind: 'boundary', summary: 'Checks host, container, path, MIME, size, SAS scope, and network destination.', why: 'A user-supplied URL must become one narrow Blob-read capability, not a general server-side fetch.', evidence: 'Validation outcome before any download, memory restore, or model invocation.', column: 2, row: 0 },
    { id: 'audio', label: 'Audio transcription', kind: 'intelligence', summary: 'Whisper converts current-turn audio into the effective text query.', why: 'Routing, retrieval, and tool selection operate on the transcription.', evidence: 'A bounded transcription step preceding classification and routing.', column: 3, row: 0 },
    { id: 'image', label: 'Vision model input', kind: 'intelligence', summary: 'Supplies the current-turn image to a vision-capable model path.', why: 'Stored history does not re-download or reprocess earlier attachments.', evidence: 'A vision/model step with safe metadata and no signed URL or description payload.', column: 3, row: 1 },
    { id: 'agent', label: 'Normal agent workflow', kind: 'telemetry', summary: 'Continues through routing, retrieval, tools, persistence, and response.', why: 'Multimodal preparation should remain legible without creating a separate agent implementation.', evidence: 'One correlated trace whose safe metadata explains whether validation, transcription, vision, tool, or model work failed.', column: 4, row: 0 },
  ],
  edges: [
    { source: 'upload', target: 'blob', label: 'store' },
    { source: 'blob', target: 'validate', label: 'read-only reference' },
    { source: 'validate', target: 'audio', label: 'audio' },
    { source: 'validate', target: 'image', label: 'image' },
    { source: 'audio', target: 'agent', label: 'effective text' },
    { source: 'image', target: 'agent', label: 'model content' },
  ],
}];

export const mcpBoundaryFlows = [{
  id: 'mcp-boundary',
  label: 'Tool-call trace',
  description: 'Each stage represents a different responsibility. W3C trace context should connect them without collapsing them into duplicate spans.',
  nodes: [
    { id: 'decision', label: 'Agent tool decision', kind: 'intelligence', summary: 'Chooses an external capability required by the answer.', why: 'This span explains intent: why the agent selected the tool.', evidence: 'Agent Observability tool name, arguments policy, and parent agent operation.' },
    { id: 'client', label: 'MCP client request', kind: 'boundary', summary: 'Turns tool intent into a protocol operation.', why: 'Client-library instrumentation explains serialization and protocol latency.', evidence: 'A child operation sharing the agent trace ID.' },
    { id: 'http', label: 'POST /mcp', kind: 'service', summary: 'Carries trace context from Agent API to MCP server.', why: 'This is the network and service-ownership boundary.', evidence: 'Outbound client span and server request span with the expected parent-child relationship.' },
    { id: 'server', label: 'tools/call handler', kind: 'boundary', summary: 'Identifies and executes the selected server-side tool.', why: 'A stateful session can fail here even when tracing is configured correctly.', evidence: 'Named operation, session routing, status, and safe error fields.' },
    { id: 'provider', label: 'Provider work', kind: 'source', summary: 'Performs the downstream HTTP, database, or search request.', why: 'The actual latency or error may live beyond the MCP server boundary.', evidence: 'Provider span below `tools/call`, without credentials or raw response bodies.' },
  ],
  edges: [
    { source: 'decision', target: 'client', label: 'execute' },
    { source: 'client', target: 'http', label: 'traceparent' },
    { source: 'http', target: 'server', label: 'propagate' },
    { source: 'server', target: 'provider', label: 'request' },
  ],
}];

export const backendShapeFlows = [
  {
    id: 'python-shape', label: 'Python trace shape', description: 'Explicit routing and specialist operations surround model and MCP work.',
    nodes: [
      { id: 'query', label: 'query-processing', kind: 'service', summary: 'Represents the complete Python request workflow.', evidence: 'Root duration, error state, service, environment, and version.' },
      { id: 'history', label: 'load-history', kind: 'state', summary: 'Restores durable and hot conversation context.', evidence: 'PostgreSQL and Redis operations associated with the authenticated scope.', column: 1, row: 0 },
      { id: 'router', label: 'router + model', kind: 'intelligence', summary: 'Chooses the specialist path using model-assisted orchestration.', evidence: 'Router decision and nested model operation.', column: 1, row: 1 },
      { id: 'specialist', label: 'specialist', kind: 'intelligence', summary: 'Reasons within the selected domain and decides whether to call a tool.', evidence: 'Specialist, nested model, and tool operations.', column: 2, row: 1 },
      { id: 'tool', label: 'MCP tool', kind: 'boundary', summary: 'Crosses into the matching MCP server and provider.', evidence: 'Tool name, MCP protocol/server spans, and provider work.', column: 3, row: 1 },
    ],
    edges: [
      { source: 'query', target: 'history' }, { source: 'query', target: 'router' }, { source: 'router', target: 'specialist' }, { source: 'specialist', target: 'tool' },
    ],
  },
  {
    id: 'dotnet-shape', label: '.NET trace shape', description: 'Deterministic classification and retrieval lead into one Microsoft agent invocation and its tool execution.',
    nodes: [
      { id: 'request', label: 'HTTP request', kind: 'service', summary: 'Represents the complete .NET application request.', evidence: 'ASP.NET request duration, status, service, environment, and version.' },
      { id: 'classify', label: 'classify_domain', kind: 'service', summary: 'Selects the domain deterministically.', evidence: 'Classification operation and selected domain metadata.', column: 1, row: 0 },
      { id: 'retrieve', label: 'retrieval / embeddings', kind: 'intelligence', summary: 'Builds context before agent invocation.', evidence: 'Embedding/model and Azure AI Search operations.', column: 1, row: 1 },
      { id: 'agent', label: 'invoke_agent + chat', kind: 'intelligence', summary: 'Runs the Microsoft agent and model reasoning.', evidence: 'Agent and chat operations with supported GenAI attributes.', column: 2, row: 1 },
      { id: 'tool', label: 'execute_tool + MCP', kind: 'boundary', summary: 'Executes the chosen tool through the .NET MCP client and server.', evidence: 'Tool, protocol, server, and provider operations sharing the trace.', column: 3, row: 1 },
    ],
    edges: [
      { source: 'request', target: 'classify' }, { source: 'request', target: 'retrieve' }, { source: 'retrieve', target: 'agent' }, { source: 'agent', target: 'tool' },
    ],
  },
];
