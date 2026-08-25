---
title: Native Mobile RUM
description: Native iOS and Android RUM, resource monitoring, and distributed tracing into the InfraAdvisor AI backend
---

InfraAdvisor AI includes two intentionally small native mobile applications that demonstrate how a client interaction becomes a correlated RUM action, network resource, mobile span, backend APM trace, and AI response. Both clients use the deployed `/auth/login` and `/api/query` contracts without changing the backend.

The source lives under `mobile/native/ios` and `mobile/native/android`. Reserved `mobile/cross-platform/react-native` and `mobile/cross-platform/maui` directories make the platform boundary explicit for future examples.

## What the demo proves

```text
Login or Chat view
  -> RUM action
  -> POST resource
  -> mobile client span with Datadog and W3C propagation
  -> backend APM trace
  -> AI agent, model, and tool spans
```

The Login view calls `POST /auth/login`. A successful response is retained only in memory, and the backend user ID and email are passed to Datadog `setUserInfo`. Logout drops the JWT and clears the Datadog identity so a later session cannot inherit the previous user.

The Chat view generates a session ID and calls `POST /api/query`. It displays the answer, sources, model, session ID, and backend trace ID, providing a visible check that the request reached the instrumented AI service.

## iOS automatic instrumentation

The iOS 16+ project uses SwiftUI, CocoaPods, `DatadogCore`, `DatadogRUM`, `DatadogTrace`, and a typed `URLSession` API client.

`InfraAdvisorMobileApp.swift` initializes Datadog with the US3 site, demo environment, service name, 100% demo sampling, SwiftUI view/action predicates, and a narrow first-party host rule. Requests created by `APIClient.swift` are observed automatically: the SDK starts the RUM resource, creates the correlated mobile span, and injects trace context only for the trusted InfraAdvisor host.

Use automatic instrumentation for URLSession rather than adding manual resources and spans around the same request. Mixing both patterns would double-count the operation.

## Android Volley adapter

The Android API 23+ project contains Java source only and uses Volley for all HTTP calls. Datadog does not automatically instrument this request path, so `ObservedJsonRequest.java` and `VolleyTelemetry.java` provide a reusable manual boundary.

For each request, the adapter starts one RUM resource and one client span, injects Datadog and W3C trace headers into a copy of the request headers, attaches the trace/span correlation fields to the RUM resource, and completes both signals on success, HTTP error, transport error, or cancellation.

An atomic compare-and-set guard makes terminal completion exactly once. This is important because cancellation can race a late Volley callback. Unit tests cover this invariant, propagation-header merging, and removal of query strings and fragments from telemetry URLs.

## Data minimization

The mobile examples record the HTTP method, sanitized scheme/host/path, status, duration, response size, and trace/span correlation identifiers. They do not add passwords, bearer tokens, authorization headers, prompts, answers, response bodies, or raw error bodies to telemetry attributes.

Datadog RUM application IDs and client tokens are public client-side identifiers designed to ship in an application binary. They are not Datadog API or application keys. Never add privileged Datadog keys, backend credentials, real user credentials, or JWT values to mobile configuration, source, tests, documentation, logs, spans, or RUM attributes.

## Run locally

### iOS

Prerequisites: macOS, Xcode with an iOS 16+ Simulator runtime, CocoaPods, and an existing InfraAdvisor account.

```bash
cd mobile/native/ios
pod install
open InfraAdvisorMobile.xcworkspace
```

In Xcode, select the `InfraAdvisorMobile` scheme, choose an iPhone simulator, and press Run. Open the `.xcworkspace`, not the `.xcodeproj`, because CocoaPods supplies the Datadog frameworks. The app uses `https://infra-advisor-ai.kyletaylor.dev` by default.

Run the unit suite without opening Xcode:

```bash
xcodebuild test \
  -workspace InfraAdvisorMobile.xcworkspace \
  -scheme InfraAdvisorMobile \
  -destination 'platform=iOS Simulator,name=iPhone 16' \
  CODE_SIGNING_ALLOWED=NO
```

Build-time defaults live in `mobile/native/ios/Config/Shared.xcconfig`. Create the ignored `Config/Local.xcconfig` to override `API_BASE_URL`, `DD_SITE`, `DD_ENV`, `DD_SERVICE`, `DD_RUM_APPLICATION_ID`, `DD_CLIENT_TOKEN`, or `DD_TRACE_SAMPLE_RATE` without changing tracked files. Use `API_BASE_URL = https:/$()/example.test` inside an xcconfig because `//` begins a comment. Use an HTTPS endpoint for alternate backends.

### Android

Prerequisites: Android Studio with Android SDK 37, JDK 17, an API 23+ emulator or device, and an existing InfraAdvisor account.

Open `mobile/native/android` as a Gradle project in Android Studio, let Gradle sync, select the `app` run configuration, choose an emulator, and press Run. The app uses the deployed HTTPS endpoint by default.

Build and verify from a terminal:

```bash
cd mobile/native/android
./gradlew test lint assembleDebug
```

The debug APK is generated at `mobile/native/android/app/build/outputs/apk/debug/app-debug.apk`.

Defaults live in `mobile/native/android/gradle.properties`. Environment variables override them without editing tracked files:

```bash
export INFRA_ADVISOR_API_BASE_URL=https://example.test
export DD_ENV=local
export DD_SERVICE=infra-advisor-mobile-android-local
./gradlew assembleDebug
```

Available environment overrides are `INFRA_ADVISOR_API_BASE_URL`, `DD_SITE`, `DD_ENV`, `DD_SERVICE`, `DD_RUM_APPLICATION_ID`, `DD_CLIENT_TOKEN`, and `DD_TRACE_SAMPLE_RATE`. One-off Gradle project properties are also supported:

```bash
./gradlew assembleDebug \
  -PinfraAdvisorApiBaseUrl=https://example.test \
  -PdatadogEnv=local \
  -PdatadogService=infra-advisor-mobile-android-local
```

Do not pass secrets on a shared shell because command-line arguments may be retained in shell history or process listings. These mobile configuration values must remain public client identifiers and non-sensitive environment metadata.

## Verify in Datadog

1. Sign in and confirm the session contains the backend user ID and email.
2. Confirm `Login`/`LoginActivity` and `Chat`/`ChatActivity` RUM views and their button actions.
3. Submit a query and open the `/api/query` resource in the session timeline.
4. Pivot from the resource to its mobile client span.
5. Confirm the trace continues through the InfraAdvisor backend and into the AI agent/model/tool spans.
6. Log out and confirm subsequent events no longer carry the previous user identity.

The local builds and automated tests prove compilation, serialization, error handling, header behavior, and completion lifecycles. The final RUM-to-backend trace check requires a live run because it depends on an active account, network access, and the Datadog applications receiving telemetry.
