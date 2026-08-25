# Infra Advisor mobile clients

Native demo clients live under `native/`; future cross-platform clients have reserved locations under `cross-platform/`.

| Client       | Location                      | Stack                               |
| ------------ | ----------------------------- | ----------------------------------- |
| iOS          | `native/ios`                  | SwiftUI, CocoaPods, Datadog iOS SDK |
| Android      | `native/android`              | Java, Volley, Datadog Android SDK   |
| React Native | `cross-platform/react-native` | Reserved                            |
| .NET MAUI    | `cross-platform/maui`         | Reserved                            |

Both native apps default to `https://infra-advisor-ai.kyletaylor.dev` and use the existing `/auth/login` and `/api/query` endpoints. Runtime credentials are kept in memory and are never committed.

Start with [Observability implementation patterns](OBSERVABILITY_PATTERNS.md), then see each native project README for complete local build, run, test, configuration, and Datadog verification steps.
