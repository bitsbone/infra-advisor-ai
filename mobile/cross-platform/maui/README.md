# Infra Advisor .NET MAUI client

This .NET 10 MAUI application runs on Android API 23+ and iOS 15+. It includes chat, history, model/backend selection, file/audio uploads, and Datadog observability.

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
| Field Advisor workspace, transcript, evidence sheet, and Syncfusion selector | [`src/InfraAdvisor.Mobile/Views/ChatPage.xaml`](src/InfraAdvisor.Mobile/Views/ChatPage.xaml) and [`src/InfraAdvisor.Mobile/ViewModels/ChatViewModel.cs`](src/InfraAdvisor.Mobile/ViewModels/ChatViewModel.cs) |
| Dedicated conversation History destination | [`src/InfraAdvisor.Mobile/Views/HistoryPage.xaml`](src/InfraAdvisor.Mobile/Views/HistoryPage.xaml) and [`src/InfraAdvisor.Mobile/ViewModels/HistoryViewModel.cs`](src/InfraAdvisor.Mobile/ViewModels/HistoryViewModel.cs) |
| Versioned chat-artifact API models | [`src/InfraAdvisor.Mobile.Core/Models/ApiModels.cs`](src/InfraAdvisor.Mobile.Core/Models/ApiModels.cs) |
| Evidence presentation and privacy-safe source links | [`src/InfraAdvisor.Mobile/Models/ChatPresentationModels.cs`](src/InfraAdvisor.Mobile/Models/ChatPresentationModels.cs) |
| Linker-safe Syncfusion selector wrapper | [`src/InfraAdvisor.Mobile/Controls/BackendSegmentedControl.cs`](src/InfraAdvisor.Mobile/Controls/BackendSegmentedControl.cs) |
| Safe Markdown and link renderer | [`src/InfraAdvisor.Mobile/Controls/MarkdownLabel.cs`](src/InfraAdvisor.Mobile/Controls/MarkdownLabel.cs) |
| Shared design tokens and reusable styles | [`src/InfraAdvisor.Mobile/Resources/Styles/Colors.xaml`](src/InfraAdvisor.Mobile/Resources/Styles/Colors.xaml) and [`src/InfraAdvisor.Mobile/Resources/Styles/Styles.xaml`](src/InfraAdvisor.Mobile/Resources/Styles/Styles.xaml) |
| Adaptive layout and accessibility contract | [`src/InfraAdvisor.Mobile/Views/ChatPage.xaml`](src/InfraAdvisor.Mobile/Views/ChatPage.xaml), [`src/InfraAdvisor.Mobile/Views/LoginPage.xaml.cs`](src/InfraAdvisor.Mobile/Views/LoginPage.xaml.cs), and [`tests/InfraAdvisor.Mobile.Core.Tests/XamlAccessibilityGuardTests.cs`](tests/InfraAdvisor.Mobile.Core.Tests/XamlAccessibilityGuardTests.cs) |
| Login and `SetUserInfo` | [`src/InfraAdvisor.Mobile/ViewModels/LoginViewModel.cs`](src/InfraAdvisor.Mobile/ViewModels/LoginViewModel.cs) |
| Streaming AI/chat operations | [`src/InfraAdvisor.Mobile/ViewModels/ChatViewModel.cs`](src/InfraAdvisor.Mobile/ViewModels/ChatViewModel.cs) |
| Logs, handled errors, API failures, crash | [`src/InfraAdvisor.Mobile/ViewModels/ErrorLabViewModel.cs`](src/InfraAdvisor.Mobile/ViewModels/ErrorLabViewModel.cs) |
| Logout and safe configuration display | [`src/InfraAdvisor.Mobile/ViewModels/InfoViewModel.cs`](src/InfraAdvisor.Mobile/ViewModels/InfoViewModel.cs) |
| Android permissions | [`src/InfraAdvisor.Mobile/Platforms/Android/AndroidManifest.xml`](src/InfraAdvisor.Mobile/Platforms/Android/AndroidManifest.xml) |
| iOS privacy descriptions | [`src/InfraAdvisor.Mobile/Platforms/iOS/Info.plist`](src/InfraAdvisor.Mobile/Platforms/iOS/Info.plist) |
| Symbol/mapping build settings | [`src/InfraAdvisor.Mobile/InfraAdvisor.Mobile.csproj`](src/InfraAdvisor.Mobile/InfraAdvisor.Mobile.csproj) |
| Contract tests | [`tests/InfraAdvisor.Mobile.Core.Tests`](tests/InfraAdvisor.Mobile.Core.Tests) |

One DI-managed `HttpClient` performs every request. Datadog automatically records its resources and propagates Datadog and W3C trace headers to the configured API host.

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

In Android Studio, open **Tools → Device Manager**, create a Pixel device using API 35 or newer, and start it. Then run:

```bash
make run-android
```

Build an APK without launching it:

```bash
cd mobile/cross-platform/maui
dotnet publish src/InfraAdvisor.Mobile/InfraAdvisor.Mobile.csproj -f net10.0-android -c Release
```

Release builds enable R8 and generate `mapping.txt`.

## Run iOS

Open Xcode once to install the required platform and accept its license. Start the intended simulator, then run from the repository root:

```bash
make run-ios
```

The target selects the currently booted simulator. To choose a specific device when more than one simulator is open:

```bash
xcrun simctl list devices available
make run-ios IOS_SIMULATOR_UDID=SIMULATOR-UDID
```

The Make target passes `_DeviceName` to prevent `mlaunch` from choosing another compatible simulator. It targets Apple Silicon; Intel hosts can run the equivalent `dotnet build` command with `iossimulator-x64`. If the intended device is missing from `xcrun simctl`, verify `xcode-select -p` points to the Xcode installation that owns its runtime. Use an Xcode version supported by the installed .NET iOS workload.

## Exercise the AI and observability flows

1. Sign in and verify the RUM user ID and email.
2. Send a prompt and follow the mobile resource into the backend and AI trace.
3. Test history, image/audio upload, logs, errors, and a debugger-free crash.
4. Verify Session Replay masks sensitive inputs and logout clears the user.

## Release symbols and Mobile App Testing uploads

[`Datadog.Maui`](https://docs.datadoghq.com/real_user_monitoring/application_monitoring/maui/error_tracking/) uploads the Android `mapping.txt` and iOS dSYM when the release workflow sets `DatadogUploadSymbols=true`.

### GitHub Actions secrets

- `DD_API_KEY`: Datadog API key.
- `DD_APP_KEY`: Datadog application key.
- `MAUI_IOS_SIGNING_CERTIFICATE_BASE64`: Base64-encoded Development or Ad Hoc `.p12`.
- `MAUI_IOS_SIGNING_CERTIFICATE_PASSWORD`: `.p12` password.
- `APP_STORE_CONNECT_KEY_ID`: App Store Connect API key ID.
- `APP_STORE_CONNECT_ISSUER_ID`: App Store Connect API issuer ID.
- `APP_STORE_CONNECT_PRIVATE_KEY`: App Store Connect `.p8` contents.

Android uses a temporary test keystore generated by the workflow. No Android signing secrets are required.

The iOS signing secrets are required only for `upload-symbols` and `build-and-sync`. [`maui-actions/apple-provisioning`](https://github.com/maui-actions/apple-provisioning) imports the certificate and downloads the Development profile for `dev.kyletaylor.infraadvisor.maui`. A `build-only` run produces an unsigned simulator `.app`.

### GitHub Actions variables

- `DATADOG_SYNTHETICS_MAUI_ANDROID_APPLICATION_ID`: Android Mobile App Testing application ID.
- `DATADOG_SYNTHETICS_MAUI_IOS_APPLICATION_ID`: iOS Mobile App Testing application ID.

The Synthetics IDs identify the separately uploaded APK and IPA. They are not RUM IDs.

Both platforms share this MAUI RUM configuration:

- Application ID: `fe90f908-da00-4d7c-9b24-6af11cee68a4`
- Client token: `pub884d0800477e2d252b992acb168fc7a5`

Run [Build and sync .NET MAUI mobile applications](../../../.github/workflows/maui-release.yml) from GitHub Actions:

- `build-only`: build an Android APK and iOS simulator app without signing secrets.
- `upload-symbols`: build and upload symbols.
- `build-and-sync`: build, upload symbols, and upload the APK/IPA to Mobile App Testing.

## Demo safeguards

- Image/audio uploads are limited to supported types and 10 MB.
- Filenames, prompts, responses, tokens, and media are excluded from custom telemetry.
- RUM, tracing, and Session Replay use 100% sampling for this demo.
- Sensitive inputs are masked in Session Replay.
- Layout and accessibility contracts are covered by static XAML tests.

Public defaults can be overridden at build time without editing tracked files:

```bash
dotnet build src/InfraAdvisor.Mobile/InfraAdvisor.Mobile.csproj -f net10.0-android -p:InfraAdvisorApiBaseUrl=https://example.test/ -p:InfraAdvisorDatadogEnvironment=local -p:InfraAdvisorDatadogService=infra-advisor-mobile-maui-local
```

Supported public build properties:

- `InfraAdvisorApiBaseUrl`
- `InfraAdvisorDatadogEnvironment`
- `InfraAdvisorDatadogService`
- `InfraAdvisorDatadogClientToken`
- `InfraAdvisorDatadogRumApplicationId`
