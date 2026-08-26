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
- [`mobile-release.yml`](../../../.github/workflows/mobile-release.yml) is the manual, selective release boundary. It imports ephemeral Development signing assets, exports and validates an IPA, and writes to Datadog only when the operator selects `build-and-sync`.
- [`Assets.xcassets/AppIcon.appiconset`](InfraAdvisorMobile/Assets.xcassets/AppIcon.appiconset) contains the full-bleed iOS icon generated from the shared documentation favicon.

## App icon

The iOS and Android icons share [`docs/public/favicon.svg`](../../../docs/public/favicon.svg) as their source. After changing that SVG, regenerate both platform asset sets from the repository root. The script prefers Inkscape and can alternatively use Quick Look, `sips`, Perl, and FFmpeg on macOS:

```bash
./mobile/scripts/generate-app-icons.sh
```

The generator removes transparency from the iOS export because App Store icons must be opaque and preserves adaptive-icon padding for Android. Commit the generated PNG files with the source change so local and CI builds do not require Inkscape.

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

## Mobile App Testing application versions

Datadog does not accept an `.xcarchive` as a Mobile App Testing application. Exporting the required `.ipa` requires an Apple Development or Ad Hoc signing identity and matching provisioning profile. Datadog recommends Development or Ad Hoc provisioning because Mobile App Testing re-signs the uploaded application and some entitlements can otherwise be lost.

Set the Apple Developer team only in the local command or CI secret configuration, create a signed device archive, and export it with Xcode's Development distribution method. Xcode 26 names that method `debugging`; older Xcode versions accept the deprecated name `development`.

```bash
export DEVELOPMENT_TEAM=<YOUR_APPLE_TEAM_ID>
xcodebuild archive \
  -workspace InfraAdvisorMobile.xcworkspace \
  -scheme InfraAdvisorMobile \
  -configuration Release \
  -destination 'generic/platform=iOS' \
  -archivePath build/0.1.0-signed/InfraAdvisorMobile.xcarchive \
  DEVELOPMENT_TEAM="$DEVELOPMENT_TEAM" \
  CODE_SIGN_STYLE=Automatic \
  -allowProvisioningUpdates
```

Create an ignored export-options plist with `method` set to `debugging`, `signingStyle` set to `automatic`, and `teamID` set to the same local team, then export the IPA:

```bash
xcodebuild -exportArchive \
  -archivePath build/0.1.0-signed/InfraAdvisorMobile.xcarchive \
  -exportPath build/0.1.0-signed/export \
  -exportOptionsPlist build/0.1.0-signed/ExportOptions.mobile-testing.plist \
  -allowProvisioningUpdates
```

The first IPA must be uploaded manually in Datadog under **Digital Experience → Settings → Mobile Applications → Create Application**. Choose native iOS, upload the signed IPA, name the version `0.1.0`, and optionally mark it latest. The Mobile Application ID created by this workflow is not the iOS RUM Application ID and must not replace `DD_RUM_APPLICATION_ID`.

After the application exists, store its Mobile Application ID as `DATADOG_SYNTHETICS_IOS_APPLICATION_ID` in a local secret manager or CI secret store. New IPA versions can then be uploaded with Datadog CI:

```bash
# Set DD_API_KEY, DD_APP_KEY, and DATADOG_SYNTHETICS_IOS_APPLICATION_ID through a secret manager.
export DATADOG_SITE=us3.datadoghq.com
npx @datadog/datadog-ci synthetics upload-application \
  --mobileApplicationId "$DATADOG_SYNTHETICS_IOS_APPLICATION_ID" \
  --mobileApplicationVersionFilePath build/0.1.0-signed/InfraAdvisorMobile-0.1.0-ios.ipa \
  --versionName "0.1.0" \
  --latest
```

Archives, IPAs, and upload credentials remain ignored and must not be committed. The dSYM upload occurs while archiving and does not depend on the later IPA upload; Datadog matches the crash and dSYM by UUID.

## GitHub Actions releases

Run **Build and sync native mobile applications** from the repository's Actions tab. Select `ios` or `both`, choose a semantic version, optionally provide a positive build number, and choose `build-only` or `build-and-sync`. An omitted build number uses the GitHub run number. Every successful job retains the Development IPA and its matching dSYM bundles for 14 days.

Configure these GitHub Actions secrets for every iOS release:

- `IOS_SIGNING_CERTIFICATE_BASE64` — base64-encoded `.p12` containing an Apple Development certificate and its private key.
- `IOS_SIGNING_CERTIFICATE_PASSWORD` — password used when exporting the `.p12`.
- `IOS_PROVISIONING_PROFILE_BASE64` — base64-encoded Development `.mobileprovision` valid for `dev.kyletaylor.infraadvisor.mobile.ios` or a compatible wildcard App ID.

Configure `IOS_DEVELOPMENT_TEAM` as a repository variable. For `build-and-sync`, also configure `DD_API_KEY` and `DD_APP_KEY` as secrets and `DATADOG_SYNTHETICS_IOS_APPLICATION_ID` as a repository variable after the first manual application creation.

The workflow decodes signing files only under the ephemeral runner temporary directory, creates a temporary keychain, verifies that the profile belongs to the configured team, authorizes this app's bundle ID, and has Development entitlement `get-task-allow=true`. Xcode automatically selects the installed Development profile for the archive and Development export, which supports both Xcode-managed and manually created Development profiles; the export method is `development` through Xcode 16 and `debugging` beginning with Xcode 26. The workflow validates the embedded profile, uploads matching dSYMs during the archive phase, and finally uploads the IPA with pinned Datadog CI. An `always()` cleanup step removes the temporary keychain, certificate, and provisioning-profile files even after failure.

For a crash smoke test, first build and run the Debug app normally, then press Xcode's **Stop** button. Return to the Simulator home screen and tap the **Infra Advisor** icon directly; launching from the icon ensures LLDB is not attached. Sign in again, open Errors, tap **Trigger test crash**, and confirm **Crash now**. The app should close immediately. Tap its Simulator icon again: the app returns to Login because authentication is intentionally memory-only, and the SDK uploads the stored crash report after startup. If Xcode's debugger is still attached, the app blocks the crash and displays these instructions because LLDB would otherwise pause on `fatalError` and make the simulator appear frozen or blank. Simulator crashes validate capture, but Datadog symbolication uses dSYMs from physical-device builds.

The deeper lifecycle and privacy rationale is documented in [`../../OBSERVABILITY_PATTERNS.md`](../../OBSERVABILITY_PATTERNS.md).

## Verify in Datadog

After signing in and submitting a prompt, verify `Login`, `Chat`, `Error Lab`, and `Info` in RUM Explorer, open the session's replay, inspect the authentication, model, conversation, and query resources, and follow the query resource into its distributed APM trace. Use Error Lab and confirm its fixed logs carry RUM context, its missing-route request creates a failed resource, and its handled error appears in Error Tracking. Select the saved conversation again and confirm its messages reload. Logout should remove the authenticated user from subsequent events.

In a Debug run, the Xcode console should show Datadog SDK debug messages. To inspect propagation without exposing values, add a symbolic breakpoint or use a debugging proxy and verify that first-party requests contain the header names `x-datadog-trace-id`, `x-datadog-parent-id`, and `traceparent`. Do not log their values in application code.
