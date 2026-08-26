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
| URL, action, error, and attribute privacy guard | [`src/InfraAdvisor.Mobile.Core/Services/TelemetrySanitizer.cs`](src/InfraAdvisor.Mobile.Core/Services/TelemetrySanitizer.cs) |
| Automatic HTTP resource/trace boundary | [`src/InfraAdvisor.Mobile.Core/Services/InfraAdvisorApiClient.cs`](src/InfraAdvisor.Mobile.Core/Services/InfraAdvisorApiClient.cs) |
| Memory-only JWT and user session | [`src/InfraAdvisor.Mobile.Core/Services/AppSession.cs`](src/InfraAdvisor.Mobile.Core/Services/AppSession.cs) |
| Fragment-safe SSE parsing | [`src/InfraAdvisor.Mobile.Core/Services/SseParser.cs`](src/InfraAdvisor.Mobile.Core/Services/SseParser.cs) |
| Testable presentation abstractions | [`src/InfraAdvisor.Mobile.Presentation/Services/ApplicationAbstractions.cs`](src/InfraAdvisor.Mobile.Presentation/Services/ApplicationAbstractions.cs) |
| MAUI platform adapters | [`src/InfraAdvisor.Mobile/Services/MauiApplicationAdapters.cs`](src/InfraAdvisor.Mobile/Services/MauiApplicationAdapters.cs) |
| Attachment privacy, MIME, size, recording | [`src/InfraAdvisor.Mobile/Services/Media/MediaInputService.cs`](src/InfraAdvisor.Mobile/Services/Media/MediaInputService.cs) and [`src/InfraAdvisor.Mobile.Core/Services/MediaValidator.cs`](src/InfraAdvisor.Mobile.Core/Services/MediaValidator.cs) |
| Adaptive chat, history sheet/rail, transcript, and Syncfusion selector | [`src/InfraAdvisor.Mobile/Views/ChatPage.xaml`](src/InfraAdvisor.Mobile/Views/ChatPage.xaml) |
| Linker-safe Syncfusion selector wrapper | [`src/InfraAdvisor.Mobile/Controls/BackendSegmentedControl.cs`](src/InfraAdvisor.Mobile/Controls/BackendSegmentedControl.cs) |
| Safe Markdown and link renderer | [`src/InfraAdvisor.Mobile/Controls/MarkdownLabel.cs`](src/InfraAdvisor.Mobile/Controls/MarkdownLabel.cs) |
| Shared design tokens and reusable styles | [`src/InfraAdvisor.Mobile/Resources/Styles/Colors.xaml`](src/InfraAdvisor.Mobile/Resources/Styles/Colors.xaml) and [`src/InfraAdvisor.Mobile/Resources/Styles/Styles.xaml`](src/InfraAdvisor.Mobile/Resources/Styles/Styles.xaml) |
| Login and `SetUserInfo` | [`src/InfraAdvisor.Mobile/ViewModels/LoginViewModel.cs`](src/InfraAdvisor.Mobile/ViewModels/LoginViewModel.cs) |
| Streaming AI/chat operations | [`src/InfraAdvisor.Mobile/ViewModels/ChatViewModel.cs`](src/InfraAdvisor.Mobile/ViewModels/ChatViewModel.cs) |
| Logs, handled errors, API failures, crash | [`src/InfraAdvisor.Mobile/ViewModels/ErrorLabViewModel.cs`](src/InfraAdvisor.Mobile/ViewModels/ErrorLabViewModel.cs) |
| Logout and safe configuration display | [`src/InfraAdvisor.Mobile/ViewModels/InfoViewModel.cs`](src/InfraAdvisor.Mobile/ViewModels/InfoViewModel.cs) |
| Android permissions | [`src/InfraAdvisor.Mobile/Platforms/Android/AndroidManifest.xml`](src/InfraAdvisor.Mobile/Platforms/Android/AndroidManifest.xml) |
| iOS privacy descriptions | [`src/InfraAdvisor.Mobile/Platforms/iOS/Info.plist`](src/InfraAdvisor.Mobile/Platforms/iOS/Info.plist) |
| Symbol/mapping build settings | [`src/InfraAdvisor.Mobile/InfraAdvisor.Mobile.csproj`](src/InfraAdvisor.Mobile/InfraAdvisor.Mobile.csproj) |
| MAUI symbol upload package | [`kyletaylored.Datadog.MAUI.Symbols`](https://www.nuget.org/packages/kyletaylored.Datadog.MAUI.Symbols) |
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

Android Studio supplies the SDK and emulator even though the MAUI solution itself is built by the .NET CLI. Open Android Studio, choose **More Actions → Virtual Device Manager** from the welcome screen or **Tools → Device Manager** from any project, create a Pixel device using API 35 or newer, and start it. Confirm that the emulator is visible, then install and launch the Debug target from a terminal:

```bash
adb devices
cd mobile/cross-platform/maui
dotnet build src/InfraAdvisor.Mobile/InfraAdvisor.Mobile.csproj -f net10.0-android -t:Run
```

If you install an APK manually instead of using `-t:Run`, embed the managed assemblies so the package does not depend on Visual Studio/CLI fast deployment:

```bash
dotnet build src/InfraAdvisor.Mobile/InfraAdvisor.Mobile.csproj -f net10.0-android -c Debug -p:EmbedAssembliesIntoApk=true
adb install -r src/InfraAdvisor.Mobile/bin/Debug/net10.0-android/dev.kyletaylor.infraadvisor.maui-Signed.apk
adb shell monkey -p dev.kyletaylor.infraadvisor.maui 1
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

For simulator-only diagnosis on an older Xcode installation, the SDK-only linker fallback can avoid APIs from the newer platform SDK while you arrange the supported Xcode/workload pairing:

```bash
dotnet build src/InfraAdvisor.Mobile/InfraAdvisor.Mobile.csproj -f net10.0-ios -c Debug -p:RuntimeIdentifier=iossimulator-arm64 -p:ValidateXcodeVersion=false -p:MtouchLink=SdkOnly -p:MtouchDebug=false
```

`MtouchDebug=false` is only needed when installing and launching that fallback build directly with `simctl`; `-t:Run` supplies its own debugger connection. The project explicitly names the generated `appicon` asset set for iOS so Release and linked simulator builds package `Assets.car` and the required `CFBundleIcons` metadata.

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

The pinned community package `kyletaylored.Datadog.MAUI.Symbols` 0.1.0 owns symbol discovery and upload. `DatadogSymbolUploadEnabled` defaults to `false` in this public demo. Set it only during an authorized Release publish and provide `DATADOG_API_KEY` through the environment. Its MSBuild target uploads the matching Android R8 mapping or iOS dSYM after `Publish`; portable PDBs preserve managed C# file/line information. The package does not generate symbols, so the project separately enables R8 mapping and dSYM generation. Package 0.1.0's build filenames do not include the full NuGet package ID, so the project explicitly imports its `.props` and `.targets`; remove that compatibility import after a package version with convention-matching filenames is adopted.

```bash
export DATADOG_API_KEY="$(security find-generic-password -w -s datadog-api-key)"
dotnet publish src/InfraAdvisor.Mobile/InfraAdvisor.Mobile.csproj -f net10.0-android -c Release -p:DatadogSymbolUploadEnabled=true
dotnet publish src/InfraAdvisor.Mobile/InfraAdvisor.Mobile.csproj -f net10.0-ios -c Release -p:RuntimeIdentifier=ios-arm64 -p:ArchiveOnBuild=true -p:DatadogSymbolUploadEnabled=true
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
