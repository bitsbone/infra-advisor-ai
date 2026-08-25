# Infra Advisor iOS demo

SwiftUI iOS 16+ client using CocoaPods and Datadog RUM/Trace.

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

The default API is the deployed `https://infra-advisor-ai.kyletaylor.dev`, so a local backend is not required. Sign in with an existing account, submit the example prompt, inspect the answer and trace metadata, then use Logout to clear the in-memory JWT and Datadog identity.

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

- `InfraAdvisorMobileApp.swift` initializes Core, RUM, and Trace and limits distributed tracing to the configured first-party backend host.
- `SessionStore.swift` calls `setUserInfo` only after login succeeds and clears both the in-memory JWT and Datadog identity at logout.
- `APIClient.swift` demonstrates typed `URLSession` requests. The Datadog SDK automatically creates resources, spans, and propagation headers; application code does not copy authorization headers or payloads into telemetry.
- `LoginView.swift` and `ChatView.swift` use named SwiftUI RUM views. Their button labels allow the configured SwiftUI action predicate to report useful action names.
- `Config/Shared.xcconfig` is the single build-time configuration surface.

The deeper lifecycle and privacy rationale is documented in [`../../OBSERVABILITY_PATTERNS.md`](../../OBSERVABILITY_PATTERNS.md).

## Verify in Datadog

After signing in and submitting a prompt, verify `Login` and `Chat` in RUM Explorer, inspect the `/auth/login` and `/api/query` resources, and follow the query resource into its distributed APM trace. Logout should remove the authenticated user from subsequent events.
