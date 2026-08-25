# Infra Advisor iOS demo

SwiftUI iOS 16+ client using CocoaPods and Datadog RUM, Session Replay, Trace, Logs, and Crash Reporting.

## Prerequisites

- macOS with Xcode and an iOS 16+ Simulator runtime
- CocoaPods 1.16+
- An existing InfraAdvisor account; never add its credentials to source or configuration

## Install and run

```bash
cd mobile/native/ios
pod install
open InfraAdvisorMobile.xcworkspace
```

Select the `InfraAdvisorMobile` scheme, choose an iPhone simulator, and press Run. Always open the generated `.xcworkspace`, not the `.xcodeproj`, because CocoaPods provides the Datadog frameworks.

The default API is the deployed `https://infra-advisor-ai.kyletaylor.dev`, so a local backend is not required. The Chat tab supports persisted conversation selection, prompt suggestions, live model discovery, and Python/.NET backend selection. Error Lab demonstrates handled RUM errors, an instrumented API error, correlated logs, and a debug-only crash. The Info tab shows the logged-in user and safe Datadog/API configuration without displaying the client token. Logout clears the in-memory JWT and Datadog identity.

## Test from the terminal

```bash
xcodebuild test \
  -workspace InfraAdvisorMobile.xcworkspace \
  -scheme InfraAdvisorMobile \
  -destination 'platform=iOS Simulator,name=iPhone 16' \
  CODE_SIGNING_ALLOWED=NO
```

If that simulator name is unavailable, run `xcrun simctl list devices available` and substitute an installed device name.

## Build-time configuration

Defaults are in `Config/Shared.xcconfig`. For machine-local values, create the ignored `Config/Local.xcconfig`; its values load after the shared defaults:

```xcconfig
API_BASE_URL = https:/$()/example.test
DD_ENV = local
DD_SERVICE = infra-advisor-mobile-ios-local
```

You can also pass these names as `xcodebuild` build settings. Use the `https:/$()/host` form inside xcconfig files because `//` otherwise begins an xcconfig comment.

Datadog RUM application IDs and client tokens are public client identifiers designed to ship in an app binary. Never add a Datadog API key, application key, account credential, JWT, or other privileged value to this project.

## Reference patterns

- [`InfraAdvisorMobileApp.swift`](InfraAdvisorMobile/InfraAdvisorMobileApp.swift) initializes Core, RUM, Session Replay, Trace, Logs, and Crash Reporting, records 100% of sampled demo sessions and logs, enables SwiftUI replay with `.maskSensitiveInputs`, creates the safe shared logger, enables `URLSessionInstrumentation`, and limits distributed tracing to the configured first-party backend host.
- [`SessionStore.swift`](InfraAdvisorMobile/SessionStore.swift) calls `setUserInfo` only after login succeeds and clears both the in-memory JWT and Datadog identity at logout.
- [`APIClient.swift`](InfraAdvisorMobile/APIClient.swift) demonstrates typed `URLSession` requests and the intentionally missing Error Lab route. The Datadog SDK automatically creates resources, spans, and propagation headers; application code does not copy authorization headers or payloads into telemetry.
- [`LoginView.swift`](InfraAdvisorMobile/LoginView.swift) and [`ChatView.swift`](InfraAdvisorMobile/ChatView.swift) use named SwiftUI RUM views. Their button labels allow the configured SwiftUI action predicate to report useful action names.
- [`ChatView.swift`](InfraAdvisorMobile/ChatView.swift) provides the shared visual language, Chat/Error Lab/Info tabs, persisted conversation selection, backend/model menus, prompt suggestions, handled-error recording, sample log emission, and the guarded debug crash action.
- The Chat screen keeps its composer pinned above the tab bar while conversation content scrolls independently. Conversation and model menus use full-width rows, backend selection uses a full-width segmented control, and prompt suggestions expand into readable full-width actions instead of compressing controls on narrow devices.
- [`Config/Shared.xcconfig`](Config/Shared.xcconfig) is the single build-time configuration surface.
- [`Podfile`](Podfile) declares the modular Datadog dependencies installed through CocoaPods.
- [`scripts/upload-dsyms.sh`](scripts/upload-dsyms.sh) is the release symbolication boundary. The Xcode build phase skips Debug and simulator builds, reads `DATADOG_API_KEY` only from the build environment, defaults `DATADOG_SITE` to `us3.datadoghq.com`, and uploads device dSYMs with `@datadog/datadog-ci`.

## Logs and Error Lab

Logs are enabled at startup with 100% remote sampling and automatic RUM/trace correlation. The app sends one safe initialization log, and the Error Lab can send fixed info, warning, and error examples. The logging facade accepts only fixed event names and safe `demo.*` attributes; do not extend it with email addresses, tokens, prompts, headers, request bodies, or response bodies.

Use the three app tabs to open **Errors**, then choose **Record handled error**, **Request missing route**, or **Send info, warning, and error logs**. The API example uses the normal instrumented `URLSession` against an intentionally nonexistent route, so it creates a real failed RUM resource without requiring a backend-only failure endpoint.

## Crash reporting and dSYM uploads

Mobile native builds do not produce JavaScript source maps. iOS produces dSYM bundles that Datadog uses to turn crash addresses into function names, files, and line numbers. The Release target is configured with `DWARF with dSYM File`, and its final build phase invokes the tracked upload script.

Provide the API key only through a local or CI secret environment variable, then archive a device build. The script intentionally succeeds without uploading when the key is absent so public-repository builds remain safe.

```bash
cd mobile/native/ios
# Set DATADOG_API_KEY through your local secret manager or CI secret store.
export DATADOG_SITE=us3.datadoghq.com
xcodebuild archive -workspace InfraAdvisorMobile.xcworkspace -scheme InfraAdvisorMobile -configuration Release -destination 'generic/platform=iOS' -archivePath build/InfraAdvisorMobile.xcarchive
```

Do not place the API key in `Shared.xcconfig`, `Local.xcconfig`, the Xcode project, or an application scheme. The client token initializes the shipped SDK; the API key is privileged CI-only upload authorization.

For a crash smoke test, run a Debug build without the Xcode debugger attached, open Errors, and tap **Trigger test crash**. Relaunch the application so the SDK can send the stored crash report, then confirm the issue in RUM Error Tracking. Simulator crashes can validate capture, but Datadog symbolication uses dSYMs from physical-device builds.

The deeper lifecycle and privacy rationale is documented in [`../../OBSERVABILITY_PATTERNS.md`](../../OBSERVABILITY_PATTERNS.md).

## Verify in Datadog

After signing in and submitting a prompt, verify `Login`, `Chat`, `Error Lab`, and `Info` in RUM Explorer, open the session's replay, inspect the authentication, model, conversation, and query resources, and follow the query resource into its distributed APM trace. Use Error Lab and confirm its fixed logs carry RUM context, its missing-route request creates a failed resource, and its handled error appears in Error Tracking. Select the saved conversation again and confirm its messages reload. Logout should remove the authenticated user from subsequent events.

In a Debug run, the Xcode console should show Datadog SDK debug messages. To inspect propagation without exposing values, add a symbolic breakpoint or use a debugging proxy and verify that first-party requests contain the header names `x-datadog-trace-id`, `x-datadog-parent-id`, and `traceparent`. Do not log their values in application code.
