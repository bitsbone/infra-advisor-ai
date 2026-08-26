# Infra Advisor .NET MAUI client

This .NET 10 MAUI application is the cross-platform reference implementation of Infra Advisor for Android API 23+ and iOS 15+. It shares authentication, typed API contracts, streaming chat, conversation history, model/backend selection, image/audio uploads, feedback, and an observability Error Lab while retaining native platform packaging and permissions.

The runtime client token and RUM application ID are public mobile identifiers. Credentials and JWTs remain in memory. Do not add a Datadog API key, application key, signing certificate, provisioning profile, keystore, real account credentials, prompt text, response body, filename, local path, or attachment URL to tracked configuration or telemetry.

## Architecture

| Area | Reference implementation |
| --- | --- |
| Host, DI, Datadog modules | [`src/InfraAdvisor.Mobile/MauiProgram.cs`](src/InfraAdvisor.Mobile/MauiProgram.cs) |
| Public build/runtime defaults | [`src/InfraAdvisor.Mobile/Configuration/AppConfiguration.cs`](src/InfraAdvisor.Mobile/Configuration/AppConfiguration.cs) |
| RUM identity, logs, errors, session lifecycle | [`src/InfraAdvisor.Mobile/Observability/DatadogObservability.cs`](src/InfraAdvisor.Mobile/Observability/DatadogObservability.cs) |
| RUM session correlation header | [`src/InfraAdvisor.Mobile/Observability/MauiRumSessionProvider.cs`](src/InfraAdvisor.Mobile/Observability/MauiRumSessionProvider.cs) |
| Automatic HTTP resource/trace boundary | [`src/InfraAdvisor.Mobile.Core/Services/InfraAdvisorApiClient.cs`](src/InfraAdvisor.Mobile.Core/Services/InfraAdvisorApiClient.cs) |
| Memory-only JWT and user session | [`src/InfraAdvisor.Mobile.Core/Services/AppSession.cs`](src/InfraAdvisor.Mobile.Core/Services/AppSession.cs) |
| Fragment-safe SSE parsing | [`src/InfraAdvisor.Mobile.Core/Services/SseParser.cs`](src/InfraAdvisor.Mobile.Core/Services/SseParser.cs) |
| Attachment privacy, MIME, size, recording | [`src/InfraAdvisor.Mobile/Services/Media/MediaInputService.cs`](src/InfraAdvisor.Mobile/Services/Media/MediaInputService.cs) and [`src/InfraAdvisor.Mobile.Core/Services/MediaValidator.cs`](src/InfraAdvisor.Mobile.Core/Services/MediaValidator.cs) |
| Login and `SetUserInfo` | [`src/InfraAdvisor.Mobile/ViewModels/LoginViewModel.cs`](src/InfraAdvisor.Mobile/ViewModels/LoginViewModel.cs) |
| Streaming AI/chat operations | [`src/InfraAdvisor.Mobile/ViewModels/ChatViewModel.cs`](src/InfraAdvisor.Mobile/ViewModels/ChatViewModel.cs) |
| Logs, handled errors, API failures, crash | [`src/InfraAdvisor.Mobile/ViewModels/ErrorLabViewModel.cs`](src/InfraAdvisor.Mobile/ViewModels/ErrorLabViewModel.cs) |
| Logout and safe configuration display | [`src/InfraAdvisor.Mobile/ViewModels/InfoViewModel.cs`](src/InfraAdvisor.Mobile/ViewModels/InfoViewModel.cs) |
| Android permissions | [`src/InfraAdvisor.Mobile/Platforms/Android/AndroidManifest.xml`](src/InfraAdvisor.Mobile/Platforms/Android/AndroidManifest.xml) |
| iOS privacy descriptions | [`src/InfraAdvisor.Mobile/Platforms/iOS/Info.plist`](src/InfraAdvisor.Mobile/Platforms/iOS/Info.plist) |
| Symbol/mapping build settings | [`src/InfraAdvisor.Mobile/InfraAdvisor.Mobile.csproj`](src/InfraAdvisor.Mobile/InfraAdvisor.Mobile.csproj) |
| Contract tests | [`tests/InfraAdvisor.Mobile.Core.Tests`](tests/InfraAdvisor.Mobile.Core.Tests) |

One DI-managed `HttpClient` performs every application request. Datadog's MAUI SDK automatically observes this standard client, starts the RUM resource and mobile span, and injects Datadog plus W3C headers only for `infra-advisor-ai.kyletaylor.dev`. Application code adds `X-Session-ID`, `X-DD-RUM-Session-ID`, `X-User-ID`, and `X-Conversation-ID` for backend memory, RUM/AI correlation, and conversation persistence. It does not manually create duplicate network resources.

## Prerequisites

- .NET SDK 10 with `maui`, `maui-android`, and `maui-ios` workloads.
- Android Studio with Android SDK Platform 36 and an API 23+ emulator, or Xcode matching the installed .NET iOS workload and an iOS 15+ simulator.
- An existing Infra Advisor account. Registration is intentionally outside this demo.

Verify the workloads and restore packages:

```bash
dotnet workload list
cd mobile/cross-platform/maui
dotnet restore InfraAdvisor.Mobile.slnx
dotnet test tests/InfraAdvisor.Mobile.Core.Tests/InfraAdvisor.Mobile.Core.Tests.csproj
```

## Run Android

From Android Studio, open `mobile/cross-platform/maui/InfraAdvisor.Mobile.slnx` or the repository folder, allow the .NET/MAUI and Android plugins to discover the project, open **Tools → Device Manager**, create a Pixel device using API 35 or newer, and start it. Use the terminal command below to install and launch the Debug target on the running emulator:

```bash
cd mobile/cross-platform/maui
dotnet build src/InfraAdvisor.Mobile/InfraAdvisor.Mobile.csproj -f net10.0-android -t:Run
```

Build an APK without launching it:

```bash
dotnet publish src/InfraAdvisor.Mobile/InfraAdvisor.Mobile.csproj -f net10.0-android -c Release
```

The Release project enables R8 and generates `mapping.txt` under the intermediate/output tree. Android uses edge-to-edge-safe MAUI layout rather than manually drawing content behind the status bar.

## Run iOS

Open Xcode once to install the required platform and accept its license, start a simulator, then run:

```bash
cd mobile/cross-platform/maui
dotnet build src/InfraAdvisor.Mobile/InfraAdvisor.Mobile.csproj -f net10.0-ios -t:Run
```

If the build reports that the installed .NET iOS workload requires a newer Xcode, update Xcode to the exact requested version or install a compatible .NET iOS workload; this is a workload/Xcode pairing requirement rather than an application error. For a signed physical-device archive, provide the Apple team, signing key, and provisioning profile only through local MSBuild properties or CI secrets.

## Exercise the AI and observability flows

1. Sign in and confirm Datadog shows a named `Login` view followed by `Chat`, with the backend user ID/email associated with the RUM session.
2. Select Python or .NET and a discovered model, choose a suggestion, or use the federal procurement prompt to exercise an MCP tool call.
3. Watch streaming text and pipeline/tool chips, then inspect citations, trace metadata, response feedback, and contextual follow-up suggestions.
4. Start another conversation, reopen history, and confirm backend/model metadata and transcript restoration.
5. Attach one supported image and one supported audio file, or record WAV audio after granting microphone access. Verify upload and query resources without filenames, payloads, SAS URLs, prompts, or responses in custom telemetry.
6. Open Error Lab to send safe logs, record a handled C# error, create an expected API error resource, or intentionally terminate a debugger-free Debug app.
7. Relaunch after a crash so the SDK can submit the stored crash report. Open Info to inspect safe configuration, then log out and confirm the user identity and RUM session are cleared.
8. Open Session Replay and verify that email/password and other sensitive inputs are masked while navigation remains understandable.

## Release symbols and Mobile App Testing uploads

`DatadogUploadSymbols` defaults to `false`. Set it only during an authorized Release publish and provide `DATADOG_API_KEY` through the environment. The package build targets upload the matching Android R8 mapping or iOS dSYM; portable PDBs preserve managed C# file/line information.

```bash
export DATADOG_API_KEY="$(security find-generic-password -w -s datadog-api-key)"
dotnet publish src/InfraAdvisor.Mobile/InfraAdvisor.Mobile.csproj -f net10.0-android -c Release -p:DatadogUploadSymbols=true
dotnet publish src/InfraAdvisor.Mobile/InfraAdvisor.Mobile.csproj -f net10.0-ios -c Release -p:RuntimeIdentifier=ios-arm64 -p:ArchiveOnBuild=true -p:DatadogUploadSymbols=true
unset DATADOG_API_KEY
```

Do not paste a real key into shell history; the example uses macOS Keychain to make the boundary explicit. The manual GitHub Actions workflow uses repository secrets instead.

Mobile App Testing applications must first be created manually in Datadog. Their platform-specific Mobile Application IDs differ from the RUM application ID. Once created, a signed development/ad-hoc IPA or signed APK can be synchronized with `datadog-ci synthetics upload-application`; unsigned packages and `.xcarchive` bundles are not accepted.

## Data and media boundaries

The backend accepts JPEG, PNG, WebP, WAV, MP3, OGG, and WebM up to 10 MB. The current AI pipelines consume the first image and first audio attachment, so the client enforces one of each per turn. Arbitrary documents are not offered because the backend does not accept document MIME types. Recorded files use the OS cache and are not telemetry attributes.

The 100% RUM, trace, and replay sampling rates make demonstrations deterministic. Revisit those rates before adopting the pattern in production.

Public defaults can be overridden at build time without editing tracked files:

```bash
dotnet build src/InfraAdvisor.Mobile/InfraAdvisor.Mobile.csproj -f net10.0-android -p:InfraAdvisorApiBaseUrl=https://example.test/ -p:InfraAdvisorDatadogEnvironment=local -p:InfraAdvisorDatadogService=infra-advisor-mobile-maui-local
```

The supported properties are `InfraAdvisorApiBaseUrl`, `InfraAdvisorDatadogEnvironment`, `InfraAdvisorDatadogService`, `InfraAdvisorDatadogClientToken`, and `InfraAdvisorDatadogRumApplicationId`. Only public client configuration belongs in these properties; never pass privileged keys or account credentials into the application build.
