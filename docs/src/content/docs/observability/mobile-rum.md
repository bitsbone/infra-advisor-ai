---
title: Mobile RUM
description: RUM, tracing, Session Replay, logs, and Error Tracking for the InfraAdvisor mobile clients
---

InfraAdvisor includes native iOS, native Android, and .NET MAUI clients. Each client demonstrates the same observable AI workflow:

```text
User action → RUM resource → mobile trace → backend trace → AI model or MCP tool
```

## What is instrumented

- Login and authenticated user identity
- Chat, history, diagnostics, and profile views
- API resources and distributed trace headers
- AI model and backend selection
- Image and audio uploads
- Logs, handled errors, API failures, and crashes
- Session Replay with sensitive inputs masked

Credentials, JWTs, prompts, responses, and uploaded media are not added to telemetry.

## Native iOS

The SwiftUI app uses CocoaPods and Datadog's iOS SDK. `URLSession` instrumentation creates RUM resources and propagates Datadog and W3C trace headers to the InfraAdvisor API.

See the [iOS source and run guide](https://github.com/kyletaylored/infra-advisor-ai/tree/main/mobile/native/ios).

## Native Android

The Java app uses Volley. Its reusable request adapter creates one RUM resource and client span per request, injects trace headers, and completes telemetry exactly once.

See the [Android source and run guide](https://github.com/kyletaylored/infra-advisor-ai/tree/main/mobile/native/android).

## .NET MAUI

The MAUI app uses one typed `HttpClient`, which Datadog instruments automatically. It includes streaming chat, conversation history, structured infrastructure evidence, model/backend selection, image/audio uploads, and diagnostics.

`DdSdk.SetUserInfo` associates the backend user ID and email after login. `DdSdk.ClearUserInfo` removes the identity during logout.

Android and iOS share one MAUI RUM application:

- Application ID: `fe90f908-da00-4d7c-9b24-6af11cee68a4`
- Client token: `pub884d0800477e2d252b992acb168fc7a5`

See the [MAUI source and run guide](https://github.com/kyletaylored/infra-advisor-ai/tree/main/mobile/cross-platform/maui).

## MAUI release configuration

`Datadog.Maui` uploads Android R8 mappings and iOS dSYMs when `DatadogUploadSymbols=true`.

Add these GitHub Actions secrets:

- `DD_API_KEY`: Datadog API key.
- `DD_APP_KEY`: Datadog application key.
- `MAUI_IOS_SIGNING_CERTIFICATE_BASE64`: Base64-encoded Development or Ad Hoc `.p12`.
- `MAUI_IOS_SIGNING_CERTIFICATE_PASSWORD`: `.p12` password.
- `MAUI_IOS_PROVISIONING_PROFILE_BASE64`: Base64-encoded Development or Ad Hoc provisioning profile.

Android uses a temporary test keystore generated during the workflow. The iOS signing values are required only when producing an installable IPA; `build-only` produces a simulator `.app` without them.

Add these GitHub Actions variables:

- `DATADOG_SYNTHETICS_MAUI_ANDROID_APPLICATION_ID`: Android Mobile App Testing application ID.
- `DATADOG_SYNTHETICS_MAUI_IOS_APPLICATION_ID`: iOS Mobile App Testing application ID.

The Synthetics IDs are platform-specific Mobile App Testing upload targets. They are separate from the shared MAUI RUM application ID.

Run **Build and sync .NET MAUI mobile applications** from GitHub Actions:

- `build-only`: build an Android APK and iOS simulator app.
- `upload-symbols`: build and upload mapping and dSYM files.
- `build-and-sync`: build, upload symbols, and upload the APK and IPA to Mobile App Testing.

The same variable names are listed in `.env.example` as a setup template. Keep API keys and signing material in GitHub Secrets, never in committed environment files.
