export const promptTargetingFlow = [
  {
    id: 'prompt-targeting',
    label: 'Prompt version targeting',
    description: 'How a subagent resolves which prompt version to run, and how that choice becomes visible on the resulting LLM span.',
    nodes: [
      { id: 'request', label: 'Subagent request', kind: 'client', summary: 'A router, specialist, or the .NET agent is about to run.', detail: 'Every managed prompt_id (Python’s router + 5 specialists + describe-image + faithfulness-eval, .NET’s single merged agent) goes through this same resolution path before its system prompt is set.', why: 'One resolution path for both backends keeps them comparable instead of accidentally diverging.', evidence: 'fetch_prompt() in agent-api, AgentHolder.GetAgentAsync() in agent-api-dotnet.', position: { x: 0, y: 0 } },
      { id: 'flag-eval', label: 'OpenFeature flag evaluation', kind: 'intelligence', summary: 'Evaluates prompt-version.<prompt_id> with env=DD_ENV.', detail: 'Python calls ddtrace.openfeature.DataDogProvider through the plain openfeature-sdk client; .NET calls Datadog.FeatureFlags.OpenFeature’s DatadogProvider through the same OpenFeature API. Both read the identical flag, evaluated locally (cached, no network round-trip per call).', why: 'One flag per prompt_id, evaluated the same way in both languages, is the single configuration surface — not two disconnected mechanisms.', evidence: 'observability/feature_flags.py (Python), Services/PromptVersionFlags.cs (.NET).', position: { x: 245, y: 0 } },
      { id: 'version-decision', label: 'Resolved version', kind: 'boundary', summary: '0 (no override) or a pinned registry version.', detail: 'The flag’s integer value is the decision point: 0 means fall through to the existing default-version behavior unchanged; any positive value pins an exact registry version for this environment.', why: 'A single sentinel value keeps the fail-open path and the pinned path from being two different code shapes.', evidence: 'The flag_value column in the admin UI’s prompt-versions panel.', position: { x: 490, y: 0 } },
      { id: 'registry-fetch', label: 'Prompt Registry fetch', kind: 'state', summary: 'Fetches the pinned version, or the existing default/fallback.', detail: 'Python: LLMObs.get_prompt(prompt_id, version=N, fallback=...) when pinned. .NET: DatadogPromptManagementClient hits /prompts/{id}/versions/{N} when pinned, the static latest-version path otherwise. Both fail open to the hardcoded template on any registry error.', why: 'Pinning a version must never be less reliable than today’s always-on fallback.', evidence: 'HTTP spans to api.<site>/api/unstable/llm-obs/v1/prompts/...', position: { x: 735, y: 0 } },
      { id: 'span-annotate', label: 'LLM span annotation', kind: 'telemetry', summary: 'Tags the resulting chat/agent span with the resolved version.', detail: 'No new plumbing here: the resolved {id, version, source} flows into the exact same tagging path this project already had — Python’s LLMObs.annotation_context(prompt=...), .NET’s prompt.version/_dd.ml_obs.prompt_tracking activity tags.', why: 'Correlating a prompt version to behavior only works if every resolution path reaches the same span attribute.', evidence: 'prompt.version and _dd.ml_obs.prompt_tracking tags on invoke_agent/chat spans.', position: { x: 980, y: 0 } },
      { id: 'trace', label: 'Correlated in Datadog', kind: 'telemetry', summary: 'A trace, eval score, or dashboard can now be grouped by prompt.version.', detail: 'The admin UI’s prompt-versions panel shows the same data live, per pod, without needing to query Datadog at all — useful for confirming a flag change actually took effect.', why: 'The whole point of pinning a version is being able to ask "did that change help?" afterward.', evidence: 'GET /admin/prompts/status (Python), GET /prompts/status (.NET), and the AdminTab → Prompt versions panel.', position: { x: 1225, y: 0 } },
    ],
    edges: [
      { source: 'request', target: 'flag-eval', label: 'resolve prompt_id' },
      { source: 'flag-eval', target: 'version-decision', label: 'flag value' },
      { source: 'version-decision', target: 'registry-fetch', label: 'pinned or default' },
      { source: 'registry-fetch', target: 'span-annotate', label: '{id, version, source}' },
      { source: 'span-annotate', target: 'trace', label: 'prompt.version tag' },
    ],
  },
];
