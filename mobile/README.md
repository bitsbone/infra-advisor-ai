# Infra Advisor mobile clients

Native demo clients live under `native/`; future cross-platform clients have reserved locations under `cross-platform/`.

| Client       | Location                      | Stack                               |
| ------------ | ----------------------------- | ----------------------------------- |
| iOS          | `native/ios`                  | SwiftUI, CocoaPods, Datadog iOS SDK |
| Android      | `native/android`              | Java, Volley, Datadog Android SDK   |
| React Native | `cross-platform/react-native` | Reserved                            |
| .NET MAUI    | `cross-platform/maui`         | .NET 10, Android/iOS, Datadog MAUI  |

All implemented apps default to `https://infra-advisor-ai.kyletaylor.dev` and use the existing authentication, model, conversation, upload, streaming query, suggestion, and feedback endpoints. Runtime credentials are kept in memory and are never committed.

Start with [Observability implementation patterns](OBSERVABILITY_PATTERNS.md), then see the [iOS](native/ios/README.md), [Android](native/android/README.md), or [.NET MAUI](cross-platform/maui/README.md) README for complete local build, run, test, configuration, and Datadog verification steps.
