using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace InfraAdvisor.Mobile.Models;

public partial class ChatMessageItem : ObservableObject
{
    public required string Role { get; init; }
    public bool IsUser => Role == "user";
    [ObservableProperty] private string content = string.Empty;
    [ObservableProperty] private string sourceText = string.Empty;
    [ObservableProperty] private IReadOnlyList<string> sources = [];
    [ObservableProperty] private IReadOnlyList<MediaReference> attachments = [];
    public ObservableCollection<PipelineStepItem> Steps { get; } = [];
    [ObservableProperty] private string metadata = string.Empty;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string TimestampText => Timestamp.ToLocalTime().ToString("t");
    public string? MessageId { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanFeedback))] private string? traceId;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanFeedback))] private string? spanId;
    public bool CanFeedback => TraceId is not null && SpanId is not null;
}

public sealed record PipelineStepItem(string Id, string Kind, string Label, string Status, string? Detail = null, IReadOnlyList<string>? Sources = null, double? DurationMs = null);

public partial class AttachmentItem : ObservableObject
{
    public required string DisplayName { get; init; }
    public required string Kind { get; init; }
    public required string MimeType { get; init; }
    public required long SizeBytes { get; init; }
    public required Func<Task<Stream>> OpenReadAsync { get; init; }
    public string? TemporaryPath { get; init; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsUploading)), NotifyPropertyChangedFor(nameof(CanRetry)), NotifyPropertyChangedFor(nameof(CanCancel))] private string state = "Ready";
    [ObservableProperty] private MediaReference? remote;
    [ObservableProperty] private double progress;
    public CancellationTokenSource? UploadCancellation { get; set; }
    public bool IsAudio => Kind == "audio";
    public bool IsUploading => State == "Uploading…";
    public bool CanRetry => State is "Upload canceled" or "Upload failed — tap retry";
    public bool CanCancel => IsUploading;
}
