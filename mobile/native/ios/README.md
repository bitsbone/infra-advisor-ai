# Infra Advisor iOS demo

SwiftUI iOS 16+ client using CocoaPods and Datadog RUM, Session Replay, and Trace.

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

The default API is the deployed `https://infra-advisor-ai.kyletaylor.dev`, so a local backend is not required. The Chat tab supports persisted conversation selection, prompt suggestions, live model discovery, and Python/.NET backend selection. The Info tab shows the logged-in user and safe Datadog/API configuration without displaying the client token. Logout clears the in-memory JWT and Datadog identity.

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

- `InfraAdvisorMobileApp.swift` initializes Core, RUM, Session Replay, and Trace, records 100% of sampled demo sessions with SwiftUI replay support and `.maskSensitiveInputs`, enables `URLSessionInstrumentation` for the app's concrete session delegate, and limits distributed tracing to the configured first-party backend host.
- `SessionStore.swift` calls `setUserInfo` only after login succeeds and clears both the in-memory JWT and Datadog identity at logout.
- `APIClient.swift` demonstrates typed `URLSession` requests. The Datadog SDK automatically creates resources, spans, and propagation headers; application code does not copy authorization headers or payloads into telemetry.
- `LoginView.swift` and `ChatView.swift` use named SwiftUI RUM views. Their button labels allow the configured SwiftUI action predicate to report useful action names.
- `ChatView.swift` provides the shared purple visual language, Chat/Info tabs, persisted conversation selection, backend/model menus, and prompt suggestions including an MCP procurement example.
- The Chat screen keeps its composer pinned above the tab bar while conversation content scrolls independently. Conversation and model menus use full-width rows, backend selection uses a full-width segmented control, and prompt suggestions expand into readable full-width actions instead of compressing controls on narrow devices.
- `Config/Shared.xcconfig` is the single build-time configuration surface.

The deeper lifecycle and privacy rationale is documented in [`../../OBSERVABILITY_PATTERNS.md`](../../OBSERVABILITY_PATTERNS.md).

## Verify in Datadog

After signing in and submitting a prompt, verify `Login`, `Chat`, and `Info` in RUM Explorer, open the session's replay, inspect the authentication, model, conversation, and query resources, and follow the query resource into its distributed APM trace. Select the saved conversation again and confirm its messages reload. Logout should remove the authenticated user from subsequent events.

In a Debug run, the Xcode console should show Datadog SDK debug messages. To inspect propagation without exposing values, add a symbolic breakpoint or use a debugging proxy and verify that first-party requests contain the header names `x-datadog-trace-id`, `x-datadog-parent-id`, and `traceparent`. Do not log their values in application code.
