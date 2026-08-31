# Infra Advisor Android demo

Native Java Android client using Volley and Datadog RUM, Session Replay, Trace, Logs, and Error Tracking. Android API 23+ is supported; compile/target SDK is 37.

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

The default API is the deployed `https://infra-advisor-ai.bitsbone.com`, so a local backend is not required. The chat composer provides sample prompts, persisted conversation selection, model discovery from the selected backend, and Python/.NET backend selection. The first prompt creates a conversation record; later turns include its ID so the backend persists the exchange. Choose **New** to clear the visible transcript, create a fresh session, and unlock backend selection. The JWT is never persisted by the app.

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

- [`InfraAdvisorApplication.java`](app/src/main/java/dev/kyletaylor/infraadvisor/mobile/InfraAdvisorApplication.java) initializes Core, RUM, Trace, Logs, Session Replay, and the safe shared logger once, then installs activity view tracking for every activity.
- [`Session.java`](app/src/main/java/dev/kyletaylor/infraadvisor/mobile/Session.java) keeps the JWT in memory, calls `setUserInfo` after a successful login, and clears both values during logout.
- [`ApiClient.java`](app/src/main/java/dev/kyletaylor/infraadvisor/mobile/ApiClient.java) is the single Volley request factory used by login, chat, conversation, model, and Error Lab API operations.
- [`InstrumentedJsonRequest.java`](app/src/main/java/dev/kyletaylor/infraadvisor/mobile/observability/InstrumentedJsonRequest.java) is the reusable per-call Volley boundary. `ApiClient` creates a fresh instance for every operation; the class centralizes trace propagation and exactly-once telemetry completion without logging the bearer token or JSON body.
- [`VolleyTelemetry.java`](app/src/main/java/dev/kyletaylor/infraadvisor/mobile/observability/VolleyTelemetry.java) owns one RUM resource and one client span per request. Its atomic terminal guard makes success, HTTP error, transport error, and cancellation mutually exclusive completion paths.
- [`ChatActivity.java`](app/src/main/java/dev/kyletaylor/infraadvisor/mobile/ChatActivity.java) demonstrates persisted conversations, prompt suggestions including an MCP procurement example, live model discovery, and backend routing.
- [`ErrorLabActivity.java`](app/src/main/java/dev/kyletaylor/infraadvisor/mobile/ErrorLabActivity.java) demonstrates a handled RUM error, an instrumented HTTP failure, fixed correlated logs, and a guarded debug crash.
- [`InfoActivity.java`](app/src/main/java/dev/kyletaylor/infraadvisor/mobile/InfoActivity.java) shows the authenticated user and safe API/Datadog configuration without displaying the client token.
- [`AppTabs.java`](app/src/main/java/dev/kyletaylor/infraadvisor/mobile/AppTabs.java) provides shared Chat/Error Lab/Info navigation, while [`SystemBarInsets.java`](app/src/main/java/dev/kyletaylor/infraadvisor/mobile/SystemBarInsets.java) keeps every activity below system bars and display cutouts.
- [`app/build.gradle`](app/build.gradle) declares the Datadog modules, exposes non-secret build-time settings as `BuildConfig` fields, enables Java desugaring, configures release R8, and applies the Datadog mapping upload plugin.
- [`mobile-release.yml`](../../../.github/workflows/mobile-release.yml) is the manual, selective release boundary. It builds signed native artifacts on GitHub-hosted runners and writes to Datadog only when the operator selects `build-and-sync`.
- [`AndroidManifest.xml`](app/src/main/AndroidManifest.xml), [`mipmap-anydpi-v26`](app/src/main/res/mipmap-anydpi-v26), and the density-specific `mipmap-*` directories provide adaptive and legacy launcher icons generated from the shared documentation favicon.
- Enabling RUM installs Datadog's uncaught Java exception collection. Error Lab exposes a confirmation-gated debug-only crash trigger; release builds keep that destructive control hidden.
- The Datadog Gradle plugin adds a unique build ID to obfuscated release artifacts and registers `uploadMappingRelease` so R8 stack frames can be deobfuscated in Error Tracking.

## App icon

The Android and iOS icons share [`docs/public/favicon.svg`](../../../docs/public/favicon.svg) as their source. After changing that SVG, regenerate both platform asset sets from the repository root. The script prefers Inkscape and can alternatively use Quick Look, `sips`, Perl, and FFmpeg on macOS:

```bash
./mobile/scripts/generate-app-icons.sh
```

The generator creates density-specific fallback PNGs for Android API 23–25 and a padded foreground plus full-bleed blue background for adaptive icons on API 26+. Commit the generated PNG files with the source change so local and CI builds do not require Inkscape.

## Logs and Error Lab

Logs are enabled at startup with 100% remote sampling and automatic RUM/trace correlation. One fixed initialization log is sent when the app starts, and Error Lab can send fixed info, warning, and error examples. `demoAttributes` contains only controlled `demo.*` metadata; never add email addresses, tokens, prompts, request headers, or payload bodies.

Use the bottom **Errors** tab, then choose **Record handled error**, **Request missing API route**, or **Send info, warning, and error logs**. The API example goes through `ApiClient` and `InstrumentedJsonRequest`, proving that failure resources and spans use the exact same Volley observability lifecycle as normal backend calls.

## Crash reporting and R8 mapping uploads

Android native Java builds do not produce JavaScript source maps. With release minification enabled, R8 produces `app/build/outputs/mapping/release/mapping.txt`. The Datadog Gradle plugin associates that file with the release build ID and uploads it using a Datadog API key supplied only at build time.

Store `DATADOG_API_KEY` in your CI secret manager or a local credential helper, export it only for the upload process, and run:

```bash
cd mobile/native/android
# Set DATADOG_API_KEY through your local secret manager or CI secret store.
./gradlew assembleRelease uploadMappingRelease
```

The plugin is configured for US3 and also accepts `DD_API_KEY`. Never add `datadog-ci.json`, an API key, or an application key to the repository or `BuildConfig`; only the public mobile client token belongs in the application binary. Debug builds are not obfuscated and therefore do not have a mapping upload task.

For a crash smoke test, run the Debug app, open Errors, and tap **Trigger test crash**. Relaunch the application and confirm the uncaught `IllegalStateException` appears in RUM Error Tracking. Release crashes become readable after their matching `uploadMappingRelease` task succeeds.

## Mobile App Testing application versions

The first APK must be uploaded manually in Datadog under **Digital Experience → Settings → Mobile Applications → Create Application**. Choose native Android, upload a signed APK, name the version `0.1.0`, and optionally mark it latest. The Mobile Application ID created by this workflow is not the Android RUM Application ID and must not replace `DD_RUM_APPLICATION_ID`.

After the application exists, store its Mobile Application ID as `DATADOG_SYNTHETICS_ANDROID_APPLICATION_ID` in a local secret manager or CI secret store. New signed APK versions can then be uploaded with Datadog CI:

```bash
# Set DD_API_KEY, DD_APP_KEY, and DATADOG_SYNTHETICS_ANDROID_APPLICATION_ID through a secret manager.
export DATADOG_SITE=us3.datadoghq.com
npx @datadog/datadog-ci synthetics upload-application \
  --mobileApplicationId "$DATADOG_SYNTHETICS_ANDROID_APPLICATION_ID" \
  --mobileApplicationVersionFilePath app/build/outputs/apk/release/InfraAdvisorMobile-0.1.0-android.apk \
  --versionName "0.1.0" \
  --latest
```

Release artifacts remain under ignored `app/build/outputs/` and must not be committed. The local `InfraAdvisorMobile-0.1.0-android.apk` demo artifact is aligned and signed with the machine-local Android debug certificate for Datadog device testing only; use an organization-controlled release key for any distributed build.

## GitHub Actions releases

Run **Build and sync native mobile applications** from the repository's Actions tab. Select `android` or `both`, choose a semantic version, optionally provide a positive version code, and choose `build-only` or `build-and-sync`. An omitted version code uses the GitHub run number. Every successful job retains the signed APK and matching `mapping.txt` for 14 days.

Configure these GitHub Actions secrets for every Android release:

- `ANDROID_KEYSTORE_BASE64` — base64-encoded organization-controlled keystore file.
- `ANDROID_KEYSTORE_PASSWORD` — keystore password.
- `ANDROID_KEY_ALIAS` — signing key alias.
- `ANDROID_KEY_PASSWORD` — signing key password.

Configure `DD_API_KEY` and `DD_APP_KEY` as secrets only for `build-and-sync`, and configure `DATADOG_SYNTHETICS_ANDROID_APPLICATION_ID` as a repository variable after the first manual application creation. The workflow validates presence without printing values, writes the decoded keystore only under the ephemeral runner temporary directory, pins Datadog CI, uploads the R8 mapping generated for the exact build, signs and verifies the APK, and then uploads that APK as a uniquely named Datadog application version. An `always()` cleanup step removes the decoded keystore even after failure.

The workflow passes `MOBILE_VERSION_NAME` and `MOBILE_VERSION_CODE` to Gradle. Local builds retain the tracked `0.1.0` and version-code `1` defaults, while CI can create new versions without editing source files.

See [`../../OBSERVABILITY_PATTERNS.md`](../../OBSERVABILITY_PATTERNS.md) for the event lifecycle, field-sanitization rules, and comparison with iOS automatic instrumentation.

## Verify in Datadog

After a live login and query, verify Login/Chat/ErrorLab/Info views, conversation and model resources, the authenticated user, the Session Replay recording, and the continued backend APM trace in Datadog US3. Use Error Lab and confirm its fixed logs carry RUM context, its missing-route request creates a failed resource, and its handled error appears in Error Tracking. Select the saved conversation again and confirm its messages reload. Logout should remove the authenticated user from subsequent events.

If a query fails, the app distinguishes agent timeout, emulator connectivity, authentication, HTTP, and response parsing failures. AI queries use a 90-second timeout and zero retries so a slow request is not duplicated. Confirm the emulator can open `https://infra-advisor-ai.bitsbone.com` in its browser if the app reports that it cannot reach the API.
