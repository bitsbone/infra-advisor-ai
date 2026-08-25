# Infra Advisor Android demo

Native Java Android client using Volley and Datadog RUM/Trace. Android API 23+ is supported; compile/target SDK is 37.

## Prerequisites

- Android Studio with Android SDK 37
- JDK 17
- An API 23+ emulator or physical device
- An existing InfraAdvisor account; never add its credentials to source or configuration

## Install and run

Open `mobile/native/android` as a Gradle project in Android Studio, allow Gradle sync to finish, select the `app` run configuration, choose an emulator or connected device, and press Run.

The default API is the deployed `https://infra-advisor-ai.kyletaylor.dev`, so a local backend is not required. Sign in with an existing account, submit the example prompt, inspect the answer and trace metadata, then use Logout to clear the in-memory JWT and Datadog identity.

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

- `InfraAdvisorApplication.java` initializes Core, RUM, and Trace once and installs activity view tracking for `LoginActivity` and `ChatActivity`.
- `Session.java` keeps the JWT in memory, calls `setUserInfo` after a successful login, and clears both values during logout.
- `ObservedJsonRequest.java` is the reusable Volley boundary. It merges trace propagation headers without logging the bearer token or JSON request body.
- `VolleyTelemetry.java` owns one RUM resource and one client span per request. Its atomic terminal guard makes success, HTTP error, transport error, and cancellation mutually exclusive completion paths.
- `app/build.gradle` exposes non-secret build-time settings as `BuildConfig` fields and enables Java desugaring required by the SDK.

See [`../../OBSERVABILITY_PATTERNS.md`](../../OBSERVABILITY_PATTERNS.md) for the event lifecycle, field-sanitization rules, and comparison with iOS automatic instrumentation.

## Verify in Datadog

After a live login and query, verify Login/Chat views, the two POST resources, the authenticated user, and the continued backend APM trace in Datadog US3. Logout should remove the authenticated user from subsequent events.
