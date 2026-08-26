using System.Net;
using System.Text;
using InfraAdvisor.Mobile.Models;
using InfraAdvisor.Mobile.Observability;
using InfraAdvisor.Mobile.Services;
using InfraAdvisor.Mobile.Services.Media;
using InfraAdvisor.Mobile.ViewModels;

namespace InfraAdvisor.Mobile.Tests;

public sealed class ViewModelTests
{
    [Fact]
    public async Task LoginAssociatesUserClearsPasswordAndNavigatesOnce()
    {
        var handler = new RoutingHandler();
        var session = new AppSession();
        var navigator = new FakeNavigator();
        var telemetry = new FakeObservability();
        var viewModel = new LoginViewModel(CreateApi(handler, session), session, navigator, telemetry) { Email = " person@example.com ", Password = "secret" };

        await viewModel.LoginCommand.ExecuteAsync(null);

        Assert.True(session.IsAuthenticated);
        Assert.Equal(string.Empty, viewModel.Password);
        Assert.Equal(1, navigator.AuthenticatedNavigations);
        Assert.Equal("u1", telemetry.IdentifiedUserId);
        Assert.Contains(telemetry.SucceededOperations, operation => operation.Name == "authentication.login");
        Assert.DoesNotContain(telemetry.Attributes, pair => pair.Key.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InvalidLoginNeverCallsTheApi()
    {
        var handler = new RoutingHandler();
        var session = new AppSession();
        var viewModel = new LoginViewModel(CreateApi(handler, session), session, new FakeNavigator(), new FakeObservability());

        await viewModel.LoginCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasError);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task ChatInitializationIsGuardedAndUsesServerSuggestionObjects()
    {
        var (viewModel, handler, _, _, _) = CreateChatViewModel();

        await viewModel.InitializeCommand.ExecuteAsync(null);
        await viewModel.InitializeCommand.ExecuteAsync(null);

        Assert.Equal(3, handler.RequestCount);
        var suggestion = Assert.Single(viewModel.Suggestions);
        Assert.Equal("Procurement", suggestion.Label);
        viewModel.UseSuggestionCommand.Execute(suggestion.Query);
        Assert.Equal("Find opportunities", viewModel.Prompt);
    }

    [Fact]
    public async Task StreamingQueryUpsertsToolStepsAndRecoversControls()
    {
        var (viewModel, _, _, telemetry, _) = CreateChatViewModel();
        await viewModel.InitializeCommand.ExecuteAsync(null);
        viewModel.Prompt = "Inspect Texas bridges";

        await viewModel.SendCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsBusy);
        Assert.Equal(2, viewModel.Messages.Count);
        var assistant = viewModel.Messages[1];
        Assert.Equal("Three bridges need review.", assistant.Content);
        Assert.Equal("42", assistant.TraceId);
        var step = Assert.Single(assistant.Steps);
        Assert.Equal("get_bridge_condition", step.Label);
        Assert.Equal("ok", step.Status);
        Assert.Contains(telemetry.SucceededOperations, operation => operation.Name == "ai.query");
    }

    [Fact]
    public async Task ExistingConversationRestoresTranscriptStepsAndBackendLock()
    {
        var (viewModel, handler, session, _, _) = CreateChatViewModel();
        handler.IncludeConversation = true;
        await viewModel.InitializeCommand.ExecuteAsync(null);

        viewModel.SelectedConversation = Assert.Single(viewModel.Conversations);
        await WaitUntilAsync(() => viewModel.Messages.Count == 2);

        Assert.Equal("c1", session.ConversationId);
        Assert.False(viewModel.CanChangeBackend);
        Assert.Equal("get_bridge_condition", Assert.Single(viewModel.Messages[1].Steps).Label);
        Assert.Equal("image", Assert.Single(viewModel.Messages[0].Attachments).Kind);
    }

    [Fact]
    public async Task BackendSelectionRoutesMetadataToDotNet()
    {
        var (viewModel, handler, session, _, _) = CreateChatViewModel();
        await viewModel.InitializeCommand.ExecuteAsync(null);

        viewModel.SelectedBackendIndex = 1;
        await WaitUntilAsync(() => handler.DotNetRequestCount >= 2);

        Assert.Equal(BackendKind.DotNet, session.Backend);
        Assert.Equal(".NET", viewModel.SelectedBackend);
        Assert.Equal("gpt-4.1-mini", viewModel.SelectedModel);
    }

    [Fact]
    public void CompactHistoryCanBeOpenedAndDismissedWithoutChangingConversationState()
    {
        var (viewModel, _, session, _, _) = CreateChatViewModel();

        viewModel.ToggleHistoryCommand.Execute(null);
        Assert.True(viewModel.IsHistoryVisible);
        Assert.Null(session.ConversationId);

        viewModel.CloseHistoryCommand.Execute(null);
        Assert.False(viewModel.IsHistoryVisible);
    }

    [Fact]
    public async Task AttachmentUploadShowsReadyStateAndRemovalCleansLocalMedia()
    {
        var media = new FakeMediaInputService
        {
            PickedItem = new AttachmentItem
            {
                DisplayName = "inspection.png",
                Kind = "image",
                MimeType = "image/png",
                SizeBytes = 4,
                OpenReadAsync = () => Task.FromResult<Stream>(new MemoryStream([1, 2, 3, 4])),
            },
        };
        var (viewModel, _, _, telemetry, _) = CreateChatViewModel(media);
        await viewModel.InitializeCommand.ExecuteAsync(null);

        await viewModel.PickAttachmentCommand.ExecuteAsync(null);

        var attachment = Assert.Single(viewModel.Attachments);
        Assert.Equal("Ready", attachment.State);
        Assert.NotNull(attachment.Remote);
        Assert.Contains(telemetry.SucceededOperations, operation => operation.Name == "media.upload");

        await viewModel.RemoveAttachmentCommand.ExecuteAsync(attachment);

        Assert.Empty(viewModel.Attachments);
        Assert.Equal(1, media.RemoveCount);
    }

    [Fact]
    public async Task FailedAttachmentCanBeRetriedWithoutSelectingTheFileAgain()
    {
        var media = new FakeMediaInputService
        {
            PickedItem = new AttachmentItem
            {
                DisplayName = "inspection.png",
                Kind = "image",
                MimeType = "image/png",
                SizeBytes = 4,
                OpenReadAsync = () => Task.FromResult<Stream>(new MemoryStream([1, 2, 3, 4])),
            },
        };
        var (viewModel, handler, _, _, _) = CreateChatViewModel(media);
        handler.MediaUploadFailuresRemaining = 1;
        await viewModel.InitializeCommand.ExecuteAsync(null);

        await viewModel.PickAttachmentCommand.ExecuteAsync(null);
        var attachment = Assert.Single(viewModel.Attachments);
        Assert.True(attachment.CanRetry);

        await viewModel.RetryAttachmentCommand.ExecuteAsync(attachment);

        Assert.Equal("Ready", attachment.State);
        Assert.NotNull(attachment.Remote);
    }

    [Fact]
    public async Task SuccessfulQueryRemovesTemporaryAttachmentExactlyOnce()
    {
        var media = new FakeMediaInputService { PickedItem = ImageAttachment() };
        var (viewModel, _, _, _, _) = CreateChatViewModel(media);
        await viewModel.InitializeCommand.ExecuteAsync(null);
        await viewModel.PickAttachmentCommand.ExecuteAsync(null);
        viewModel.Prompt = "Inspect this image";

        await viewModel.SendCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.Attachments);
        Assert.Equal(1, media.RemoveCount);
        Assert.Equal("image", Assert.Single(viewModel.Messages[0].Attachments).Kind);
    }

    [Fact]
    public async Task LogoutCancelsAnActiveUploadAndRemovesItsLocalState()
    {
        var media = new FakeMediaInputService { PickedItem = ImageAttachment() };
        var (viewModel, handler, session, telemetry, _) = CreateChatViewModel(media);
        handler.BlockMediaUpload = true;
        await viewModel.InitializeCommand.ExecuteAsync(null);

        var upload = viewModel.PickAttachmentCommand.ExecuteAsync(null);
        await handler.MediaUploadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await session.SignOutAsync();
        await upload;

        Assert.False(session.IsAuthenticated);
        Assert.Empty(viewModel.Attachments);
        Assert.Equal(1, media.RemoveCount);
        Assert.Contains(telemetry.FailedOperations, operation => operation.Name == "media.upload" && operation.Abandoned);
    }

    [Fact]
    public async Task FeedbackUsesTraceMetadataAndCompletesTheOperation()
    {
        var (viewModel, _, _, telemetry, _) = CreateChatViewModel();
        var message = new ChatMessageItem { Role = "assistant", Content = "Answer", TraceId = "42", SpanId = "7" };

        await viewModel.PositiveFeedbackCommand.ExecuteAsync(message);

        Assert.Contains(telemetry.SucceededOperations, operation => operation.Name == "ai.feedback");
    }

    [Fact]
    public async Task MicrophoneDenialIsReadableAndDoesNotLeaveRecordingActive()
    {
        var media = new FakeMediaInputService { StartException = new ApiException("Enable microphone access.", category: "microphone_denied") };
        var (viewModel, _, _, _, _) = CreateChatViewModel(media);

        await viewModel.RecordAudioCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsRecording);
        Assert.Contains("microphone", viewModel.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LogoutAlwaysClearsDatadogAndReturnsToLogin()
    {
        var session = SignedInSession();
        var cleaned = false;
        session.RegisterSessionCleanup(() => { cleaned = true; return Task.CompletedTask; });
        var navigator = new FakeNavigator();
        var telemetry = new FakeObservability();
        var viewModel = new InfoViewModel(session, navigator, telemetry, new FakeRuntimeInfo());

        await viewModel.LogoutCommand.ExecuteAsync(null);

        Assert.True(cleaned);
        Assert.False(session.IsAuthenticated);
        Assert.True(telemetry.UserCleared);
        Assert.True(telemetry.SessionStopped);
        Assert.Equal(1, navigator.LoginNavigations);
    }

    [Fact]
    public async Task ErrorLabCoversLogsHandledErrorsApiErrorsAndDebugCrashBoundary()
    {
        var session = SignedInSession();
        var telemetry = new FakeObservability();
        var terminator = new FakeTerminator();
        var viewModel = new ErrorLabViewModel(CreateApi(new RoutingHandler(), session), telemetry, terminator);

        viewModel.SendSampleLogsCommand.Execute(null);
        viewModel.TriggerHandledErrorCommand.Execute(null);
        await viewModel.TriggerApiErrorCommand.ExecuteAsync(null);
        viewModel.CrashAppCommand.Execute(null);

        Assert.Contains(telemetry.LogMessages, message => message.Contains("informational", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(telemetry.ErrorMessages, message => message.Contains("Handled", StringComparison.Ordinal));
        Assert.Contains(telemetry.ErrorMessages, message => message.Contains("API", StringComparison.Ordinal));
#if DEBUG
        Assert.Equal(1, terminator.CrashCount);
#else
        Assert.Equal(0, terminator.CrashCount);
#endif
    }

    private static (ChatViewModel ViewModel, RoutingHandler Handler, AppSession Session, FakeObservability Telemetry, FakeMediaInputService Media) CreateChatViewModel(FakeMediaInputService? media = null)
    {
        var handler = new RoutingHandler();
        var session = SignedInSession();
        var telemetry = new FakeObservability();
        var mediaService = media ?? new FakeMediaInputService();
        var viewModel = new ChatViewModel(CreateApi(handler, session), session, telemetry, mediaService, new FakePreferences(), new FakeClipboard(), new FakeLinkLauncher());
        return (viewModel, handler, session, telemetry, mediaService);
    }

    private static AppSession SignedInSession()
    {
        var session = new AppSession();
        session.SignIn(new LoginResponse("jwt", new User("u1", "person@example.com", false, false, null)));
        return session;
    }

    private static AttachmentItem ImageAttachment() => new()
    {
        DisplayName = "inspection.png",
        Kind = "image",
        MimeType = "image/png",
        SizeBytes = 4,
        OpenReadAsync = () => Task.FromResult<Stream>(new MemoryStream([1, 2, 3, 4])),
    };

    private static InfraAdvisorApiClient CreateApi(HttpMessageHandler handler, AppSession session) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") }, session, new EmptyRumSessionProvider());

    private sealed class RoutingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public int DotNetRequestCount { get; private set; }
        public bool IncludeConversation { get; set; }
        public int MediaUploadFailuresRemaining { get; set; }
        public bool BlockMediaUpload { get; set; }
        public TaskCompletionSource MediaUploadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/api-dotnet/", StringComparison.Ordinal)) DotNetRequestCount++;
            if (request.Content is not null)
            {
                _ = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            }

            if (path == "/api/media/upload" && BlockMediaUpload)
            {
                MediaUploadStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return path switch
            {
                "/auth/login" => Json("{\"token\":\"jwt\",\"user\":{\"id\":\"u1\",\"email\":\"person@example.com\",\"is_admin\":false,\"is_service_account\":false,\"created_at\":null}}"),
                "/api/models" or "/api-dotnet/models" => Json("{\"models\":[\"gpt-4.1-mini\"],\"default\":\"gpt-4.1-mini\"}"),
                "/api/suggestions/initial" => Json("{\"suggestions\":[{\"label\":\"Procurement\",\"query\":\"Find opportunities\"}]}"),
                "/api-dotnet/suggestions/initial" => Json("{\"suggestions\":[{\"label\":\".NET resilience\",\"query\":\"Inspect resilience\"}]}"),
                "/api/suggestions" => Json("{\"suggestions\":[{\"label\":\"Follow up\",\"query\":\"Show details\"}]}"),
                "/api/conversations" when request.Method == HttpMethod.Get && IncludeConversation => Json("{\"conversations\":[{\"id\":\"c1\",\"user_id\":\"u1\",\"title\":\"Inspection\",\"model\":\"gpt-4.1-mini\",\"backend\":\"python\",\"message_count\":2}]}"),
                "/api/conversations" when request.Method == HttpMethod.Get => Json("{\"conversations\":[]}"),
                "/api/conversations" when request.Method == HttpMethod.Post => Json("{\"id\":\"c1\",\"user_id\":\"u1\",\"title\":\"Inspection\",\"model\":\"gpt-4.1-mini\",\"backend\":\"python\",\"message_count\":0,\"messages\":[]}"),
                "/api/conversations/c1" => Json("{\"id\":\"c1\",\"user_id\":\"u1\",\"title\":\"Inspection\",\"model\":\"gpt-4.1-mini\",\"backend\":\"python\",\"message_count\":2,\"messages\":[{\"id\":\"m1\",\"conversation_id\":\"c1\",\"role\":\"user\",\"content\":\"Inspect\",\"sources\":[],\"steps\":[],\"attachments\":[{\"url\":\"https://storage.example.test/item\",\"kind\":\"image\",\"mime_type\":\"image/png\",\"size_bytes\":4}]},{\"id\":\"m2\",\"conversation_id\":\"c1\",\"role\":\"assistant\",\"content\":\"Done\",\"sources\":[],\"trace_id\":\"42\",\"span_id\":\"7\",\"steps\":[{\"kind\":\"tool\",\"id\":\"tool-1\",\"name\":\"get_bridge_condition\",\"status\":\"ok\"}],\"attachments\":[]}]}"),
                "/api/query/stream" => Sse("event: tool_call_start\ndata: {\"id\":\"tool-1\",\"name\":\"get_bridge_condition\"}\n\nevent: tool_call_end\ndata: {\"id\":\"tool-1\",\"name\":\"get_bridge_condition\",\"status\":\"ok\"}\n\nevent: text_chunk\ndata: {\"chunk\":\"Three bridges need review.\"}\n\nevent: done\ndata: {\"sources\":[\"https://example.test/source\"],\"trace_id\":\"42\",\"span_id\":\"7\",\"model\":\"gpt-4.1-mini\"}\n\n"),
                "/api/media/upload" when MediaUploadFailuresRemaining-- > 0 => Json("{\"detail\":\"Temporary upload failure\"}", HttpStatusCode.InternalServerError),
                "/api/media/upload" => Json("{\"url\":\"https://storage.example.test/item\",\"kind\":\"image\",\"mime_type\":\"image/png\",\"size_bytes\":4}"),
                "/api/feedback" => Json("{}", HttpStatusCode.NoContent),
                "/api/observability-demo/not-found" => Json("{\"detail\":\"Expected missing route\"}", HttpStatusCode.NotFound),
                _ => Json("{}", HttpStatusCode.NotFound),
            };
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeNavigator : IAppNavigator
    {
        public int AuthenticatedNavigations { get; private set; }
        public int LoginNavigations { get; private set; }
        public void ShowAuthenticatedApp() => AuthenticatedNavigations++;
        public void ShowLogin() => LoginNavigations++;
    }

    private sealed class FakePreferences : IAppPreferences
    {
        private readonly Dictionary<string, string> values = [];
        public string? Get(string key, string? fallback) => values.TryGetValue(key, out var value) ? value : fallback;
        public void Set(string key, string value) => values[key] = value;
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public Task SetTextAsync(string value) => Task.CompletedTask;
    }

    private sealed class FakeLinkLauncher : ILinkLauncher
    {
        public Task OpenAsync(Uri uri) => Task.CompletedTask;
    }

    private sealed class FakeRuntimeInfo : IAppRuntimeInfo
    {
        public string ApiBaseUrl => "https://example.test/";
        public string DatadogSite => "US3";
        public string DatadogEnvironment => "demo";
        public string DatadogService => "infra-advisor-mobile-maui";
        public string Version => "0.1.0 (1)";
    }

    private sealed class FakeTerminator : IAppTerminator
    {
        public int CrashCount { get; private set; }
        public void Crash(string reason) => CrashCount++;
    }

    private sealed class FakeMediaInputService : IMediaInputService
    {
        public AttachmentItem? PickedItem { get; init; }
        public Exception? StartException { get; init; }
        public int RemoveCount { get; private set; }
        public bool IsRecording { get; private set; }
        public Task<AttachmentItem?> PickAsync(CancellationToken cancellationToken = default) => Task.FromResult(PickedItem);
        public Task StartRecordingAsync(CancellationToken cancellationToken = default)
        {
            if (StartException is not null) return Task.FromException(StartException);
            IsRecording = true;
            return Task.CompletedTask;
        }
        public Task<AttachmentItem> StopRecordingAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CancelRecordingAsync() { IsRecording = false; return Task.CompletedTask; }
        public Task PlayAsync(AttachmentItem item) => Task.CompletedTask;
        public Task RemoveAsync(AttachmentItem item) { RemoveCount++; return Task.CompletedTask; }
    }

    private sealed class FakeObservability : IObservability
    {
        private int nextOperation;
        public string? IdentifiedUserId { get; private set; }
        public bool UserCleared { get; private set; }
        public bool SessionStopped { get; private set; }
        public List<string> LogMessages { get; } = [];
        public List<string> ErrorMessages { get; } = [];
        public List<KeyValuePair<string, object>> Attributes { get; } = [];
        public List<(string Name, string Key)> SucceededOperations { get; } = [];
        public List<(string Name, string Key, bool Abandoned)> FailedOperations { get; } = [];
        public void IdentifyUser(string id, string email) => IdentifiedUserId = id;
        public void ClearUser() => UserCleared = true;
        public void StopSession() => SessionStopped = true;
        public string StartOperation(string name, IReadOnlyDictionary<string, object>? attributes = null) { Capture(attributes); return $"operation-{++nextOperation}"; }
        public void SucceedOperation(string name, string operationKey, IReadOnlyDictionary<string, object>? attributes = null) { Capture(attributes); SucceededOperations.Add((name, operationKey)); }
        public void FailOperation(string name, string operationKey, bool abandoned, IReadOnlyDictionary<string, object>? attributes = null) { Capture(attributes); FailedOperations.Add((name, operationKey, abandoned)); }
        public void Info(string message, IReadOnlyDictionary<string, object>? attributes = null) { LogMessages.Add(message); Capture(attributes); }
        public void Error(string message, Exception exception, IReadOnlyDictionary<string, object>? attributes = null) { ErrorMessages.Add(message); Capture(attributes); }
        private void Capture(IReadOnlyDictionary<string, object>? attributes)
        {
            if (attributes is not null) Attributes.AddRange(attributes);
        }
    }
}
