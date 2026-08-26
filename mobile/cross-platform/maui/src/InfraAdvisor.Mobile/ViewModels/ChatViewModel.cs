using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfraAdvisor.Mobile.Models;
using InfraAdvisor.Mobile.Observability;
using InfraAdvisor.Mobile.Services;
using InfraAdvisor.Mobile.Services.Media;

namespace InfraAdvisor.Mobile.ViewModels;

public partial class ChatViewModel(InfraAdvisorApiClient api, AppSession session, IObservability observability, IMediaInputService mediaInput, IAppPreferences preferences, IClipboardService clipboard, ILinkLauncher linkLauncher) : ObservableObject
{
    private static readonly SuggestionItem[] FallbackSuggestions =
    [
        new("Hurricane readiness", "What infrastructure risks should a Texas city review before hurricane season?"),
        new("FEMA declarations", "Summarize FEMA disaster declarations affecting Texas this year."),
        new("Bridge priorities", "Which bridges should be prioritized based on condition and traffic?"),
        new("Federal procurement (MCP)", "What current federal procurement opportunities exist related to operational resilience or emergency management enhancements in Texas infrastructure systems?"),
    ];

    private CancellationTokenSource? queryCancellation;
    private CancellationTokenSource? recordingTimerCancellation;
    private CancellationTokenSource? stillWorkingCancellation;
    private readonly CancellationTokenSource sessionCancellation = new();
    private string? recordingOperationKey;
    private bool initialized;
    private int metadataGeneration;
    private int conversationGeneration;

    public ObservableCollection<ChatMessageItem> Messages { get; } = [];
    public ObservableCollection<PipelineStepItem> PipelineSteps { get; } = [];
    public ObservableCollection<SuggestionItem> Suggestions { get; } = [];
    public ObservableCollection<string> Models { get; } = [];
    public ObservableCollection<ConversationSummary> Conversations { get; } = [];
    public ObservableCollection<AttachmentItem> Attachments { get; } = [];
    public IReadOnlyList<string> Backends { get; } = ["Python", ".NET"];

    [ObservableProperty] private string prompt = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSend)), NotifyPropertyChangedFor(nameof(SendLabel))] private bool isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string? errorMessage;
    [ObservableProperty] private string selectedBackend = "Python";
    [ObservableProperty] private int? selectedBackendIndex = 0;
    [ObservableProperty] private string? selectedModel;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelectedConversation))] private ConversationSummary? selectedConversation;
    [ObservableProperty] private bool isHistoryVisible;
    [ObservableProperty] private bool isHistoryLoading;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasHistoryError))] private string? historyErrorMessage;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RecordLabel))] private bool isRecording;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RecordLabel))] private TimeSpan recordingDuration;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanChangeBackend))] private bool isConversationLocked;
    [ObservableProperty] private bool isStillWorking;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasMessages => Messages.Count > 0;
    public bool HasNoMessages => !HasMessages;
    public bool HasNoConversations => Conversations.Count == 0;
    public bool HasSelectedConversation => SelectedConversation is not null;
    public bool HasHistoryError => !string.IsNullOrWhiteSpace(HistoryErrorMessage);
    public bool CanSend => !IsBusy && !string.IsNullOrWhiteSpace(Prompt) && SelectedModel is not null && Attachments.All(item => item.Remote is not null);
    public string SendLabel => IsBusy ? "Working…" : "Ask Infra Advisor";
    public string RecordLabel => IsRecording ? $"Stop {RecordingDuration:mm\\:ss}" : "Record";
    public bool CanChangeBackend => !IsConversationLocked;

    partial void OnPromptChanged(string value) => OnPropertyChanged(nameof(CanSend));

    partial void OnSelectedModelChanged(string? value)
    {
        session.Model = value;
        if (value is not null) preferences.Set("chat.model", value);
        OnPropertyChanged(nameof(CanSend));
    }

    partial void OnSelectedBackendChanged(string value)
    {
        var index = value == ".NET" ? 1 : 0;
        if (SelectedBackendIndex != index)
        {
            SelectedBackendIndex = index;
        }

        var next = value == ".NET" ? BackendKind.DotNet : BackendKind.Python;
        preferences.Set("chat.backend", next.ApiValue());
        if (session.Backend != next && session.ConversationId is null)
        {
            session.Backend = next;
            _ = LoadBackendMetadataAsync();
        }
    }

    partial void OnSelectedBackendIndexChanged(int? value)
    {
        var backend = value == 1 ? ".NET" : "Python";
        if (SelectedBackend != backend)
        {
            SelectedBackend = backend;
        }
    }

    partial void OnSelectedConversationChanged(ConversationSummary? value)
    {
        if (value is not null && value.Id != session.ConversationId)
        {
            _ = OpenConversationAsync(value);
        }
    }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        if (initialized)
        {
            return;
        }

        initialized = true;
        session.RegisterSessionCleanup(CleanupSessionAsync);
        session.Backend = BackendKindExtensions.ParseBackend(preferences.Get("chat.backend", session.Backend.ApiValue()));
        session.Model = preferences.Get("chat.model", null);
        SelectedBackend = session.Backend.DisplayName();
        await Task.WhenAll(LoadBackendMetadataAsync(), LoadHistoryAsync());
    }

    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        if (IsHistoryLoading)
        {
            return;
        }

        IsHistoryLoading = true;
        HistoryErrorMessage = null;
        try
        {
            var values = await api.GetConversationsAsync(sessionCancellation.Token);
            Conversations.Clear();
            foreach (var value in values)
            {
                Conversations.Add(value);
            }

            OnPropertyChanged(nameof(HasNoConversations));
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException)
        {
            HistoryErrorMessage = "Conversation history is temporarily unavailable.";
            observability.Error("Conversation history load failed", exception, new Dictionary<string, object> { ["backend"] = session.Backend.ApiValue() });
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested)
        {
            // Logout owns session cancellation; the replacement Login page should not inherit an error from this view model.
        }
        finally
        {
            IsHistoryLoading = false;
        }
    }

    [RelayCommand]
    private void ToggleHistory() => IsHistoryVisible = !IsHistoryVisible;

    [RelayCommand]
    private void CloseHistory() => IsHistoryVisible = false;

    [RelayCommand]
    private async Task NewConversationAsync()
    {
        queryCancellation?.Cancel();
        if (IsRecording)
        {
            await CancelRecordingAsync();
        }
        foreach (var attachment in Attachments.ToArray())
        {
            attachment.UploadCancellation?.Cancel();
            await mediaInput.RemoveAsync(attachment);
        }
        session.StartNewConversation();
        SelectedConversation = null;
        Messages.Clear();
        PipelineSteps.Clear();
        Attachments.Clear();
        ErrorMessage = null;
        IsHistoryVisible = false;
        IsConversationLocked = false;
        NotifyMessageState();
    }

    [RelayCommand]
    private void UseSuggestion(string suggestion) => Prompt = suggestion;

    [RelayCommand]
    private void CancelQuery() => queryCancellation?.Cancel();

    [RelayCommand]
    private async Task PickAttachmentAsync()
    {
        try
        {
            var item = await mediaInput.PickAsync();
            if (item is null)
            {
                return;
            }

            await AddAndUploadAsync(item);
        }
        catch (Exception exception) when (exception is ApiException or IOException)
        {
            ErrorMessage = exception.Message;
            observability.Error("Attachment selection failed", exception);
        }
    }

    [RelayCommand]
    private async Task RecordAudioAsync()
    {
        try
        {
            if (!IsRecording)
            {
                if (Attachments.Any(item => item.Kind == "audio"))
                {
                    ErrorMessage = "Remove the current audio attachment before recording another.";
                    return;
                }

                await mediaInput.StartRecordingAsync();
                IsRecording = true;
                RecordingDuration = TimeSpan.Zero;
                recordingTimerCancellation = new CancellationTokenSource();
                _ = RunRecordingTimerAsync(recordingTimerCancellation.Token);
                recordingOperationKey = observability.StartOperation("media.record", new Dictionary<string, object> { ["modality"] = "audio" });
                observability.Info("Audio recording started", new Dictionary<string, object> { ["modality"] = "audio" });
            }
            else
            {
                var item = await mediaInput.StopRecordingAsync();
                recordingTimerCancellation?.Cancel();
                IsRecording = false;
                if (recordingOperationKey is { } operationKey)
                {
                    observability.SucceedOperation("media.record", operationKey, new Dictionary<string, object> { ["modality"] = "audio", ["duration_ms"] = (long)RecordingDuration.TotalMilliseconds, ["size_bytes"] = item.SizeBytes });
                    recordingOperationKey = null;
                }
                await AddAndUploadAsync(item);
            }
        }
        catch (Exception exception) when (exception is ApiException or IOException or UnauthorizedAccessException)
        {
            IsRecording = false;
            ErrorMessage = exception.Message;
            if (recordingOperationKey is { } operationKey)
            {
                observability.FailOperation("media.record", operationKey, abandoned: false, new Dictionary<string, object> { ["modality"] = "audio", ["duration_ms"] = (long)RecordingDuration.TotalMilliseconds });
                recordingOperationKey = null;
            }
            observability.Error("Audio recording failed", exception, new Dictionary<string, object> { ["modality"] = "audio" });
        }
    }

    [RelayCommand]
    private async Task RetryAttachmentAsync(AttachmentItem item) => await UploadAsync(item);

    [RelayCommand]
    private async Task RemoveAttachmentAsync(AttachmentItem item)
    {
        item.UploadCancellation?.Cancel();
        Attachments.Remove(item);
        await mediaInput.RemoveAsync(item);
        OnPropertyChanged(nameof(CanSend));
    }

    [RelayCommand]
    private Task PlayAttachmentAsync(AttachmentItem item) => mediaInput.PlayAsync(item);

    [RelayCommand]
    private void CancelAttachment(AttachmentItem item) => item.UploadCancellation?.Cancel();

    [RelayCommand]
    private async Task CancelRecordingAsync()
    {
        var elapsedMilliseconds = (long)RecordingDuration.TotalMilliseconds;
        recordingTimerCancellation?.Cancel();
        await mediaInput.CancelRecordingAsync();
        IsRecording = false;
        RecordingDuration = TimeSpan.Zero;
        if (recordingOperationKey is { } operationKey)
        {
            observability.FailOperation("media.record", operationKey, abandoned: true, new Dictionary<string, object> { ["modality"] = "audio", ["duration_ms"] = elapsedMilliseconds, ["result"] = "canceled" });
            recordingOperationKey = null;
        }
        observability.Info("Audio recording canceled", new Dictionary<string, object> { ["modality"] = "audio" });
    }

    [RelayCommand]
    private Task PositiveFeedbackAsync(ChatMessageItem message) => SubmitFeedbackAsync(message, "positive");

    [RelayCommand]
    private Task NegativeFeedbackAsync(ChatMessageItem message) => SubmitFeedbackAsync(message, "negative");

    [RelayCommand]
    private Task ReportFeedbackAsync(ChatMessageItem message) => SubmitFeedbackAsync(message, "reported");

    [RelayCommand]
    private Task CopyMessageAsync(ChatMessageItem message) => clipboard.SetTextAsync(message.Content);

    [RelayCommand]
    private async Task OpenSourceAsync(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            await linkLauncher.OpenAsync(uri);
        }
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        if (!CanSend || SelectedModel is null)
        {
            return;
        }

        var query = Prompt.Trim();
        var queryOperationKey = observability.StartOperation("ai.query", new Dictionary<string, object> { ["backend"] = session.Backend.ApiValue(), ["attachment_count"] = Attachments.Count });
        Prompt = string.Empty;
        IsBusy = true;
        ErrorMessage = null;
        PipelineSteps.Clear();
        queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation.Token);
        ResetStillWorkingTimer();
        var messageAttachments = Attachments.Where(item => item.Remote is not null).Select(item => item.Remote!).ToArray();
        var assistantMessage = new ChatMessageItem { Role = "assistant", Content = string.Empty };
        Messages.Add(new ChatMessageItem { Role = "user", Content = query, Attachments = messageAttachments });
        Messages.Add(assistantMessage);
        NotifyMessageState();

        try
        {
            if (session.ConversationId is null)
            {
                var title = query.Length <= 64 ? query : string.Concat(query.AsSpan(0, 61), "…");
                var conversation = await api.CreateConversationAsync(title, SelectedModel, queryCancellation.Token);
                session.ConversationId = conversation.Id;
                IsConversationLocked = true;
            }

            var uploaded = messageAttachments;
            observability.Info("AI query stream started", new Dictionary<string, object> { ["backend"] = session.Backend.ApiValue(), ["attachment_count"] = uploaded.Length });
            await foreach (var streamEvent in api.StreamQueryAsync(new QueryStreamRequest(query, session.SessionId, SelectedModel, uploaded), queryCancellation.Token))
            {
                ResetStillWorkingTimer();
                ApplyStreamEvent(streamEvent, assistantMessage);
            }

            observability.Info("AI query stream completed", new Dictionary<string, object> { ["backend"] = session.Backend.ApiValue(), ["result"] = "success" });
            observability.SucceedOperation("ai.query", queryOperationKey, new Dictionary<string, object> { ["backend"] = session.Backend.ApiValue(), ["result"] = "success" });
            await LoadHistoryAsync();
            _ = LoadContextualSuggestionsAsync(query, assistantMessage);
            foreach (var attachment in Attachments.ToArray())
            {
                await mediaInput.RemoveAsync(attachment);
            }
            Attachments.Clear();
        }
        catch (OperationCanceledException)
        {
            assistantMessage.Content = string.IsNullOrWhiteSpace(assistantMessage.Content) ? "Request canceled." : assistantMessage.Content;
            observability.FailOperation("ai.query", queryOperationKey, abandoned: true, new Dictionary<string, object> { ["backend"] = session.Backend.ApiValue(), ["result"] = "canceled" });
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException)
        {
            ErrorMessage = exception is ApiException apiException ? apiException.Message : "The service could not be reached. Check your connection and try again.";
            assistantMessage.Content = string.IsNullOrWhiteSpace(assistantMessage.Content) ? "I couldn't complete that request." : assistantMessage.Content;
            observability.FailOperation("ai.query", queryOperationKey, abandoned: false, new Dictionary<string, object> { ["backend"] = session.Backend.ApiValue(), ["result"] = "error" });
            observability.Error("AI query stream failed", exception, new Dictionary<string, object> { ["backend"] = session.Backend.ApiValue() });
        }
        finally
        {
            stillWorkingCancellation?.Cancel();
            stillWorkingCancellation?.Dispose();
            stillWorkingCancellation = null;
            IsStillWorking = false;
            IsBusy = false;
            queryCancellation.Dispose();
            queryCancellation = null;
        }
    }

    [RelayCommand]
    private async Task DeleteConversationAsync(ConversationSummary conversation)
    {
        try
        {
            await api.DeleteConversationAsync(conversation.Id, sessionCancellation.Token);
            if (session.ConversationId == conversation.Id)
            {
                await NewConversationAsync();
            }

            await LoadHistoryAsync();
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException)
        {
            ErrorMessage = "The conversation could not be deleted.";
            observability.Error("Conversation delete failed", exception, new Dictionary<string, object> { ["backend"] = session.Backend.ApiValue() });
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested)
        {
        }
    }

    private async Task LoadBackendMetadataAsync()
    {
        var generation = Interlocked.Increment(ref metadataGeneration);
        try
        {
            var models = await api.GetModelsAsync(sessionCancellation.Token);
            if (generation != metadataGeneration)
            {
                return;
            }

            Models.Clear();
            foreach (var model in models.Models)
            {
                Models.Add(model);
            }

            SelectedModel = session.Model is { } savedModel && Models.Contains(savedModel) ? savedModel : models.DefaultModel;
            var suggestions = await api.GetInitialSuggestionsAsync(sessionCancellation.Token);
            if (generation != metadataGeneration)
            {
                return;
            }

            SetSuggestions(suggestions.Count > 0 ? suggestions : FallbackSuggestions);
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException)
        {
            if (generation == metadataGeneration)
            {
                SetSuggestions(FallbackSuggestions);
                observability.Error("Chat metadata load failed", exception, new Dictionary<string, object> { ["backend"] = session.Backend.ApiValue() });
            }
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested)
        {
        }
    }

    private async Task OpenConversationAsync(ConversationSummary summary)
    {
        var generation = Interlocked.Increment(ref conversationGeneration);
        var previousBackend = session.Backend;
        try
        {
            session.Backend = BackendKindExtensions.ParseBackend(summary.Backend);
            SelectedBackend = session.Backend.DisplayName();
            var detail = await api.GetConversationAsync(summary.Id, sessionCancellation.Token);
            if (generation != conversationGeneration)
            {
                return;
            }

            session.ConversationId = summary.Id;
            IsConversationLocked = true;
            Messages.Clear();
            foreach (var message in detail.Messages)
            {
                var item = new ChatMessageItem { Role = message.Role, Content = message.Content, Sources = message.Sources ?? [], Attachments = message.Attachments ?? [], SourceText = message.Sources is { Count: > 0 } ? $"Sources: {string.Join(", ", message.Sources)}" : string.Empty, Metadata = message.TraceId is null ? string.Empty : $"Trace {message.TraceId}", MessageId = message.Id, TraceId = message.TraceId, SpanId = message.SpanId, Timestamp = DateTimeOffset.TryParse(message.CreatedAt, out var created) ? created : DateTimeOffset.Now };
                foreach (var step in message.Steps ?? [])
                {
                    item.Steps.Add(new PipelineStepItem(step.Id, step.Kind, step.Name, step.Status, step.Detail ?? step.ResultSummary, step.Sources, step.DurationMs));
                }

                Messages.Add(item);
            }

            SelectedModel = detail.Model ?? SelectedModel;
            IsHistoryVisible = false;
            NotifyMessageState();
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException)
        {
            if (generation != conversationGeneration)
            {
                return;
            }

            session.Backend = previousBackend;
            session.ConversationId = null;
            SelectedBackend = previousBackend.DisplayName();
            IsConversationLocked = false;
            SelectedConversation = null;
            ErrorMessage = "That conversation could not be loaded.";
            observability.Error("Conversation detail load failed", exception, new Dictionary<string, object> { ["backend"] = session.Backend.ApiValue() });
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested)
        {
        }
    }

    private void ApplyStreamEvent(StreamEvent streamEvent, ChatMessageItem assistant)
    {
        switch (streamEvent.Event)
        {
            case "step":
            case "tool_call_start":
            case "tool_call_end":
                var step = ToPipelineStep(streamEvent);
                UpsertStep(PipelineSteps, step);
                UpsertStep(assistant.Steps, step);
                break;
            case "text_chunk": assistant.Content += streamEvent.Chunk; break;
            case "done":
                assistant.SourceText = streamEvent.Sources is { Count: > 0 } ? $"Sources: {string.Join(", ", streamEvent.Sources)}" : string.Empty;
                assistant.Sources = streamEvent.Sources ?? [];
                assistant.Metadata = streamEvent.TraceId is null ? string.Empty : $"Trace {streamEvent.TraceId} · {streamEvent.Model}";
                assistant.MessageId = streamEvent.MessageId;
                assistant.TraceId = streamEvent.TraceId;
                assistant.SpanId = streamEvent.SpanId;
                break;
            case "error": throw new ApiException(streamEvent.Message ?? "The AI pipeline returned an error.", category: streamEvent.Category ?? "stream_error");
        }
    }

    private async Task LoadContextualSuggestionsAsync(string query, ChatMessageItem assistant)
    {
        try
        {
            var suggestions = await api.GetContextualSuggestionsAsync(query, assistant.Content, assistant.Sources, sessionCancellation.Token);
            SetSuggestions(suggestions.Count > 0 ? suggestions : FallbackSuggestions);
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException)
        {
            observability.Info("Contextual suggestions unavailable", new Dictionary<string, object> { ["error_type"] = exception.GetType().Name });
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested)
        {
        }
    }

    private void SetSuggestions(IEnumerable<SuggestionItem> values)
    {
        Suggestions.Clear();
        foreach (var value in values.Take(6)) Suggestions.Add(value);
    }

    private void NotifyMessageState()
    {
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(HasNoMessages));
    }

    private async Task AddAndUploadAsync(AttachmentItem item)
    {
        if (Attachments.Any(existing => existing.Kind == item.Kind))
        {
            ErrorMessage = $"Only one {item.Kind} attachment can be sent with each question.";
            return;
        }

        Attachments.Add(item);
        OnPropertyChanged(nameof(CanSend));
        await UploadAsync(item);
    }

    private async Task UploadAsync(AttachmentItem item)
    {
        var duration = Stopwatch.StartNew();
        var operationKey = observability.StartOperation("media.upload", new Dictionary<string, object> { ["modality"] = item.Kind, ["size_bytes"] = item.SizeBytes });
        item.State = "Uploading…";
        item.Progress = 0;
        item.UploadCancellation?.Dispose();
        item.UploadCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation.Token);
        try
        {
            await using var stream = await item.OpenReadAsync();
            var progress = new Progress<double>(value => item.Progress = value);
            var response = await api.UploadMediaAsync(stream, item.DisplayName, item.MimeType, item.SizeBytes, progress, item.UploadCancellation.Token);
            item.Remote = new MediaReference(response.Url, response.Kind, response.MimeType, response.SizeBytes);
            item.State = "Ready";
            OnPropertyChanged(nameof(CanSend));
            observability.SucceedOperation("media.upload", operationKey, new Dictionary<string, object> { ["modality"] = item.Kind, ["size_bytes"] = item.SizeBytes, ["duration_ms"] = duration.ElapsedMilliseconds, ["result"] = "success" });
            observability.Info("Attachment upload completed", new Dictionary<string, object> { ["modality"] = item.Kind, ["size_bytes"] = item.SizeBytes, ["duration_ms"] = duration.ElapsedMilliseconds, ["result"] = "success" });
        }
        catch (OperationCanceledException)
        {
            item.State = "Upload canceled";
            OnPropertyChanged(nameof(CanSend));
            observability.FailOperation("media.upload", operationKey, abandoned: true, new Dictionary<string, object> { ["modality"] = item.Kind, ["size_bytes"] = item.SizeBytes, ["duration_ms"] = duration.ElapsedMilliseconds, ["result"] = "canceled" });
            observability.Info("Attachment upload canceled", new Dictionary<string, object> { ["modality"] = item.Kind, ["size_bytes"] = item.SizeBytes, ["duration_ms"] = duration.ElapsedMilliseconds, ["result"] = "canceled" });
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException or IOException)
        {
            item.State = "Upload failed — tap retry";
            OnPropertyChanged(nameof(CanSend));
            observability.FailOperation("media.upload", operationKey, abandoned: false, new Dictionary<string, object> { ["modality"] = item.Kind, ["size_bytes"] = item.SizeBytes, ["duration_ms"] = duration.ElapsedMilliseconds, ["result"] = "error" });
            observability.Error("Attachment upload failed", exception, new Dictionary<string, object> { ["modality"] = item.Kind, ["size_bytes"] = item.SizeBytes, ["duration_ms"] = duration.ElapsedMilliseconds });
        }
        finally
        {
            item.UploadCancellation.Dispose();
            item.UploadCancellation = null;
        }
    }

    private async Task SubmitFeedbackAsync(ChatMessageItem message, string rating)
    {
        if (message.TraceId is null || message.SpanId is null)
        {
            return;
        }

        var operationKey = observability.StartOperation("ai.feedback", new Dictionary<string, object> { ["rating"] = rating, ["backend"] = session.Backend.ApiValue() });
        try
        {
            await api.SendFeedbackAsync(new FeedbackRequest(message.TraceId, message.SpanId, rating, session.SessionId), sessionCancellation.Token);
            observability.SucceedOperation("ai.feedback", operationKey, new Dictionary<string, object> { ["rating"] = rating, ["backend"] = session.Backend.ApiValue() });
            observability.Info("AI response feedback submitted", new Dictionary<string, object> { ["rating"] = rating, ["backend"] = session.Backend.ApiValue() });
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException)
        {
            ErrorMessage = "Feedback could not be submitted.";
            observability.FailOperation("ai.feedback", operationKey, abandoned: false, new Dictionary<string, object> { ["rating"] = rating, ["backend"] = session.Backend.ApiValue() });
            observability.Error("AI response feedback failed", exception, new Dictionary<string, object> { ["rating"] = rating });
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested)
        {
            observability.FailOperation("ai.feedback", operationKey, abandoned: true, new Dictionary<string, object> { ["rating"] = rating, ["backend"] = session.Backend.ApiValue() });
        }
    }

    private async Task RunRecordingTimerAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                RecordingDuration += TimeSpan.FromSeconds(1);
                if (RecordingDuration >= TimeSpan.FromSeconds(90))
                {
                    await RecordAudioAsync();
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ResetStillWorkingTimer()
    {
        stillWorkingCancellation?.Cancel();
        stillWorkingCancellation?.Dispose();
        IsStillWorking = false;
        stillWorkingCancellation = CancellationTokenSource.CreateLinkedTokenSource(queryCancellation?.Token ?? CancellationToken.None);
        _ = ShowStillWorkingAfterDelayAsync(stillWorkingCancellation.Token);
    }

    private async Task ShowStillWorkingAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
            IsStillWorking = true;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static PipelineStepItem ToPipelineStep(StreamEvent streamEvent) => streamEvent.Event switch
    {
        "step" => new PipelineStepItem($"internal:{streamEvent.Step ?? "processing"}", "internal", streamEvent.Step ?? "Processing", streamEvent.Status ?? "running", streamEvent.Detail),
        "tool_call_start" => new PipelineStepItem(streamEvent.Id ?? $"tool:{streamEvent.Name ?? "unknown"}", "tool", streamEvent.Name ?? "Tool call", "running"),
        "tool_call_end" => new PipelineStepItem(streamEvent.Id ?? $"tool:{streamEvent.Name ?? "unknown"}", "tool", streamEvent.Name ?? "Tool call", streamEvent.Status ?? "complete", streamEvent.ResultSummary, streamEvent.Sources, streamEvent.DurationMs),
        _ => throw new ArgumentOutOfRangeException(nameof(streamEvent)),
    };

    private static void UpsertStep(ObservableCollection<PipelineStepItem> steps, PipelineStepItem value)
    {
        var index = steps.Select((step, position) => (step, position)).FirstOrDefault(pair => pair.step.Id == value.Id).position;
        if (steps.Count > 0 && index >= 0 && index < steps.Count && steps[index].Id == value.Id)
        {
            steps[index] = value;
            return;
        }

        steps.Add(value);
    }

    private async Task CleanupSessionAsync()
    {
        sessionCancellation.Cancel();
        queryCancellation?.Cancel();
        stillWorkingCancellation?.Cancel();
        recordingTimerCancellation?.Cancel();
        if (IsRecording || recordingOperationKey is not null)
        {
            await CancelRecordingAsync();
        }
        else
        {
            await mediaInput.CancelRecordingAsync();
        }
        foreach (var attachment in Attachments.ToArray())
        {
            attachment.UploadCancellation?.Cancel();
            await mediaInput.RemoveAsync(attachment);
        }
        Attachments.Clear();
    }
}
