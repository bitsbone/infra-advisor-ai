# Multimodal (image + audio) input — todo

Plan: /Users/kyle.taylor/.claude/plans/wondrous-percolating-unicorn.md

## Phase 0 — Infra
- [x] Add `chat-media` container to `infra/bicep/modules/azure-storage.bicep`
- [x] Add `whisper` model deployment to `infra/bicep/modules/azure-openai.bicep`
- [x] Add `AZURE_STORAGE_CONNECTION_STRING` to `k8s/agent-api` secret (+ Makefile create-agent-api-secret target)
- [x] Add `AZURE_STORAGE_MEDIA_CONTAINER`, `AZURE_OPENAI_WHISPER_DEPLOYMENT`, `MEDIA_SAS_EXPIRY_HOURS` to `k8s/agent-api` configmap
- [x] Add `AZURE_OPENAI_WHISPER_DEPLOYMENT` to `k8s/agent-api-dotnet` configmap

## Phase 1 — Python agent-api
- [x] New `services/agent-api/src/media.py` (get_blob_service_client, upload_media, transcribe_audio)
- [x] `main.py`: Attachment model, QueryRequest.attachments, POST /media/upload, pass-through in /query and /query/stream
- [x] `agent.py`: _build_effective_query helper (STT task span + LLMObs tags; multi-part HumanMessage for images), workflow span tag additions
- [x] `memory.py`: append_exchange_with_attachments

## Phase 2 — .NET agent-api-dotnet
- [x] Verified Azure.AI.OpenAI audio-transcription API surface (GetAudioClient/TranscribeAudioAsync) and ChatClientAgent.RunAsync(ChatMessage) overload via nuget cache strings + MS docs example
- [x] New Models/AttachmentDto.cs, QueryRequest.Attachments
- [x] AgentService.cs: TranscribeAudioIfPresentAsync (OTel span + tags), multimodal ChatMessage (TextContent+UriContent)
- [x] Program.cs pass-through + new "agent-media-download" HttpClient registration
- [x] dotnet build -c Release: 0 warnings, 0 errors

## Phase 3 — UI
- [x] api.ts: Attachment type, uploadMedia(), attachments param on sendQuery/sendQueryStream
- [x] Chat.tsx: paperclip + mic buttons, MediaRecorder, pendingAttachments state, AttachmentChip rendering
- [x] datadog-rum.ts: trackAttachmentAdded/trackUploadStarted/Completed/Failed
- [x] New AttachmentChip.tsx component
- [x] npx tsc --noEmit: 0 errors; npm run build: succeeds

## Phase 4 — Testing
- [x] test_media_upload.py (5 tests: image/audio upload, 415, 413, 401)
- [x] test_agent_multimodal.py (8 tests: effective-query building, image/audio/both, transcription failure fallback, message shape)
- [x] Extended test_memory.py for attachments + backward compat (3 new tests)
- [x] Fixed test_agent_integration.py's client fixture to patch renamed append_exchange_with_attachments

## Phase 5 — Verification
- [x] uv run pytest services/agent-api/tests/: 42/42 passed
- [x] Fixed `make deploy-infra` failure: whisper-001's "Standard" SKU isn't offered in `eastus` (confirmed via Cognitive Services models API — empty `skus: []`); added a second Cognitive Services account (`oai-infra-advisor-whisper-dev`) in `eastus2` dedicated to Whisper, with its own secrets (`AZURE_OPENAI_WHISPER_ENDPOINT`/`AZURE_OPENAI_WHISPER_API_KEY`) on both services
- [x] `az bicep build` clean; `az deployment sub validate` against live Azure: 0 errors, resource diff is exactly the expected additive set (whisper account + deployment, chat-media container)
- [x] Re-ran pytest (42/42) and `dotnet build` (0/0) after the whisper-account rework
- [ ] Manual .NET verification via curl / /run skill — **not run**: requires actually running `make deploy-infra` + rolling out K8s changes
- [ ] End-to-end live verification against Datadog traces — **not run**, same reason; this is a real Azure/K8s deployment action requiring explicit user go-ahead

## Review

Implemented multimodal (image + audio) chat input end-to-end per the approved plan:
- **Infra**: new `chat-media` Blob container + `whisper` Azure OpenAI deployment (Bicep), new env vars/secret wired into both services' K8s manifests and the `create-agent-api-secret` Makefile target.
- **Python agent-api**: new `media.py` (upload + SAS URL minting + Whisper transcription), `POST /media/upload` endpoint, `agent.py` cascade wiring (audio → transcript folded into query inside a nested `transcribe-audio` LLMObs task span; image → multi-part `HumanMessage` vision content), Redis history gains optional `attachments` field (backward compatible).
- **.NET agent-api-dotnet**: mirrors the Python shape — `AttachmentDto`, `TranscribeAudioIfPresentAsync` (own OTel span, `dd.llmobs.span.kind=task` tag), multimodal `ChatMessage` (`TextContent`+`UriContent`) fed to `agent.RunAsync`/`RunStreamingAsync`. Verified the exact SDK surfaces (`AzureOpenAIClient.GetAudioClient`, `AIAgent.RunAsync(ChatMessage)`) via nuget package inspection + the MS Learn multimodal doc's C# example before writing the code, per the plan's flagged open items.
- **UI**: paperclip (file picker) + mic (MediaRecorder) buttons, shared upload-and-track flow, attachment chips both pending and on sent messages, new RUM actions for the upload lifecycle. Uploads always go to the Python backend's `/media/upload` regardless of which backend is selected for chat (the shared-endpoint decision) — no nginx changes needed.
- **Tests**: 42/42 agent-api pytest tests pass, including 13 new ones covering upload validation, effective-query construction (audio-only, image-only, both, transcription failure fallback), and attachment persistence/backward-compatibility in Redis history. `dotnet build` and `tsc --noEmit`/`vite build` are both clean.

**Not done / needs your call:** actually deploying (`make deploy-infra` to provision the Whisper deployment + blob container, then rolling out the K8s changes) and live end-to-end verification (upload an image/voice message through the running app, confirm the vision-grounded answer and check Datadog for the new `transcribe-audio`/`transcribe_audio` spans on both backends) — provisioning real Azure resources and rolling a live cluster is a deliberate infra action I didn't take without your go-ahead. Also per the plan, no automated .NET test project was added (none exists for this service today) — verification there is manual only.

**Lesson for next time:** the module-level slowapi `Limiter` singleton persists across an entire pytest session (module caching means `from main import app` across different test files shares the same rate-limit counters) — a test hitting a tightly-limited endpoint (`10/minute`) needs its own JWT subject if it can't guarantee it's the first caller in the session, otherwise it flakes on 429 depending on test order/count.

**Post-implementation fix:** the user ran `make deploy-infra` and hit `InvalidResourceProperties: The specified SKU 'Standard' for model 'whisper 001' is not supported in this region 'eastus'`. Queried the Cognitive Services models API directly (`az rest .../locations/<region>/models`) across several regions rather than guessing — confirmed eastus genuinely has no deployable SKU for whisper-001 (empty `skus: []`), while eastus2/westeurope/northcentralus/swedencentral do. Also ruled out the `GlobalStandard` SKU that also matches "whisper" in a naive search — it's actually a distinct model (`gpt-realtime-whisper`, tied to the Realtime API), not a substitute for batch transcription. Fix: a second, region-appropriate Cognitive Services account dedicated to Whisper, since a deployment's region is fixed to its parent account. This also prompted a live read-only audit of the resource group (`az resource list`) before re-running deploy-infra, which additionally surfaced that `main.bicep`'s resource-inventory comment was stale (documented a manual VNet + two orphaned resources that no longer exist) — updated it to reflect the audited state and to explicitly note the Incremental-deployment-mode safety guarantee.
