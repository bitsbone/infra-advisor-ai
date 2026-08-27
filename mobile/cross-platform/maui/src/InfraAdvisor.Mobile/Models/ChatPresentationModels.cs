using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace InfraAdvisor.Mobile.Models;

public partial class ChatMessageItem : ObservableObject
{
    public required string Role { get; init; }
    public bool IsUser => Role == "user";
    [ObservableProperty] private string content = string.Empty;
    [ObservableProperty] private string sourceText = string.Empty;
    [ObservableProperty] private IReadOnlyList<string> sources = [];
    [ObservableProperty] private IReadOnlyList<MediaReference> attachments = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasEvidence)), NotifyPropertyChangedFor(nameof(EvidenceLabel))] private IReadOnlyList<EvidenceCardItem> evidence = [];
    public ObservableCollection<PipelineStepItem> Steps { get; } = [];
    [ObservableProperty] private string metadata = string.Empty;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string TimestampText => Timestamp.ToLocalTime().ToString("t");
    public string? MessageId { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanFeedback))] private string? traceId;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanFeedback))] private string? spanId;
    public bool CanFeedback => TraceId is not null && SpanId is not null;
    public bool HasEvidence => Evidence.Count > 0;
    public string EvidenceLabel => Evidence.Count == 1 ? "Review 1 evidence item" : $"Review {Evidence.Count} evidence items";
}

public sealed record PipelineStepItem(string Id, string Kind, string Label, string Status, string? Detail = null, IReadOnlyList<string>? Sources = null, double? DurationMs = null);

public sealed record EvidenceCardItem(ProcurementOpportunity Value)
{
    public string Kind => Value.OpportunityType;
    public string Source => Value.Provider;
    public string Title => Value.Title;
    public string Summary => Value.Summary;
    public string Agency => Value.Agency.Name;
    public string Location => string.Join(", ", new[] { Value.Location.City, Value.Location.StateName ?? Value.Location.StateCode }.Where(value => !string.IsNullOrWhiteSpace(value)));
    public string Deadline => DateTimeOffset.TryParse(Value.DeadlineAt, out var deadline) ? $"Due {deadline.ToLocalTime():MMM d, yyyy}" : string.Empty;
    public string Amount => Value.Funding.Total is { } total ? $"{total:C0} total" : Value.Funding.Maximum is { } maximum ? $"Up to {maximum:C0}" : Value.Funding.Minimum is { } minimum ? $"From {minimum:C0}" : string.Empty;
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
    public bool HasAgency => !string.IsNullOrWhiteSpace(Agency);
    public bool HasLocation => !string.IsNullOrWhiteSpace(Location);
    public bool HasDeadline => !string.IsNullOrWhiteSpace(Deadline);
    public bool HasAmount => !string.IsNullOrWhiteSpace(Amount);
    public bool HasLink => Uri.TryCreate(Value.Source.Url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
}

/// <summary>Converts only known, versioned artifacts into UI cards and safely ignores malformed or future shapes.</summary>
public static class ArtifactPresentationMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<EvidenceCardItem> ToCards(IEnumerable<ChatArtifact> artifacts) => artifacts.SelectMany(ToCards).GroupBy(value => value.Value.Id).Select(group => group.First()).ToArray();

    public static IReadOnlyList<EvidenceCardItem> ToCards(ChatArtifact artifact)
    {
        if (artifact is not { Kind: "procurement_opportunities", SchemaVersion: "1.0" } || artifact.Items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var cards = new List<EvidenceCardItem>();
        foreach (var value in artifact.Items.EnumerateArray())
        {
            try
            {
                if (value.Deserialize<ProcurementOpportunity>(JsonOptions) is { } opportunity && IsRenderable(opportunity))
                {
                    cards.Add(new EvidenceCardItem(opportunity));
                }
            }
            catch (JsonException)
            {
                // Keep rendering other evidence and streamed answer text when one bounded artifact item is malformed.
            }
        }
        return cards;
    }

    private static bool IsRenderable(ProcurementOpportunity value) => !string.IsNullOrWhiteSpace(value.Id) && !string.IsNullOrWhiteSpace(value.Provider) && !string.IsNullOrWhiteSpace(value.Title) && value.Agency is not null && value.Location is not null && value.Funding is not null && value.Source is not null;
}

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
    public string DisplayLabel => IsAudio ? "Audio attachment" : "Image attachment";
}
