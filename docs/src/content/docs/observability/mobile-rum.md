---
title: Native Mobile RUM
description: Native iOS and Android RUM, Session Replay, logs, error tracking, resource monitoring, and distributed tracing into the InfraAdvisor AI backend
---

InfraAdvisor AI includes two intentionally small native mobile applications that demonstrate how a client interaction becomes a correlated RUM action, network resource, mobile span, backend APM trace, and AI response. They also demonstrate crash capture and release symbol upload so Error Tracking shows actionable native stack frames. Both clients use the deployed `/auth/login` and `/api/query` contracts without changing the backend.

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

Authenticated navigation exposes matching Chat, Error Lab, and Info destinations on both platforms. Error Lab generates handled mobile errors, instrumented API failures, fixed logs, and debug-only crashes. Info shows the logged-in user, API endpoint, Datadog site/environment/service/application ID, and sampling/privacy settings, but deliberately omits the client token. Both apps use the same purple brand color, grouped background, rounded white controls, blue user messages, and light-purple assistant messages.

The native layouts also demonstrate platform-safe responsive patterns. Android uses an in-layout toolbar and applies system-bar plus display-cutout insets to the outer activity root, which is required when modern target SDKs enforce edge-to-edge drawing. iOS gives conversation and model menus full-width rows, uses a full-width backend segmented control, exposes suggestions through an expandable list, and keeps the composer pinned while the rest of the chat scrolls.

Both native applications use `docs/public/favicon.svg` as their icon source so the website, iOS app, and Android app share one identity. `mobile/scripts/generate-app-icons.sh` creates an opaque 1024-pixel iOS asset, Android density-specific legacy icons, and a padded Android adaptive-icon foreground. Generated PNGs are committed so normal builds do not depend on local graphics tooling.

## iOS automatic instrumentation

The iOS 16+ project uses SwiftUI, CocoaPods, `DatadogCore`, `DatadogCrashReporting`, `DatadogLogs`, `DatadogRUM`, `DatadogSessionReplay`, `DatadogTrace`, and a typed `URLSession` API client.

`InfraAdvisorMobileApp.swift` initializes Datadog with the US3 site, demo environment, service name, 100% demo sampling, SwiftUI view/action predicates, and a narrow first-party host rule. It then enables `URLSessionInstrumentation` for `InfraAdvisorURLSessionDelegate` and constructs the API session with an instance of that delegate. Requests created by `APIClient.swift` are observed automatically: the SDK starts the RUM resource, creates the correlated mobile span, and injects trace context only for the trusted InfraAdvisor host.

The RUM `urlSessionTracking` policy and `URLSessionInstrumentation` delegate binding are separate required steps in Datadog SDK 3.x. `URLSession.shared` does not expose the concrete delegate class needed by this integration. Use the delegate-backed automatic instrumentation rather than adding manual resources and spans around the same request, because mixing both patterns would double-count the operation.

Session Replay is enabled after RUM with `replaySampleRate: 100`, `textAndInputPrivacyLevel: .maskSensitiveInputs`, and the SDK's SwiftUI feature flag. Datadog masks fields classified as sensitive, including email, password, and phone, while ordinary application text can remain visible in the replay.

## Android Volley adapter

The Android API 23+ project contains Java source only and uses Volley for all HTTP calls. Datadog does not automatically instrument this request path, so `InstrumentedJsonRequest.java` and `VolleyTelemetry.java` provide a reusable manual boundary. `ApiClient` creates a new instrumented request for every operation; it does not reuse one mutable Volley request instance.

For each request, the adapter starts one RUM resource and one client span, injects Datadog and W3C trace headers into a copy of the request headers, attaches the trace/span correlation fields to the RUM resource, and completes both signals on success, HTTP error, transport error, or cancellation.

An atomic compare-and-set guard makes terminal completion exactly once. This is important because cancellation can race a late Volley callback. Unit tests cover this invariant, propagation-header merging, and removal of query strings and fragments from telemetry URLs.

Android installs `dd-sdk-android-session-replay` and enables replay with a 100% sample rate and `TextAndInputPrivacy.MASK_SENSITIVE_INPUTS` after RUM initialization. The reusable Volley request accepts GET and POST operations, applies a 90-second zero-retry policy to AI queries, and reports timeout, connectivity, authentication, HTTP, and parsing failures distinctly. Avoiding automatic POST retries prevents one prompt from executing twice.

## Logs and Error Lab

Both apps enable Datadog Logs after Core initialization and create a reusable logger with 100% remote sampling, RUM correlation, and trace correlation. They send a fixed startup event, while Error Lab can emit fixed info, warning, and error logs. The logger interface intentionally accepts only controlled `demo.*` metadata and never receives email addresses, passwords, JWTs, prompts, authorization headers, request bodies, response bodies, or raw backend error content.

The handled-error action reports a synthetic exception to RUM Error Tracking and emits a correlated error log. The API-error action requests an intentionally missing route through the same typed `URLSession` or instrumented Volley client used by production calls, which demonstrates a real failed resource and client span without adding a backend failure endpoint. The crash action is available only in Debug builds and requires confirmation.

## Crash reporting and native symbols

iOS enables `DatadogCrashReporting` immediately after Core initialization. Native crash state is persisted on the device and uploaded after the next application launch. Android's enabled RUM module collects uncaught Java exceptions; the optional NDK module is not included because this educational Android application contains Java source only and no application C or C++ code.

Although teams sometimes call every client artifact a source map, these native applications use platform symbol files. iOS Release device builds generate dSYM bundles, and Android's obfuscated Release build generates an R8 `mapping.txt`. Datadog matches those artifacts to crash reports using the iOS dSYM UUID or the Android build ID.

The iOS target has a final build phase that calls `scripts/upload-dsyms.sh`. The script uploads only Release device symbols, defaults to `us3.datadoghq.com`, and safely skips when `DATADOG_API_KEY` is absent. Android applies the Datadog Gradle plugin, enables R8 for Release, and exposes `uploadMappingRelease`. Both upload paths obtain their API key only from the build environment; the API key is never placed in app configuration, source, an application bundle, or telemetry.

For iOS, provide the key through a CI secret or local credential helper and create a device archive. For Android, build the obfuscated release and invoke its mapping task:

```bash
# iOS, from mobile/native/ios
# Set DATADOG_API_KEY through your local secret manager or CI secret store.
export DATADOG_SITE=us3.datadoghq.com
xcodebuild archive -workspace InfraAdvisorMobile.xcworkspace -scheme InfraAdvisorMobile -configuration Release -destination 'generic/platform=iOS' -archivePath build/InfraAdvisorMobile.xcarchive

# Android, from mobile/native/android
# Set DATADOG_API_KEY through your local secret manager or CI secret store.
./gradlew assembleRelease uploadMappingRelease
```

The first Android APK and iOS IPA are created as separate Mobile App Testing applications through **Digital Experience → Settings → Mobile Applications**. This manual creation returns platform-specific Mobile Application IDs; these are Synthetics identifiers and are not the RUM Application IDs packaged in the apps. Datadog does not accept an iOS `.xcarchive`; export a signed `.ipa` with an Apple Development or Ad Hoc identity and provisioning profile. Xcode 26 calls the Development export method `debugging`. An Android testing APK must also be signed before upload.

Once those applications exist, later versions can be automated with `datadog-ci synthetics upload-application`. The command requires `DD_API_KEY`, `DD_APP_KEY`, the applicable Mobile Application ID, a signed `.apk` or `.ipa` path, a unique version name, and optionally `--latest`. Keep privileged keys in a local credential manager or CI secret store, keep the non-secret Mobile Application IDs in local or CI configuration, and keep generated archives under ignored build directories.

```bash
# Android example; use the corresponding iOS application ID and IPA path for iOS.
export DATADOG_SITE=us3.datadoghq.com
npx @datadog/datadog-ci synthetics upload-application \
  --mobileApplicationId "$DATADOG_SYNTHETICS_ANDROID_APPLICATION_ID" \
  --mobileApplicationVersionFilePath app/build/outputs/apk/release/InfraAdvisorMobile-0.1.0-android.apk \
  --versionName "0.1.0" \
  --latest
```

## Selective GitHub Actions releases

The manual `.github/workflows/mobile-release.yml` workflow automates the same release lifecycle on GitHub-hosted runners without granting write access on pushes or pull requests. Its `platform` input selects Android, iOS, or both; `operation` defaults to `build-only` and must be changed to `build-and-sync` before any Datadog mutation occurs; `version_name` controls the public application version; an optional numeric `build_number` controls Android's version code and iOS's bundle version; and `mark_latest` controls whether uploaded Datadog application versions become the default for tests targeting latest.

Every release requires signing credentials because Mobile App Testing needs installable artifacts. Android uses an organization-controlled keystore provided through `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, and `ANDROID_KEY_PASSWORD`. iOS uses an Apple Development certificate/private-key `.p12` and Development provisioning profile provided through `IOS_SIGNING_CERTIFICATE_BASE64`, `IOS_SIGNING_CERTIFICATE_PASSWORD`, and `IOS_PROVISIONING_PROFILE_BASE64`; the non-secret team identifier is stored as repository variable `IOS_DEVELOPMENT_TEAM`. The workflow imports these files into runner-temporary locations, validates signatures, team, bundle-ID authorization, and Development entitlements, uses automatic profile selection so Xcode-managed profiles remain valid, selects the Xcode-version-appropriate Development export method, and removes both platforms' decoded signing files in unconditional cleanup steps.

Explicit Datadog sync additionally requires secrets `DD_API_KEY` and `DD_APP_KEY` plus repository variables `DATADOG_SYNTHETICS_ANDROID_APPLICATION_ID` and/or `DATADOG_SYNTHETICS_IOS_APPLICATION_ID`. The application identifiers come from the initial manual Mobile App Testing application creation and are distinct from the public RUM Application IDs. The workflow pins `@datadog/datadog-ci`, uploads the exact R8 mapping or dSYM set generated by that build, then calls `synthetics upload-application` with a unique `<semantic version> (<build number>)` label. It retains the signed APK/IPA and native symbols as private GitHub Actions artifacts for 14 days whether or not Datadog synchronization is selected.

To configure the workflow, open **Repository Settings → Secrets and variables → Actions**, add the secret values under **Secrets**, and add the three identifiers under **Variables**. Then open **Actions → Build and sync native mobile applications → Run workflow**. Start with `build-only` to validate signing and download the artifacts; use `build-and-sync` only after both manually created Datadog applications and their repository variables exist.

Each Error Lab screen includes a debug-only **Trigger test crash** control. On iOS, Xcode's debugger intercepts `fatalError` and suspends the process, which looks like a frozen or blank simulator app and prevents the SDK from handling a real crash. The iOS control detects this condition and shows recovery instructions instead: build the app, press Stop in Xcode, launch Infra Advisor directly from the Simulator home screen, trigger the crash, and tap the icon again. A debugger-free crash closes the app, and the next launch returns to Login because the JWT is memory-only while the SDK uploads the saved report. iOS crash symbolication requires dSYMs from a physical-device build; Android release deobfuscation requires the mapping file for that exact build ID.

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
7. Sign in, choose a sample prompt, select Python or .NET and an available model, submit multiple turns, reopen the saved conversation, exercise Error Lab, navigate to Info, inspect the response and trace metadata, start a new chat, then log out.

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
2. Confirm `Login`/`LoginActivity`, `Chat`/`ChatActivity`, `Error Lab`/`ErrorLabActivity`, and `Info`/`InfoActivity` RUM views and their navigation/actions.
3. Submit a query and open the `/api/query` resource in the session timeline.
4. Pivot from the resource to its mobile client span.
5. Confirm the trace continues through the InfraAdvisor backend and into the AI agent/model/tool spans.
6. Reopen the saved conversation and confirm its messages, backend, and model reload.
7. Open Error Lab, record a handled error, request the missing route, send the fixed sample logs, and verify their RUM context and safe attributes.
8. Open the session replay and confirm the Login-to-Chat-to-Error-Lab-to-Info journey was recorded with sensitive inputs masked.
9. Log out and confirm subsequent events no longer carry the previous user identity.
10. Run a Debug build without an attached debugger, use Error Lab to trigger the intentional crash, reopen the app, and confirm the crash reaches RUM Error Tracking.
11. Upload the matching Release dSYM or R8 mapping artifact and verify that the Error Tracking stack includes application function, file, and line information.

The local builds and automated tests prove compilation, serialization, error handling, header behavior, and completion lifecycles. The final RUM-to-backend trace check requires a live run because it depends on an active account, network access, and the Datadog applications receiving telemetry.
