# Lessons

- Every new feature must ship with public site documentation under `docs/src/content/docs`, including its application behavior, AI relationship, operational workflow, and observability story.
- Treat the repository as public educational material: never commit privileged secrets or real user data, and explicitly explain when a client-side identifier is designed to be public.
- Do not hard-wrap Markdown prose; keep each paragraph or list item on one physical line.
- A runnable example is incomplete without platform-specific prerequisites, install, run, test, configuration, and telemetry verification instructions.
- For Datadog iOS SDK 3.x network monitoring, configuring RUM `urlSessionTracking` is not sufficient: enable `URLSessionInstrumentation` for a concrete `URLSessionDataDelegate` class and create the session with that delegate; do not assume `URLSession.shared` is instrumented.
- Volley's default timeout is too short for many AI requests; use an explicit long timeout with zero automatic POST retries, preserve exactly-once telemetry completion, and classify timeout versus offline versus HTTP failures for the UI.
- For mobile Session Replay, use the platform-specific `maskSensitiveInputs`/`MASK_SENSITIVE_INPUTS` setting when the requested policy is `mask_sensitive_inputs`; document that it masks Datadog-classified sensitive fields rather than every editable field.
