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
        var sessionStore = new FakeSessionStore();
        var navigator = new FakeNavigator();
        var telemetry = new FakeObservability();
        var viewModel = new LoginViewModel(CreateApi(handler, session), session, sessionStore, navigator, telemetry) { Email = " person@example.com ", Password = "secret" };

        await viewModel.LoginCommand.ExecuteAsync(null);

        Assert.True(session.IsAuthenticated);
        Assert.Equal(string.Empty, viewModel.Password);
        Assert.Equal(1, navigator.AuthenticatedNavigations);
        Assert.Equal("u1", telemetry.IdentifiedUserId);
        Assert.Equal("person@example.com", telemetry.IdentifiedUserEmail);
        Assert.Equal("jwt", sessionStore.Saved?.Token);
        Assert.Contains(telemetry.SucceededOperations, operation => operation.Name == "authentication.login");
        Assert.DoesNotContain(telemetry.Attributes, pair => pair.Key.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task InvalidLoginNeverCallsTheApi()
    {
        var handler = new RoutingHandler();
        var session = new AppSession();
        var viewModel = new LoginViewModel(CreateApi(handler, session), session, new FakeSessionStore(), new FakeNavigator(), new FakeObservability());

        await viewModel.LoginCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasError);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task NavigationFailureReturnsToAReadableSignedOutState()
    {
        var handler = new RoutingHandler();
        var session = new AppSession();
        var navigator = new FakeNavigator { AuthenticatedNavigationException = new InvalidOperationException("Prism navigation failed") };
        var telemetry = new FakeObservability();
        var sessionStore = new FakeSessionStore();
        var viewModel = new LoginViewModel(CreateApi(handler, session), session, sessionStore, navigator, telemetry) { Email = "person@example.com", Password = "secret" };

        await viewModel.LoginCommand.ExecuteAsync(null);

        Assert.False(session.IsAuthenticated);
        Assert.True(viewModel.HasError);
        Assert.True(telemetry.UserCleared);
        Assert.True(sessionStore.WasCleared);
        Assert.Contains(telemetry.FailedOperations, operation => operation.Name == "authentication.login");
    }

    [Fact]
    public async Task ChatInitializationIsGuardedAndUsesServerSuggestionObjects()
    {
        var (viewModel, handler, _, _, _) = CreateChatViewModel();

        await viewModel.InitializeCommand.ExecuteAsync(null);
        await viewModel.InitializeCommand.ExecuteAsync(null);

        Assert.Equal(3, handler.RequestCount);
        Assert.Contains(viewModel.Suggestions, value => value.Label == "Federal resilience grants" && value.Query.Contains("Grants.gov", StringComparison.Ordinal));
        Assert.Contains(viewModel.Suggestions, value => value.Label == "Infrastructure bids" && value.Query.Contains("SAM.gov", StringComparison.Ordinal));
        var suggestion = Assert.Single(viewModel.Suggestions, value => value.Label == "Procurement");
        Assert.Equal("Procurement", suggestion.Label);
        viewModel.UseSuggestionCommand.Execute(suggestion.Query);
        Assert.Equal("Find opportunities", viewModel.Prompt);
        Assert.True(viewModel.IsNewConversationVisible);
        Assert.True(viewModel.IsComposerVisible);
    }

    [Fact]
    public async Task ChatAlwaysPresentsANewConversationWhenTranscriptIsEmpty()
    {
        var (viewModel, _, _, _, _) = CreateChatViewModel();
        await viewModel.InitializeCommand.ExecuteAsync(null);

        await viewModel.NewConversationCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsNewConversationVisible);
        Assert.True(viewModel.IsComposerVisible);
        Assert.Equal("Ask about infrastructure…", viewModel.ComposerPlaceholder);
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
        var evidence = Assert.Single(assistant.Evidence);
        Assert.Equal("sam.gov", evidence.Source);
        Assert.Equal("Resilience planning support", evidence.Title);
        Assert.Contains(telemetry.SucceededOperations, operation => operation.Name == "ai.query");
    }

    [Fact]
    public async Task TruncatedStreamIsReportedAsFailureAndKeepsPartialAnswer()
    {
        var (viewModel, handler, _, telemetry, _) = CreateChatViewModel();
        handler.TruncateQueryStream = true;
        await viewModel.InitializeCommand.ExecuteAsync(null);
        viewModel.Prompt = "Inspect Texas bridges";

        await viewModel.SendCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsBusy);
        Assert.True(viewModel.HasError);
        Assert.Contains("ended before completion", viewModel.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Partial answer", viewModel.Messages[1].Content);
        Assert.DoesNotContain(telemetry.SucceededOperations, operation => operation.Name == "ai.query");
        Assert.Contains(telemetry.FailedOperations, operation => operation.Name == "ai.query" && !operation.Abandoned);
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
    public async Task CrossBackendConversationRefreshesDisjointModelsBeforeRestoringSavedModelAndStartsNewCleanly()
    {
        var (viewModel, handler, session, _, _) = CreateChatViewModel();
        handler.IncludeConversation = true;
        handler.ConversationBackend = BackendKind.DotNet;
        handler.UseDisjointModels = true;
        await viewModel.InitializeCommand.ExecuteAsync(null);

        Assert.Equal(["python-model"], viewModel.Models);
        Assert.Equal("python-model", viewModel.SelectedModel);

        viewModel.SelectedConversation = Assert.Single(viewModel.Conversations);
        await WaitUntilAsync(() => viewModel.Messages.Count == 2);

        Assert.Equal(BackendKind.DotNet, session.Backend);
        Assert.Equal(".NET", viewModel.SelectedBackend);
        Assert.Equal(["dotnet-default", "dotnet-saved"], viewModel.Models);
        Assert.Equal("dotnet-saved", viewModel.SelectedModel);
        var modelRequestIndex = handler.RequestPaths.IndexOf("/api-dotnet/models");
        var conversationRequestIndex = handler.RequestPaths.IndexOf("/api-dotnet/conversations/c1");
        Assert.True(modelRequestIndex >= 0);
        Assert.True(conversationRequestIndex > modelRequestIndex);
        Assert.False(viewModel.CanChangeBackend);

        await viewModel.NewConversationCommand.ExecuteAsync(null);

        Assert.Equal(BackendKind.DotNet, session.Backend);
        Assert.Null(session.ConversationId);
        Assert.Null(viewModel.SelectedConversation);
        Assert.Empty(viewModel.Messages);
        Assert.True(viewModel.CanChangeBackend);
        Assert.True(viewModel.IsNewConversationVisible);
        Assert.Equal(["dotnet-default", "dotnet-saved"], viewModel.Models);
        Assert.Equal("dotnet-saved", viewModel.SelectedModel);
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
    public async Task FailedBackendMetadataCannotReusePreviousBackendModel()
    {
        var (viewModel, handler, session, telemetry, _) = CreateChatViewModel();
        await viewModel.InitializeCommand.ExecuteAsync(null);
        Assert.Equal("gpt-4.1-mini", viewModel.SelectedModel);
        handler.FailDotNetMetadata = true;

        viewModel.SelectedBackendIndex = 1;
        await WaitUntilAsync(() => telemetry.ErrorMessages.Any(message => message.Contains("metadata", StringComparison.OrdinalIgnoreCase)));

        Assert.Equal(BackendKind.DotNet, session.Backend);
        Assert.Empty(viewModel.Models);
        Assert.Null(viewModel.SelectedModel);
        Assert.False(viewModel.CanSend);
        Assert.True(viewModel.CanChangeBackend);
        Assert.Contains(viewModel.Suggestions, suggestion => suggestion.Label.Contains("Federal procurement", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OptionalSuggestionFailureKeepsTheValidModelCatalog()
    {
        var (viewModel, handler, _, telemetry, _) = CreateChatViewModel();
        handler.FailPythonSuggestions = true;

        await viewModel.InitializeCommand.ExecuteAsync(null);

        Assert.Equal(["gpt-4.1-mini"], viewModel.Models);
        Assert.Equal("gpt-4.1-mini", viewModel.SelectedModel);
        Assert.Contains(viewModel.Suggestions, suggestion => suggestion.Label.Contains("Federal procurement", StringComparison.Ordinal));
        Assert.Contains(telemetry.LogMessages, message => message.Contains("suggestions", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MetadataTimeoutEndsLoadingAndCannotEscapeTheCommand()
    {
        var (viewModel, handler, _, telemetry, _) = CreateChatViewModel();
        handler.CancelPythonModels = true;

        await viewModel.InitializeCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsMetadataLoading);
        Assert.Empty(viewModel.Models);
        Assert.Null(viewModel.SelectedModel);
        Assert.Contains(telemetry.ErrorMessages, message => message.Contains("metadata", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HistorySelectionHandsConversationToAdvisorWithoutPersistingSensitiveContent()
    {
        var handler = new RoutingHandler { IncludeConversation = true };
        var session = SignedInSession();
        var navigator = new FakeNavigator();
        var telemetry = new FakeObservability();
        var viewModel = new HistoryViewModel(CreateApi(handler, session), session, navigator, telemetry);

        await viewModel.LoadCommand.ExecuteAsync(null);
        viewModel.SelectedConversation = Assert.Single(viewModel.Conversations);
        await WaitUntilAsync(() => navigator.AdvisorNavigations == 1);

        Assert.Equal("c1", session.RequestedConversationId);
        Assert.DoesNotContain(telemetry.Attributes, value => value.Key.Contains("title", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HistoryNewConversationClearsSelectionAndNavigatesToAdvisor()
    {
        var session = SignedInSession();
        session.ConversationId = "existing";
        var navigator = new FakeNavigator();
        var viewModel = new HistoryViewModel(CreateApi(new RoutingHandler(), session), session, navigator, new FakeObservability());

        await viewModel.NewConversationCommand.ExecuteAsync(null);

        Assert.Null(session.ConversationId);
        Assert.Null(session.RequestedConversationId);
        Assert.True(session.IsNewConversationRequested);
        Assert.Equal(1, navigator.AdvisorNavigations);
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
    public async Task DotNetSelectedAttachmentUploadsThroughDotNetApi()
    {
        var media = new FakeMediaInputService { PickedItem = ImageAttachment() };
        var (viewModel, handler, session, _, _) = CreateChatViewModel(media);
        await viewModel.InitializeCommand.ExecuteAsync(null);
        viewModel.SelectedBackendIndex = 1;
        await WaitUntilAsync(() => session.Backend == BackendKind.DotNet && handler.RequestPaths.Contains("/api-dotnet/models"));

        await viewModel.PickAttachmentCommand.ExecuteAsync(null);

        Assert.Equal("Ready", Assert.Single(viewModel.Attachments).State);
        Assert.Equal("/api-dotnet/media/upload", handler.LastMediaUploadPath);
        Assert.DoesNotContain("/api/media/upload", handler.RequestPaths);
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
    public async Task UploadedAttachmentCanBeSubmittedWithoutTypedPrompt()
    {
        var media = new FakeMediaInputService { PickedItem = ImageAttachment() };
        var (viewModel, handler, _, _, _) = CreateChatViewModel(media);
        await viewModel.InitializeCommand.ExecuteAsync(null);
        await viewModel.PickAttachmentCommand.ExecuteAsync(null);

        Assert.True(viewModel.CanSend);
        await viewModel.SendCommand.ExecuteAsync(null);

        Assert.Equal("Image assessment", handler.LastConversationTitle);
        var userMessage = viewModel.Messages[0];
        Assert.Equal(string.Empty, userMessage.Content);
        Assert.Equal("image", Assert.Single(userMessage.Attachments).Kind);
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
        Assert.Equal("positive", message.SubmittedFeedback);
        Assert.Equal("Helpful ✓", message.HelpfulLabel);
        Assert.Equal("Thanks—feedback submitted.", message.ActionStatus);
        Assert.False(message.CanSubmitFeedback);
    }

    [Fact]
    public async Task CopyProvidesVisibleConfirmation()
    {
        var session = SignedInSession();
        var clipboard = new FakeClipboard();
        var viewModel = new ChatViewModel(CreateApi(new RoutingHandler(), session), session, new FakeObservability(), new FakeMediaInputService(), new FakePreferences(), clipboard, new FakeLinkLauncher());
        var message = new ChatMessageItem { Role = "assistant", Content = "Answer" };

        await viewModel.CopyMessageCommand.ExecuteAsync(message);

        Assert.Equal("Answer", clipboard.LastValue);
        Assert.True(message.IsCopied);
        Assert.Equal("Copied ✓", message.CopyLabel);
        Assert.Equal("Copied to clipboard.", message.ActionStatus);
    }

    [Fact]
    public async Task ReportProvidesVisibleConfirmation()
    {
        var (viewModel, _, _, _, _) = CreateChatViewModel();
        var message = new ChatMessageItem { Role = "assistant", Content = "Answer", TraceId = "42", SpanId = "7" };

        await viewModel.ReportFeedbackCommand.ExecuteAsync(message);

        Assert.Equal("reported", message.SubmittedFeedback);
        Assert.Equal("Reported ✓", message.ReportLabel);
        Assert.Equal("Report signal submitted.", message.ActionStatus);
    }

    [Fact]
    public async Task EvidenceSourceLaunchAllowsHttpAndRemovesQueryData()
    {
        var session = SignedInSession();
        var launcher = new FakeLinkLauncher();
        var viewModel = new ChatViewModel(CreateApi(new RoutingHandler(), session), session, new FakeObservability(), new FakeMediaInputService(), new FakePreferences(), new FakeClipboard(), launcher);
        var card = EvidenceCard("https://sam.gov/opportunities/example?account=private#details");

        await viewModel.OpenEvidenceSourceCommand.ExecuteAsync(card);

        Assert.Equal("https://sam.gov/opportunities/example", launcher.LastOpened?.AbsoluteUri);
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
        var sessionStore = new FakeSessionStore { Saved = new LoginResponse("jwt", session.User!) };
        var viewModel = new InfoViewModel(session, sessionStore, navigator, telemetry, new FakeRuntimeInfo());

        await viewModel.LogoutCommand.ExecuteAsync(null);

        Assert.True(cleaned);
        Assert.False(session.IsAuthenticated);
        Assert.True(telemetry.UserCleared);
        Assert.True(telemetry.SessionStopped);
        Assert.Equal(1, navigator.LoginNavigations);
        Assert.True(sessionStore.WasCleared);
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
        Assert.Contains(telemetry.LogMessages, message => message.Contains("warning", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(telemetry.LogMessages, message => message.Contains("error sample", StringComparison.OrdinalIgnoreCase));
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

    private static EvidenceCardItem EvidenceCard(string url) => new(new ProcurementOpportunity(
        "sam.gov:sample", "sam.gov", "sample", "contract", "Sample opportunity", new ProcurementAgency("Example Agency", null), "Sanitized sample.", "posted", "2026-08-01", "2026-09-30", new ProcurementLocation("TX", "Texas", null), new ProcurementClassifications([], [], null), new ProcurementFunding("USD", null, null, null, null), new ProcurementSource(url, null), new ProcurementDataQuality([])));

    private static InfraAdvisorApiClient CreateApi(HttpMessageHandler handler, AppSession session) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") }, session, new EmptyRumSessionProvider());

    private sealed class RoutingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public int DotNetRequestCount { get; private set; }
        public bool IncludeConversation { get; set; }
        public BackendKind ConversationBackend { get; set; } = BackendKind.Python;
        public bool UseDisjointModels { get; set; }
        public int MediaUploadFailuresRemaining { get; set; }
        public bool BlockMediaUpload { get; set; }
        public bool TruncateQueryStream { get; set; }
        public bool FailDotNetMetadata { get; set; }
        public bool FailPythonSuggestions { get; set; }
        public bool CancelPythonModels { get; set; }
        public string? LastConversationTitle { get; private set; }
        public string? LastMediaUploadPath { get; private set; }
        public List<string> RequestPaths { get; } = [];
        public TaskCompletionSource MediaUploadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var path = request.RequestUri!.AbsolutePath;
            lock (RequestPaths)
            {
                RequestPaths.Add(path);
            }
            if (path.StartsWith("/api-dotnet/", StringComparison.Ordinal)) DotNetRequestCount++;
            if (path.EndsWith("/media/upload", StringComparison.Ordinal)) LastMediaUploadPath = path;
            if (request.Content is not null)
            {
                var requestBody = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                if (path == "/api/conversations" && request.Method == HttpMethod.Post)
                {
                    using var body = System.Text.Json.JsonDocument.Parse(requestBody);
                    LastConversationTitle = body.RootElement.GetProperty("title").GetString();
                }
            }

            if (path == "/api/media/upload" && BlockMediaUpload)
            {
                MediaUploadStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return path switch
            {
                "/auth/login" => Json("{\"token\":\"jwt\",\"user\":{\"id\":\"u1\",\"email\":\"person@example.com\",\"is_admin\":false,\"is_service_account\":false,\"created_at\":null}}"),
                "/api/models" when CancelPythonModels => throw new TaskCanceledException("Synthetic metadata timeout"),
                "/api/models" when UseDisjointModels => Json("{\"models\":[\"python-model\"],\"default\":\"python-model\"}"),
                "/api-dotnet/models" when FailDotNetMetadata => Json("{\"detail\":\"Metadata unavailable\"}", HttpStatusCode.ServiceUnavailable),
                "/api-dotnet/models" when UseDisjointModels => Json("{\"models\":[\"dotnet-default\",\"dotnet-saved\"],\"default\":\"dotnet-default\"}"),
                "/api/models" or "/api-dotnet/models" => Json("{\"models\":[\"gpt-4.1-mini\"],\"default\":\"gpt-4.1-mini\"}"),
                "/api/suggestions/initial" when FailPythonSuggestions => Json("{\"detail\":\"Suggestions unavailable\"}", HttpStatusCode.ServiceUnavailable),
                "/api/suggestions/initial" => Json("{\"suggestions\":[{\"label\":\"Procurement\",\"query\":\"Find opportunities\"}]}"),
                "/api-dotnet/suggestions/initial" => Json("{\"suggestions\":[{\"label\":\".NET resilience\",\"query\":\"Inspect resilience\"}]}"),
                "/api/suggestions" => Json("{\"suggestions\":[{\"label\":\"Follow up\",\"query\":\"Show details\"}]}"),
                "/api/conversations" when request.Method == HttpMethod.Get && IncludeConversation && ConversationBackend == BackendKind.DotNet => Json("{\"conversations\":[{\"id\":\"c1\",\"user_id\":\"u1\",\"title\":\"Inspection\",\"model\":\"dotnet-saved\",\"backend\":\"dotnet\",\"message_count\":2}]}"),
                "/api/conversations" when request.Method == HttpMethod.Get && IncludeConversation => Json("{\"conversations\":[{\"id\":\"c1\",\"user_id\":\"u1\",\"title\":\"Inspection\",\"model\":\"gpt-4.1-mini\",\"backend\":\"python\",\"message_count\":2}]}"),
                "/api/conversations" when request.Method == HttpMethod.Get => Json("{\"conversations\":[]}"),
                "/api/conversations" when request.Method == HttpMethod.Post => Json("{\"id\":\"c1\",\"user_id\":\"u1\",\"title\":\"Inspection\",\"model\":\"gpt-4.1-mini\",\"backend\":\"python\",\"message_count\":0,\"messages\":[]}"),
                "/api/conversations/c1" => Json("{\"id\":\"c1\",\"user_id\":\"u1\",\"title\":\"Inspection\",\"model\":\"gpt-4.1-mini\",\"backend\":\"python\",\"message_count\":2,\"messages\":[{\"id\":\"m1\",\"conversation_id\":\"c1\",\"role\":\"user\",\"content\":\"Inspect\",\"sources\":[],\"steps\":[],\"attachments\":[{\"url\":\"https://storage.example.test/item\",\"kind\":\"image\",\"mime_type\":\"image/png\",\"size_bytes\":4}]},{\"id\":\"m2\",\"conversation_id\":\"c1\",\"role\":\"assistant\",\"content\":\"Done\",\"sources\":[],\"trace_id\":\"42\",\"span_id\":\"7\",\"steps\":[{\"kind\":\"tool\",\"id\":\"tool-1\",\"name\":\"get_bridge_condition\",\"status\":\"ok\"}],\"attachments\":[]}]}"),
                "/api-dotnet/conversations/c1" => Json("{\"id\":\"c1\",\"user_id\":\"u1\",\"title\":\"Inspection\",\"model\":\"dotnet-saved\",\"backend\":\"dotnet\",\"message_count\":2,\"messages\":[{\"id\":\"m1\",\"conversation_id\":\"c1\",\"role\":\"user\",\"content\":\"Inspect\",\"sources\":[],\"steps\":[],\"attachments\":[]},{\"id\":\"m2\",\"conversation_id\":\"c1\",\"role\":\"assistant\",\"content\":\"Done\",\"sources\":[],\"steps\":[],\"attachments\":[]}]}"),
                "/api/query/stream" when TruncateQueryStream => Sse("event: text_chunk\ndata: {\"chunk\":\"Partial answer\"}\n\n"),
                "/api/query/stream" => Sse("event: tool_call_start\ndata: {\"id\":\"tool-1\",\"name\":\"get_bridge_condition\"}\n\nevent: tool_call_end\ndata: {\"id\":\"tool-1\",\"name\":\"get_bridge_condition\",\"status\":\"ok\"}\n\nevent: artifact\ndata: {\"artifact\":{\"kind\":\"procurement_opportunities\",\"schema_version\":\"1.0\",\"status\":\"ok\",\"generated_at\":\"2026-08-26T12:00:00Z\",\"items\":[{\"id\":\"sam.gov:notice-1\",\"provider\":\"sam.gov\",\"provider_id\":\"notice-1\",\"opportunity_type\":\"contract\",\"title\":\"Resilience planning support\",\"agency\":{\"name\":\"FEMA\",\"code\":null},\"summary\":\"Sanitized sample.\",\"status\":\"posted\",\"posted_at\":\"2026-08-01\",\"deadline_at\":\"2026-09-30\",\"location\":{\"state_code\":\"TX\",\"state_name\":\"Texas\",\"city\":null},\"classifications\":{\"naics\":[\"541330\"],\"assistance_listing\":[],\"set_aside\":null},\"funding\":{\"currency\":\"USD\",\"minimum\":null,\"maximum\":null,\"total\":null,\"expected_awards\":null},\"source\":{\"url\":\"https://sam.gov/opp/notice-1\",\"retrieved_at\":\"2026-08-26T12:00:00Z\"},\"data_quality\":{\"missing_fields\":[]}}],\"meta\":{\"returned_count\":1,\"provider_counts\":{\"sam.gov\":1},\"truncated\":false,\"partial_errors\":[]}}}\n\nevent: text_chunk\ndata: {\"chunk\":\"Three bridges need review.\"}\n\nevent: done\ndata: {\"sources\":[\"https://example.test/source\"],\"trace_id\":\"42\",\"span_id\":\"7\",\"model\":\"gpt-4.1-mini\"}\n\n"),
                "/api/media/upload" when MediaUploadFailuresRemaining-- > 0 => Json("{\"detail\":\"Temporary upload failure\"}", HttpStatusCode.InternalServerError),
                "/api/media/upload" => Json("{\"url\":\"https://storage.example.test/item\",\"kind\":\"image\",\"mime_type\":\"image/png\",\"size_bytes\":4}"),
                "/api-dotnet/media/upload" => Json("{\"url\":\"https://storage.example.test/item\",\"kind\":\"image\",\"mime_type\":\"image/png\",\"size_bytes\":4}"),
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
        public int AdvisorNavigations { get; private set; }
        public Exception? AuthenticatedNavigationException { get; init; }
        public Task ShowAuthenticatedAppAsync()
        {
            AuthenticatedNavigations++;
            return AuthenticatedNavigationException is null ? Task.CompletedTask : Task.FromException(AuthenticatedNavigationException);
        }

        public Task ShowLoginAsync()
        {
            LoginNavigations++;
            return Task.CompletedTask;
        }
        public Task ShowAdvisorAsync() { AdvisorNavigations++; return Task.CompletedTask; }
    }

    private sealed class FakePreferences : IAppPreferences
    {
        private readonly Dictionary<string, string> values = [];
        public string? Get(string key, string? fallback) => values.TryGetValue(key, out var value) ? value : fallback;
        public void Set(string key, string value) => values[key] = value;
    }

    private sealed class FakeSessionStore : ISessionStore
    {
        public LoginResponse? Saved { get; set; }
        public bool WasCleared { get; private set; }
        public Task SaveAsync(LoginResponse response) { Saved = response; return Task.CompletedTask; }
        public Task<LoginResponse?> RestoreAsync() => Task.FromResult(Saved);
        public Task ClearAsync() { Saved = null; WasCleared = true; return Task.CompletedTask; }
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public string? LastValue { get; private set; }
        public Task SetTextAsync(string value) { LastValue = value; return Task.CompletedTask; }
    }

    private sealed class FakeLinkLauncher : ILinkLauncher
    {
        public Uri? LastOpened { get; private set; }
        public Task OpenAsync(Uri uri) { LastOpened = uri; return Task.CompletedTask; }
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
        public string? IdentifiedUserEmail { get; private set; }
        public bool UserCleared { get; private set; }
        public bool SessionStopped { get; private set; }
        public List<string> LogMessages { get; } = [];
        public List<string> ErrorMessages { get; } = [];
        public List<KeyValuePair<string, object>> Attributes { get; } = [];
        public List<(string Name, string Key)> SucceededOperations { get; } = [];
        public List<(string Name, string Key, bool Abandoned)> FailedOperations { get; } = [];
        public void IdentifyUser(string id, string email)
        {
            IdentifiedUserId = id;
            IdentifiedUserEmail = email;
        }
        public void ClearUser() => UserCleared = true;
        public void StopSession() => SessionStopped = true;
        public string StartOperation(string name, IReadOnlyDictionary<string, object>? attributes = null) { Capture(attributes); return $"operation-{++nextOperation}"; }
        public void SucceedOperation(string name, string operationKey, IReadOnlyDictionary<string, object>? attributes = null) { Capture(attributes); SucceededOperations.Add((name, operationKey)); }
        public void FailOperation(string name, string operationKey, bool abandoned, IReadOnlyDictionary<string, object>? attributes = null) { Capture(attributes); FailedOperations.Add((name, operationKey, abandoned)); }
        public void Info(string message, IReadOnlyDictionary<string, object>? attributes = null) { LogMessages.Add(message); Capture(attributes); }
        public void Warning(string message, IReadOnlyDictionary<string, object>? attributes = null) { LogMessages.Add(message); Capture(attributes); }
        public void ErrorLog(string message, IReadOnlyDictionary<string, object>? attributes = null) { LogMessages.Add(message); Capture(attributes); }
        public void Error(string message, Exception exception, IReadOnlyDictionary<string, object>? attributes = null) { ErrorMessages.Add(message); Capture(attributes); }
        private void Capture(IReadOnlyDictionary<string, object>? attributes)
        {
            if (attributes is not null) Attributes.AddRange(attributes);
        }
    }
}
