---
title: Compare mobile observability paths
description: See how native iOS, native Android, and .NET MAUI represent the same authenticated AI workflow
docType: concept
audience:
  - mobile-developer
  - observability-engineer
maturity: stable
verifiedOn: 2026-08-27
sidebar:
  order: 3
  label: Mobile RUM
---

InfraAdvisor includes native iOS, native Android, and .NET MAUI clients. Each should make the same causal chain observable:

```text
mobile action → RUM resource → client span → backend request → agent/model/tool work
```

## Shared privacy and lifecycle contract

All clients can observe views, authenticated resources, backend/model selection, media uploads, handled errors, logs, and crashes. They do not add passwords, JWTs, prompts, answers, filenames, signed URLs, or media content to custom telemetry.

Identity is set only after authentication and cleared on logout. The MAUI client stores its authenticated session in iOS Keychain or Android secure storage, restores the same Datadog user when the app restarts, and deletes the protected value during logout. A request resource and span must start once and complete once—even when cancellation, streaming, or error paths race.

## Implementation comparison

| Client | Networking path | Key learning point |
|---|---|---|
| Native iOS | Instrumented `URLSession` | SDK-native RUM and trace propagation around SwiftUI workflows |
| Native Android | Volley adapter | Explicit lifecycle wrapper that prevents duplicate completion |
| .NET MAUI | Typed `HttpClient` and Datadog MAUI SDK | Shared presentation/domain code with platform release artifacts |

The MAUI client also demonstrates streaming chat, history, structured evidence, backend/model selection, image/audio upload, and diagnostics. See the repository's mobile READMEs for local simulator and device prerequisites; those commands change more often than the observability model.

The MAUI client uses Prism page navigation rather than MAUI Shell. The global Prism navigation event records only navigation type and outcome through the existing sanitized observability facade, while Datadog's automatic view tracking continues to name Login, Chat, History, Errors, and Info. Navigation failures therefore remain observable without adding routes, query parameters, prompts, or account data to custom telemetry.

The authenticated Chat destination opens directly to a compact new-conversation composer. Backend and model use native platform pickers, suggestions appear once and include broad Grants.gov and SAM.gov MCP examples, saved conversations live only in the History tab, and Android uses the same bottom tab placement as iOS. Assistant Markdown is normalized into readable headings and lists; raw citation-button grids are omitted because normalized tool results already appear in the evidence sheet. These UI-only states remain inside the named Chat RUM view so a demo session stays easy to follow without creating noisy view transitions.

Copy and feedback actions show their outcome beside the answer. Successful feedback still emits the sanitized `ai.feedback` operation and API resource, while visible failure text lets the user retry without adding answer content to telemetry.

Helpful sends the categorical value `positive` with a passing assessment; Report sends `reported` with a failing assessment. The .NET backend uses Datadog's Evaluations API at `/api/intake/llm-obs/v2/eval-metric`, retaining `data.type=evaluation_metric` and setting `event_kind=feedback`. Both backends include the authenticated user ID as `submitter.id`, use the response span as the only target, and omit `join_on`. The signal appears in LLM Observability feedback views and can drive analysis or automation, but Report does not create a review queue or support ticket by itself. The Datadog API key remains server-side.

The event contract follows the [Datadog Evaluations API](https://docs.datadoghq.com/llm_observability/instrument/api/?tab=example#evaluations-api) and [end-user feedback requirements](https://docs.datadoghq.com/llm_observability/configure/evaluations/end_user_feedback/).

## Verify one mobile request

1. Log in, restart the app, and confirm the protected session restores the intended stable RUM user identity without recording the JWT.
2. Submit a query and locate one resource with one matching client span.
3. Follow distributed trace context into the selected backend.
4. Add media and verify telemetry contains kind/size/duration but not filename, signed URL, or content.
5. Log out and verify the saved session is removed and later events no longer carry the prior user identity.
6. Exercise a handled failure and confirm the resource, span, log, and error agree on outcome.

## Release symbols and test artifacts

Release builds produce platform-specific debug artifacts: Android R8 mappings and iOS dSYMs. The MAUI workflow can upload those artifacts only in authorized operations where Datadog credentials come from secret storage. Mobile App Testing application uploads are a separate step with platform-specific application IDs.

Build-only workflows should remain useful without signing or Datadog write credentials. Installable iOS artifacts require the appropriate certificate and App Store Connect material; none belongs in committed configuration.

<span id="source-guides"></span>

- [Native iOS source and run guide](https://github.com/bitsbone/infra-advisor-ai/tree/main/mobile/native/ios)
- [Native Android source and run guide](https://github.com/bitsbone/infra-advisor-ai/tree/main/mobile/native/android)
- [.NET MAUI source and run guide](https://github.com/bitsbone/infra-advisor-ai/tree/main/mobile/cross-platform/maui)

Continue to [Browser RUM](../rum/) to compare the web path or [Multimodal input](/infra-advisor-ai/llm-engineering/multimodal/) for the backend media boundary.
