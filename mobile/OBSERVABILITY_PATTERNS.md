# Datadog mobile observability patterns

This guide makes the demo's reusable implementation decisions explicit. The two apps deliberately show both SDK-supported automatic instrumentation and a manual adapter for an HTTP client that needs custom integration.

## End-to-end signal flow

```text
Login or Chat view
  -> RUM action
  -> RUM resource for POST /auth/login or POST /api/query
  -> mobile client span carrying Datadog + W3C trace headers
  -> existing backend APM span
```

At login success, each app sets the Datadog user to the backend user ID and email. Logout clears that identity. Tokens live only in memory, so terminating the app also ends the authenticated application session.

## iOS: automatic URLSession instrumentation

`InfraAdvisorMobileApp.swift` configures `firstPartyHostsTracing` for the Infra Advisor host. Requests made by `APIClient.swift` through `URLSession` then get:

1. A RUM resource timed by the SDK.
2. A correlated client span because Trace is enabled with RUM bundling.
3. Trace propagation headers only when the destination matches the trusted first-party host.

This is the preferred pattern for URLSession. Keep application networking typed and ordinary; do not manually start a second resource or span around the same request, because that would double-count it.

Named `.trackRUMView(name:)` modifiers make SwiftUI's `Login` and `Chat` screens stable RUM view names even if their Swift type names change later.

## Android: manual Volley adapter

Volley is wrapped by `ObservedJsonRequest`. At construction it asks `VolleyTelemetry` to:

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

## Sampling and production adaptation

RUM sessions and first-party traces are sampled at 100% in this demo so every live walkthrough is observable. For production, lower those rates based on traffic and cost, use tracking-consent behavior appropriate to the product, and inject configuration per environment. Keep the first-party host allowlist narrow in every environment.

## Live verification checklist

1. Log in with an existing Infra Advisor account.
2. Confirm the RUM session has the backend user ID and email.
3. Confirm separate `Login` and `Chat` views and their button actions.
4. Submit a query and open the `/api/query` RUM resource.
5. Pivot to its mobile client span, then confirm the propagated trace continues into the Infra Advisor backend.
6. Log out and confirm later events no longer carry the prior user identity.
