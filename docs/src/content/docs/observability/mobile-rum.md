---
title: Native Mobile RUM
description: Native iOS and Android RUM, Session Replay, resource monitoring, and distributed tracing into the InfraAdvisor AI backend
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

The Chat view generates a session ID and calls the selected backend's query endpoint. Both apps list and load persisted conversations, discover available models, provide Python/.NET backend selection, and send `X-Conversation-ID` so the backend saves each exchange. Starting a new chat clears the visible transcript and creates a fresh session; the JWT remains memory-only. Prompt suggestions include a federal procurement question designed to exercise the MCP procurement search path.

Authenticated navigation exposes matching Chat and Info destinations on both platforms. Info shows the logged-in user, API endpoint, Datadog site/environment/service/application ID, and sampling/privacy settings, but deliberately omits the client token. Both apps use the same purple brand color, grouped background, rounded white controls, blue user messages, and light-purple assistant messages.

The native layouts also demonstrate platform-safe responsive patterns. Android uses an in-layout toolbar and applies system-bar plus display-cutout insets to the outer activity root, which is required when modern target SDKs enforce edge-to-edge drawing. iOS gives conversation and model menus full-width rows, uses a full-width backend segmented control, exposes suggestions through an expandable list, and keeps the composer pinned while the rest of the chat scrolls.

## iOS automatic instrumentation

The iOS 16+ project uses SwiftUI, CocoaPods, `DatadogCore`, `DatadogRUM`, `DatadogSessionReplay`, `DatadogTrace`, and a typed `URLSession` API client.

`InfraAdvisorMobileApp.swift` initializes Datadog with the US3 site, demo environment, service name, 100% demo sampling, SwiftUI view/action predicates, and a narrow first-party host rule. It then enables `URLSessionInstrumentation` for `InfraAdvisorURLSessionDelegate` and constructs the API session with an instance of that delegate. Requests created by `APIClient.swift` are observed automatically: the SDK starts the RUM resource, creates the correlated mobile span, and injects trace context only for the trusted InfraAdvisor host.

The RUM `urlSessionTracking` policy and `URLSessionInstrumentation` delegate binding are separate required steps in Datadog SDK 3.x. `URLSession.shared` does not expose the concrete delegate class needed by this integration. Use the delegate-backed automatic instrumentation rather than adding manual resources and spans around the same request, because mixing both patterns would double-count the operation.

Session Replay is enabled after RUM with `replaySampleRate: 100`, `textAndInputPrivacyLevel: .maskSensitiveInputs`, and the SDK's SwiftUI feature flag. Datadog masks fields classified as sensitive, including email, password, and phone, while ordinary application text can remain visible in the replay.

## Android Volley adapter

The Android API 23+ project contains Java source only and uses Volley for all HTTP calls. Datadog does not automatically instrument this request path, so `ObservedJsonRequest.java` and `VolleyTelemetry.java` provide a reusable manual boundary.

For each request, the adapter starts one RUM resource and one client span, injects Datadog and W3C trace headers into a copy of the request headers, attaches the trace/span correlation fields to the RUM resource, and completes both signals on success, HTTP error, transport error, or cancellation.

An atomic compare-and-set guard makes terminal completion exactly once. This is important because cancellation can race a late Volley callback. Unit tests cover this invariant, propagation-header merging, and removal of query strings and fragments from telemetry URLs.

Android installs `dd-sdk-android-session-replay` and enables replay with a 100% sample rate and `TextAndInputPrivacy.MASK_SENSITIVE_INPUTS` after RUM initialization. The reusable Volley request accepts GET and POST operations, applies a 90-second zero-retry policy to AI queries, and reports timeout, connectivity, authentication, HTTP, and parsing failures distinctly. Avoiding automatic POST retries prevents one prompt from executing twice.

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

1. Open Android Studio and choose **Open**, then select `mobile/native/android` rather than the repository root.
2. In **Settings → Build, Execution, Deployment → Build Tools → Gradle**, select JDK 17 if Android Studio requests a Gradle JDK.
3. In **Tools → SDK Manager → SDK Platforms**, install Android SDK Platform 37.
4. In **Tools → Device Manager**, choose **Create Virtual Device**, select a Pixel profile, and choose a Google APIs system image with API 35 or newer.
5. Finish the device, start it with the Play button, and wait for the Android home screen.
6. After Gradle sync completes, choose the `app` run configuration and the running emulator in the toolbar, then click **Run**.
7. Sign in, choose a sample prompt, select Python or .NET and an available model, submit multiple turns, reopen the saved conversation, navigate to Info, inspect the response and trace metadata, start a new chat, then log out.

The app uses the deployed HTTPS endpoint by default. If the run configuration is missing, use **File → Sync Project with Gradle Files**. If the emulator is missing from the device selector, verify that it is running in Device Manager and that Platform Tools are installed.

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
2. Confirm `Login`/`LoginActivity`, `Chat`/`ChatActivity`, and `Info`/`InfoActivity` RUM views and their navigation/actions.
3. Submit a query and open the `/api/query` resource in the session timeline.
4. Pivot from the resource to its mobile client span.
5. Confirm the trace continues through the InfraAdvisor backend and into the AI agent/model/tool spans.
6. Reopen the saved conversation and confirm its messages, backend, and model reload.
7. Open the session replay and confirm the Login-to-Chat-to-Info journey was recorded with sensitive inputs masked.
8. Log out and confirm subsequent events no longer carry the previous user identity.

The local builds and automated tests prove compilation, serialization, error handling, header behavior, and completion lifecycles. The final RUM-to-backend trace check requires a live run because it depends on an active account, network access, and the Datadog applications receiving telemetry.
