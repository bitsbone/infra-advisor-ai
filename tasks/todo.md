# ACA agentic POC: managed OTel agent vs Datadog sidecar — todo

Plan: /Users/kyle.taylor/.claude/plans/wondrous-percolating-unicorn.md

## Phase 1 — Bicep module

- [x] `infra/bicep/modules/aca-agentic-poc.bicep`: shared Container Apps Environment (openTelemetryConfiguration → Datadog) + two Container Apps (`-managed` single-container, `-sidecar` two-container)
- [x] `infra/bicep/main.bicep`: wire module behind `deployAcaAgenticPoc` (default false) opt-in flag, new secure params (never in .bicepparam)
- [x] `infra/bicep/modules/monitoring.bicep`: new `workspaceSharedKey` output for reuse
- [x] `az bicep build`: compiles clean (2 non-blocking linter warnings)

## Phase 2 — Minimal .NET agentic app

- [x] `services/aca-agentic-poc-dotnet/`: Program.cs (one `/run` endpoint, one tool, ChatClientAgent), Observability/TelemetrySetup.cs (env-driven OTLP protocol, no hardcoded endpoint), Dockerfile, .dockerignore
- [x] `dotnet build -c Release`: 0 warnings, 0 errors
- [x] Local boot test with fake config: `/health` → 200
- [x] `docker build` + containerized boot test: `/health` → 200

## Phase 3 — Docs

- [x] New `### ACA Agentic POC (aca-agentic-poc.bicep)` section in `docs/src/content/docs/architecture/infrastructure.md` — comparison table, secrets-flow-through-Bicep callout, status note (code done, deployment pending)
- [x] `npx astro build` + `npm run check-links`: clean

## Phase 0 — Spike (NOT started — requires real cloud deployment + secrets)

- [ ] Deploy `aca-agentic-poc-managed` for real, fire a request, confirm trace lands in Datadog
- [ ] Deploy `aca-agentic-poc-sidecar` for real, fire a request, confirm trace lands in Datadog
- [ ] If either path doesn't work as documented, note the gap and adjust Bicep/app config accordingly

## Phase 4 — End-to-end verification (blocked on Phase 0)

- [ ] `make deploy-infra` with `deployAcaAgenticPoc=true` + real secrets
- [ ] `curl` both apps' `/run` endpoints
- [ ] Confirm full `invoke_agent → execute_tool → chat` span tree in Datadog for both
- [ ] Write up comparison findings in the docs section above

## Status

All code-level work (Bicep + .NET app + docs) is written and verified locally/in Docker — nothing has been deployed to Azure yet. Actual deployment requires:

- A Datadog API key
- GHCR credentials for image pull (or switch to a different registry)
- Building + pushing the container image
- Running `make deploy-infra` with `deployAcaAgenticPoc=true` and the above secrets passed via `--parameters` (never committed)

This is a real, billable Azure resource provisioning step — paused here for the user to decide when/how to proceed, per the "confirm before costly/hard-to-reverse actions" rule. No `az deployment` or `az containerapp` command has been run.

---

# Native mobile 0.1.0 release

## Plan

- [x] Verify Datadog symbol/application upload requirements and inspect local signing prerequisites without exposing credentials.
- [x] Set the iOS and Android marketing versions to `0.1.0`.
- [x] Build the Android Release APK and upload its matching R8 mapping file.
- [x] Build the iOS Release device archive and upload its matching dSYM files.
- [x] Export the iOS IPA with a Development signing identity and provisioning profile.
- [x] Document artifact locations, first manual Mobile App Testing uploads, and later `datadog-ci` update commands.
- [x] Verify the release outputs and record results below.

## Review

- Android `0.1.0`/version code `1` built successfully. Datadog accepted the R8 mapping with build ID `9cb5f256-87b8-44d2-a44e-64358495e097`; the ignored testing APK is `mobile/native/android/app/build/outputs/apk/release/InfraAdvisorMobile-0.1.0-android.apk` and is signed with the machine-local Android debug certificate for Datadog device testing only.
- iOS `0.1.0`/build `1` archived successfully for arm64 at `mobile/native/ios/build/0.1.0/InfraAdvisorMobile.xcarchive`. Datadog CI uploaded nine dSYM bundles, including the app UUID `A543F5FD-E6C6-34E0-B186-1D86EF85F6FD`.
- The initial unsigned archive was not uploadable. Automatic provisioning with the configured Apple Developer team created/downloaded the required Development signing assets, and Xcode exported `mobile/native/ios/build/0.1.0-signed/InfraAdvisorMobile-0.1.0-ios.ipa`. The IPA validates successfully and embeds a Development profile with `get-task-allow=true`.
- Android unit tests and lint passed. The documentation site built 62 pages and reported zero broken internal links. `git diff --check` passed.
- The first APK/IPA upload remains intentionally manual because Datadog assigns a distinct Mobile Application ID during application creation. Later versions use the documented `datadog-ci synthetics upload-application` commands with secret-provided API/application keys and those IDs.

---

# Selective native mobile release workflow

## Plan

- [x] Inspect existing workflow conventions, native release requirements, and the current Datadog application upload contract.
- [x] Add CI-safe Android version overrides without changing the tracked default version.
- [x] Add a manual workflow that selects Android, iOS, or both and defaults to build-only behavior.
- [x] Require platform signing assets for every release and Datadog credentials/application IDs only for explicit sync runs.
- [x] Document GitHub secrets, repository variables, dispatch inputs, symbols, application upload behavior, and artifact retention.
- [x] Validate workflow syntax, native builds, documentation, and secret hygiene.

## Review

- The manual workflow is safe by default: `build-only` is the default operation, no push or pull-request trigger exists, repository permissions are read-only, and only an explicit `build-and-sync` dispatch receives the Datadog API/application keys.
- Android unit tests, lint, and `assembleRelease` passed with the CI version overrides. The workflow creates and verifies a signed APK, uploads the exact release mapping only during sync, retains artifacts for 14 days, and removes its decoded keystore in an unconditional cleanup step.
- A Development IPA export from the existing signed archive passed `unzip` validation and embedded `get-task-allow=true`. Forced manual export rejected an Xcode-managed profile, so the workflow now validates the supplied profile and uses automatic profile selection plus the Xcode-version-appropriate `development`/`debugging` method.
- Checksum-verified Actionlint 1.7.12 and Ruby YAML parsing passed. Datadog CI 5.21.2 help confirmed the application-upload arguments; the workflow passes `--latest` only when selected and otherwise uses the command's default behavior.
- The documentation site built 62 pages, the link checker reported zero broken internal links, and `git diff --check` passed. A live GitHub-hosted run and Datadog mutation were not triggered because this workflow and its repository secrets/variables have not been published from this local worktree.

---

# Cross-platform .NET MAUI mobile application

## Scope decisions

- Build one shared .NET 10 MAUI application under `mobile/cross-platform/maui` targeting `net10.0-android` and `net10.0-ios`, with Android API 23+ and iOS 15+ to match the supported Datadog MAUI SDK range.
- Use Shell navigation, dependency injection, XAML, and MVVM with `CommunityToolkit.Mvvm`; keep platform-specific code limited to permissions, signing, and OS integration.
- Treat Syncfusion's Essential UI Kit as an MIT-licensed design/template source, selectively port only useful responsive XAML patterns, and record attribution. Do not import its full sample application, stock imagery, FFImageLoading dependency, or third-party assets. Use the MIT `Syncfusion.Maui.Toolkit` package only for controls that materially improve the interface, beginning with the backend segmented selector.
- Match the web client's current multimodal contract: JPEG, PNG, WebP, WAV, MP3, and OGG files up to 10 MB, plus microphone-recorded WAV audio. Arbitrary document upload is out of scope because neither backend currently accepts document MIME types.
- Preserve the repository's security boundary: JWT and account credentials remain memory-only; the public MAUI RUM application ID and client token may be compiled into the app; no Datadog API/application key, signing secret, prompt, response body, SAS query string, or raw attachment content enters source or telemetry.

## Phase 1 — Project foundation and design system

- [x] Replace the reserved README-only directory with a .NET 10 MAUI solution and one multi-targeted application project.
- [x] Add pinned package versions for `Datadog.Maui` 0.2.0, `CommunityToolkit.Mvvm`, `Plugin.Maui.Audio`, and the minimal Syncfusion MAUI Toolkit dependency selected during the UI spike.
- [x] Add build-time configuration for API base URL, Datadog site/environment/service, RUM application ID, client token, session/resource trace/replay sampling, and app version without introducing privileged values.
- [x] Create shared color, typography, spacing, icon, card, button, input, skeleton, and message-bubble resources that mirror the web interface's blue/gray visual language and use `docs/public/favicon.svg` as the app-icon source.
- [x] Add `THIRD_PARTY_NOTICES.md` entries for any Syncfusion template/control or other external asset retained in the application.

## Phase 2 — Application architecture and API layer

- [x] Implement typed records for login, user, query, stream events, sources, suggestions, models, feedback, conversations, stored tool steps, and attachments using the existing backend JSON contracts.
- [x] Implement a single DI-managed `HttpClient` API service for `/auth/login`, `/models`, `/suggestions/initial`, `/suggestions`, `/media/upload`, `/query/stream`, `/feedback`, and conversation create/list/detail/delete operations against the selected `/api` or `/api-dotnet` prefix.
- [x] Add a streaming SSE parser that handles fragmented buffers, named events, cancellation, HTTP failures, malformed blocks, and final metadata without retrying query POSTs.
- [x] Add multipart streaming uploads with MIME and 10 MB validation before transmission, progress state, retry/cancel support, sanitized errors, and no buffering or logging of payload bodies beyond what the upload API requires.
- [x] Implement an in-memory session store for JWT/user identity and a preferences store only for non-sensitive backend/model/theme choices; generate a stable conversation session UUID and never persist the token.

## Phase 3 — Authentication and adaptive navigation

- [x] Build a branded Login page with validation, password masking, disabled/loading states, readable authentication errors, and successful `DdSdk.SetUserInfo` association.
- [x] Build an adaptive Shell: Chat, Errors, and Info destinations; a history flyout/bottom sheet on phones; and a persistent history rail on wider layouts.
- [x] Implement logout so it cancels active operations, clears the in-memory JWT, calls `DdSdk.ClearUserInfo`, stops the current RUM session as appropriate, and returns to Login.
- [x] Add stable page names and `AutomationId` values so Datadog automatic view/action tracking and UI automation produce intentional names.

## Phase 4 — Polished chat and conversation history

- [x] Build the empty state with web-aligned domain cards and prompt suggestions, including the federal-procurement MCP example.
- [x] Build a virtualized transcript with distinct user/assistant bubbles, Markdown rendering, selectable links, citations/source cards, trace metadata, copy, positive/negative/report feedback, timestamps, and accessible loading/error states.
- [x] Add live tool/pipeline step chips from `/query/stream`, partial assistant text, a still-working indicator, cancellation, and recovery after stream errors.
- [x] Add backend and model selectors; discover models from the selected backend, remember the non-sensitive selection, restore saved conversation metadata, and lock backend selection while viewing an existing conversation.
- [x] Add history load, refresh, select, delete-with-confirmation, new conversation, and empty/error states using the authenticated conversation endpoints.
- [x] Fetch initial suggestions and contextual follow-ups, with curated offline fallbacks so the demo remains usable when suggestion generation fails.

## Phase 5 — Image and audio attachments

- [x] Add a system file picker restricted to backend-supported image/audio formats and show immediate local preview chips with uploading, complete, failed, retry, remove, and cancel states.
- [x] Add microphone permission-on-demand and `Plugin.Maui.Audio` recording with an elapsed timer, explicit stop/cancel controls, WAV output, local playback preview, maximum-size handling, and deletion of temporary recordings after removal/upload lifecycle completion.
- [x] Add iOS `NSMicrophoneUsageDescription` and Android microphone/media permissions with user-facing denied/restricted recovery messages; do not request microphone access until the record action is used.
- [x] Send completed attachment references with the query and render persisted image/audio attachments when a conversation is reopened. Limit each turn to one image and one audio item because the current agent pipelines consume the first attachment of each modality.
- [x] Track upload and recording workflows as safe RUM operations/actions with modality, status, duration, and size only; never attach filenames, local paths, SAS URLs, transcripts, prompts, or binary content to telemetry.

## Phase 6 — Datadog reference implementation

- [x] Initialize `Datadog.Maui` 0.2.0 in `MauiProgram.cs` with US3, `demo`, service `infra-advisor-mobile-maui`, the supplied public client token and RUM application ID, granted demo consent, native crash reporting, Logs, Trace, RUM, and Session Replay.
- [x] Configure `infra-advisor-ai.kyletaylor.dev` as the only first-party host with Datadog and W3C headers, enable 100% RUM sessions, 100% first-party resource trace sampling, 100% replay sampling, background crash correlation, and the SDK's mask-sensitive-inputs replay privacy mode where exposed by the pinned package.
- [x] Rely on the SDK's automatic `HttpClient` resource/span instrumentation and header injection; add custom actions/operations only for higher-level flows such as login, recording, upload, query, feedback, and logout so resources are not double-counted.
- [x] Add a narrow observability facade for controlled startup, authentication-state, upload-state, query-state, and Error Lab logs/errors. Add event mappers that strip URL query strings and reject sensitive application attributes.
- [x] Add an Errors page demonstrating a handled C# error, an instrumented missing API request, safe sample logs, and a debug-only confirmed crash with relaunch instructions.
- [x] Add an Info page that shows the authenticated user and safe runtime configuration but omits the JWT, client token, and privileged build credentials.

## Phase 7 — Symbols, builds, and selective delivery

- [x] Configure Release portable PDB inclusion, Android R8/mapping generation, iOS dSYM generation, and opt-in `kyletaylored.Datadog.MAUI.Symbols` upload support with `DATADOG_API_KEY` supplied only by the build environment.
- [x] Add local run instructions for Android Studio/CLI emulators and Xcode/CLI iOS simulators, plus signed physical-device publishing and debugger-free crash validation.
- [x] Add a manual GitHub Actions MAUI release workflow, or extend the existing native workflow cleanly, with Android/iOS/both and build-only/build-and-sync inputs, signing secrets, unique version/build metadata, private artifact retention, symbol uploads, and `datadog-ci synthetics upload-application` after the platform-specific Mobile App Testing applications are manually created.
- [x] Keep Android keystores, Apple certificates/profiles, Datadog API/application keys, generated APK/AAB/IPA files, dSYMs, mappings, and local overrides out of git and remove decoded signing files from runners unconditionally.

## Phase 8 — Tests, documentation, and acceptance

- [x] Unit-test serialization, bearer headers, backend routing, session IDs, MIME/size validation, multipart requests, SSE fragmentation, cancellation, error parsing, conversation compatibility differences, upload state transitions, and view-model loading guards.
- [x] Add UI/view-model coverage for Login to Chat, adaptive history, conversation restoration, backend/model selection, prompts, streaming/tool chips, attachment retry/removal, microphone denial, feedback, Errors, Info, and logout.
- [ ] Verify Debug builds and tests for both target frameworks, Android emulator behavior, iOS simulator behavior, Release publish outputs, trimming/R8 behavior, and app restart after an intentional crash.
- [x] Update `mobile/README.md`, the MAUI README, `mobile/OBSERVABILITY_PATTERNS.md`, and `docs/src/content/docs/observability/mobile-rum.md` with architecture, source-file links, local setup, permissions, build/release steps, privacy, AI attachment flow, RUM-to-APM verification, and symbol/application upload instructions.
- [x] Build the documentation site, run its link checker, and run secret/diff hygiene checks.
- [ ] Complete live acceptance: named MAUI RUM views/actions, authenticated user identity, replay with sensitive input masked, safe correlated logs/errors, upload/query resources, Datadog and W3C propagation into both backends, mobile span-to-APM continuation, AI/tool spans including audio transcription or vision, symbolicated crashes, and cleared identity after logout.

## Implementation gate

Implementation is complete. Image/audio parity follows the existing web/backend contract rather than adding unsupported arbitrary-document uploads.

Local verification on 2026-08-26 passed 44 Release tests, a zero-warning Android R8 Release build, Android Debug build and emulator startup, iOS Debug and linked Release simulator builds, iOS simulator startup, documentation build/link checks, and repository hygiene. Debug startup eagerly resolved every Login, Chat, Errors, and Info page on both platforms; root and cross-template XAML bindings are compiled and guarded as errors against future type regressions. Debugger-free SIGSEGV tests terminated and relaunched both apps; Datadog received crash events with stacks from Android and iOS. The final Android Release mapping for build ID `a9abef8fb495b1ce` was uploaded through `kyletaylored.Datadog.MAUI.Symbols` 0.1.0. Live US3 data showed Android and iOS MAUI sessions, named Login views, Session Replay, controlled logs, a correlated 401 login resource with trace/span IDs, and a continued trace containing both `infra-advisor-mobile-maui` and `infra-advisor-auth-api`.

The installed local Xcode 26.0.1 cannot create the signed iOS device IPA/dSYM because the installed .NET iOS workload requires the iOS 26.2 SDK. The manual `macos-26` workflow is the authoritative signed-device path and runs on a GitHub image with a compatible newer Xcode. The remaining unchecked acceptance work requires publishing the uncommitted workflow, supplying its signing/Mobile App Testing configuration, and using a valid public-deployment account to exercise authenticated Chat, both backends, multimodal AI/tool spans, identity clearing, masked replay inspection, and symbolicated Release crashes.

---

# Admin-managed passwords

- [x] Add an admin-only Auth API endpoint that securely sets a user's password, enforces the shared minimum length, clears outstanding reset tokens, and emits safe audit telemetry.
- [x] Add an admin user-management dialog with new-password confirmation, accessible controls, loading/error states, and no password persistence.
- [x] Cover authorization, validation, missing users, password hashing, and reset-token invalidation with Auth API tests.
- [x] Document the admin workflow, security boundary, and observability signals in the public Auth API and UI content.
- [x] Run Auth API tests, the UI production build, documentation checks, secret hygiene, and `git diff --check`.

## Review

- Added `PUT /admin/users/{user_id}/password` behind the existing administrator dependency. It applies the shared 8-character/72-byte policy, bcrypt-hashes the replacement, invalidates outstanding reset tokens, returns no credential material, and logs only the actor and target UUIDs.
- Added a responsive Password action and dedicated confirmation dialog to the admin user table. Password values remain ephemeral component state, use masked inputs, and are cleared on success or closure; the UI also states that existing JWT sessions are not revoked.
- Verification passed on Python 3.12 with 16 Auth API tests, the TypeScript/Vite production build, a 62-page documentation build with zero broken internal links, a changed-lines credential-pattern scan, and `git diff --check`.
