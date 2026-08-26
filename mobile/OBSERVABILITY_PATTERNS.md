# Datadog mobile observability patterns

This guide makes the demo's reusable implementation decisions explicit. The two apps deliberately show both SDK-supported automatic instrumentation and a manual adapter for an HTTP client that needs custom integration.

## End-to-end signal flow

```text
Login, Chat, Error Lab, or Info view
  -> RUM action
  -> RUM resource for POST /auth/login or POST /api/query
  -> mobile client span carrying Datadog + W3C trace headers
  -> existing backend APM span
```

At login success, each app sets the Datadog user to the backend user ID and email. Logout clears that identity. Tokens live only in memory, so terminating the app also ends the authenticated application session.

## iOS: automatic URLSession instrumentation

`InfraAdvisorMobileApp.swift` configures `firstPartyHostsTracing` for the Infra Advisor host, enables `URLSessionInstrumentation` for `InfraAdvisorURLSessionDelegate`, and creates a session using that delegate. Requests made by `APIClient.swift` through this session then get:

1. A RUM resource timed by the SDK.
2. A correlated client span because Trace is enabled with RUM bundling.
3. Trace propagation headers only when the destination matches the trusted first-party host.

Both the RUM `urlSessionTracking` configuration and delegate instrumentation are required. `URLSession.shared` does not provide the concrete delegate class the SDK needs for this integration. Keep application networking typed and ordinary; do not manually start a second resource or span around the same request, because that would double-count it.

Named `.trackRUMView(name:)` modifiers make SwiftUI's `Login` and `Chat` screens stable RUM view names even if their Swift type names change later.

## Logs and intentional error examples

Both apps enable the dedicated Datadog Logs module after Core and create one reusable logger with 100% remote sampling plus RUM and trace correlation. A safe initialization event confirms intake after launch. Error Lab can emit fixed information, warning, and error examples, and handled mobile errors are sent to both RUM Error Tracking and Logs so the relationship is visible during a demo.

The logging facades accept only controlled event names and attributes such as `demo.signal`, `demo.platform`, and `demo.intentional`. They never receive identity values, credentials, JWTs, prompts, authorization headers, request bodies, response bodies, or raw backend errors. Preserve that narrow interface when reusing the pattern.

The API-error action requests `/api/error-lab/not-found` through the normal instrumented client. The expected 404 exercises genuine resource, span, header-propagation, duration, status, and completion behavior without creating a privileged failure endpoint or sending user content. Crash actions are confirmation-gated and compiled only into Debug builds.

## Session Replay on both native platforms

Both apps install the dedicated Datadog Session Replay module after RUM is enabled and use a 100% replay sample rate. The iOS configuration explicitly enables SwiftUI recording and `.maskSensitiveInputs`; Android records the activity-based UI with `TextAndInputPrivacy.MASK_SENSITIVE_INPUTS`. This policy masks inputs Datadog classifies as sensitive, including email, password, and phone fields, while allowing ordinary application text to remain useful in the replay. Neither app adds credentials, JWTs, prompts, or response bodies as RUM or span attributes.

The 100% rate is intentional for a deterministic educational demo, not a production recommendation. Session Replay sampling applies within sampled RUM sessions, so both RUM and replay sampling must be considered when adapting the pattern.

## Conversation persistence and backend routing

Both clients use the authenticated Python `/api/conversations` endpoints as a shared conversation control plane because the Python and .NET services use the same conversation store. Model discovery and query execution still route through the selected `/api` or `/api-dotnet` prefix. The first prompt creates a conversation with its model/backend metadata, and each query sends `X-Conversation-ID`, `X-User-ID`, and the stable session ID so the selected backend persists the user/assistant exchange.

Conversation list and detail responses populate the history selector and transcript. Loading a saved conversation restores its backend and model, then locks backend selection until the user starts a new conversation. JWTs remain memory-only even though conversation messages are intentionally persisted by the backend.

## Android: manual Volley adapter

Volley is wrapped by `InstrumentedJsonRequest`. `ApiClient` creates a new request instance for each API operation, while this reusable class applies the same instrumentation lifecycle to every request. At construction it asks `VolleyTelemetry` to:

1. Generate a unique resource key.
2. Start a client span and a matching RUM resource.
3. Inject both Datadog and W3C propagation headers into a separate header map.
4. Add trace/span correlation IDs to the RUM resource attributes.

The request delegates to exactly one terminal method:

| Volley result | RUM result | Span result |
| --- | --- | --- |
| 2xx response | stop resource | finish span |
| HTTP failure | stop resource with status/error | mark error and finish |
| Transport/offline failure | stop resource with error | mark error and finish |
| Cancellation | stop resource with cancellation error | mark error and finish |

`VolleyTelemetry` uses an atomic compare-and-set guard before every terminal path. This matters because cancellation and a late network callback can race; without the guard the resource and span could be completed twice.

To reuse the pattern for another Volley request type, keep telemetry ownership inside the request wrapper: start it immediately before enqueueing, merge the returned trace headers in `getHeaders()`, and route every terminal callback and `cancel()` through the same telemetry instance.

The wrapper accepts the real HTTP method so model discovery appears as a GET resource. Agent queries receive a 90-second timeout and no automatic retries: AI latency can exceed Volley's short default, while retrying a POST could execute the same prompt twice. `ApiError` converts timeouts, offline failures, authentication errors, and backend responses into distinct user-facing messages without exposing payload bodies.

## .NET MAUI: shared automatic HttpClient instrumentation

The .NET 10 MAUI application under `cross-platform/maui` shows the automatic path for one Android/iOS codebase. `MauiProgram.cs` initializes Datadog Core, Logs, Trace, RUM, and Session Replay before registering the application services. RUM sessions, first-party resource traces, and replays use 100% sampling for deterministic demonstrations, and replay uses `TextAndInputPrivacy.MaskSensitiveInputs`.

`InfraAdvisorApiClient` is the only application HTTP boundary and receives one DI-managed `HttpClient`. Because it uses standard `HttpClient` requests, `Datadog.Maui` creates the RUM resource and correlated client span automatically and injects Datadog plus W3C trace context only for `infra-advisor-ai.kyletaylor.dev`. The client adds application correlation headers for the stable AI session, current RUM session, authenticated user, and selected conversation. Do not add manual RUM resources or spans around these same requests.

The streaming query parser reads SSE by line rather than by arbitrary transport buffer. Named `event:` fields are combined with multiline `data:` fields, fragmented responses remain valid, cancellation stops enumeration, malformed events become sanitized application errors, and query POSTs are never automatically replayed. Tool and pipeline events can therefore update the UI while the network resource stays correlated with the backend agent, model, MCP, vision, or transcription spans.

The MAUI observability facade deliberately exposes only controlled logs/errors, user association, and session lifecycle. Login calls `DdSdk.SetUserInfo`; logout clears the in-memory JWT, calls `DdSdk.ClearUserInfo`, and stops the current RUM session. Attachment events record modality, byte size, status, and duration only. The Error Lab uses a handled exception, a missing authenticated API route, fixed logs, and `Environment.FailFast` for a genuine process crash; relaunch is required for stored crash delivery.

Release builds preserve portable PDB information, enable Android R8 mapping output, and generate iOS dSYMs. The `DatadogUploadSymbols` MSBuild property remains off by default and is enabled only in an authorized release job where `DATADOG_API_KEY` is supplied from a secret store. Mobile App Testing upload remains a separate `datadog-ci synthetics upload-application` step using a manually created platform application ID and a signed APK or development/ad-hoc IPA.

## Data minimization boundary

Recorded fields are intentionally limited to:

- HTTP method
- sanitized URL (scheme, host, path; no query or fragment)
- response status
- duration
- response size
- trace/span correlation identifiers

Never add these values to RUM attributes or span tags:

- email/password request bodies
- chat prompts or answer bodies
- bearer tokens or authorization headers
- raw error response bodies

The apps parse response bodies to render the UI, but telemetry receives only a readable error classification and safe transport metadata. Datadog client tokens and RUM application IDs are public client identifiers expected to be present in a mobile binary; Datadog API and application keys are secrets and must never be added.

## Crash reporting and symbolication

iOS enables the dedicated `DatadogCrashReporting` module after Core initialization. A native crash is written on-device and submitted after the next application launch. Android RUM installs uncaught Java exception collection as part of `Rum.enable`; this Java-only app does not add the optional NDK crash module because it contains no application C or C++ code.

Native crash artifacts are symbol files rather than JavaScript source maps. iOS Release device builds produce dSYM bundles whose UUIDs match captured crashes. The final Xcode build phase calls `scripts/upload-dsyms.sh`, which refuses to upload Debug or simulator artifacts and reads `DATADOG_API_KEY` only from the build environment. Android enables R8 for Release, and the Datadog Gradle plugin injects a build ID and registers `uploadMappingRelease` for the generated `mapping.txt` file.

Symbol upload is privileged build infrastructure. It uses a Datadog API key from a local credential helper or CI secret store, while the runtime SDK uses the public client token. Never pass the API key through `BuildConfig`, xcconfig files, application schemes, tracked `datadog-ci.json`, or the mobile binary.

Both Error Lab screens include a debug-only **Trigger test crash** action. On iOS, LLDB intercepts `fatalError` before the SDK can capture it, leaving the process suspended and making the simulator look frozen or blank. The iOS example detects an attached debugger and blocks the crash with recovery instructions: build the app, press Stop in Xcode, launch Infra Advisor directly from the Simulator home screen, trigger the crash, and tap the icon again. A debugger-free crash terminates the process, and the next launch returns to Login because the session is memory-only while the SDK uploads its persisted report. The control is unavailable in release builds, and the intentional error contains no user, prompt, credential, or response data.

## Sampling and production adaptation

RUM sessions, Session Replay recordings, and first-party traces are sampled at 100% in this demo so every live walkthrough is observable. For production, lower those rates based on traffic and cost, use tracking-consent and replay privacy behavior appropriate to the product, and inject configuration per environment. Keep the first-party host allowlist narrow in every environment.

## Live verification checklist

1. Log in with an existing Infra Advisor account.
2. Confirm the RUM session has the backend user ID and email.
3. Confirm separate `Login`, `Chat`, `Error Lab`, and `Info` views and their navigation/actions.
4. Submit a query and open the `/api/query` RUM resource.
5. Pivot to its mobile client span, then confirm the propagated trace continues into the Infra Advisor backend.
6. Reopen the saved conversation and confirm its messages, backend, and model reload.
7. Open Error Lab, record a handled error, request the missing route, send the three sample logs, and confirm the signals carry the active RUM context.
8. Open the session replay and confirm the Login-to-Chat-to-Error-Lab-to-Info journey was recorded with sensitive inputs masked.
9. Log out and confirm later events no longer carry the prior user identity.
10. In a debugger-free Debug run, trigger the platform's test crash from Error Lab, reopen the app, and confirm the issue reaches RUM Error Tracking.
11. Build a Release artifact with a secret-provided API key and confirm its dSYM or R8 mapping file appears under Datadog RUM Debug Symbols.
