# Infra Advisor Android demo

Native Java Android client using Volley and Datadog RUM, Session Replay, and Trace. Android API 23+ is supported; compile/target SDK is 37.

## Prerequisites

- Android Studio with Android SDK 37
- JDK 17
- An API 23+ emulator or physical device
- An existing InfraAdvisor account; never add its credentials to source or configuration

## Install and run

1. Open Android Studio and choose **Open**.
2. Select the repository's `mobile/native/android` directory, not the repository root.
3. If Android Studio prompts for a Gradle JDK, open **Settings → Build, Execution, Deployment → Build Tools → Gradle** and select JDK 17.
4. Open **Tools → SDK Manager → SDK Platforms**, install Android SDK Platform 37, and apply the changes.
5. Open **Tools → Device Manager**, choose **Create Virtual Device**, select a Pixel phone profile, and click **Next**.
6. Select or download a modern Google APIs system image with API 35 or newer, finish creating the device, and click its Play button to boot it.
7. Wait for Gradle sync and indexing to complete, select the `app` run configuration and the running virtual device in the toolbar, then click **Run**.
8. When the app opens, sign in with an existing InfraAdvisor account, submit the example prompt, inspect the response and trace metadata, and use Logout to clear the session.

The default API is the deployed `https://infra-advisor-ai.kyletaylor.dev`, so a local backend is not required. The chat composer provides sample prompts, persisted conversation selection, model discovery from the selected backend, and Python/.NET backend selection. The first prompt creates a conversation record; later turns include its ID so the backend persists the exchange. Choose **New** to clear the visible transcript, create a fresh session, and unlock backend selection. The JWT is never persisted by the app.

If the `app` run configuration does not appear, use **File → Sync Project with Gradle Files**. If the emulator is not listed, confirm it is running in Device Manager and that Android Studio's selected SDK contains Platform Tools. A physical device requires USB debugging; an emulator does not.

## Test and build from the terminal

```bash
cd mobile/native/android
./gradlew test lint assembleDebug
```

The debug APK is generated at `app/build/outputs/apk/debug/app-debug.apk`.

## Build-time configuration

Defaults are tracked Gradle properties. Override them with environment variables for Android Studio or local shell builds:

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

Do not put secrets on a shared command line or export them into a long-lived shell because history, process listings, and child processes may expose them. The supported mobile values must remain non-sensitive configuration. Datadog RUM application IDs and client tokens are public client identifiers designed to ship in an app binary. Never add a Datadog API key, application key, account credential, JWT, or other privileged value.

The Volley adapter in `observability/` manually records RUM resources, creates mobile spans, injects Datadog/W3C context, and completes telemetry on every terminal request path.

## Reference patterns

- `InfraAdvisorApplication.java` initializes Core, RUM, and Trace once and installs activity view tracking for `LoginActivity`, `ChatActivity`, and `InfoActivity`.
- `InfraAdvisorApplication.java` also enables Session Replay at 100% for sampled demo sessions and explicitly uses `TextAndInputPrivacy.MASK_SENSITIVE_INPUTS`.
- `Session.java` keeps the JWT in memory, calls `setUserInfo` after a successful login, and clears both values during logout.
- `ObservedJsonRequest.java` is the reusable Volley boundary. It merges trace propagation headers without logging the bearer token or JSON request body, supports the HTTP method used by each API operation, and applies an explicit timeout without automatic retries.
- `VolleyTelemetry.java` owns one RUM resource and one client span per request. Its atomic terminal guard makes success, HTTP error, transport error, and cancellation mutually exclusive completion paths.
- `ChatActivity.java` demonstrates persisted conversation creation/list/detail, stable multi-turn sessions, prompt suggestions including an MCP procurement example, live model discovery, and backend routing through `/api` or `/api-dotnet`.
- `InfoActivity.java` is a separately tracked view that shows the authenticated user and safe API/Datadog configuration without displaying the client token.
- `SystemBarInsets.java` is the reusable edge-to-edge pattern for target SDK 35 and later: each activity uses an in-layout toolbar and applies status-bar, display-cutout, and navigation-bar insets to its outer root so controls never render underneath system chrome.
- `app/build.gradle` exposes non-secret build-time settings as `BuildConfig` fields and enables Java desugaring required by the SDK.

See [`../../OBSERVABILITY_PATTERNS.md`](../../OBSERVABILITY_PATTERNS.md) for the event lifecycle, field-sanitization rules, and comparison with iOS automatic instrumentation.

## Verify in Datadog

After a live login and query, verify Login/Chat/Info views, conversation and model resources, the authenticated user, the Session Replay recording, and the continued backend APM trace in Datadog US3. Select the saved conversation again and confirm its messages reload. Logout should remove the authenticated user from subsequent events.

If a query fails, the app distinguishes agent timeout, emulator connectivity, authentication, HTTP, and response parsing failures. AI queries use a 90-second timeout and zero retries so a slow request is not duplicated. Confirm the emulator can open `https://infra-advisor-ai.kyletaylor.dev` in its browser if the app reports that it cannot reach the API.
