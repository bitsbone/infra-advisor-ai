using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InfraAdvisor.Mobile.Models;
using InfraAdvisor.Mobile.Observability;
using InfraAdvisor.Mobile.Services;
using InfraAdvisor.Mobile.Services.Media;

namespace InfraAdvisor.Mobile.ViewModels;

public partial class ChatViewModel(InfraAdvisorApiClient api, AppSession session, IObservability observability, IMediaInputService mediaInput) : ObservableObject
{
    private static readonly string[] FallbackSuggestions =
    [
        "What infrastructure risks should a Texas city review before hurricane season?",
        "Summarize FEMA disaster declarations affecting Texas this year.",
        "Which bridges should be prioritized based on condition and traffic?",
        "What current federal procurement opportunities exist related to operational resilience or emergency management enhancements in Texas infrastructure systems?",
    ];

    private CancellationTokenSource? queryCancellation;
    private CancellationTokenSource? recordingTimerCancellation;
    private bool initialized;

    public ObservableCollection<ChatMessageItem> Messages { get; } = [];
    public ObservableCollection<PipelineStepItem> PipelineSteps { get; } = [];
    public ObservableCollection<string> Suggestions { get; } = [];
    public ObservableCollection<string> Models { get; } = [];
    public ObservableCollection<ConversationSummary> Conversations { get; } = [];
    public ObservableCollection<AttachmentItem> Attachments { get; } = [];
    public IReadOnlyList<string> Backends { get; } = ["Python", ".NET"];

    [ObservableProperty] private string prompt = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSend)), NotifyPropertyChangedFor(nameof(SendLabel))] private bool isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string? errorMessage;
    [ObservableProperty] private string selectedBackend = "Python";
    [ObservableProperty] private string? selectedModel;
    [ObservableProperty] private ConversationSummary? selectedConversation;
    [ObservableProperty] private bool isHistoryVisible;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RecordLabel))] private bool isRecording;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RecordLabel))] private TimeSpan recordingDuration;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanChangeBackend))] private bool isConversationLocked;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasMessages => Messages.Count > 0;
    public bool HasNoMessages => !HasMessages;
    public bool CanSend => !IsBusy && !string.IsNullOrWhiteSpace(Prompt) && SelectedModel is not null && Attachments.All(item => item.Remote is not null);
    public string SendLabel => IsBusy ? "Working…" : "Ask Infra Advisor";
    public string RecordLabel => IsRecording ? $"Stop {RecordingDuration:mm\\:ss}" : "Record";
    public bool CanChangeBackend => !IsConversationLocked;

    partial void OnPromptChanged(string value) => OnPropertyChanged(nameof(CanSend));

    partial void OnSelectedModelChanged(string? value)
    {
        session.Model = value;
        if (value is not null) Preferences.Default.Set("chat.model", value);
        OnPropertyChanged(nameof(CanSend));
    }

    partial void OnSelectedBackendChanged(string value)
    {
        var next = value == ".NET" ? BackendKind.DotNet : BackendKind.Python;
        Preferences.Default.Set("chat.backend", next.ApiValue());
        if (session.Backend != next && session.ConversationId is null)
        {
            session.Backend = next;
            _ = LoadBackendMetadataAsync();
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
        session.Backend = BackendKindExtensions.ParseBackend(Preferences.Default.Get("chat.backend", session.Backend.ApiValue()));
        session.Model = Preferences.Default.Get<string?>("chat.model", null);
        SelectedBackend = session.Backend.DisplayName();
        await Task.WhenAll(LoadBackendMetadataAsync(), LoadHistoryAsync());
    }

    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        try
        {
            var values = await api.GetConversationsAsync();
            Conversations.Clear();
            foreach (var value in values)
            {
                Conversations.Add(value);
            }
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException)
        {
            ErrorMessage = "Conversation history is temporarily unavailable.";
            observability.Error("Conversation history load failed", exception, new Dictionary<string, object> { ["backend"] = session.Backend.ApiValue() });
        }
    }

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
                observability.Info("Audio recording started", new Dictionary<string, object> { ["modality"] = "audio" });
            }
            else
            {
                var item = await mediaInput.StopRecordingAsync();
                recordingTimerCancellation?.Cancel();
                IsRecording = false;
                await AddAndUploadAsync(item);
            }
        }
        catch (Exception exception) when (exception is ApiException or IOException or UnauthorizedAccessException)
        {
            IsRecording = false;
            ErrorMessage = exception.Message;
            observability.Error("Audio recording failed", exception, new Dictionary<string, object> { ["modality"] = "audio" });
        }
    }

    [RelayCommand]
    private async Task RetryAttachmentAsync(AttachmentItem item) => await UploadAsync(item);

    [RelayCommand]
    private async Task RemoveAttachmentAsync(AttachmentItem item)
    {
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
        recordingTimerCancellation?.Cancel();
        await mediaInput.CancelRecordingAsync();
        IsRecording = false;
        RecordingDuration = TimeSpan.Zero;
        observability.Info("Audio recording canceled", new Dictionary<string, object> { ["modality"] = "audio" });
    }

    [RelayCommand]
    private Task PositiveFeedbackAsync(ChatMessageItem message) => SubmitFeedbackAsync(message, "positive");

    [RelayCommand]
    private Task NegativeFeedbackAsync(ChatMessageItem message) => SubmitFeedbackAsync(message, "negative");

    [RelayCommand]
    private Task ReportFeedbackAsync(ChatMessageItem message) => SubmitFeedbackAsync(message, "reported");

    [RelayCommand]
    private static Task CopyMessageAsync(ChatMessageItem message) => Clipboard.Default.SetTextAsync(message.Content);

    [RelayCommand]
    private async Task SendAsync()
    {
        if (!CanSend || SelectedModel is null)
        {
            return;
        }

        var query = Prompt.Trim();
        Prompt = string.Empty;
        IsBusy = true;
        ErrorMessage = null;
        PipelineSteps.Clear();
        queryCancellation = new CancellationTokenSource();
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
                ApplyStreamEvent(streamEvent, assistantMessage);
            }

            observability.Info("AI query stream completed", new Dictionary<string, object> { ["backend"] = session.Backend.ApiValue(), ["result"] = "success" });
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
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException)
        {
            ErrorMessage = exception is ApiException apiException ? apiException.Message : "The service could not be reached. Check your connection and try again.";
            assistantMessage.Content = string.IsNullOrWhiteSpace(assistantMessage.Content) ? "I couldn't complete that request." : assistantMessage.Content;
            observability.Error("AI query stream failed", exception, new Dictionary<string, object> { ["backend"] = session.Backend.ApiValue() });
        }
        finally
        {
            IsBusy = false;
            queryCancellation.Dispose();
            queryCancellation = null;
        }
    }

    [RelayCommand]
    private async Task DeleteConversationAsync(ConversationSummary conversation)
    {
        await api.DeleteConversationAsync(conversation.Id);
        if (session.ConversationId == conversation.Id)
        {
            await NewConversationAsync();
        }

        await LoadHistoryAsync();
    }

    private async Task LoadBackendMetadataAsync()
    {
        try
        {
            var models = await api.GetModelsAsync();
            Models.Clear();
            foreach (var model in models.Models)
            {
                Models.Add(model);
            }

            SelectedModel = session.Model is { } savedModel && Models.Contains(savedModel) ? savedModel : models.DefaultModel;
            var suggestions = await api.GetInitialSuggestionsAsync();
            SetSuggestions(suggestions.Count > 0 ? suggestions : FallbackSuggestions);
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException)
        {
            SetSuggestions(FallbackSuggestions);
            observability.Error("Chat metadata load failed", exception, new Dictionary<string, object> { ["backend"] = session.Backend.ApiValue() });
        }
    }

    private async Task OpenConversationAsync(ConversationSummary summary)
    {
        try
        {
            session.Backend = BackendKindExtensions.ParseBackend(summary.Backend);
            session.ConversationId = summary.Id;
            IsConversationLocked = true;
            SelectedBackend = session.Backend.DisplayName();
            var detail = await api.GetConversationAsync(summary.Id);
            Messages.Clear();
            foreach (var message in detail.Messages)
            {
                Messages.Add(new ChatMessageItem { Role = message.Role, Content = message.Content, Sources = message.Sources ?? [], Attachments = message.Attachments ?? [], SourceText = message.Sources is { Count: > 0 } ? $"Sources: {string.Join(", ", message.Sources)}" : string.Empty, Metadata = message.TraceId is null ? string.Empty : $"Trace {message.TraceId}", MessageId = message.Id, TraceId = message.TraceId, SpanId = message.SpanId, Timestamp = DateTimeOffset.TryParse(message.CreatedAt, out var created) ? created : DateTimeOffset.Now });
            }

            SelectedModel = detail.Model ?? SelectedModel;
            IsHistoryVisible = false;
            NotifyMessageState();
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException)
        {
            ErrorMessage = "That conversation could not be loaded.";
            observability.Error("Conversation detail load failed", exception, new Dictionary<string, object> { ["backend"] = session.Backend.ApiValue() });
        }
    }

    private void ApplyStreamEvent(StreamEvent streamEvent, ChatMessageItem assistant)
    {
        switch (streamEvent.Event)
        {
            case "step": PipelineSteps.Add(new PipelineStepItem(streamEvent.Step ?? "Processing", streamEvent.Status ?? "active")); break;
            case "tool_call_start": PipelineSteps.Add(new PipelineStepItem(streamEvent.Name ?? "Tool call", "running")); break;
            case "tool_call_end": PipelineSteps.Add(new PipelineStepItem(streamEvent.Name ?? "Tool call", "complete")); break;
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
            var sources = string.IsNullOrWhiteSpace(assistant.SourceText) ? Array.Empty<string>() : [assistant.SourceText];
            SetSuggestions(await api.GetContextualSuggestionsAsync(query, assistant.Content, sources));
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException)
        {
            observability.Info("Contextual suggestions unavailable", new Dictionary<string, object> { ["error_type"] = exception.GetType().Name });
        }
    }

    private void SetSuggestions(IEnumerable<string> values)
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
        item.State = "Uploading…";
        item.Progress = 0;
        item.UploadCancellation?.Dispose();
        item.UploadCancellation = new CancellationTokenSource();
        try
        {
            await using var stream = await item.OpenReadAsync();
            var progress = new Progress<double>(value => item.Progress = value);
            var response = await api.UploadMediaAsync(stream, item.DisplayName, item.MimeType, item.SizeBytes, progress, item.UploadCancellation.Token);
            item.Remote = new MediaReference(response.Url, response.Kind, response.MimeType, response.SizeBytes);
            item.State = "Ready";
            OnPropertyChanged(nameof(CanSend));
            observability.Info("Attachment upload completed", new Dictionary<string, object> { ["modality"] = item.Kind, ["size_bytes"] = item.SizeBytes, ["result"] = "success" });
        }
        catch (OperationCanceledException)
        {
            item.State = "Upload canceled";
            OnPropertyChanged(nameof(CanSend));
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException or IOException)
        {
            item.State = "Upload failed — tap retry";
            OnPropertyChanged(nameof(CanSend));
            observability.Error("Attachment upload failed", exception, new Dictionary<string, object> { ["modality"] = item.Kind, ["size_bytes"] = item.SizeBytes });
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

        try
        {
            await api.SendFeedbackAsync(new FeedbackRequest(message.TraceId, message.SpanId, rating, session.SessionId));
            observability.Info("AI response feedback submitted", new Dictionary<string, object> { ["rating"] = rating, ["backend"] = session.Backend.ApiValue() });
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException)
        {
            ErrorMessage = "Feedback could not be submitted.";
            observability.Error("AI response feedback failed", exception, new Dictionary<string, object> { ["rating"] = rating });
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

    private async Task CleanupSessionAsync()
    {
        queryCancellation?.Cancel();
        recordingTimerCancellation?.Cancel();
        await mediaInput.CancelRecordingAsync();
        foreach (var attachment in Attachments.ToArray())
        {
            await mediaInput.RemoveAsync(attachment);
        }
        Attachments.Clear();
    }
}
